using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace DotnetMcp.Services;

/// <summary>
/// Maps symbol reference locations in Razor-generated .g.cs files back to their
/// original .cshtml source locations using #line directives.
/// Supports both old-style physical .g.cs files (pre-.NET 6) and new-style
/// in-memory Roslyn Source Generator documents (.NET 6+).
/// </summary>
public class RazorSourceMapper
{
    record LineMappingEntry(int GeneratedLine, string SourceFile, int SourceLine, int SourceCol = 1);

    // Cache parsed mappings per generated file path (synthetic or real)
    readonly ConcurrentDictionary<string, List<LineMappingEntry>> _mappingCache = new(StringComparer.OrdinalIgnoreCase);
    // Use ConcurrentDictionary as a thread-safe set to guard against concurrent EnsureGeneratedFilesAsync calls
    readonly ConcurrentDictionary<string, byte> _builtProjects = new(StringComparer.OrdinalIgnoreCase);

    static readonly Regex OldLineDirective = new(@"^\s*#line\s+(\d+)\s+""([^""]+)""", RegexOptions.Compiled);
    static readonly Regex NewLineDirective = new(@"^\s*#line\s+\((\d+),(\d+)\)-\(\d+,\d+\)(?:\s+\d+)?\s+""([^""]+)""", RegexOptions.Compiled);
    static readonly Regex HiddenOrDefault = new(@"^\s*#line\s+(hidden|default)\b", RegexOptions.Compiled);

    // Match both separator styles: Roslyn often uses forward slashes even on Windows
    public static bool IsGeneratedFile(string filePath) =>
        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
        (filePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
         filePath.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Given a location in an original .cshtml file, tries to find the corresponding
    /// location in a generated .g.cs file. Returns null if no mapping is found.
    /// Call EnsureGeneratedFilesAsync first to populate the cache.
    /// </summary>
    public (string gcsPath, int gcsLine, int gcsCol)? TryMapReverse(string cshtmlPath, int cshtmlLine, int cshtmlCol = 1)
    {
        cshtmlPath = Path.GetFullPath(cshtmlPath);

        foreach (var (gcsPath, mappings) in _mappingCache)
        {
            LineMappingEntry? best = null;
            foreach (var entry in mappings)
            {
                if (string.IsNullOrEmpty(entry.SourceFile)) continue;

                var resolved = ResolveCshtmlPath(entry.SourceFile, gcsPath);
                if (resolved is null) continue;
                if (!string.Equals(resolved, cshtmlPath, StringComparison.OrdinalIgnoreCase)) continue;

                // Keep entry with highest SourceLine that is still <= cshtmlLine
                if (entry.SourceLine <= cshtmlLine && (best is null || entry.SourceLine > best.SourceLine))
                    best = entry;
            }

            if (best is null) continue;

            // Inverse of TryMap: gcsLine = best.GeneratedLine + 1 + (cshtmlLine - best.SourceLine)
            var gcsLine = best.GeneratedLine + 1 + (cshtmlLine - best.SourceLine);
            return (gcsPath, gcsLine, best.SourceCol);
        }

        return null;
    }

    /// <summary>
    /// Given a location in a .g.cs file (real or synthetic), tries to map it to
    /// the original .cshtml file location. Returns null if no mapping is found.
    /// </summary>
    public (string cshtmlPath, int cshtmlLine, int cshtmlCol)? TryMap(string generatedFilePath, int refLine)
    {
        var mappings = GetOrParseMappings(generatedFilePath);

        LineMappingEntry? last = null;
        foreach (var entry in mappings)
        {
            if (entry.GeneratedLine > refLine) break;
            last = entry;
        }

        if (last is null || string.IsNullOrEmpty(last.SourceFile))
            return null;

        // The #line directive at GeneratedLine means "next line = SourceLine", so the offset is (refLine - GeneratedLine - 1)
        var sourceLine = last.SourceLine + (refLine - last.GeneratedLine - 1);
        if (sourceLine < 1) sourceLine = 1;

        var cshtmlPath = ResolveCshtmlPath(last.SourceFile, generatedFilePath);
        return cshtmlPath is null ? null : (cshtmlPath, sourceLine, last.SourceCol);
    }

    /// <summary>
    /// Ensures the mapping cache is populated for all Razor projects in the solution.
    /// For .NET 6+ projects: populates from in-memory source-generated documents.
    /// For older projects: triggers dotnet build if no physical .g.cs files exist.
    /// </summary>
    public async Task EnsureGeneratedFilesAsync(Solution solution, CancellationToken ct = default)
    {
        // Primary path: populate from Roslyn Source Generator documents (in-memory, .NET 6+)
        await PopulateFromSourceGeneratorsAsync(solution, ct);

        // Fallback path: for old-style projects, trigger a build if no .g.cs files found
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is null) continue;
            var projectDir = Path.GetDirectoryName(project.FilePath)!;
            if (!_builtProjects.TryAdd(projectDir, 0)) continue;

            bool hasCshtml;
            try
            {
                hasCshtml = Directory.EnumerateFiles(projectDir, "*.cshtml", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                }).Any();
            }
            catch (UnauthorizedAccessException) { continue; }

            if (!hasCshtml) continue;

            // If source-generator population already found cshtml mappings for this project, skip the build
            var objDir = Path.Combine(projectDir, "obj");
            var hasMappings = _mappingCache.Any(kv =>
                kv.Key.StartsWith(objDir, StringComparison.OrdinalIgnoreCase) &&
                kv.Value.Any(m => !string.IsNullOrEmpty(m.SourceFile)));
            if (!hasMappings && !FindGeneratedRazorFiles([projectDir]).Any())
                await TriggerBuildAsync(project.FilePath, ct);
        }
    }

    /// <summary>Returns all physical .g.cs files under obj/ directories that contain cshtml mappings.</summary>
    public IEnumerable<string> FindGeneratedRazorFiles(IEnumerable<string> projectDirs)
    {
        foreach (var dir in projectDirs)
        {
            var objDir = Path.Combine(dir, "obj");
            if (!Directory.Exists(objDir)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(objDir, "*.g.cs", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                });
            }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
            {
                if (ContainsCshtmlMappings(file))
                    yield return file;
            }
        }
    }

    /// <summary>
    /// Text-search .cshtml files for the given symbol name.
    /// Used as a last-resort fallback when .g.cs mapping is unavailable.
    /// </summary>
    public IEnumerable<(string filePath, int line)> TextSearchCshtml(
        IEnumerable<string> projectDirs, string symbolSimpleName)
    {
        foreach (var dir in projectDirs)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.cshtml", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                });
            }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var cshtml in files)
            {
                var lineNum = 0;
                foreach (var text in File.ReadLines(cshtml))
                {
                    lineNum++;
                    if (text.Contains(symbolSimpleName, StringComparison.Ordinal))
                        yield return (cshtml, lineNum);
                }
            }
        }
    }

    // --- Source Generator support ---

    /// <summary>
    /// Populates the mapping cache from in-memory Roslyn source-generated documents.
    /// This is the primary path for .NET 6+ projects using the Razor Source Generator.
    /// </summary>
    async Task PopulateFromSourceGeneratorsAsync(Solution solution, CancellationToken ct)
    {
        foreach (var project in solution.Projects)
        {
            try
            {
                var generatedDocs = await project.GetSourceGeneratedDocumentsAsync(ct);
                foreach (var doc in generatedDocs)
                {
                    if (doc.FilePath is null) continue;
                    if (!doc.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (_mappingCache.ContainsKey(doc.FilePath)) continue;

                    var text = await doc.GetTextAsync(ct);
                    var mappings = ParseMappingsFromLines(doc.FilePath, GetLines(text.ToString()));
                    if (mappings.Any(m => !string.IsNullOrEmpty(m.SourceFile)))
                        _mappingCache.TryAdd(doc.FilePath, mappings);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* source generator errors are non-fatal */ }
        }
    }

    // --- Private helpers ---

    List<LineMappingEntry> GetOrParseMappings(string generatedFilePath)
    {
        if (_mappingCache.TryGetValue(generatedFilePath, out var cached))
            return cached;

        // Try reading from disk (old-style physical .g.cs files)
        return _mappingCache.GetOrAdd(generatedFilePath, path =>
        {
            try { return ParseMappingsFromLines(path, File.ReadAllLines(path)); }
            catch { return []; }
        });
    }

    static List<LineMappingEntry> ParseMappingsFromLines(string filePath, string[] lines)
    {
        var mappings = new List<LineMappingEntry>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Enhanced format: #line (row,col)-(row,col) N "file.cshtml"
            var m = NewLineDirective.Match(line);
            if (m.Success)
            {
                if (!m.Groups[3].Value.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) continue;
                mappings.Add(new(i + 1, m.Groups[3].Value, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
                continue;
            }

            // Classic format: #line N "file.cshtml"
            m = OldLineDirective.Match(line);
            if (m.Success)
            {
                if (!m.Groups[2].Value.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) continue;
                mappings.Add(new(i + 1, m.Groups[2].Value, int.Parse(m.Groups[1].Value)));
                continue;
            }

            // #line hidden / #line default — clears active mapping
            if (HiddenOrDefault.IsMatch(line))
                mappings.Add(new(i + 1, "", 0));
        }
        return mappings;
    }

    static string[] GetLines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n');

    static bool ContainsCshtmlMappings(string filePath)
    {
        try
        {
            foreach (var line in File.ReadLines(filePath))
                if (line.Contains(".cshtml\"", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        catch (IOException) { }
        return false;
    }

    static string? ResolveCshtmlPath(string rawPath, string generatedFilePath)
    {
        if (Path.IsPathRooted(rawPath))
            return File.Exists(rawPath) ? Path.GetFullPath(rawPath) : null;

        var projectDir = GetProjectDir(generatedFilePath);
        if (projectDir is null) return null;

        var relative = rawPath.TrimStart('/', '\\');
        var candidate = Path.GetFullPath(Path.Combine(projectDir, relative));
        return File.Exists(candidate) ? candidate : null;
    }

    static string? GetProjectDir(string generatedFilePath)
    {
        var dir = Path.GetDirectoryName(generatedFilePath);
        while (dir is not null)
        {
            if (string.Equals(Path.GetFileName(dir), "obj", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(dir);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    async Task TriggerBuildAsync(string projectFilePath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{projectFilePath}\" --no-restore -v q")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        try
        {
            using var proc = Process.Start(psi)!;
            try
            {
                await proc.WaitForExitAsync(ct);
                _mappingCache.Clear();
            }
            catch (OperationCanceledException)
            {
                proc.Kill(entireProcessTree: true);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }
}
