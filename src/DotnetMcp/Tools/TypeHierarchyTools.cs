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
        "Show the full type hierarchy for a named type: base types (ancestors), " +
        "implemented interfaces, and derived types (descendants).")]
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
        sb.AppendLine($"# Type Hierarchy for {type.ToDisplayString()}");
        sb.AppendLine();

        // Ancestors (base type chain)
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
        var interfaces = type.AllInterfaces;
        if (interfaces.IsEmpty)
        {
            sb.AppendLine("  (none)");
        }
        else
        {
            foreach (var iface in interfaces.OrderBy(i => i.ToDisplayString()))
                sb.AppendLine($"  : {FormatType(iface)}");
        }

        // Derived types
        sb.AppendLine();
        sb.AppendLine("## Derived Types");
        if (type.TypeKind == TypeKind.Interface)
        {
            var implementations = await workspace.FindImplementationsAsync(type, ct);
            if (!implementations.Any())
            {
                sb.AppendLine("  (no implementations found)");
            }
            else
            {
                foreach (var impl in implementations.OrderBy(t => t.ToDisplayString()))
                    sb.AppendLine($"  → {FormatType(impl)}");
            }
        }
        else if (!type.IsSealed)
        {
            var derived = await workspace.FindDerivedTypesAsync(type, ct);
            if (!derived.Any())
            {
                sb.AppendLine("  (no derived types found)");
            }
            else
            {
                foreach (var d in derived.OrderBy(t => t.ToDisplayString()))
                    sb.AppendLine($"  → {FormatType(d)}");
            }
        }
        else
        {
            sb.AppendLine("  (sealed type)");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "list-members"), Description(
        "List all members of a type (methods, properties, fields, events). " +
        "Shows visibility, return types, and parameter signatures.")]
    public static async Task<string> ListMembers(
        WorkspaceService workspace,
        [Description("Type name to inspect")] string typeName,
        [Description("Optional: filter by member kind (method, property, field, event)")] string? memberKind,
        [Description("Optional: include inherited members (default: false)")] bool includeInherited,
        CancellationToken ct)
    {
        var symbols = await workspace.FindSymbolsAsync(typeName, ct);
        var type = symbols.OfType<INamedTypeSymbol>().FirstOrDefault();

        if (type is null)
            return $"No type found matching '{typeName}'.";

        var members = includeInherited
            ? type.GetMembers().Concat(GetInheritedMembers(type))
            : type.GetMembers();

        // Filter out compiler-generated
        members = members.Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName);

        if (!string.IsNullOrEmpty(memberKind))
        {
            members = memberKind.ToLowerInvariant() switch
            {
                "method" => members.Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary }),
                "property" => members.Where(m => m is IPropertySymbol),
                "field" => members.Where(m => m is IFieldSymbol),
                "event" => members.Where(m => m is IEventSymbol),
                _ => members
            };
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# Members of {type.ToDisplayString()}");
        sb.AppendLine();

        foreach (var member in members.OrderBy(m => m.Kind).ThenBy(m => m.Name))
        {
            var vis = member.DeclaredAccessibility.ToString().ToLowerInvariant();
            var loc = member.Locations.FirstOrDefault();
            var locStr = loc?.IsInSource == true
                ? $" @ {loc.SourceTree?.FilePath}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
                : "";

            switch (member)
            {
                case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
                    var parms = string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"));
                    sb.AppendLine($"  [{vis}] {m.ReturnType.ToDisplayString()} {m.Name}({parms}){locStr}");
                    break;
                case IPropertySymbol p:
                    sb.AppendLine($"  [{vis}] {p.Type.ToDisplayString()} {p.Name} {{ {(p.GetMethod is not null ? "get; " : "")}{(p.SetMethod is not null ? "set; " : "")}}}{locStr}");
                    break;
                case IFieldSymbol f:
                    sb.AppendLine($"  [{vis}] {f.Type.ToDisplayString()} {f.Name}{locStr}");
                    break;
                case IEventSymbol e:
                    sb.AppendLine($"  [{vis}] event {e.Type.ToDisplayString()} {e.Name}{locStr}");
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    static IEnumerable<ISymbol> GetInheritedMembers(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            foreach (var member in current.GetMembers().Where(m => !m.IsImplicitlyDeclared && m.CanBeReferencedByName))
                yield return member;
            current = current.BaseType;
        }
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
