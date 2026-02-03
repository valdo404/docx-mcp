# docx-mcp

A Model Context Protocol (MCP) server for Microsoft Word DOCX manipulation. Single NativeAOT binary, no runtime required.

**Features:** Structured queries, undo/redo with checkpoints, session persistence, JSON patch editing, merge-based styling, comments, Track Changes support.

## Quick Start

```bash
docker pull valdo404/docx-mcp:latest
```

## Usage

### MCP Server (AI Tool Integration)

The image runs the MCP server by default, communicating over stdio:

```bash
docker run -i --rm \
  -v /path/to/documents:/data \
  -v docx-sessions:/home/app/.docx-mcp/sessions \
  valdo404/docx-mcp:latest
```

### CLI Mode

The image includes `docx-cli` for batch operations:

```bash
docker run --rm \
  -v /path/to/documents:/data \
  -v docx-sessions:/home/app/.docx-mcp/sessions \
  --entrypoint ./docx-cli \
  valdo404/docx-mcp:latest \
  open /data/report.docx
```

## Volume Mounts

| Mount | Purpose | Required |
|-------|---------|----------|
| `/data` | Your documents directory | Yes (for file access) |
| `/home/app/.docx-mcp/sessions` | Session persistence (WAL, checkpoints) | Recommended |

### Why Mount Sessions?

Sessions persist editing history, undo/redo state, and checkpoints. Without mounting:
- Sessions are lost when the container stops
- Undo history is not preserved
- Checkpoints must be rebuilt

With a named volume (`docx-sessions`), sessions survive container restarts.

## AI Tool Configuration

### Claude Desktop

**macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
**Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "docx": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
        "-v", "/Users/you/Documents:/data",
        "-v", "docx-sessions:/home/app/.docx-mcp/sessions",
        "valdo404/docx-mcp:latest"]
    }
  }
}
```

### Cursor

```json
{
  "mcp": {
    "servers": {
      "docx": {
        "command": "docker",
        "args": ["run", "-i", "--rm",
          "-v", "/Users/you/Documents:/data",
          "-v", "docx-sessions:/home/app/.docx-mcp/sessions",
          "valdo404/docx-mcp:latest"]
      }
    }
  }
}
```

### VS Code / Continue

```json
{
  "models": [],
  "mcpServers": {
    "docx": {
      "command": "docker",
      "args": ["run", "-i", "--rm",
        "-v", "${workspaceFolder}:/data",
        "-v", "docx-sessions:/home/app/.docx-mcp/sessions",
        "valdo404/docx-mcp:latest"]
    }
  }
}
```

## Available Tools

### Document Management
- `document_open` — Open .docx or create new document
- `document_save` — Save to disk
- `document_list` — List open sessions

### Query & Navigation
- `query` — Read document parts using typed paths (`/body/paragraph[0]`, `/body/heading[level=1]`)
- `count_elements` — Count elements by type
- `read_section` — Read by section index
- `read_heading_content` — Read content under a heading

### Editing
- `add_element` — Add paragraph, heading, table, image, etc.
- `replace_element` — Replace an element
- `remove_element` — Delete an element
- `move_element` — Move element to new location
- `copy_element` — Duplicate an element
- `replace_text` — Find/replace preserving formatting
- `remove_table_column` — Remove a column from a table

### Styling (Merge Semantics)
- `style_element` — Character formatting (bold, color, font)
- `style_paragraph` — Paragraph formatting (alignment, spacing)
- `style_table` — Table/cell/row formatting

### Track Changes (Revision Mode)
- `track_changes_enable` — Enable/disable Track Changes
- `revision_list` — List tracked changes
- `revision_accept` — Accept a revision
- `revision_reject` — Reject a revision

### Comments
- `comment_add` — Add anchored comment
- `comment_list` — List comments
- `comment_delete` — Delete comments

### History (Undo/Redo)
- `document_undo` — Undo N steps
- `document_redo` — Redo N steps
- `document_history` — View edit timeline
- `document_jump_to` — Jump to any point

### Export
- `export_html` — Export to HTML
- `export_markdown` — Export to Markdown
- `export_pdf` — Export to PDF (requires LibreOffice)

## CLI Commands

```bash
# Document lifecycle
docx-cli open /data/report.docx     # Returns session ID
docx-cli list                        # List sessions
docx-cli save <id> /data/output.docx
docx-cli close <id>

# Query
docx-cli query <id> '/body/paragraph[*]' --format text
docx-cli count <id> '/body/heading[*]'

# Edit
docx-cli add <id> '/body/children/0' '{"type":"heading","level":1,"text":"Title"}'
docx-cli replace-text <id> '/body' 'old' 'new'
docx-cli remove <id> '/body/paragraph[0]'

# Style
docx-cli style-element <id> '{"bold":true,"color":"FF0000"}'
docx-cli style-paragraph <id> '{"alignment":"center"}'

# Track Changes
docx-cli track-changes-enable <id> true
docx-cli revision-list <id>
docx-cli revision-accept <id> <revision_id>

# History
docx-cli undo <id>
docx-cli redo <id> 3
docx-cli history <id>
```

## Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `DOCX_SESSIONS_DIR` | `/home/app/.docx-mcp/sessions` | Sessions directory |
| `DOCX_CHECKPOINT_INTERVAL` | `10` | Create checkpoint every N edits |
| `DOCX_WAL_COMPACT_THRESHOLD` | `50` | Auto-compact WAL after N entries |

## Image Details

- **Base:** `mcr.microsoft.com/dotnet/runtime-deps:10.0-preview`
- **Architecture:** `linux/amd64`, `linux/arm64`
- **Size:** ~60MB (compressed)
- **Binaries:** `docx-mcp` (MCP server), `docx-cli` (CLI)
- **User:** `app` (non-root)

## Links

- **GitHub:** https://github.com/valdo404/docx-mcp
- **Documentation:** https://github.com/valdo404/docx-mcp#readme
- **Issues:** https://github.com/valdo404/docx-mcp/issues
- **MCP Specification:** https://modelcontextprotocol.io

## License

MIT
