using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class FindReferencesTools
{
    [McpServerTool(Name = "find-references"), Description(
        "Find all references (usages) of a symbol across the entire solution. " +
        "Returns file locations and surrounding context for each reference.")]
    public static async Task<string> FindReferences(
        WorkspaceService workspace,
        [Description("Symbol name to find references for")] string symbolName,
        [Description("Optional: filter by symbol kind (class, method, property, field, interface)")] string? kind,
        [Description("Optional: max number of references to return (default: 100)")] int? maxResults,
        CancellationToken ct)
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

        var refs = await workspace.FindReferencesAsync(target, ct);
        var limit = maxResults ?? 100;

        var sb = new StringBuilder();
        sb.AppendLine($"# References to {target.ToDisplayString()} [{GetKind(target)}]");
        sb.AppendLine();

        var total = 0;
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

                sb.AppendLine($"  {filePath}:{line}:{col}");
                total++;
            }
        }

        if (total == 0)
            sb.AppendLine("  (no references found)");
        else
            sb.AppendLine($"\nTotal: {total} references");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "find-implementations"), Description(
        "Find all implementations of an interface or overrides of a virtual/abstract method.")]
    public static async Task<string> FindImplementations(
        WorkspaceService workspace,
        [Description("Interface or base type name")] string typeName,
        CancellationToken ct)
    {
        var symbols = await workspace.FindSymbolsAsync(typeName, ct);
        var type = symbols.OfType<INamedTypeSymbol>().FirstOrDefault();

        if (type is null)
            return $"No type found matching '{typeName}'.";

        var sb = new StringBuilder();

        if (type.TypeKind == TypeKind.Interface)
        {
            var implementations = await workspace.FindImplementationsAsync(type, ct);
            sb.AppendLine($"# Implementations of {type.ToDisplayString()}");
            sb.AppendLine();

            foreach (var impl in implementations.OrderBy(t => t.ToDisplayString()))
            {
                var loc = impl.Locations.FirstOrDefault();
                var locStr = loc?.IsInSource == true
                    ? $" @ {loc.SourceTree?.FilePath}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
                    : " (metadata)";
                sb.AppendLine($"  → {impl.ToDisplayString()}{locStr}");
            }

            if (!implementations.Any())
                sb.AppendLine("  (no implementations found)");
        }
        else
        {
            var derived = await workspace.FindDerivedTypesAsync(type, ct);
            sb.AppendLine($"# Types derived from {type.ToDisplayString()}");
            sb.AppendLine();

            foreach (var d in derived.OrderBy(t => t.ToDisplayString()))
            {
                var loc = d.Locations.FirstOrDefault();
                var locStr = loc?.IsInSource == true
                    ? $" @ {loc.SourceTree?.FilePath}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
                    : " (metadata)";
                sb.AppendLine($"  → {d.ToDisplayString()}{locStr}");
            }

            if (!derived.Any())
                sb.AppendLine("  (no derived types found)");
        }

        return sb.ToString().TrimEnd();
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
