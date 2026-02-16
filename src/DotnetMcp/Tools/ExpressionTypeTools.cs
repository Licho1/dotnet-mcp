using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class ExpressionTypeTools
{
    [McpServerTool(Name = "expression-type"), Description(
        "Resolve the type of an expression or symbol at a specific location in a source file. " +
        "Useful for understanding what type a variable, expression, or method call resolves to.")]
    public static async Task<string> ExpressionType(
        WorkspaceService workspace,
        [Description("Full path to the source file")] string filePath,
        [Description("Line number (1-based)")] int line,
        [Description("Column number (1-based)")] int column,
        CancellationToken ct)
    {
        var semanticModel = await workspace.GetSemanticModelAsync(filePath, ct);
        if (semanticModel is null)
            return $"File not found in loaded solution: {filePath}";

        var syntaxTree = await workspace.GetSyntaxTreeAsync(filePath, ct);
        if (syntaxTree is null)
            return $"Could not get syntax tree for: {filePath}";

        var root = await syntaxTree.GetRootAsync(ct);
        var position = syntaxTree.GetText(ct).Lines[line - 1].Start + (column - 1);
        var token = root.FindToken(position);
        var node = token.Parent;

        if (node is null)
            return $"No syntax node found at {filePath}:{line}:{column}";

        var sb = new StringBuilder();
        sb.AppendLine($"# Type Info at {filePath}:{line}:{column}");
        sb.AppendLine();
        sb.AppendLine($"Token: `{token.Text}`");
        sb.AppendLine($"Syntax: {node.Kind()} — `{node.ToString().Truncate(100)}`");

        // Try symbol info first
        var symbolInfo = semanticModel.GetSymbolInfo(node);
        if (symbolInfo.Symbol is { } symbol)
        {
            sb.AppendLine();
            sb.AppendLine($"## Symbol");
            sb.AppendLine($"Name: {symbol.ToDisplayString()}");
            sb.AppendLine($"Kind: {GetKind(symbol)}");

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
                sb.AppendLine($"Type: {typeSymbol.ToDisplayString()}");

            var loc = symbol.Locations.FirstOrDefault();
            if (loc?.IsInSource == true)
                sb.AppendLine($"Defined at: {loc.SourceTree?.FilePath}:{loc.GetLineSpan().StartLinePosition.Line + 1}");
        }

        // Try type info (for expressions)
        var typeInfo = semanticModel.GetTypeInfo(node);
        if (typeInfo.Type is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"## Expression Type");
            sb.AppendLine($"Type: {typeInfo.Type.ToDisplayString()}");
            if (typeInfo.ConvertedType is not null && !SymbolEqualityComparer.Default.Equals(typeInfo.Type, typeInfo.ConvertedType))
                sb.AppendLine($"Converted to: {typeInfo.ConvertedType.ToDisplayString()}");
        }

        // If it's an invocation, show overload info
        if (node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax)
        {
            if (symbolInfo.CandidateSymbols.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"## Candidate Overloads ({symbolInfo.CandidateReason})");
                foreach (var candidate in symbolInfo.CandidateSymbols.Take(10))
                    sb.AppendLine($"  - {candidate.ToDisplayString()}");
            }
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

static class StringExtensions
{
    public static string Truncate(this string s, int maxLength) =>
        s.Length <= maxLength ? s : s[..maxLength] + "...";
}
