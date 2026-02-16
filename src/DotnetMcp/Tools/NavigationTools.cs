using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class NavigationTools
{
    [McpServerTool(Name = "goto-definition"), Description(
        "Navigate from a usage to the definition of the symbol at a given file location. " +
        "Returns the file path and line number of the definition.")]
    public static async Task<string> GotoDefinition(
        WorkspaceService workspace,
        [Description("Full path to the source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")] int column,
        CancellationToken ct = default)
    {
        var semanticModel = await workspace.GetSemanticModelAsync(filePath, ct);
        if (semanticModel is null)
            return $"File not found in loaded solution: {filePath}";

        var syntaxTree = await workspace.GetSyntaxTreeAsync(filePath, ct);
        if (syntaxTree is null)
            return $"Could not get syntax tree for: {filePath}";

        var root = await syntaxTree.GetRootAsync(ct);
        var text = await syntaxTree.GetTextAsync(ct);

        if (line < 1 || line > text.Lines.Count)
            return $"Line {line} out of range (file has {text.Lines.Count} lines).";

        var position = text.Lines[line - 1].Start + (column - 1);
        var token = root.FindToken(position);
        var node = token.Parent;

        if (node is null)
            return $"No syntax node found at {filePath}:{line}:{column}";

        var symbolInfo = semanticModel.GetSymbolInfo(node);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        // Also try declared symbol (if we're on a declaration)
        symbol ??= semanticModel.GetDeclaredSymbol(node);

        if (symbol is null)
            return $"No symbol found at {filePath}:{line}:{column} (token: '{token.Text}')";

        var sb = new StringBuilder();
        sb.AppendLine($"# Definition of {symbol.ToDisplayString()}");
        sb.AppendLine($"Kind: {GetKind(symbol)}");

        foreach (var loc in symbol.Locations)
        {
            if (loc.IsInSource)
            {
                var span = loc.GetLineSpan();
                sb.AppendLine($"Source: {span.Path}:{span.StartLinePosition.Line + 1}:{span.StartLinePosition.Character + 1}");
            }
            else if (loc.IsInMetadata)
            {
                sb.AppendLine($"Metadata: {symbol.ContainingAssembly?.ToDisplayString()}");
            }
        }

        // Show containing type if it's a member
        if (symbol.ContainingType is not null)
            sb.AppendLine($"Containing type: {symbol.ContainingType.ToDisplayString()}");

        // Show signature details
        switch (symbol)
        {
            case IMethodSymbol method:
                sb.AppendLine($"Returns: {method.ReturnType.ToDisplayString()}");
                if (method.Parameters.Length > 0)
                    sb.AppendLine($"Params: {string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))}");
                break;
            case IPropertySymbol prop:
                sb.AppendLine($"Type: {prop.Type.ToDisplayString()}");
                break;
            case IFieldSymbol field:
                sb.AppendLine($"Type: {field.Type.ToDisplayString()}");
                break;
        }

        return sb.ToString().TrimEnd();
    }

    static string GetKind(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol t => t.TypeKind.ToString(),
        IMethodSymbol => "Method",
        IPropertySymbol => "Property",
        IFieldSymbol => "Field",
        ILocalSymbol => "Local",
        IParameterSymbol => "Parameter",
        _ => symbol.Kind.ToString()
    };
}
