using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class CallGraphTools
{
    [McpServerTool(Name = "find-callers"), Description(
        "Find all methods that call a given method. Returns the calling method name and location.")]
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
        var sln = workspace.GetSolution();

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

                // Find the enclosing method of this reference
                var node = root.FindNode(location.Location.SourceSpan);
                var enclosing = node.AncestorsAndSelf().FirstOrDefault(n =>
                    n is MethodDeclarationSyntax or ConstructorDeclarationSyntax
                    or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
                    or ArrowExpressionClauseSyntax);

                if (enclosing is null) continue;

                var enclosingSymbol = model.GetDeclaredSymbol(enclosing)
                    ?? model.GetDeclaredSymbol(enclosing.Parent!);

                var callerName = enclosingSymbol is not null
                    ? FormatMethodSymbol(enclosingSymbol)
                    : GetEnclosingName(enclosing);

                callers.Add((callerName, span.Path, span.StartLinePosition.Line + 1));
            }
        }

        // Deduplicate (same method might call target multiple times)
        var unique = callers.DistinctBy(c => c.Method).Take(limit).ToList();

        foreach (var (method, file, line) in unique)
            sb.AppendLine($"  {method} @ {file}:{line}");

        if (unique.Count == 0)
            sb.AppendLine("  (no callers found)");
        else
            sb.AppendLine($"\nTotal: {unique.Count} callers");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "find-callees"), Description(
        "Find all methods called by a given method. Returns the called method names and their locations.")]
    public static async Task<string> FindCallees(
        WorkspaceService workspace,
        [Description("Method name to find callees of")] string methodName,
        [Description("Optional: containing type name to disambiguate")] string? typeName = null,
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

        // Get the syntax node for this method
        var location = target.Locations.FirstOrDefault();
        if (location?.IsInSource != true)
            return $"Method '{methodName}' has no source location.";

        var tree = location.SourceTree;
        if (tree is null) return "Could not get syntax tree.";

        var sln = workspace.GetSolution();
        var docId = sln.GetDocumentIdsWithFilePath(tree.FilePath).FirstOrDefault();
        if (docId is null) return "Could not find document.";

        var doc = sln.GetDocument(docId);
        if (doc is null) return "Could not find document.";

        var model = await doc.GetSemanticModelAsync(ct);
        if (model is null) return "Could not get semantic model.";

        var root = await tree.GetRootAsync(ct);
        var methodNode = root.FindNode(location.SourceSpan);

        // Walk up to the full method declaration
        var declaration = methodNode.AncestorsAndSelf().FirstOrDefault(n =>
            n is MethodDeclarationSyntax or ConstructorDeclarationSyntax
            or LocalFunctionStatementSyntax);
        declaration ??= methodNode;

        var sb = new StringBuilder();
        sb.AppendLine($"# Callees of {target.ContainingType?.Name}.{target.Name}");
        sb.AppendLine();

        var callees = new List<(string Name, string Location)>();

        // Find all invocations within this method body
        foreach (var invocation in declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var symbolInfo = model.GetSymbolInfo(invocation);
            if (symbolInfo.Symbol is IMethodSymbol callee)
            {
                var calleeLoc = callee.Locations.FirstOrDefault();
                var locStr = calleeLoc?.IsInSource == true
                    ? $"{calleeLoc.SourceTree?.FilePath}:{calleeLoc.GetLineSpan().StartLinePosition.Line + 1}"
                    : $"{callee.ContainingAssembly?.Name} (metadata)";

                callees.Add((FormatMethodSymbol(callee), locStr));
            }
        }

        // Also find object creation (constructor calls)
        foreach (var creation in declaration.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var symbolInfo = model.GetSymbolInfo(creation);
            if (symbolInfo.Symbol is IMethodSymbol ctor)
            {
                var ctorLoc = ctor.Locations.FirstOrDefault();
                var locStr = ctorLoc?.IsInSource == true
                    ? $"{ctorLoc.SourceTree?.FilePath}:{ctorLoc.GetLineSpan().StartLinePosition.Line + 1}"
                    : $"{ctor.ContainingAssembly?.Name} (metadata)";

                callees.Add(($"new {ctor.ContainingType?.Name}()", locStr));
            }
        }

        // Also find member access that resolves to property getters/setters
        foreach (var access in declaration.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            var symbolInfo = model.GetSymbolInfo(access);
            if (symbolInfo.Symbol is IPropertySymbol prop)
            {
                var propLoc = prop.Locations.FirstOrDefault();
                var locStr = propLoc?.IsInSource == true
                    ? $"{propLoc.SourceTree?.FilePath}:{propLoc.GetLineSpan().StartLinePosition.Line + 1}"
                    : $"{prop.ContainingAssembly?.Name} (metadata)";

                callees.Add(($"{prop.ContainingType?.Name}.{prop.Name}", locStr));
            }
        }

        var unique = callees.DistinctBy(c => c.Name).ToList();

        foreach (var (name, loc) in unique)
            sb.AppendLine($"  → {name} @ {loc}");

        if (unique.Count == 0)
            sb.AppendLine("  (no callees found)");
        else
            sb.AppendLine($"\nTotal: {unique.Count} callees");

        return sb.ToString().TrimEnd();
    }

    static string FormatMethodSymbol(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => $"{m.ContainingType?.Name}.{m.Name}",
        IPropertySymbol p => $"{p.ContainingType?.Name}.{p.Name}",
        _ => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
    };

    static string GetEnclosingName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        LocalFunctionStatementSyntax l => l.Identifier.Text,
        AccessorDeclarationSyntax a => a.Keyword.Text,
        _ => node.Kind().ToString()
    };
}
