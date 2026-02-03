# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build (requires .NET 10 SDK)
dotnet build

# Run unit tests (xUnit, ~323 tests)
dotnet test tests/DocxMcp.Tests/

# Run a single test by name
dotnet test tests/DocxMcp.Tests/ --filter "FullyQualifiedName~TestMethodName"

# Run tests in a single class
dotnet test tests/DocxMcp.Tests/ --filter "FullyQualifiedName~PathParserTests"

# Publish NativeAOT binaries (outputs to dist/)
./publish.sh                  # auto-detect current platform
./publish.sh macos-arm64      # specific target
./publish.sh all              # all 6 platform targets

# Integration tests (requires mcptools: brew install mcptools)
./test-mcp.sh                          # new document
./test-mcp.sh ~/Documents/sample.docx  # existing file

# Run MCP server directly (stdio transport)
dotnet run --project src/DocxMcp/
```

## Architecture

This is an MCP (Model Context Protocol) server and standalone CLI for programmatic DOCX manipulation, built with .NET 10 and NativeAOT.

### Three Projects

- **DocxMcp** (`src/DocxMcp/`) — MCP server. Entry point registers tool classes with the MCP SDK via `WithTools<T>()` in `Program.cs`.
- **DocxMcp.Cli** (`src/DocxMcp.Cli/`) — Standalone CLI mirroring all MCP tools as shell commands. References DocxMcp as a library.
- **DocxMcp.Tests** (`tests/DocxMcp.Tests/`) — xUnit tests covering paths, patching, querying, styling, comments, undo/redo, persistence, and concurrency.

### Core Data Flow

```
MCP stdio / CLI command
  → Tool classes (src/DocxMcp/Tools/)
    → SessionManager (session lifecycle + undo/redo + WAL coordination)
      → DocxSession (in-memory MemoryStream + WordprocessingDocument)
        → Open XML SDK (DocumentFormat.OpenXml)
```

### Typed Path System (`src/DocxMcp/Paths/`)

Documents are navigated via typed paths like `/body/table[0]/row[1]/cell[0]/paragraph[*]`.

- **PathSegment** — 14 discriminated union record types (Body, Paragraph, Table, Row, Cell, Run, etc.)
- **PathParser** — Parses string paths into typed `DocxPath` with validation via `PathSchema`
- **PathResolver** — Resolves `DocxPath` to Open XML elements using SDK typed accessors
- **Selectors** — `[0]` (index), `[-1]` (last), `[*]` (all), `[text~='...']` (text match), `[style='...']` (style match)

### Patch Engine (`Tools/PatchTool.cs`, `Helpers/ElementFactory.cs`)

RFC 6902-adapted JSON patches with ops: `add`, `replace`, `remove`, `move`, `copy`, `replace_text`, `remove_column`. Max 10 operations per call. `ElementFactory` converts JSON value definitions into Open XML elements.

### Session Persistence (`src/DocxMcp/Persistence/`)

- **SessionStore** — Disk I/O for baselines and WAL files
- **MappedWal** — Memory-mapped WAL (JSONL) with random access for efficient undo/redo
- **SessionIndex** — JSON metadata tracking sessions, WAL counts, cursor positions, checkpoint markers
- **SessionLock** — Cross-process file locking (file-based `.lock` with exponential backoff)
- **Checkpoints** — Full document snapshots every N edits (default 10, via `DOCX_CHECKPOINT_INTERVAL`)
- **Undo** rebuilds from nearest checkpoint then replays. **Redo** replays forward patches (no rebuild).

### Styling (`Tools/StyleTools.cs`, `Helpers/StyleHelper.cs`)

Three tools: `style_element`, `style_paragraph`, `style_table`. All use **merge semantics** — only specified properties change, others are preserved.

### Tool Registration Pattern

Tools use attribute-based registration with DI:
```csharp
[McpServerToolType]
public sealed class SomeTools
{
    [McpServerTool(Name = "tool_name"), Description("...")]
    public static string ToolMethod(SessionManager sessions, string param) { ... }
}
```
`SessionManager` and other services are auto-injected from the DI container.

### Environment Variables

| Variable | Default | Purpose |
|----------|---------|---------|
| `DOCX_SESSIONS_DIR` | `~/.docx-mcp/sessions` | Session storage location |
| `DOCX_CHECKPOINT_INTERVAL` | `10` | Edits between checkpoints |
| `DOCX_WAL_COMPACT_THRESHOLD` | `50` | WAL entries before compaction |

## Key Conventions

- **NativeAOT**: All code must be AOT-compatible. Tool types are registered explicitly (no reflection-based discovery). `InvariantGlobalization` is `false`.
- **MCP stdio**: All logging goes to stderr (`LogToStandardErrorThreshold = LogLevel.Trace`). Stdout is reserved for MCP protocol messages.
- **Internal visibility**: `DocxMcp` exposes internals to `DocxMcp.Tests` via `InternalsVisibleTo`.
- **No `apply_xml_patch`**: Deliberately omitted — raw XML patching is too fragile for LLM callers. Use the typed JSON patch system instead.
- **Pagination limits**: Queries return max 50 elements; patches accept max 10 operations per call.
