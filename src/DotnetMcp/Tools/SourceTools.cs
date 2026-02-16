using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using ModelContextProtocol.Server;
using FullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class SourceTools
{
    [McpServerTool(Name = "get-source"), Description(
        "Get the source code of a symbol. For source symbols, extracts from syntax tree. " +
        "For metadata symbols (NuGet packages, framework), decompiles the assembly.")]
    public static async Task<string> GetSource(
        WorkspaceService workspace,
        [Description("Symbol name to get source for")] string symbolName,
        [Description("Optional: filter by symbol kind (class, method, property, field, interface)")] string? kind = null,
        CancellationToken ct = default)
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

        var symbol = symbols.FirstOrDefault();
        if (symbol is null)
            return $"No symbol found matching '{symbolName}'.";

        var location = symbol.Locations.FirstOrDefault();

        // Source symbol — extract from syntax tree
        if (location?.IsInSource == true)
        {
            var tree = location.SourceTree;
            if (tree is null) return "Could not get syntax tree.";

            var root = await tree.GetRootAsync(ct);
            var node = root.FindNode(location.SourceSpan);

            // Walk up to the declaration node
            var declaration = node.AncestorsAndSelf().FirstOrDefault(n =>
                n is TypeDeclarationSyntax or MethodDeclarationSyntax or PropertyDeclarationSyntax
                or FieldDeclarationSyntax or EnumDeclarationSyntax or InterfaceDeclarationSyntax
                or ConstructorDeclarationSyntax or EventDeclarationSyntax or DelegateDeclarationSyntax
                or RecordDeclarationSyntax);

            var sourceText = (declaration ?? node).ToFullString().Trim();
            var lineSpan = location.GetLineSpan();
            return $"// {lineSpan.Path}:{lineSpan.StartLinePosition.Line + 1}\n{sourceText}";
        }

        // Metadata symbol — try to decompile
        if (location?.IsInMetadata == true && symbol.ContainingAssembly is not null)
        {
            return DecompileSymbol(workspace, symbol);
        }

        return $"Symbol '{symbolName}' has no source or metadata location.";
    }

    static string DecompileSymbol(WorkspaceService workspace, ISymbol symbol)
    {
        // Find the assembly path from the compilation references
        var sln = workspace.GetSolution();
        foreach (var project in sln.Projects)
        {
            foreach (var reference in project.MetadataReferences)
            {
                if (reference is not PortableExecutableReference peRef) continue;
                if (peRef.FilePath is null) continue;

                // Check if this reference contains the symbol's assembly
                if (!peRef.FilePath.Contains(symbol.ContainingAssembly.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var decompiler = new CSharpDecompiler(peRef.FilePath, new DecompilerSettings());

                    if (symbol is INamedTypeSymbol namedType)
                    {
                        var fullName = new FullTypeName(namedType.GetMetadataName());
                        return $"// Decompiled from {peRef.FilePath}\n{decompiler.DecompileTypeAsString(fullName)}";
                    }

                    // For members, find the parent type first
                    if (symbol.ContainingType is not null)
                    {
                        var parentName = new FullTypeName(symbol.ContainingType.GetMetadataName());
                        var typeDef = decompiler.TypeSystem.MainModule.GetTypeDefinition(parentName.TopLevelTypeName);
                        if (typeDef is not null)
                        {
                            var member = typeDef.Members.FirstOrDefault(m =>
                                m.Name == symbol.Name);
                            if (member is not null)
                                return $"// Decompiled from {peRef.FilePath}\n{decompiler.DecompileAsString(member.MetadataToken)}";
                        }

                        // Fall back to decompiling the whole type
                        return $"// Decompiled from {peRef.FilePath}\n{decompiler.DecompileTypeAsString(parentName)}";
                    }
                }
                catch (Exception ex)
                {
                    return $"Failed to decompile: {ex.Message}";
                }
            }
        }

        return $"Could not find assembly for '{symbol.ContainingAssembly.Name}' to decompile.";
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
