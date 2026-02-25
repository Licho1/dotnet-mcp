# Razor (.cshtml) Reference Support — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make `find-references` show symbol usages in `.cshtml` Razor views alongside normal C# references.

**Architecture:** The Razor compiler generates `.g.cs` files inside `obj/` that contain `#line` directives mapping generated code back to source `.cshtml` files. We add a `RazorSourceMapper` service that (a) finds these generated files, (b) triggers `dotnet build` if they're missing, (c) parses `#line` directives to map generated-file locations back to `.cshtml` locations, and (d) is injected into `FindReferencesTools` to post-process any references found in `.g.cs` files.

**Tech Stack:** Roslyn (`Microsoft.CodeAnalysis`), System.Text.RegularExpressions, System.Diagnostics.Process, .NET 10

---

### Task 1: Create `RazorSourceMapper` service

**Files:**
- Create: `src/DotnetMcp/Services/RazorSourceMapper.cs`

**Step 1: Create the file with the `RazorSourceMapper` class**

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace DotnetMcp.Services;

/// <summary>
/// Maps symbol reference locations in Razor-generated .g.cs files back to their
/// original .cshtml source locations using #line directives.
/// </summary>
public class RazorSourceMapper
{
    record LineMappingEntry(int GeneratedLine, string SourceFile, int SourceLine, int SourceCol = 1);

    // Cache parsed mappings per generated file path
    readonly ConcurrentDictionary<string, List<LineMappingEntry>> _mappingCache = new(StringComparer.OrdinalIgnoreCase);
    // Track which projects we've already triggered a build for
    readonly HashSet<string> _builtProjects = new(StringComparer.OrdinalIgnoreCase);

    static readonly Regex OldLineDirective = new(@"^\s*#line\s+(\d+)\s+""([^""]+)""", RegexOptions.Compiled);
    static readonly Regex NewLineDirective = new(@"^\s*#line\s+\((\d+),(\d+)\)-\(\d+,\d+\)\s+\d+\s+""([^""]+)""", RegexOptions.Compiled);
    static readonly Regex HiddenOrDefault = new(@"^\s*#line\s+(hidden|default)\b", RegexOptions.Compiled);

    /// <summary>Returns true if the file is a generated .g.cs file inside an obj/ directory.</summary>
    public static bool IsGeneratedFile(string filePath) =>
        filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
        filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Given a .g.cs file location, tries to map it to the original .cshtml file location.
    /// Returns null if the location has no cshtml mapping.
    /// </summary>
    public (string cshtmlPath, int cshtmlLine, int cshtmlCol)? TryMap(string generatedFilePath, int refLine)
    {
        var mappings = GetOrParseMappings(generatedFilePath);

        // Find the last #line directive at or before refLine
        LineMappingEntry? last = null;
        foreach (var entry in mappings)
        {
            if (entry.GeneratedLine > refLine) break;
            last = entry;
        }

        if (last is null || string.IsNullOrEmpty(last.SourceFile))
            return null;

        // Source line = base + offset from directive line to reference line
        var sourceLine = last.SourceLine + (refLine - last.GeneratedLine - 1);
        if (sourceLine < 1) sourceLine = last.SourceLine;

        var cshtmlPath = ResolveCshtmlPath(last.SourceFile, generatedFilePath);
        return cshtmlPath is null ? null : (cshtmlPath, sourceLine, last.SourceCol);
    }

    /// <summary>Returns all .g.cs files under obj/ directories that contain cshtml line mappings.</summary>
    public IEnumerable<string> FindGeneratedRazorFiles(IEnumerable<string> projectDirs)
    {
        foreach (var dir in projectDirs)
        {
            var objDir = Path.Combine(dir, "obj");
            if (!Directory.Exists(objDir)) continue;

            foreach (var file in Directory.EnumerateFiles(objDir, "*.g.cs", SearchOption.AllDirectories))
            {
                if (ContainsCshtmlMappings(file))
                    yield return file;
            }
        }
    }

    /// <summary>
    /// For each project in the solution that has .cshtml files but no generated .g.cs files,
    /// runs `dotnet build` to generate them.
    /// </summary>
    public async Task EnsureGeneratedFilesAsync(Solution solution, CancellationToken ct = default)
    {
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is null) continue;
            var projectDir = Path.GetDirectoryName(project.FilePath)!;
            if (_builtProjects.Contains(projectDir)) continue;

            var hasCshtml = Directory.EnumerateFiles(projectDir, "*.cshtml", SearchOption.AllDirectories).Any();
            if (!hasCshtml) continue;

            var hasGeneratedFiles = FindGeneratedRazorFiles([projectDir]).Any();
            if (!hasGeneratedFiles)
                await TriggerBuildAsync(project.FilePath, ct);

            _builtProjects.Add(projectDir);
        }
    }

    // --- Private helpers ---

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

                // Enhanced format: #line (row,col)-(row,col) N "file"
                var m = NewLineDirective.Match(line);
                if (m.Success)
                {
                    mappings.Add(new(i + 1, m.Groups[3].Value, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
                    continue;
                }

                // Classic format: #line N "file"
                m = OldLineDirective.Match(line);
                if (m.Success)
                {
                    // Skip if file doesn't look like cshtml (avoid false matches on #line 1 "SomeFile.cs")
                    if (!m.Groups[2].Value.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) continue;
                    mappings.Add(new(i + 1, m.Groups[2].Value, int.Parse(m.Groups[1].Value)));
                    continue;
                }

                // #line hidden / #line default — clears active mapping
                if (HiddenOrDefault.IsMatch(line))
                    mappings.Add(new(i + 1, "", 0));
            }
        }
        catch (IOException) { /* file locked / deleted; return what we have */ }
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
        // Absolute path — check it directly
        if (Path.IsPathRooted(rawPath))
            return File.Exists(rawPath) ? Path.GetFullPath(rawPath) : null;

        // The generated file lives inside obj/; walk up to find the project directory
        var projectDir = GetProjectDir(generatedFilePath);
        if (projectDir is null) return null;

        // Strip leading slash (common in razor-generated paths like "/Views/Home/Index.cshtml")
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
            await proc.WaitForExitAsync(ct);
            _mappingCache.Clear(); // invalidate cache; files may have changed
        }
        catch { /* build failed; continue without generated files */ }
    }
}
```

**Step 2: Verify the file compiles**

```bash
cd /c/work/dotnet-mcp
dotnet build src/DotnetMcp/DotnetMcp.csproj -v q
```

Expected: Build succeeded (0 errors).

**Step 3: Commit**

```bash
git add src/DotnetMcp/Services/RazorSourceMapper.cs
git commit -m "feat: add RazorSourceMapper service for cshtml #line directive parsing"
```

---

### Task 2: Register `RazorSourceMapper` in DI and inject into `FindReferencesTools`

**Files:**
- Modify: `src/DotnetMcp/Program.cs` — add `AddSingleton<RazorSourceMapper>()`
- Modify: `src/DotnetMcp/Tools/FindReferencesTools.cs` — inject mapper, post-process locations

**Step 1: Register in Program.cs**

In `src/DotnetMcp/Program.cs`, add after the existing `AddSingleton` calls:
```csharp
builder.Services.AddSingleton<RazorSourceMapper>();
```

So the services block becomes:
```csharp
builder.Services.AddSingleton<WorkspaceService>();
builder.Services.AddSingleton<SourceResolutionService>();
builder.Services.AddSingleton<RazorSourceMapper>();
```

**Step 2: Update `FindReferences` in `FindReferencesTools.cs`**

Replace the current `FindReferences` method signature and body.

Current signature:
```csharp
public static async Task<string> FindReferences(
    WorkspaceService workspace,
    [Description("Symbol name to find references for")] string symbolName,
    ...
```

New signature adds `RazorSourceMapper razorMapper` after `workspace`:
```csharp
public static async Task<string> FindReferences(
    WorkspaceService workspace,
    RazorSourceMapper razorMapper,
    [Description("Symbol name to find references for")] string symbolName,
    [Description("Optional: filter by symbol kind (class, method, property, field, interface)")] string? kind = null,
    [Description("Optional: max number of references to return (default: 100)")] int? maxResults = null,
    CancellationToken ct = default)
{
    var symbols = await workspace.FindSymbolsAsync(symbolName, ct);

    if (!string.IsNullOrEmpty(kind))
    {
        symbols = kind.ToLowerInvariant() switch
        {
            "class" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Class }),
            "interface" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Interface }),
            "method" => symbols.Where(s => s is IMethodSymbol),
            "property" => symbols.Where(s => s is IPropertySymbol),
            "field" => symbols.Where(s => s is IFieldSymbol),
            _ => symbols
        };
    }

    var target = symbols.FirstOrDefault();
    if (target is null)
        return $"No symbol found matching '{symbolName}'.";

    // Ensure .g.cs files exist for any Razor-using projects (build if needed)
    var sln = await workspace.GetSolutionAsync(ct);
    await razorMapper.EnsureGeneratedFilesAsync(sln, ct);

    var refs = await workspace.FindReferencesAsync(target, ct);
    var limit = maxResults ?? 100;

    var sb = new StringBuilder();
    sb.AppendLine($"# References to {target.ToDisplayString()} [{GetKind(target)}]");
    sb.AppendLine();

    var total = 0;
    var razorTotal = 0;

    foreach (var refSymbol in refs)
    {
        foreach (var location in refSymbol.Locations)
        {
            if (total >= limit)
            {
                sb.AppendLine($"\n... truncated at {limit} results");
                return sb.ToString().TrimEnd();
            }

            var span = location.Location.GetLineSpan();
            var filePath = span.Path;
            var line = span.StartLinePosition.Line + 1;
            var col = span.StartLinePosition.Character + 1;

            // If this reference is in a generated Razor .g.cs file, map it back to .cshtml
            if (RazorSourceMapper.IsGeneratedFile(filePath))
            {
                var mapped = razorMapper.TryMap(filePath, line);
                if (mapped is not null)
                {
                    sb.AppendLine($"  {mapped.Value.cshtmlPath}:{mapped.Value.cshtmlLine}   [Razor]");
                    total++;
                    razorTotal++;
                    continue;
                }
            }

            sb.AppendLine($"  {filePath}:{line}:{col}");
            total++;
        }
    }

    if (total == 0)
        sb.AppendLine("  (no references found)");
    else
    {
        var suffix = razorTotal > 0 ? $" ({razorTotal} Razor)" : "";
        sb.AppendLine($"\nTotal: {total} references{suffix}");
    }

    return sb.ToString().TrimEnd();
}
```

**Step 3: Verify the file compiles**

```bash
cd /c/work/dotnet-mcp
dotnet build src/DotnetMcp/DotnetMcp.csproj -v q
```

Expected: Build succeeded (0 errors).

**Step 4: Commit**

```bash
git add src/DotnetMcp/Program.cs src/DotnetMcp/Tools/FindReferencesTools.cs
git commit -m "feat: integrate RazorSourceMapper into find-references tool"
```

---

### Task 3: Test against zenidweb

**Goal:** Verify that `find-references` on a type used in .cshtml files returns those .cshtml locations.

**Step 1: Build zenidweb (to generate .g.cs files)**

```bash
cd /c/work/zenid/zenidweb
dotnet build ZenidWeb.csproj --no-restore -v q
```

Expected: Build succeeded. Check that `obj/` now contains `.g.cs` files with cshtml mappings:
```bash
find /c/work/zenid/zenidweb/obj -name "*.g.cs" | head -5
```

**Step 2: Rebuild dotnet-mcp**

```bash
cd /c/work/dotnet-mcp
dotnet build src/DotnetMcp/DotnetMcp.csproj
```

**Step 3: Verify manually via the MCP tool**

Load the solution and call find-references on `AccessSettings` (which is `@model ZenidShared.AccessSettings` in `Views/Access/AccessIndex.cshtml`):

Use the `load` tool with path `c:\work\zenid\zenidweb\ZenidWeb.csproj`, then call `find-references` with symbolName `AccessSettings`.

Expected output should include:
```
  C:\work\zenid\zenidweb\Views\Access\AccessIndex.cshtml:1   [Razor]
```

**Step 4: Investigate if .g.cs files are generated in unexpected locations**

If the above doesn't show Razor references, check where Razor .g.cs files actually are:
```bash
find /c/work/zenid/zenidweb/obj -name "*.g.cs" -exec grep -l ".cshtml" {} \; 2>/dev/null | head
```

If they're missing, the project may use runtime compilation (no build-time generation). In that case, check if `RazorCompileOnBuild` needs to be set.

**Step 5: Commit any fixes found during testing**

```bash
git add -p
git commit -m "fix: address issues found during cshtml reference testing"
```

---

### Task 4: Handle edge case — runtime-compiled Razor projects

Some ASP.NET Core projects don't generate `.g.cs` files at build time (they use `AddRazorRuntimeCompilation()`). For these, no `.g.cs` files will be in `obj/` even after building.

**Step 1: Check if zenidweb uses runtime compilation**

```bash
grep -r "RuntimeCompilation\|RazorCompileOnBuild\|UseRuntimeCompilation" /c/work/zenid/zenidweb/ 2>/dev/null
```

**Step 2: If runtime compilation is used, add text-search fallback**

If `EnsureGeneratedFilesAsync` builds the project but still no `.g.cs` files appear, we need a text search fallback for .cshtml files. Add this method to `RazorSourceMapper`:

```csharp
/// <summary>
/// Text-search .cshtml files in projectDirs for the given symbol simple name.
/// Returns (filePath, lineNumber) for each match. Marked as lower-confidence.
/// </summary>
public IEnumerable<(string filePath, int line)> TextSearchCshtml(
    IEnumerable<string> projectDirs, string symbolSimpleName)
{
    foreach (var dir in projectDirs)
    {
        foreach (var cshtml in Directory.EnumerateFiles(dir, "*.cshtml", SearchOption.AllDirectories))
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
```

Then in `FindReferencesTools.FindReferences`, after the Roslyn reference loop, check if `razorTotal == 0` and call `TextSearchCshtml` as a fallback:

```csharp
// Fallback: text search in .cshtml if no Razor references found via .g.cs mapping
if (razorTotal == 0)
{
    var projectDirs = sln.Projects
        .Where(p => p.FilePath is not null)
        .Select(p => Path.GetDirectoryName(p.FilePath)!)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    foreach (var (cshtmlPath, cshtmlLine) in razorMapper.TextSearchCshtml(projectDirs, target.Name))
    {
        if (total >= limit) break;
        sb.AppendLine($"  {cshtmlPath}:{cshtmlLine}   [Razor/text]");
        total++;
        razorTotal++;
    }
}
```

**Step 3: Build and verify**

```bash
cd /c/work/dotnet-mcp
dotnet build src/DotnetMcp/DotnetMcp.csproj -v q
```

**Step 4: Commit**

```bash
git add src/DotnetMcp/Services/RazorSourceMapper.cs src/DotnetMcp/Tools/FindReferencesTools.cs
git commit -m "feat: add text-search fallback for runtime-compiled Razor projects"
```

---

### Task 5: Update README

**Files:**
- Modify: `README.md` — update `find-references` description to mention cshtml support

**Step 1: Update the find-references row in the tools table**

Change:
```
| `find-references` | Find all usages of a symbol across the solution (semantic, not text search) |
```
To:
```
| `find-references` | Find all usages of a symbol across the solution. Semantic for .cs files; also finds usages in .cshtml Razor views (via generated code mapping, with text-search fallback) |
```

**Step 2: Commit**

```bash
git add README.md
git commit -m "docs: update find-references description to mention cshtml support"
```
