# dotnet-mcp

MCP server providing semantic C# code understanding to AI assistants via [Roslyn](https://github.com/dotnet/roslyn).

**What this does:** Gives AI tools the same code understanding that an IDE has — symbol resolution, type hierarchies, semantic references, refactoring — things that text search and CLI tools can't provide.

## Tools

| Tool | Description |
|------|-------------|
| `load` | Load a .sln or .csproj file for analysis |
| `get-source` | Resolve a symbol (by name or file:line:col) and get its source code + metadata. 4-tier resolution: local source, SourceLink, embedded PDB, decompilation |
| `type-hierarchy` | Full type info: base types, interfaces, derived types/implementations, and all members |
| `find-references` | Find all usages of a symbol across the solution (semantic, not text search) |
| `find-implementations` | Find interface implementations or derived classes |
| `find-callers` | Find all methods that call a given method |
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
      "args": ["run", "--project", "/path/to/dotnet-mcp/src/DotnetMcp/DotnetMcp.csproj"]
    }
  }
}
```

## Requirements

- .NET 10 SDK
- A .NET solution/project to analyze
