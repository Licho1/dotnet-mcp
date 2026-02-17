using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Rename;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class RenameTools
{
    [McpServerTool(Name = "rename-symbol"), Description(
        "Rename a symbol across the entire solution. Applies changes to disk and returns a summary of modified files.")]
    public static async Task<string> RenameSymbol(
        WorkspaceService workspace,
        [Description("Current symbol name")] string symbolName,
        [Description("New name for the symbol")] string newName,
        [Description("Optional: filter by symbol kind (class, method, property, field, interface, enum, struct)")] string? kind = null,
        CancellationToken ct = default)
    {
        var symbols = await workspace.FindSymbolsAsync(symbolName, ct);

        if (!string.IsNullOrEmpty(kind))
        {
            symbols = kind.ToLowerInvariant() switch
            {
                "class" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Class }),
                "interface" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Interface }),
                "struct" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Struct }),
                "enum" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Enum }),
                "method" => symbols.Where(s => s is IMethodSymbol),
                "property" => symbols.Where(s => s is IPropertySymbol),
                "field" => symbols.Where(s => s is IFieldSymbol),
                _ => symbols
            };
        }

        var symbolList = symbols.ToList();
        if (symbolList.Count == 0)
            return $"No symbol found matching '{symbolName}'.";
        if (symbolList.Count > 1)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Multiple symbols found matching '{symbolName}'. Use 'kind' parameter to disambiguate:");
            foreach (var s in symbolList.Take(10))
            {
                var loc = s.Locations.FirstOrDefault();
                var locStr = loc?.IsInSource == true
                    ? $" @ {loc.SourceTree?.FilePath}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
                    : "";
                sb.AppendLine($"  [{GetKind(s)}] {s.ToDisplayString()}{locStr}");
            }
            return sb.ToString().TrimEnd();
        }

        var target = symbolList[0];
        var solution = workspace.GetSolution();

        var renamedSolution = await Renamer.RenameSymbolAsync(
            solution, target, new SymbolRenameOptions(), newName, ct);

        // Find which documents changed
        var changedDocs = new List<string>();
        foreach (var projectId in renamedSolution.ProjectIds)
        {
            var oldProject = solution.GetProject(projectId);
            var newProject = renamedSolution.GetProject(projectId);
            if (oldProject is null || newProject is null) continue;

            foreach (var docId in newProject.DocumentIds)
            {
                var oldDoc = oldProject.GetDocument(docId);
                var newDoc = newProject.GetDocument(docId);
                if (oldDoc is null || newDoc is null) continue;

                var oldText = await oldDoc.GetTextAsync(ct);
                var newText = await newDoc.GetTextAsync(ct);
                if (oldText.ToString() != newText.ToString())
                {
                    changedDocs.Add(newDoc.FilePath ?? newDoc.Name);
                    // Write to disk
                    if (newDoc.FilePath is not null)
                        File.WriteAllText(newDoc.FilePath, newText.ToString());
                }
            }
        }

        // Reload the solution to pick up changes
        if (workspace.LoadedPath is not null)
        {
            if (workspace.LoadedPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
                await workspace.LoadSolutionAsync(workspace.LoadedPath, ct);
            else
                await workspace.LoadProjectAsync(workspace.LoadedPath, ct);
        }

        var result = new StringBuilder();
        result.AppendLine($"Renamed '{symbolName}' → '{newName}'");
        result.AppendLine($"Modified {changedDocs.Count} files:");
        foreach (var doc in changedDocs)
            result.AppendLine($"  {doc}");

        return result.ToString().TrimEnd();
    }

    static string GetKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString(),
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        _ => symbol.Kind.ToString()
    };
}
