using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class CallGraphTools
{
    [McpServerTool(Name = "find-callers"), Description(
        "Find all methods that call a given method. Semantic analysis across the entire solution — " +
        "resolves overloads correctly, unlike text search.")]
    public static async Task<string> FindCallers(
        WorkspaceService workspace,
        [Description("Method name to find callers of")] string methodName,
        [Description("Optional: containing type name to disambiguate")] string? typeName = null,
        [Description("Optional: max results (default: 100)")] int? maxResults = null,
        CancellationToken ct = default)
    {
        var symbols = await workspace.FindSymbolsAsync(methodName, ct);
        var methods = symbols.OfType<IMethodSymbol>().AsEnumerable();

        if (!string.IsNullOrEmpty(typeName))
            methods = methods.Where(m =>
                m.ContainingType?.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase) == true);

        var target = methods.FirstOrDefault();
        if (target is null)
            return $"No method found matching '{methodName}'" +
                   (typeName is not null ? $" in type '{typeName}'" : "") + ".";

        var refs = await workspace.FindReferencesAsync(target, ct);
        var limit = maxResults ?? 100;
        var sln = await workspace.GetSolutionAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# Callers of {target.ContainingType?.Name}.{target.Name}");
        sb.AppendLine();

        var callers = new List<(string Method, string File, int Line)>();

        foreach (var refSymbol in refs)
        {
            foreach (var location in refSymbol.Locations)
            {
                if (!location.Location.IsInSource) continue;

                var span = location.Location.GetLineSpan();
                var docId = sln.GetDocumentIdsWithFilePath(span.Path).FirstOrDefault();
                if (docId is null) continue;

                var doc = sln.GetDocument(docId);
                if (doc is null) continue;

                var model = await doc.GetSemanticModelAsync(ct);
                var root = await doc.GetSyntaxRootAsync(ct);
                if (model is null || root is null) continue;

                var node = root.FindNode(location.Location.SourceSpan);
                var enclosing = node.AncestorsAndSelf().FirstOrDefault(n =>
                    n is MethodDeclarationSyntax or ConstructorDeclarationSyntax
                    or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
                    or ArrowExpressionClauseSyntax);

                if (enclosing is null) continue;

                var enclosingSymbol = model.GetDeclaredSymbol(enclosing)
                    ?? model.GetDeclaredSymbol(enclosing.Parent!);

                var callerName = enclosingSymbol is not null
                    ? $"{enclosingSymbol.ContainingType?.Name}.{enclosingSymbol.Name}"
                    : GetEnclosingName(enclosing);

                callers.Add((callerName, span.Path, span.StartLinePosition.Line + 1));
            }
        }

        var unique = callers.DistinctBy(c => c.Method).Take(limit).ToList();

        foreach (var (method, file, line) in unique)
            sb.AppendLine($"  {method} @ {file}:{line}");

        if (unique.Count == 0)
            sb.AppendLine("  (no callers found)");
        else
            sb.AppendLine($"\nTotal: {unique.Count} callers");

        return sb.ToString().TrimEnd();
    }

    static string GetEnclosingName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        LocalFunctionStatementSyntax l => l.Identifier.Text,
        AccessorDeclarationSyntax a => a.Keyword.Text,
        _ => node.Kind().ToString()
    };
}
