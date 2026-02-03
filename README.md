# docx-mcp

A Model Context Protocol (MCP) server and standalone CLI for Microsoft Word DOCX manipulation, built with .NET 10 and NativeAOT. Single binary, no runtime required.

## Why docx-mcp?

Most document tools treat Word files as opaque blobs — open, edit, save, hope for the best. docx-mcp treats them as structured data with full version control.

**Structured Query Language for Word** — Navigate documents with typed paths like `/body/heading[level=1]` or `/body/paragraph[text~='budget']`. No more guessing at element indices.

**Time Travel (Undo/Redo)** — Every edit is recorded in a write-ahead log. Undo, redo, or jump to any point in the editing history. Checkpoints every N operations keep rebuilds fast. Branch off from any past state — the future timeline is automatically discarded.

**Session Persistence** — Sessions survive server restarts. The WAL, checkpoints, and cursor position are durably stored. Pick up exactly where you left off, even across Docker container restarts.

**Patch-Based Editing** — RFC 6902 JSON Patch adapted for OOXML. Add headings, tables, images, hyperlinks, lists. Replace text while preserving run-level formatting. Move, copy, remove elements. Up to 10 operations per call, batched atomically.

**Merge-Based Styling** — Apply formatting without replacing existing properties. Bold all text without losing italic. Center paragraphs without touching indentation. Style tables, cells, and rows independently. Works globally or on targeted paths with wildcards.

**Comments** — Add, list, and delete comments anchored to any document element. Comments are persisted in the .docx file and survive save/reopen cycles.

**NativeAOT** — Compiles to a single ~28MB binary with no .NET runtime dependency. Sub-millisecond startup. Ships as a minimal Docker image.

**CLI for Batch Operations** — A standalone NativeAOT CLI (`docx-cli`) mirrors every MCP tool as a shell command. Pipe JSON patches from scripts, apply styles across hundreds of documents, or run bulk operations without consuming LLM context tokens.

## Quick Start

### Docker

```bash
docker pull valdo404/docx-mcp:1.0.0
```

MCP server (for AI tool integration):
```json
{
  "mcpServers": {
    "docx": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
        "-v", "/path/to/documents:/data",
        "-v", "docx-sessions:/home/app/.docx-mcp/sessions",
        "valdo404/docx-mcp:1.0.0"]
    }
  }
}
```

CLI (for batch operations):
```bash
# Run the CLI inside the container
docker run --rm \
  -v /path/to/documents:/data \
  -v docx-sessions:/home/app/.docx-mcp/sessions \
  --entrypoint ./docx-cli \
  valdo404/docx-mcp:1.0.0 open /data/report.docx

# Style all text bold + red
docker run --rm \
  -v docx-sessions:/home/app/.docx-mcp/sessions \
  --entrypoint ./docx-cli \
  valdo404/docx-mcp:1.0.0 style-element <doc_id> '{"bold":true,"color":"FF0000"}'
```

### Native Binary

```bash
# Build NativeAOT binaries (requires .NET 10 SDK)
./publish.sh

# Output: dist/macos-arm64/docx-mcp (~28MB) + dist/macos-arm64/docx-cli (~29MB)
```

## CLI

The `docx-cli` binary mirrors every MCP tool as a shell command. Sessions are shared with the MCP server — edits made via CLI are visible to the MCP server and vice versa.

```bash
# Document lifecycle
docx-cli open report.docx          # → Session ID: a1b2c3d4
docx-cli list                      # → List open sessions
docx-cli save a1b2c3 output.docx   # → Save to disk
docx-cli close a1b2c3              # → Close session

# Query
docx-cli query a1b2c3 '/body/paragraph[*]' --format text
docx-cli count a1b2c3 '/body/heading[*]'
docx-cli read-section a1b2c3 --index 0
docx-cli read-heading a1b2c3 --text 'Introduction' --format text

# Editing
docx-cli patch a1b2c3 '[{"op":"add","path":"/body/children/0","value":{"type":"heading","level":1,"text":"Title"}}]'
echo '[{"op":"remove","path":"/body/paragraph[0]"}]' | docx-cli patch a1b2c3

# Styling (merge semantics — only specified properties change)
docx-cli style-element a1b2c3 '{"bold":true,"color":"FF0000"}'
docx-cli style-element a1b2c3 '{"font_size":24}' --path '/body/heading[*]'
docx-cli style-paragraph a1b2c3 '{"alignment":"center"}'
docx-cli style-table a1b2c3 --style '{"border_style":"double"}' --cell-style '{"shading":"F0F0F0"}'

# Comments
docx-cli comment-add a1b2c3 '/body/paragraph[0]' 'Review this section' --author 'Alice'
docx-cli comment-list a1b2c3
docx-cli comment-delete a1b2c3 --id 0

# History
docx-cli undo a1b2c3
docx-cli redo a1b2c3 3
docx-cli history a1b2c3
docx-cli jump-to a1b2c3 5

# Export
docx-cli export-html a1b2c3 output.html
docx-cli export-markdown a1b2c3 output.md
docx-cli export-pdf a1b2c3 output.pdf
```

### Environment

| Variable | Description |
|----------|-------------|
| `DOCX_SESSIONS_DIR` | Override sessions directory (shared between MCP server and CLI) |

## AI Tool Integration

### Claude Desktop

**macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`

Using Docker (recommended):
```json
{
  "mcpServers": {
    "docx": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
        "-v", "/Users/you/Documents:/data",
        "-v", "docx-sessions:/home/app/.docx-mcp/sessions",
        "valdo404/docx-mcp:1.0.0"]
    }
  }
}
```

Using native binary:
```json
{
  "mcpServers": {
    "docx": {
      "command": "/absolute/path/to/docx-mcp/dist/macos-arm64/docx-mcp",
      "args": []
    }
  }
}
```

### Cursor

Using Docker:
```json
{
  "mcp": {
    "servers": {
      "docx": {
        "command": "docker",
        "args": ["run", "-i", "--rm",
          "-v", "/Users/you/Documents:/data",
          "-v", "docx-sessions:/home/app/.docx-mcp/sessions",
          "valdo404/docx-mcp:1.0.0"]
      }
    }
  }
}
```

Using native binary:
```json
{
  "mcp": {
    "servers": {
      "docx": {
        "command": "/absolute/path/to/docx-mcp/dist/macos-arm64/docx-mcp",
        "args": []
      }
    }
  }
}
```

## Tools

The server exposes 21 tools over MCP stdio transport:

### Document Management

| Tool | Description |
|------|-------------|
| `document_open` | Open a .docx file or create a new empty document. Returns a session ID. |
| `document_save` | Save document to disk (original path or new path). |
| `document_close` | Close session and release resources. |
| `document_list` | List all open document sessions. |

### Query

| Tool | Description |
|------|-------------|
| `query` | Read any part of a document using typed paths. Returns JSON, text, or summary. |

**Path examples:**

```
/body                           — document structure summary
/body/paragraph[0]              — first paragraph
/body/paragraph[*]              — all paragraphs
/body/table[0]                  — first table
/body/heading[*]                — all headings
/body/heading[level=1]          — level-1 headings only
/body/paragraph[text~='hello']  — paragraphs containing 'hello'
/metadata                       — document properties
/styles                         — style definitions
```

### Editing

| Tool | Description |
|------|-------------|
| `apply_patch` | Modify documents using JSON patches (RFC 6902 adapted for OOXML). |

**Operations:** `add`, `replace`, `remove`, `move`, `copy`

**Element types:** `paragraph`, `heading`, `table`, `image`, `hyperlink`, `page_break`, `list`

**Example patches:**

```json
[
  {"op": "add", "path": "/body/children/0", "value": {"type": "heading", "level": 1, "text": "Title"}},
  {"op": "add", "path": "/body/children/1", "value": {"type": "paragraph", "text": "Hello world.", "style": {"bold": true}}},
  {"op": "add", "path": "/body/children/2", "value": {"type": "table", "headers": ["Name", "Value"], "rows": [["foo", "bar"]]}},
  {"op": "remove", "path": "/body/paragraph[0]"}
]
```

### Styling (Merge Semantics)

| Tool | Description |
|------|-------------|
| `style_element` | Apply character/run-level formatting (bold, italic, color, font, etc.) with merge semantics. |
| `style_paragraph` | Apply paragraph-level formatting (alignment, spacing, indentation, shading) with merge semantics. |
| `style_table` | Apply table, cell, and row formatting (borders, shading, width, alignment) with merge semantics. |

Style tools use **merge semantics** — only the properties you specify are changed. Everything else is preserved. This is different from `replace` on `/style` paths (which replaces the entire property block).

**Element style properties:** `bold`, `italic`, `underline`, `strike`, `font_size`, `font_name`, `color`, `highlight`, `vertical_align`

**Paragraph style properties:** `alignment`, `style`, `spacing_before`, `spacing_after`, `line_spacing`, `indent_left`, `indent_right`, `indent_first_line`, `indent_hanging`, `shading`

**Table style properties:** `border_style`, `border_size`, `width`, `width_type`, `table_style`, `table_alignment` (plus `cell_style` and `row_style` for cells and rows)

All three tools support an optional `path` parameter. Omit it to apply globally (including inside table cells). Use typed paths with `[*]` wildcards for batch operations.

### Comments

| Tool | Description |
|------|-------------|
| `comment_add` | Add a comment anchored to a document element, with optional author and initials. |
| `comment_list` | List all comments with pagination, optionally filtered by author. |
| `comment_delete` | Delete comments by ID or by author. |

Comments are stored in the OOXML comments part and survive save/reopen cycles. Each comment records its author, initials, timestamp, text, and the anchored text it refers to.

### History & Time Travel

| Tool | Description |
|------|-------------|
| `document_undo` | Undo N steps. Rebuilds from the nearest checkpoint. |
| `document_redo` | Redo N steps. Replays patches forward (no rebuild needed). |
| `document_history` | List all WAL entries with timestamps, descriptions, and current position. |
| `document_jump_to` | Jump to any position in the editing timeline. |

Every `apply_patch`, `style_*`, and `comment_*` call is recorded with a timestamp and auto-generated description. Undo rebuilds the document from the nearest checkpoint (snapshots taken every 10 edits by default, configurable via `DOCX_CHECKPOINT_INTERVAL`). Redo replays patches forward on the current DOM — no rebuild overhead.

Applying a new edit after an undo discards the future timeline and starts a new branch, just like typing after undo in a text editor.

### Export

| Tool | Description |
|------|-------------|
| `export_pdf` | Export to PDF via LibreOffice CLI (requires LibreOffice installed). |
| `export_html` | Export to HTML. |
| `export_markdown` | Export to Markdown. |

### Additional Tools

| Tool | Description |
|------|-------------|
| `read_section` | Read content under a specific heading (section-based navigation). |
| `read_heading_content` | Read content between two headings. |
| `document_count` | Count elements by type (paragraphs, tables, headings, etc.). |
| `document_snapshot` | Compact the WAL into a single baseline. Optionally discard redo history. |

## Building

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- macOS: Homebrew (`openssl`, `brotli` — needed for NativeAOT linking)

### Build

```bash
# Build both binaries for current platform
./publish.sh

# Build for a specific target
./publish.sh macos-arm64
./publish.sh linux-x64

# Build all targets
./publish.sh all
```

**Supported targets:** `macos-arm64`, `macos-x64`, `linux-x64`, `linux-arm64`, `windows-x64`, `windows-arm64`

Output goes to `dist/<target>/` — both `docx-mcp` and `docx-cli` binaries.

### Docker

```bash
# Build the Docker image (both NativeAOT binaries)
docker build -t docx-mcp .

# Run MCP server
docker run -i --rm -v ~/Documents:/data -v docx-sessions:/home/app/.docx-mcp/sessions docx-mcp

# Run CLI
docker run --rm --entrypoint ./docx-cli -v ~/Documents:/data -v docx-sessions:/home/app/.docx-mcp/sessions docx-mcp open /data/report.docx
```

### Tests

```bash
# Unit tests (323 tests)
dotnet test tests/DocxMcp.Tests/

# Integration tests (requires mcptools: brew install mcptools)
./test-mcp.sh

# Integration test with a real document
./test-mcp.sh ~/Documents/somefile.docx
```

## Architecture

```
src/DocxMcp/                        MCP server
  Program.cs                      — MCP server setup (stdio transport)
  SessionManager.cs               — Document session lifecycle + undo/redo
  DocxSession.cs                  — Single document wrapper
  Tools/
    DocumentTools.cs              — open / save / close / list / snapshot
    QueryTool.cs                  — typed path queries
    PatchTool.cs                  — JSON patch operations
    StyleTools.cs                 — style_element / style_paragraph / style_table
    CommentTools.cs               — comment_add / comment_list / comment_delete
    HistoryTools.cs               — undo / redo / history / jump_to
    ExportTools.cs                — PDF / HTML / Markdown export
    ReadSectionTool.cs            — section-based navigation
    ReadHeadingContentTool.cs     — heading-based navigation
  Persistence/
    SessionStore.cs               — WAL, checkpoint, and index I/O
    MappedWal.cs                  — Memory-mapped write-ahead log with random access
    SessionIndex.cs               — Session metadata (cursor, checkpoints)
    WalEntry.cs                   — WAL entry model (patches, timestamp, description)
  Paths/
    DocxPath.cs                   — Path model (/body/paragraph[0])
    PathParser.cs                 — Path string parser
    PathResolver.cs               — Path-to-OOXML element resolution
    Selector.cs                   — Filter predicates (index, wildcard, attribute, text search)
  Helpers/
    ElementFactory.cs             — JSON → OOXML element creation
    StyleHelper.cs                — Merge-based style application
    CommentHelper.cs              — Comment manipulation
    OpenXmlExtensions.cs          — Extension methods for OpenXml types

src/DocxMcp.Cli/                    Standalone CLI (NativeAOT)
  Program.cs                      — Command dispatch mirroring all MCP tools
```

**Stack:** .NET 10, Open XML SDK 3.2, MCP C# SDK 0.7.0, NativeAOT

## License

MIT
