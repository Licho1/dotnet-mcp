using System.ComponentModel;
using System.Text;
using DotnetMcp.Services;
using Microsoft.CodeAnalysis;
using ModelContextProtocol.Server;

namespace DotnetMcp.Tools;

[McpServerToolType]
public static class TypeHierarchyTools
{
    [McpServerTool(Name = "type-hierarchy"), Description(
        "Full type information: base types, interfaces, derived types/implementations, and all members. " +
        "Provides the complete picture of a type that text search cannot — inheritance chains, " +
        "interface implementations across the solution, and member signatures with visibility.")]
    public static async Task<string> TypeHierarchy(
        WorkspaceService workspace,
        [Description("Type name to look up")] string typeName,
        CancellationToken ct)
    {
        var symbols = await workspace.FindSymbolsAsync(typeName, ct);
        var type = symbols.OfType<INamedTypeSymbol>().FirstOrDefault();

        if (type is null)
            return $"No type found matching '{typeName}'.";

        var sb = new StringBuilder();
        sb.AppendLine($"# {type.TypeKind} {type.ToDisplayString()}");
        sb.AppendLine();

        // Base types
        sb.AppendLine("## Base Types");
        var current = type.BaseType;
        var depth = 1;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            sb.AppendLine($"{new string(' ', depth * 2)}← {FormatType(current)}");
            current = current.BaseType;
            depth++;
        }
        if (depth == 1) sb.AppendLine("  (none beyond System.Object)");

        // Interfaces
        sb.AppendLine();
        sb.AppendLine("## Implemented Interfaces");
        if (type.AllInterfaces.IsEmpty)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var iface in type.AllInterfaces.OrderBy(i => i.ToDisplayString()))
                sb.AppendLine($"  : {FormatType(iface)}");
        }

        // Derived types / implementations
        sb.AppendLine();
        sb.AppendLine("## Derived Types");
        if (type.TypeKind == TypeKind.Interface)
        {
            var implementations = await workspace.FindImplementationsAsync(type, ct);
            if (!implementations.Any())
                sb.AppendLine("  (no implementations found)");
            else
                foreach (var impl in implementations.OrderBy(t => t.ToDisplayString()))
                    sb.AppendLine($"  → {FormatType(impl)}");
        }
        else if (!type.IsSealed)
        {
            var derived = await workspace.FindDerivedTypesAsync(type, ct);
            if (!derived.Any())
                sb.AppendLine("  (no derived types found)");
            else
                foreach (var d in derived.OrderBy(t => t.ToDisplayString()))
                    sb.AppendLine($"  → {FormatType(d)}");
        }
        else
        {
            sb.AppendLine("  (sealed type)");
        }

        // Members
        sb.AppendLine();
        sb.AppendLine("## Members");
        var members = type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName)
            .OrderBy(m => m.Kind).ThenBy(m => m.Name);

        foreach (var member in members)
        {
            var vis = member.DeclaredAccessibility.ToString().ToLowerInvariant();
            switch (member)
            {
                case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
                    var parms = string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
                    sb.AppendLine($"  [{vis}] {m.ReturnType.ToDisplayString()} {m.Name}({parms})");
                    break;
                case IPropertySymbol p:
                    var accessors = $"{(p.GetMethod is not null ? "get; " : "")}{(p.SetMethod is not null ? "set; " : "")}";
                    sb.AppendLine($"  [{vis}] {p.Type.ToDisplayString()} {p.Name} {{ {accessors}}}");
                    break;
                case IFieldSymbol f:
                    sb.AppendLine($"  [{vis}] {f.Type.ToDisplayString()} {f.Name}");
                    break;
                case IEventSymbol e:
                    sb.AppendLine($"  [{vis}] event {e.Type.ToDisplayString()} {e.Name}");
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    static string FormatType(INamedTypeSymbol type)
    {
        var loc = type.Locations.FirstOrDefault();
        var locStr = loc?.IsInSource == true
            ? $" @ {loc.SourceTree?.FilePath}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
            : " (metadata)";
        return $"{type.ToDisplayString()}{locStr}";
    }
}
