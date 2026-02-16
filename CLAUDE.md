# dotnet-mcp

## Purpose
MCP (Model Context Protocol) server providing **semantic C# code understanding** to AI assistants.
Fills the gap that CLI tools and text search cannot cover — everything that requires Roslyn's semantic model.

## Core Capabilities (MVP)
1. **Symbol Lookup** — find definition, references, implementations (semantic, not grep)
2. **Type Hierarchy** — inheritance chains, interface implementations, base types
3. **Find References** — all usages of a symbol across the solution
4. **Expression Type Resolution** — "what type is this expression at line X, col Y?"

## Planned Capabilities
- **Semantic Rename** — rename across solution with overload/namespace awareness
- **Dead Code Detection** — semantically unused symbols
- **Code Actions / Quick Fixes** — Roslyn lightbulb suggestions
- **Overload Resolution** — which overload gets called with given arguments
- **Decompilation** — ILSpy-style view of referenced assemblies

## Tech Stack
- .NET 9 (or latest stable)
- `ModelContextProtocol` NuGet package (official C# MCP SDK)
- `Microsoft.CodeAnalysis` (Roslyn) workspaces API
- stdio transport (simplest for Claude Code integration)

## Design Principles
- **Don't duplicate CLI tools** — no build, test, NuGet commands
- **Semantic only** — everything here requires Roslyn's compilation model
- **Fast startup** — lazy-load solutions, cache compilations
- **Minimal dependencies** — only Roslyn + MCP SDK

## Project Structure
```
dotnet-mcp/
├── src/
│   └── DotnetMcp/          # Main MCP server project
│       ├── Program.cs       # Entry point, MCP server setup
│       ├── Tools/           # MCP tool implementations
│       │   ├── SymbolLookupTool.cs
│       │   ├── TypeHierarchyTool.cs
│       │   ├── FindReferencesTool.cs
│       │   └── ExpressionTypeTool.cs
│       └── Services/        # Roslyn workspace management
│           └── WorkspaceService.cs
├── CLAUDE.md
├── README.md
└── .gitignore
```

## Style
- Modern C# (top-level statements, collection expressions, target-typed new)
- No unnecessary abstractions
- Keep it short and readable
