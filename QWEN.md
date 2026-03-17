# SharpTools MCP - Project Context

## Project Overview

**SharpTools** is a .NET 8 MCP (Model Context Protocol) server that provides AI agents with advanced C# code analysis and modification capabilities powered by Roslyn. It functions as an AI-focused IDE, exposing tools via both SSE (HTTP) and stdio transports.

### Architecture

The solution consists of three projects:

| Project | Purpose |
|---------|---------|
| `SharpTools.Tools/` | Core Roslyn-powered analysis and modification logic |
| `SharpTools.SseServer/` | HTTP/SSE MCP server (default port 3001) |
| `SharpTools.StdioServer/` | stdio MCP server for local clients |

### Key Technologies

- **.NET 8** - Target framework
- **Roslyn (Microsoft.CodeAnalysis 5.0.0)** - C# compiler platform for code analysis
- **ModelContextProtocol** - MCP server implementation
- **LibGit2Sharp** - Git integration for automatic commits/undo
- **ICSharpCode.Decompiler** - ILSpy-based decompilation fallback
- **Serilog** - Structured logging
- **System.CommandLine** - CLI argument parsing

### Core Capabilities

- **Solution/Project Analysis**: Load solutions, map project structure, resolve symbols
- **Semantic Navigation**: FQN-based fuzzy matching, find references, list implementations
- **Source Resolution**: Retrieve source from local files, SourceLink, embedded PDBs, or decompilation
- **Precise Modifications**: Add/overwrite/rename/move members using Roslyn syntax trees
- **Git Integration**: Auto-commit changes to timestamped `sharptools/` branches, undo support
- **Complexity Analysis**: Cyclomatic, cognitive, coupling metrics
- **Token Efficiency**: Indentation omitted in returned code (~10% token savings)

## Building and Running

### Build the Solution

```bash
dotnet build SharpTools.sln
```

### Run SSE Server (HTTP)

```bash
# Default (port 3001)
dotnet run --project SharpTools.SseServer

# With options
dotnet run --project SharpTools.SseServer -- --port 3005 --log-file ./logs/mcp-sse-server.log --log-level Debug
```

**Key Options:**
- `--port <number>` - Port to listen on (default: 3001)
- `--log-file <path>` - Path to log file
- `--log-level <level>` - Minimum log level (Verbose, Debug, Information, Warning, Error, Fatal)
- `--load-solution <path>` - Path to `.sln` file to load on startup
- `--build-configuration <config>` - Build configuration (Debug, Release)
- `--msbuild-path <path>` - Optional MSBuild directory path
- `--runtime-identifier <rid>` - Runtime identifier (e.g., `win10-x64`)
- `--target-framework <tfm>` - Target framework (e.g., `net9.0-windows10.0.26100.0`)
- `--disable-git` - Disable Git integration

### Run Stdio Server

```bash
dotnet run --project SharpTools.StdioServer
```

**Key Options:**
- `--log-directory <path>` - Directory for log files
- `--log-level <level>` - Minimum log level
- `--load-solution <path>` - Path to `.sln` file to load on startup
- `--build-configuration <config>` - Build configuration
- `--disable-git` - Disable Git integration

**VS Code Copilot Configuration Example:**
```json
"mcp": {
    "servers": {
        "SharpTools": {
            "type": "stdio",
            "command": "/path/to/SharpToolsMCP/SharpTools.StdioServer/bin/Debug/net8.0/SharpTools.StdioServer",
            "args": [
                "--log-directory", "/var/log/sharptools/",
                "--log-level", "Debug"
            ]
        }
    }
}
```

## Exposed MCP Tools

### Solution Tools
- `SharpTool_LoadSolution` - Initialize workspace with a `.sln` file
- `SharpTool_LoadProject` - Get project structure (namespaces, types)
- `SharpTool_GetDiagnostics` - Get compiler diagnostics for solution/project/document

### Analysis Tools
- `SharpTool_GetMembers` - List members of a type with signatures and XML docs
- `SharpTool_ViewDefinition` - Display source code of a symbol with context
- `SharpTool_ListImplementations` - Find implementations of interface/abstract methods
- `SharpTool_FindReferences` - Locate all usages of a symbol
- `SharpTool_SearchDefinitions` - Regex-based search across symbol declarations
- `SharpTool_ManageUsings` - Read or overwrite using directives
- `SharpTool_ManageAttributes` - Read or overwrite attributes on declarations
- `SharpTool_AnalyzeComplexity` - Complexity analysis (cyclomatic, cognitive, coupling)

### Document Tools
- `SharpTool_ReadRawFromRoslynDocument` - Read raw file content (indentation omitted)
- `SharpTool_CreateRoslynDocument` - Create a new file
- `SharpTool_OverwriteRoslynDocument` - Overwrite an existing file
- `SharpTool_ReadTypesFromRoslynDocument` - List types and members in a source file

### Modification Tools
- `SharpTool_AddMember` - Add a new member to a type
- `SharpTool_OverwriteMember` - Replace or delete an existing member
- `SharpTool_RenameSymbol` - Rename a symbol and update all references
- `SharpTool_FindAndReplace` - Regex-based find/replace within a symbol or glob pattern
- `SharpTool_MoveMember` - Move a member between types/namespaces
- `SharpTool_Undo` - Revert last change using Git integration

### Package Tools
- `SharpTool_AddOrModifyNugetPackage` - Add or update NuGet package references

### Misc Tools
- `SharpTool_RequestNewTool` - Request new tools/features (logs for human review)

## Development Conventions

### Coding Style (from `.editorconfig`)

| Convention | Setting |
|------------|---------|
| Indentation | 4 spaces |
| Namespaces | Block-scoped (`namespace X { }`) |
| Using directives | Outside namespaces |
| Explicit types | Prefer over `var` |
| Braces | Required on all control flow |
| Expression-bodied members | When on single line |
| File-scoped namespaces | Not preferred |
| Primary constructors | Preferred |
| Top-level statements | Preferred |

### Key Preferences
- `csharp_style_var_elsewhere = false` - Use explicit types
- `csharp_prefer_braces = true` - Always use braces
- `dotnet_style_qualification_for_* = false` - No `this.` prefix
- `csharp_style_namespace_declarations = block_scoped`
- `csharp_using_directive_placement = outside_namespace`

### Project Structure

```
SharpTools.Tools/
├── Extensions/          # Extension methods
├── Interfaces/          # Service interfaces
├── Mcp/
│   ├── Tools/          # MCP tool implementations
│   ├── ContextInjectors.cs
│   ├── ErrorHandlingHelpers.cs
│   ├── Prompts.cs
│   └── ToolHelpers.cs
├── Services/           # Core services (SolutionManager, CodeAnalysis, etc.)
├── Utilities/          # Helper utilities
└── GlobalUsings.cs     # Global using directives
```

### Key Services

| Service | Responsibility |
|---------|---------------|
| `SolutionManager` | MSBuild workspace management, solution loading, symbol resolution |
| `CodeAnalysisService` | Semantic analysis, diagnostics |
| `CodeModificationService` | Roslyn-based code modifications |
| `FuzzyFqnLookupService` | FQN resolution with fuzzy matching |
| `SourceResolutionService` | Source retrieval (local, SourceLink, decompilation) |
| `GitService` / `NoOpGitService` | Git operations for commits/undo |
| `EditorConfigProvider` | `.editorconfig` parsing and formatting rules |
| `ComplexityAnalysisService` | Cyclomatic/cognitive complexity metrics |

## Testing

No test project currently exists. If adding tests:
- Create a new `SharpTools.Tools.Tests/` project
- Name test files after the type under test (e.g., `SolutionManagerTests.cs`)
- Use `dotnet test` once the test project exists

## Git and Commit Guidelines

### Commit Message Style
- Short, imperative, sentence-case subjects
- Examples: "update nugets", "polish readme a bit"
- Include issue references when applicable: `(#16)`

### PR Guidelines
- Describe the change clearly
- Include repro/verification steps (commands and expected behavior)
- Reference issues when applicable

## Security Notes

- `logs/` directory is gitignored - avoid committing log files
- Local logs may include file paths and solution details - redact when sharing
- `secrets.json` is gitignored - use for any sensitive configuration

## Additional Resources

- **Prompts/**: Contains agent prompt templates
  - `identity.prompt` - Personal C# coding assistant prompt
  - `github-copilot-sharptools.prompt` - GitHub Copilot Agent mode prompt
- **AGENTS.md**: Repository guidelines and Clavix workflow instructions
- **debug_commands.txt**: Sample MCP JSON-RPC commands for manual testing

## Troubleshooting

### MSBuild Resolution
The servers automatically resolve MSBuild using:
1. `--msbuild-path` if provided
2. `global.json` SDK version if present
3. Latest installed .NET SDK as fallback

### Git Integration
- Changes are committed to `sharptools/<timestamp>` branches
- Use `SharpTool_Undo` to revert the last modification
- Disable with `--disable-git` if not needed

### Logging
- SSE server: Console + optional file via `--log-file`
- Stdio server: Console (stderr) + optional directory via `--log-directory`
- Log levels: Verbose, Debug, Information, Warning, Error, Fatal
