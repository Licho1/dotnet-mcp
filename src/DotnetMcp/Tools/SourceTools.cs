using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class SourceTools
{
    [McpServerTool(Name = "get-source"), Description(
        "Resolve a symbol and get its source code + metadata. Works for both local and library/framework code. " +
        "Resolves through: local source, SourceLink (downloads original from GitHub/etc), embedded PDB, or decompilation. " +
        "Returns symbol info (kind, type, location, containing type) plus source code. " +
        "Look up by symbol name OR by file:line:col (useful for navigating to library code at a call site).")]
    public static async Task<string> GetSource(
        WorkspaceService workspace,
        SourceResolutionService resolver,
        [Description("Symbol name to get source for (use this OR filePath+line+column)")] string? symbolName = null,
        [Description("Optional: filter by symbol kind (class, method, property, field, interface)")] string? kind = null,
        [Description("Full path to source file (use with line+column to resolve symbol at location)")] string? filePath = null,
        [Description("Line number (1-based, use with filePath)")] int? line = null,
        [Description("Column number (1-based, use with filePath)")] int? column = null,
        CancellationToken ct = default)
    {
        ISymbol? symbol;

        if (filePath is not null && line is not null)
        {
            symbol = await ResolveSymbolAtLocation(workspace, filePath, line.Value, column ?? 1, ct);
            if (symbol is null)
                return $"No symbol found at {filePath}:{line}:{column ?? 1}";
        }
        else if (symbolName is not null)
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

            symbol = symbols.FirstOrDefault();
            if (symbol is null)
                return $"No symbol found matching '{symbolName}'.";
        }
        else
        {
            return "Provide either 'symbolName' or 'filePath'+'line' to locate the symbol.";
        }

        var sb = new StringBuilder();

        // Symbol info header
        sb.AppendLine($"// Symbol: {symbol.Name} [{GetKind(symbol)}]");
        sb.AppendLine($"// Qualified: {symbol.ToDisplayString()}");

        if (symbol.ContainingType is not null)
            sb.AppendLine($"// ContainingType: {symbol.ContainingType.ToDisplayString()}");

        // Type info for typed symbols
        var typeSymbol = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol param => param.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol prop => prop.Type,
            IMethodSymbol method => method.ReturnType,
            IEventSymbol evt => evt.Type,
            _ => null
        };
        if (typeSymbol is not null)
            sb.AppendLine($"// Type: {typeSymbol.ToDisplayString()}");

        // Method signature
        if (symbol is IMethodSymbol m && m.Parameters.Length > 0)
            sb.AppendLine($"// Params: {string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))}");

        // Definition location
        var loc = symbol.Locations.FirstOrDefault();
        if (loc?.IsInSource == true)
        {
            var span = loc.GetLineSpan();
            sb.AppendLine($"// Defined: {span.Path}:{span.StartLinePosition.Line + 1}");
        }
        else if (loc?.IsInMetadata == true)
        {
            sb.AppendLine($"// Assembly: {symbol.ContainingAssembly?.ToDisplayString()}");
        }

        // Source resolution
        var result = await resolver.ResolveAsync(symbol, ct);
        if (result is null)
        {
            sb.AppendLine($"// Could not resolve source");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"// Source: {result.FilePath} [{result.ResolutionMethod}]");
        sb.AppendLine();
        sb.Append(result.Source);

        return sb.ToString().TrimEnd();
    }

    static async Task<ISymbol?> ResolveSymbolAtLocation(
        WorkspaceService workspace, string filePath, int line, int column, CancellationToken ct)
    {
        var semanticModel = await workspace.GetSemanticModelAsync(filePath, ct);
        var syntaxTree = await workspace.GetSyntaxTreeAsync(filePath, ct);
        if (semanticModel is null || syntaxTree is null) return null;

        var root = await syntaxTree.GetRootAsync(ct);
        var text = await syntaxTree.GetTextAsync(ct);

        if (line < 1 || line > text.Lines.Count) return null;

        var position = text.Lines[line - 1].Start + (column - 1);
        var token = root.FindToken(position);
        var node = token.Parent;
        if (node is null) return null;

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        return symbolInfo.Symbol
            ?? symbolInfo.CandidateSymbols.FirstOrDefault()
            ?? semanticModel.GetDeclaredSymbol(node);
    }

    static string GetKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString(),
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        ILocalSymbol => "Local",
        IParameterSymbol => "Parameter",
        IEventSymbol => "Event",
        _ => symbol.Kind.ToString()
    };
}

static class SymbolExtensions
{
    public static string GetMetadataName(this INamedTypeSymbol type)
    {
        var parts = new List<string>();
        var current = type;
        while (current is not null)
        {
            parts.Add(current.MetadataName);
            current = current.ContainingType;
        }

        parts.Reverse();
        var typeName = string.Join("+", parts);

        return type.ContainingNamespace?.IsGlobalNamespace == false
            ? $"{type.ContainingNamespace.ToDisplayString()}.{typeName}"
            : typeName;
    }
}
