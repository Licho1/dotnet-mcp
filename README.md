# dotnet-mcp

MCP server providing semantic C# code understanding to AI assistants via [Roslyn](https://github.com/dotnet/roslyn).

**What this does:** Gives AI tools the same code understanding that an IDE has — symbol resolution, type hierarchies, semantic references — things that text search and CLI tools can't provide.

## Tools

| Tool | Description |
|------|-------------|
| `symbol-lookup` | Find symbol definition by name (type, method, property, etc.) |
| `type-hierarchy` | Show inheritance chain, interface implementations, derived types |
| `find-references` | Find all usages of a symbol across the solution |
| `expression-type` | Resolve the type of an expression at a given file location |

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

- .NET 9 SDK
- A .NET solution/project to analyze
