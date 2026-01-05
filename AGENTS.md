# Repository Guidelines

## Project Structure & Module Organization
This repo is a .NET 8 solution (`SharpTools.sln`) with three main projects:
- `SharpTools.Tools/`: core Roslyn-powered analysis and modification logic.
- `SharpTools.SseServer/`: HTTP/SSE MCP server.
- `SharpTools.StdioServer/`: stdio MCP server for local clients.

Supporting directories:
- `Prompts/`: agent prompt templates used by MCP clients.
- `logs/`: local log output (gitignored).
- `.github/`: repo metadata and Copilot instructions.

## Build, Test, and Development Commands
- `dotnet build SharpTools.sln` — builds all projects.
- `dotnet run --project SharpTools.SseServer` — run SSE server (default port 3001).
- `dotnet run --project SharpTools.StdioServer` — run stdio server.
- `dotnet run --project SharpTools.SseServer -- --port 3005 --log-file ./logs/mcp-sse-server.log --log-level Debug` — example with options.

## Coding Style & Naming Conventions
- Indentation: 4 spaces (see `.editorconfig`).
- C# style: block-scoped namespaces, `using` directives outside namespaces, prefer explicit types over `var`, and prefer braces on control flow.
- Member ordering and formatting are handled by Roslyn conventions in `.editorconfig`; keep changes consistent with it.

## Testing Guidelines
There are no test projects in the solution currently. If you add tests, keep them in a new `*.Tests/` project and name files after the type under test (e.g., `FooServiceTests.cs`). Use `dotnet test` once a test project exists.

## Commit & Pull Request Guidelines
Recent history uses short, imperative, sentence-case subjects (e.g., “update nugets”, “polish readme a bit”) and occasionally includes issue references like `(#16)`. Follow that pattern.
PRs should describe the change, include repro/verification steps (commands and expected behavior), and reference issues when applicable.

## Security & Configuration Tips
Local logs can include paths and solution details; avoid committing anything from `logs/`. When sharing examples, redact local file paths and tokens.
