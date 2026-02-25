using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace DotnetMcp.Services;

public class RazorSourceMapper
{
    record LineMappingEntry(int GeneratedLine, string SourceFile, int SourceLine, int SourceCol = 1);

    readonly ConcurrentDictionary<string, List<LineMappingEntry>> _mappingCache = new(StringComparer.OrdinalIgnoreCase);
    // Use ConcurrentDictionary as a thread-safe set to guard against concurrent EnsureGeneratedFilesAsync calls
    readonly ConcurrentDictionary<string, byte> _builtProjects = new(StringComparer.OrdinalIgnoreCase);

    static readonly Regex OldLineDirective = new(@"^\s*#line\s+(\d+)\s+""([^""]+)""", RegexOptions.Compiled);
    static readonly Regex NewLineDirective = new(@"^\s*#line\s+\((\d+),(\d+)\)-\(\d+,\d+\)\s+\d+\s+""([^""]+)""", RegexOptions.Compiled);
    static readonly Regex HiddenOrDefault = new(@"^\s*#line\s+(hidden|default)\b", RegexOptions.Compiled);

    // Match both separator styles: Roslyn often uses forward slashes even on Windows
    public static bool IsGeneratedFile(string filePath) =>
        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
        (filePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
         filePath.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase));

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

    public async Task EnsureGeneratedFilesAsync(Solution solution, CancellationToken ct = default)
    {
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is null) continue;
            var projectDir = Path.GetDirectoryName(project.FilePath)!;
            if (!_builtProjects.TryAdd(projectDir, 0)) continue; // already checked

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

            var hasGeneratedFiles = FindGeneratedRazorFiles([projectDir]).Any();
            if (!hasGeneratedFiles)
                await TriggerBuildAsync(project.FilePath, ct);
        }
    }

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

    List<LineMappingEntry> GetOrParseMappings(string generatedFilePath) =>
        _mappingCache.GetOrAdd(generatedFilePath, ParseMappings);

    static List<LineMappingEntry> ParseMappings(string generatedFilePath)
    {
        var mappings = new List<LineMappingEntry>();
        try
        {
            var lines = File.ReadAllLines(generatedFilePath);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                var m = NewLineDirective.Match(line);
                if (m.Success)
                {
                    if (!m.Groups[3].Value.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) continue;
                    mappings.Add(new(i + 1, m.Groups[3].Value, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
                    continue;
                }

                m = OldLineDirective.Match(line);
                if (m.Success)
                {
                    if (!m.Groups[2].Value.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) continue;
                    mappings.Add(new(i + 1, m.Groups[2].Value, int.Parse(m.Groups[1].Value)));
                    continue;
                }

                if (HiddenOrDefault.IsMatch(line))
                    mappings.Add(new(i + 1, "", 0));
            }
        }
        catch (IOException) { }
        return mappings;
    }

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
