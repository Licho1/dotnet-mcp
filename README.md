# dotnet-mcp

MCP server providing semantic C# code understanding to AI assistants via [Roslyn](https://github.com/dotnet/roslyn).

**What this does:** Gives AI tools the same code understanding that an IDE has — symbol resolution, type hierarchies, semantic references, refactoring — things that text search and CLI tools can't provide.

## Tools

### Solution Management
| Tool | Description |
|------|-------------|
| `load-solution` | Load a .sln file for analysis |
| `load-project` | Load a single .csproj file |
| `list-projects` | List all projects in loaded solution |

### Symbol Discovery
| Tool | Description |
|------|-------------|
| `find-symbol` | Find symbol declarations by name with kind filter |
| `find-references` | Find all usages of a symbol across the solution |
| `find-implementations` | Find interface implementations or derived classes |
| `expression-type` | Resolve the type of an expression at file:line:col |

### Code Navigation
| Tool | Description |
|------|-------------|
| `goto-definition` | Jump from usage to definition (file:line:col → definition location) |
| `type-hierarchy` | Full hierarchy: base types, interfaces, derived types |
| `list-members` | All members of a type with signatures and visibility |
| `get-document-symbols` | File outline — all declarations with line numbers |
| `get-source` | Extract source code or decompile metadata assemblies |

### Refactoring
| Tool | Description |
|------|-------------|
| `rename-symbol` | Semantic rename across entire solution, writes to disk |

## Setup

```bash
dotnet build src/DotnetMcp/DotnetMcp.csproj
```

### Claude Code integration

Add to your MCP config (`.mcp.json`):

```json
{
  "mcpServers": {
    "dotnet-mcp": {
      "command": "dotnet",
      "args": ["run", "--project", "c:/work/dotnet-mcp/src/DotnetMcp/DotnetMcp.csproj"]
    }
  }
}
```

## Requirements

- .NET 10 SDK
- A .NET solution/project to analyze
