# SharpToolsMCP - Roslyn Powered C# Analysis & Modification MCP Server

## Project Overview
SharpToolsMCP is a robust service designed to empower AI agents with advanced capabilities for understanding, analyzing, and modifying C# codebases. It leverages the .NET Compiler Platform (Roslyn) to provide deep static analysis and precise code manipulation. It acts as a "simple IDE for AI users," providing high-signal context and surgical code modifications.

### Main Technologies
- **.NET 8 SDK**: Primary development framework.
- **Roslyn (.NET Compiler Platform)**: Core engine for code analysis, workspace management, and syntax manipulation.
- **Model Context Protocol (MCP)**: Interface for exposing tools to AI agents.
- **MSBuild**: Used for project loading, building, and dependency resolution.
- **LibGit2Sharp**: Provides automated Git integration for tracking code changes.
- **ILSpy & SourceLink**: Fallback mechanisms for source resolution from compiled assemblies and external libraries.

### Architecture
- **`SharpTools.Tools`**: The core library containing all Roslyn-based services and MCP tool implementations.
    - `Services/`: Core logic (e.g., `SolutionManager`, `CodeAnalysisService`, `CodeModificationService`).
    - `Mcp/Tools/`: Implementation of MCP tools (e.g., `SolutionTools`, `AnalysisTools`, `ModificationTools`).
- **`SharpTools.SseServer`**: An MCP server implementation using Server-Sent Events (SSE) over HTTP.
- **`SharpTools.StdioServer`**: An MCP server implementation using Standard I/O for local process communication.

---

## Building and Running

### Build the Solution
```bash
dotnet build SharpTools.sln
```

### Run the Servers
- **SSE Server (HTTP)**:
  ```bash
  dotnet run --project SharpTools.SseServer
  ```
- **Stdio Server**:
  ```bash
  dotnet run --project SharpTools.StdioServer
  ```

### Testing
- **TODO**: No dedicated test project was found in the solution. Manual testing via MCP clients is currently the primary verification method.

---

## Development Conventions

### Roslyn-Based Operations
- **Always use Roslyn APIs**: All code analysis and modifications must be performed through Roslyn's `Workspace`, `Solution`, `Project`, and `Document` abstractions.
- **Syntactic and Semantic analysis**: Leverage `Compilation` and `SemanticModel` for accurate symbol resolution and type checking.
- **Surgical Modifications**: Prefer Roslyn-based syntax transformations (e.g., `DocumentEditor`, `SyntaxNode` replacement) over raw text manipulation to maintain code integrity.

### Service-Oriented Logic
- Logic should be encapsulated in services registered via `IServiceCollection`.
- Services should be requested through dependency injection (DI) in MCP tool implementations.
- Key services:
    - `ISolutionManager`: Manages the current MSBuild workspace and solution state.
    - `ICodeAnalysisService`: Provides high-level analysis (references, implementations, etc.).
    - `ICodeModificationService`: Handles code changes, formatting, and applying updates to the workspace.

### MCP Tool Implementation
- Tools are defined as static methods decorated with `[McpServerTool]` in the `SharpTools.Tools.Mcp.Tools` namespace.
- Use `ErrorHandlingHelpers.ExecuteWithErrorHandlingAsync` for consistent error reporting.
- Tool names should follow the `SharpTool_` prefix convention.

### Git Integration
- Every code modification applied via `CodeModificationService` is automatically committed to a timestamped branch (e.g., `sharptools/YYYYMMDD-HHMMSS`).
- This provides a "safety net" and allows for the `SharpTool_Undo` feature.

### Formatting and Token Efficiency
- The server respects `.editorconfig` settings for code formatting.
- **Token Optimization**: Returned code often has indentation omitted and uses FQN-based navigation to minimize token usage for AI agents.

### Symbol Resolution
- Use `IFuzzyFqnLookupService` to resolve potentially imprecise Fully Qualified Names (FQNs) provided by agents to exact Roslyn symbols.
- Support for nested types uses `+` (e.g., `Namespace.Type+NestedType`) as per Roslyn conventions.
