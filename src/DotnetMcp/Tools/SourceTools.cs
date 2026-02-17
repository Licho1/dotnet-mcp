using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class SourceTools
{
    [McpServerTool(Name = "get-source"), Description(
        "Get the source code of a symbol. Resolves through: local source, SourceLink (downloads original from GitHub/etc), " +
        "embedded PDB source, or decompilation (ILSpy) as fallback. " +
        "Can look up by symbol name OR by file:line:col location (useful for navigating to library code at a call site).")]
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
            // Resolve symbol at file:line:col location
            symbol = await ResolveSymbolAtLocation(workspace, filePath, line.Value, column ?? 1, ct);
            if (symbol is null)
                return $"No symbol found at {filePath}:{line}:{column ?? 1}";
        }
        else if (symbolName is not null)
        {
            // Look up by name
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

        var result = await resolver.ResolveAsync(symbol, ct);
        if (result is null)
            return $"Could not resolve source for '{symbol.ToDisplayString()}'.";

        return $"// {result.FilePath} [{result.ResolutionMethod}]\n{result.Source}";
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

    [McpServerTool(Name = "get-document-symbols"), Description(
        "List all symbols declared in a source file (types, methods, properties, fields) as a structured outline. " +
        "Like VS Code's document outline / breadcrumb.")]
    public static async Task<string> GetDocumentSymbols(
        WorkspaceService workspace,
        [Description("Full path to the source file")] string filePath,
        CancellationToken ct = default)
    {
        var tree = await workspace.GetSyntaxTreeAsync(filePath, ct);
        if (tree is null)
            return $"File not found in loaded solution: {filePath}";

        var root = await tree.GetRootAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine($"# Symbols in {Path.GetFileName(filePath)}");
        sb.AppendLine();

        WalkDeclarations(root, sb, 0);
        return sb.ToString().TrimEnd();
    }

    static void WalkDeclarations(SyntaxNode node, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);

        switch (node)
        {
            case NamespaceDeclarationSyntax ns:
                sb.AppendLine($"{indent}namespace {ns.Name}");
                foreach (var child in ns.Members)
                    WalkDeclarations(child, sb, depth + 1);
                return;

            case FileScopedNamespaceDeclarationSyntax ns:
                sb.AppendLine($"{indent}namespace {ns.Name}");
                foreach (var child in ns.Members)
                    WalkDeclarations(child, sb, depth + 1);
                return;

            case TypeDeclarationSyntax type:
                var typeKind = type switch
                {
                    ClassDeclarationSyntax => "class",
                    InterfaceDeclarationSyntax => "interface",
                    RecordDeclarationSyntax r => r.ClassOrStructKeyword.Text == "struct" ? "record struct" : "record",
                    StructDeclarationSyntax => "struct",
                    _ => "type"
                };
                var line = type.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"{indent}{typeKind} {type.Identifier.Text} (line {line})");

                foreach (var member in type.Members)
                    WalkDeclarations(member, sb, depth + 1);
                return;

            case EnumDeclarationSyntax enumDecl:
                var eLine = enumDecl.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"{indent}enum {enumDecl.Identifier.Text} (line {eLine})");
                foreach (var member in enumDecl.Members)
                {
                    var mLine = member.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    sb.AppendLine($"{indent}  {member.Identifier.Text} (line {mLine})");
                }
                return;

            case DelegateDeclarationSyntax del:
                var dLine = del.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"{indent}delegate {del.ReturnType} {del.Identifier.Text} (line {dLine})");
                return;

            case MethodDeclarationSyntax method:
                var mline = method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var parms = string.Join(", ", method.ParameterList.Parameters.Select(p => $"{p.Type} {p.Identifier}"));
                sb.AppendLine($"{indent}{method.ReturnType} {method.Identifier.Text}({parms}) (line {mline})");
                return;

            case ConstructorDeclarationSyntax ctor:
                var cLine = ctor.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"{indent}{ctor.Identifier.Text}(...) (line {cLine})");
                return;

            case PropertyDeclarationSyntax prop:
                var pLine = prop.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"{indent}{prop.Type} {prop.Identifier.Text} (line {pLine})");
                return;

            case FieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                {
                    var fLine = variable.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    sb.AppendLine($"{indent}{field.Declaration.Type} {variable.Identifier.Text} (line {fLine})");
                }
                return;

            case EventDeclarationSyntax evt:
                var evLine = evt.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                sb.AppendLine($"{indent}event {evt.Type} {evt.Identifier.Text} (line {evLine})");
                return;
        }

        // For compilation unit and other containers, recurse into children
        foreach (var child in node.ChildNodes())
            WalkDeclarations(child, sb, depth);
    }
}

static class SymbolExtensions
{
    public static string GetMetadataName(this INamedTypeSymbol type)
    {
        var parts = new List<string>();
        var current = type;
        while (current is not null)
        {
            var name = current.MetadataName;
            parts.Add(name);
            current = current.ContainingType;
        }

        parts.Reverse();
        var typeName = string.Join("+", parts);

        return type.ContainingNamespace?.IsGlobalNamespace == false
            ? $"{type.ContainingNamespace.ToDisplayString()}.{typeName}"
            : typeName;
    }
}
