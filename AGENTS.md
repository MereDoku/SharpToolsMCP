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


<!-- CLAVIX:START -->
# Clavix Instructions for Generic Agents

This guide is for agents that can only read documentation (no slash-command support). If your platform supports custom slash commands, use those instead.

---

## 🚨 FIRST: Load using-clavix Skill

**Before ANY Clavix workflow, invoke the `using-clavix` skill.**

This meta-skill establishes:
- Skill invocation rules (check skills BEFORE any response)
- Required skill chains (prd → plan → implement → verify)
- Iron Laws for verification (no completion claims without evidence)
- Workflow orchestration and fix loops

If you have even a 1% chance a Clavix skill applies, you MUST check it first.

**In environments with Skill tool:** Invoke `using-clavix` before any Clavix action.
**In environments without Skill tool:** Read `.config/agents/skills/using-clavix/SKILL.md` or the bundled template.

---

## ⛔ CLAVIX MODE ENFORCEMENT

**CRITICAL: Know which mode you're in and STOP at the right point.**

**OPTIMIZATION workflows** (NO CODE ALLOWED):
- Improve mode - Prompt optimization only (auto-selects depth)
- Your role: Analyze, optimize, show improved prompt, **STOP**
- ❌ DO NOT implement the prompt's requirements
- ✅ After showing optimized prompt, tell user: "Run `/clavix:implement --latest` to implement"

**PLANNING workflows** (NO CODE ALLOWED):
- Conversational mode, requirement extraction, PRD generation
- Your role: Ask questions, create PRDs/prompts, extract requirements
- ❌ DO NOT implement features during these workflows

**IMPLEMENTATION workflows** (CODE ALLOWED):
- Only after user runs execute/implement commands
- Your role: Write code, execute tasks, implement features
- ✅ DO implement code during these workflows

**If unsure, ASK:** "Should I implement this now, or continue with planning?"

See `.clavix/instructions/core/clavix-mode.md` for complete mode documentation.

---

## 📁 Detailed Workflow Instructions

For complete step-by-step workflows, see `.clavix/instructions/`:

| Workflow | Instruction File | Purpose |
|----------|-----------------|---------|
| **Conversational Mode** | `workflows/start.md` | Natural requirements gathering through discussion |
| **Extract Requirements** | `workflows/summarize.md` | Analyze conversation → mini-PRD + optimized prompts |
| **Prompt Optimization** | `workflows/improve.md` | Intent detection + quality assessment + auto-depth selection |
| **PRD Generation** | `workflows/prd.md` | Socratic questions → full PRD + quick PRD |
| **Mode Boundaries** | `core/clavix-mode.md` | Planning vs implementation distinction |
| **File Operations** | `core/file-operations.md` | File creation patterns |
| **Verification** | `core/verification.md` | Post-implementation verification |

**Troubleshooting:**
- `troubleshooting/jumped-to-implementation.md` - If you started coding during planning
- `troubleshooting/skipped-file-creation.md` - If files weren't created
- `troubleshooting/mode-confusion.md` - When unclear about planning vs implementation

---

## 🔍 Workflow Detection Keywords

| Keywords in User Request | Recommended Workflow | File Reference |
|---------------------------|---------------------|----------------|
| "improve this prompt", "make it better", "optimize" | Improve mode → Auto-depth optimization | `workflows/improve.md` |
| "analyze thoroughly", "edge cases", "alternatives" | Improve mode (--comprehensive) | `workflows/improve.md` |
| "create a PRD", "product requirements" | PRD mode → Socratic questioning | `workflows/prd.md` |
| "let's discuss", "not sure what I want" | Conversational mode → Start gathering | `workflows/start.md` |
| "summarize our conversation" | Extract mode → Analyze thread | `workflows/summarize.md` |
| "refine", "update PRD", "change requirements", "modify prompt" | Refine mode → Update existing content | `workflows/refine.md` |
| "verify", "check my implementation" | Verify mode → Implementation verification | `core/verification.md` |

**When detected:** Reference the corresponding `.clavix/instructions/workflows/{workflow}.md` file.

---

## 📋 Clavix Commands (v5)

### Setup Commands (CLI)
| Command | Purpose |
|---------|---------|
| `clavix init` | Initialize Clavix in a project |
| `clavix update` | Update templates after package update |
| `clavix diagnose` | Check installation health |
| `clavix version` | Show version |

### Workflow Commands (Slash Commands)
All workflows are executed via slash commands that AI agents read and follow:

> **Command Format:** Commands shown with colon (`:`) format. Some tools use hyphen (`-`): Claude Code uses `/clavix:improve`, Cursor uses `/clavix-improve`. Your tool autocompletes the correct format.

| Slash Command | Purpose |
|---------------|---------|
| `/clavix:improve` | Optimize prompts (auto-selects depth) |
| `/clavix:prd` | Generate PRD through guided questions |
| `/clavix:plan` | Create task breakdown from PRD |
| `/clavix:implement` | Execute tasks or prompts (auto-detects source) |
| `/clavix:start` | Begin conversational session |
| `/clavix:summarize` | Extract requirements from conversation |
| `/clavix:refine` | Refine existing PRD or saved prompt |

### Agentic Utilities (Project Management)
These utilities provide structured workflows for project completion:

| Utility | Purpose |
|---------|---------|
| `/clavix:verify` | Check implementation against PRD requirements, run validation |
| `/clavix:archive` | Archive completed work to `.clavix/archive/` for reference |

**Quick start:**
```bash
npm install -g clavix
clavix init
```

**How it works:** Slash commands are markdown templates. When invoked, the agent reads the template and follows its instructions using native tools (Read, Write, Edit, Bash).

---

## 🔄 Standard Workflow

**Clavix follows this progression:**

```
PRD Creation → Task Planning → Implementation → Archive
```

**Detailed steps:**

1. **Planning Phase**
   - Run: `/clavix:prd` or `/clavix:start` → `/clavix:summarize`
   - Output: `.clavix/outputs/{project}/full-prd.md` + `quick-prd.md`
   - Mode: PLANNING

2. **Task Preparation**
   - Run: `/clavix:plan` transforms PRD into curated task list
   - Output: `.clavix/outputs/{project}/tasks.md`
   - Mode: PLANNING (Pre-Implementation)

3. **Implementation Phase**
   - Run: `/clavix:implement`
   - Agent executes tasks systematically
   - Mode: IMPLEMENTATION
   - Agent edits tasks.md directly to mark progress (`- [ ]` → `- [x]`)

4. **Completion**
   - Run: `/clavix:archive`
   - Archives completed work
   - Mode: Management

**Key principle:** Planning workflows create documents. Implementation workflows write code.

---

## 💡 Best Practices for Generic Agents

1. **Always reference instruction files** - Don't recreate workflow steps inline, point to `.clavix/instructions/workflows/`

2. **Respect mode boundaries** - Planning mode = no code, Implementation mode = write code

3. **Use checkpoints** - Follow the CHECKPOINT pattern from instruction files to track progress

4. **Create files explicitly** - Use Write tool for every file, verify with ls, never skip file creation

5. **Ask when unclear** - If mode is ambiguous, ask: "Should I implement or continue planning?"

6. **Track complexity** - Use conversational mode for complex requirements (15+ exchanges, 5+ features, 3+ topics)

7. **Label improvements** - When optimizing prompts, mark changes with [ADDED], [CLARIFIED], [STRUCTURED], [EXPANDED], [SCOPED]

---

## ⚠️ Common Mistakes

### ❌ Jumping to implementation during planning
**Wrong:** User discusses feature → agent generates code immediately

**Right:** User discusses feature → agent asks questions → creates PRD/prompt → asks if ready to implement

### ❌ Skipping file creation
**Wrong:** Display content in chat, don't write files

**Right:** Create directory → Write files → Verify existence → Display paths

### ❌ Recreating workflow instructions inline
**Wrong:** Copy entire fast mode workflow into response

**Right:** Reference `.clavix/instructions/workflows/improve.md` and follow its steps

### ❌ Not using instruction files
**Wrong:** Make up workflow steps or guess at process

**Right:** Read corresponding `.clavix/instructions/workflows/*.md` file and follow exactly

---

**Artifacts stored under `.clavix/`:**
- `.clavix/outputs/<project>/` - PRDs, tasks, prompts
- `.clavix/templates/` - Custom overrides

---

**For complete workflows:** Always reference `.clavix/instructions/workflows/{workflow}.md`

**For troubleshooting:** Check `.clavix/instructions/troubleshooting/`

**For mode clarification:** See `.clavix/instructions/core/clavix-mode.md`

<!-- CLAVIX:END -->
