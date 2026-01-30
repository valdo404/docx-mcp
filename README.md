# docx-mcp

A Model Context Protocol (MCP) server for Microsoft Word DOCX manipulation, built with .NET 10 and NativeAOT. Single binary, no runtime required.

## Quick Start

```bash
# Build the NativeAOT binary (requires .NET 10 SDK)
./publish.sh

# Binary output: dist/macos-arm64/docx-mcp (~28MB)
```

## AI Tool Integration

### Claude Desktop

**macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`

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

The server exposes 8 tools over MCP stdio transport:

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

### Export

| Tool | Description |
|------|-------------|
| `export_pdf` | Export to PDF via LibreOffice CLI (requires LibreOffice installed). |
| `export_html` | Export to HTML. |
| `export_markdown` | Export to Markdown. |

## Building

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- macOS: Homebrew (`openssl`, `brotli` — needed for NativeAOT linking)

### Build

```bash
# Build for current platform
./publish.sh

# Build for a specific target
./publish.sh macos-arm64
./publish.sh linux-x64

# Build all targets
./publish.sh all
```

**Supported targets:** `macos-arm64`, `macos-x64`, `linux-x64`, `linux-arm64`, `windows-x64`, `windows-arm64`

Output goes to `dist/<target>/docx-mcp`.

### Tests

```bash
# Unit tests (43 tests)
dotnet test src/DocxMcp.Tests/

# Integration tests (requires mcptools: brew install mcptools)
./test-mcp.sh

# Integration test with a real document
./test-mcp.sh ~/Documents/somefile.docx
```

## Architecture

```
src/DocxMcp/
  Program.cs              — MCP server setup (stdio transport)
  SessionManager.cs       — Document session lifecycle
  DocxSession.cs          — Single document wrapper
  Tools/
    DocumentTools.cs      — open / save / close / list
    QueryTool.cs          — typed path queries
    PatchTool.cs          — JSON patch operations
    ExportTools.cs        — PDF / HTML / Markdown export
  Paths/
    DocxPath.cs           — Path model (/body/paragraph[0])
    PathParser.cs         — Path string parser
    PathResolver.cs       — Path-to-OOXML element resolution
    Selector.cs           — Filter predicates (index, wildcard, attribute, text search)
  Helpers/
    ElementFactory.cs     — JSON → OOXML element creation
    OpenXmlExtensions.cs  — Extension methods for OpenXml types
```

**Stack:** .NET 10, Open XML SDK 3.2, MCP C# SDK 0.7.0, NativeAOT

## License

MIT
