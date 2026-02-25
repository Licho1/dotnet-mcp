# Design: Razor (.cshtml) Reference Support in find-references

**Date**: 2026-02-25
**Status**: Approved

## Problem

`find-references` uses Roslyn's `SymbolFinder` which operates on C# compilation documents. Razor `.cshtml` files are compiled to `.g.cs` files in the `obj/` directory. Roslyn can find references in these generated files, but reports locations in the `.g.cs` file — not the original `.cshtml` file. References in unbuilt projects are invisible entirely.

## Solution

Map references found in `.g.cs` files back to their original `.cshtml` source using the `#line` directives embedded by the Razor compiler. Trigger a build if `.g.cs` files don't exist.

## How Razor Compilation Works

When `dotnet build` runs on an ASP.NET Core project, the Razor compiler generates `.g.cs` files:
- Location: `obj/<Config>/` or `obj/<Config>/<Framework>/Razor/`
- Named like: `Views_Home_Index.cshtml.g.cs`
- Contain `#line` directives mapping back to source:

```csharp
// Old format:
#line 5 "Views/Home/Index.cshtml"
some generated code;  // source line 5

// New enhanced format (.NET 6+):
#line (5,1)-(5,30) 3 "Views/Home/Index.cshtml"
some generated code;

#line hidden  // no source mapping
#line default // restore file's own line numbers
```

Roslyn's `SymbolFinder` already finds references in compiled `.g.cs` files; the problem is that it reports `.g.cs` locations instead of `.cshtml` locations.

## Architecture

### New Service: `RazorSourceMapper`

```csharp
// Services/RazorSourceMapper.cs
public class RazorSourceMapper
{
    // Given a path to a .g.cs file, build a line-by-line mapping to cshtml
    public IReadOnlyList<LineMapping> ParseMappings(string generatedFilePath);

    // Try to map a location in a .g.cs file to a .cshtml location
    public (string cshtmlPath, int cshtmlLine, int cshtmlCol)? TryMap(
        string generatedFilePath, int line, int col);

    // Find all .g.cs files with cshtml mappings under project dirs
    public IEnumerable<string> FindGeneratedRazorFiles(IEnumerable<string> projectDirs);

    // Trigger dotnet build on a project if no .g.cs files found
    public Task<bool> TryBuildAsync(string projectFilePath, CancellationToken ct);
}

record LineMapping(int GeneratedLine, string SourceFile, int SourceLine, int SourceCol);
```

### `#line` Directive Parsing

Algorithm to build `LineMapping[]` from a `.g.cs` file:
1. Read all lines; track current source file/line
2. `#line N "file"` → record mapping: generated_line+1 → (file, N)
3. `#line (r1,c1)-(r2,c2) N "file"` → record: generated_line+1 → (file, r1, c1)
4. `#line hidden` / `#line default` → clear active mapping

For a reference at generated line X:
- Find last mapping entry where `GeneratedLine < X`
- Source line = mapping.SourceLine + (X - mapping.GeneratedLine - 1)

### Changes to `FindReferencesTools`

Inject `RazorSourceMapper` and use it to post-process locations:

```csharp
// After finding all refs:
foreach (var location in refLocations)
{
    var filePath = location.GetLineSpan().Path;
    if (IsGeneratedRazorFile(filePath))
    {
        var mapped = mapper.TryMap(filePath, line, col);
        if (mapped is not null)
        {
            // Report mapped.cshtmlPath:mapped.cshtmlLine  [Razor]
            continue;
        }
    }
    // Report original location
}
```

### Build Trigger

If no `.g.cs` files with cshtml mappings found for any project:
1. Run `dotnet build --no-restore` on each project file
2. Re-scan for `.g.cs` files

### Output Format

```
# References to AccessSettings [Class]

  C:\zenidweb\Views\Access\AccessIndex.cshtml:1   [Razor]
  C:\zenidweb\Controllers\AccessController.cs:42:8

Total: 2 references (1 Razor)
```

## Identifying Generated Razor Files

A `.g.cs` file is considered a Razor-generated file if:
- It's in an `obj/` directory AND
- Contains at least one `#line N "*.cshtml"` directive

Alternatively, check the Roslyn `Document.FilePath` for `.g.cs` extension + scanning for `#line "*.cshtml"`.

## Caching

`RazorSourceMapper` caches parsed mappings per file (keyed by file path + last-modified timestamp). Cache is invalidated when the file changes.

## Testing

Test against `c:\work\zenid\zenidweb`:
1. Load the solution
2. Call `find-references` on `AccessSettings` (used in `Views/Access/AccessIndex.cshtml`)
3. Verify that `AccessIndex.cshtml:1` appears in the output
