using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class SymbolLookupTools
{
    [McpServerTool(Name = "find-symbol"), Description(
        "Find symbol declarations by name (types, methods, properties, fields, etc.). " +
        "Returns symbol kind, full qualified name, and source location.")]
    public static async Task<string> FindSymbol(
        WorkspaceService workspace,
        [Description("Symbol name to search for (exact match)")] string name,
        [Description("Optional: filter by symbol kind (class, method, property, field, interface, enum, struct, namespace)")] string? kind,
        CancellationToken ct)
    {
        var symbols = await workspace.FindSymbolsAsync(name, ct);
        var filtered = FilterByKind(symbols, kind);

        if (!filtered.Any())
        {
            // Try substring match
            var fuzzy = await workspace.FindSymbolsWithFilterAsync(
                name, s => s.Contains(name, StringComparison.OrdinalIgnoreCase), ct);
            filtered = FilterByKind(fuzzy, kind);

            if (!filtered.Any())
                return $"No symbols found matching '{name}'.";
        }

        return FormatSymbols(filtered);
    }

    static IEnumerable<ISymbol> FilterByKind(IEnumerable<ISymbol> symbols, string? kind)
    {
        if (string.IsNullOrEmpty(kind)) return symbols;

        return kind.ToLowerInvariant() switch
        {
            "class" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Class }),
            "interface" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Interface }),
            "struct" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Struct }),
            "enum" => symbols.Where(s => s is INamedTypeSymbol { TypeKind: TypeKind.Enum }),
            "method" => symbols.Where(s => s is IMethodSymbol),
            "property" => symbols.Where(s => s is IPropertySymbol),
            "field" => symbols.Where(s => s is IFieldSymbol),
            "namespace" => symbols.Where(s => s is INamespaceSymbol),
            _ => symbols
        };
    }

    static string FormatSymbols(IEnumerable<ISymbol> symbols)
    {
        var sb = new StringBuilder();
        foreach (var symbol in symbols.Take(50))
        {
            var location = symbol.Locations.FirstOrDefault();
            var locationStr = location?.IsInSource == true
                ? $"{location.SourceTree?.FilePath}:{location.GetLineSpan().StartLinePosition.Line + 1}"
                : "metadata";

            sb.AppendLine($"[{GetSymbolKind(symbol)}] {symbol.ToDisplayString()} @ {locationStr}");

            if (symbol is IMethodSymbol method)
            {
                sb.AppendLine($"  Returns: {method.ReturnType.ToDisplayString()}");
                if (method.Parameters.Length > 0)
                    sb.AppendLine($"  Params: {string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))}");
            }
            else if (symbol is INamedTypeSymbol type)
            {
                if (type.BaseType is not null && type.BaseType.SpecialType != SpecialType.System_Object)
                    sb.AppendLine($"  Base: {type.BaseType.ToDisplayString()}");
                if (type.Interfaces.Length > 0)
                    sb.AppendLine($"  Implements: {string.Join(", ", type.Interfaces.Select(i => i.ToDisplayString()))}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    static string GetSymbolKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString(),
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        IEventSymbol => "Event",
        INamespaceSymbol => "Namespace",
        _ => symbol.Kind.ToString()
    };
}
