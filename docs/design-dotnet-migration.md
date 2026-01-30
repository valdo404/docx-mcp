# Design Document: Migration to .NET and Patch-Based Architecture

## Status

**Implemented** — January 2026

## Context

The current docx-mcp server is written in Rust and uses `docx-rs` (writer-only) for document creation and `roxmltree` for read-only XML parsing. This creates two code paths: an in-memory operations model for API-created documents, and an XML fallback for opened documents. Neither path provides faithful, spec-complete OOXML parsing.

After evaluating the Rust ecosystem (`docx-rs`, `docx-rust`, `ooxmlsdk`) and cross-language alternatives (`docx4j`, `python-docx`, Apache POI), the conclusion is that the **Open XML SDK** (Microsoft, .NET) is the only library that provides spec-complete, production-grade OOXML support.

## Decision

Rewrite the MCP server in **.NET 10** using the **Open XML SDK** (`DocumentFormat.OpenXml`), and migrate from a tool-per-action model to a **patch-based architecture**.

### Scope Decision: No XML Patch

The `apply_xml_patch` tool (XPath-based raw OOXML manipulation) described in the original design is **not implemented**. Reasons:

- **Too fragile** — XPath over OOXML namespaces is error-prone and hard to get right, especially for LLM callers who would need to produce valid namespace-qualified XML fragments
- **Maintenance burden** — two patch systems (JSON + XML) doubles the testing and debugging surface
- **JSON patches cover the common cases** — the typed path model with `add`/`replace`/`remove`/`move`/`copy` operations handles the scenarios that matter for document authoring

If edge cases arise that the JSON patch model cannot handle, the appropriate response is to extend the JSON patch value types rather than introduce a parallel XML escape hatch.

### MCP SDK

The implementation uses the **official MCP C# SDK** (`ModelContextProtocol` NuGet package, maintained by Microsoft and Anthropic) instead of a hand-rolled JSON-RPC transport. This provides:

- Attribute-based tool registration (`[McpServerTool]`, `[McpServerToolType]`)
- Dependency injection for `SessionManager` and other services
- Automatic stdio transport via `WithStdioServerTransport()`
- Protocol-compliant request/response handling

## Goals

1. **Faithful OOXML parsing** — rely on the Open XML SDK, not hand-rolled XML parsing
2. **Fewer, more powerful tools** — replace 30+ individual MCP tools with 3 core operations
3. **Typed path model** — validate document paths against a schema before execution
4. **Single code path** — no more in-memory vs. XML-fallback split
5. **Cross-platform distribution** — NativeAOT binaries for macOS (ARM64/x64), Linux, Windows

---

## Part 1: .NET Migration

### Runtime and Distribution

| Aspect | Choice |
|--------|--------|
| Runtime | .NET 10 (LTS) |
| OOXML library | `DocumentFormat.OpenXml` 3.x |
| MCP SDK | `ModelContextProtocol` 0.7.x (official C# SDK) |
| Compilation | NativeAOT (standalone ~30-40 MB binary) |
| Transport | stdio JSON-RPC via MCP SDK |
| Logging | stderr only (MCP requirement) |

### Document Session Model

Each opened document is held as a `WordprocessingDocument` backed by a `MemoryStream`. This gives the SDK full read/write DOM access.

```csharp
public sealed class DocxSession : IDisposable
{
    public string Id { get; }
    public MemoryStream Stream { get; }
    public WordprocessingDocument Document { get; }
    public string? SourcePath { get; }  // null for new documents

    public static DocxSession Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var stream = new MemoryStream();
        stream.Write(bytes);
        stream.Position = 0;
        var doc = WordprocessingDocument.Open(stream, isEditable: true);
        return new DocxSession(doc, stream, path);
    }

    public static DocxSession Create()
    {
        var stream = new MemoryStream();
        var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        doc.AddMainDocumentPart();
        doc.MainDocumentPart!.Document = new Document(new Body());
        return new DocxSession(doc, stream, sourcePath: null);
    }

    public void Save(string path)
    {
        Document.Save();
        File.WriteAllBytes(path, Stream.ToArray());
    }
}
```

### Project Structure

```
src/DocxMcp/
├── Program.cs                  # MCP server bootstrap (official SDK)
├── DocxSession.cs              # Document session (MemoryStream + SDK)
├── SessionManager.cs           # Thread-safe session lifecycle
├── Tools/
│   ├── DocumentTools.cs        # open, create, save, close, list
│   ├── QueryTool.cs            # unified query via typed paths
│   ├── PatchTool.cs            # apply_patch (JSON only)
│   └── ExportTools.cs          # PDF, HTML, Markdown export
├── Paths/
│   ├── DocxPath.cs             # Typed path model
│   ├── PathSegment.cs          # Segment types
│   ├── Selector.cs             # Index, text, style selectors
│   ├── PathParser.cs           # String → DocxPath
│   ├── PathSchema.cs           # Structural validation
│   └── PathResolver.cs         # DocxPath → OpenXmlElement
└── Helpers/
    ├── OpenXmlExtensions.cs    # SDK convenience methods
    └── ElementFactory.cs       # JSON value → OpenXmlElement

tests/DocxMcp.Tests/
├── PathParserTests.cs
├── PathResolverTests.cs
├── PatchEngineTests.cs
└── QueryTests.cs
```

---

## Part 2: MCP Tool Surface

The server exposes **7 tools** (down from 30+ in the Rust implementation):

| Tool | Purpose |
|------|---------|
| `document_open` | Open or create a document, returns session ID |
| `document_save` | Save to disk |
| `document_close` | Release session |
| `document_list` | List open sessions |
| `query` | Read any part of the document via typed paths |
| `apply_patch` | Modify the document via JSON patches |
| `export_pdf` / `export_html` / `export_markdown` | Export to other formats |

~~`apply_xml_patch`~~ — **Not implemented.** Too fragile for LLM callers, too much maintenance burden. The JSON patch model is sufficient. If gaps are found, extend the JSON value types instead.

---

## Part 3: Typed Path Model

### Design Principle

Paths are **parsed, validated, and resolved** through a typed object model. A path like `/body/table[0]/row[1]/cell[0]` is not a string — it is a sequence of typed segments, each corresponding to an OOXML element kind. Invalid paths are rejected at parse time, before any DOM operation.

### Path Segments

Each segment maps 1:1 to an Open XML SDK type:

```
/body                    → Body
/body/paragraph[0]       → Paragraph (by index)
/body/heading[level=2]   → Paragraph with heading style
/body/table[0]           → Table
/body/table[0]/row[1]    → TableRow
/body/table[0]/row[1]/cell[0] → TableCell
/body/paragraph[0]/run[0]     → Run
/body/paragraph[0]/hyperlink[0] → Hyperlink
/body/paragraph[0]/run[0]/drawing[0] → Drawing (image)
/body/paragraph[0]/style       → ParagraphProperties
/body/section[0]               → SectionProperties
/header[type=default]          → HeaderPart
/footer[type=first]            → FooterPart
```

### Segment Type Hierarchy

```csharp
public abstract record PathSegment;

public record BodySegment : PathSegment;
public record ParagraphSegment(Selector Selector) : PathSegment;
public record HeadingSegment(int Level, Selector Selector) : PathSegment;
public record TableSegment(Selector Selector) : PathSegment;
public record RowSegment(Selector Selector) : PathSegment;
public record CellSegment(Selector Selector) : PathSegment;
public record RunSegment(Selector Selector) : PathSegment;
public record DrawingSegment(Selector Selector) : PathSegment;
public record HyperlinkSegment(Selector Selector) : PathSegment;
public record StyleSegment : PathSegment;
public record SectionSegment(Selector Selector) : PathSegment;
public record HeaderFooterSegment(HeaderFooterKind Kind) : PathSegment;
public record BookmarkSegment(Selector Selector) : PathSegment;
public record CommentSegment(Selector Selector) : PathSegment;
public record FootnoteSegment(Selector Selector) : PathSegment;
public record ChildrenSegment(int Index) : PathSegment;  // for positional insert
```

### Selectors

```csharp
public abstract record Selector;

public record IndexSelector(int Index) : Selector;              // [0], [-1]
public record TextContainsSelector(string Text) : Selector;     // [text~='...']
public record TextEqualsSelector(string Text) : Selector;       // [text='...']
public record StyleSelector(string StyleName) : Selector;       // [style='Heading1']
public record AllSelector : Selector;                           // [*]
```

### Structural Validation

The `PathSchema` defines which segments can follow which. This is checked at parse time:

```
BodySegment         → Paragraph, Heading, Table, Drawing, Section, Children, Style, HeaderFooter, Bookmark
TableSegment        → Row, Style
RowSegment          → Cell
CellSegment         → Paragraph, Table (nested)
ParagraphSegment    → Run, Hyperlink, Drawing, Style, Bookmark
HeadingSegment      → Run, Style, Bookmark
RunSegment          → Style, Drawing
HyperlinkSegment    → Run
HeaderFooterSegment → Paragraph, Table
```

Invalid paths are rejected with a precise error message:

```
"/body/cell[0]" → Error: Cell cannot be a direct child of Body
"/body/table[0]/paragraph[0]" → Error: Paragraph cannot be a direct child of Table
```

This is critical for the MCP use case where the caller is an LLM — precise errors enable self-correction.

### Resolution

The `PathResolver` walks the typed path and resolves each segment against the Open XML DOM using the SDK's typed element accessors (`Elements<Paragraph>()`, `Elements<Table>()`, etc.), not string matching.

---

## Part 4: JSON Patch Operations

### Format

Patches follow RFC 6902 semantics adapted for OOXML:

```json
{
  "tool": "apply_patch",
  "input": {
    "doc_id": "abc-123",
    "patches": [
      {
        "op": "add",
        "path": "/body/children/0",
        "value": {
          "type": "heading",
          "level": 1,
          "text": "Introduction"
        }
      },
      {
        "op": "replace",
        "path": "/body/paragraph[text~='old text']",
        "value": {
          "type": "paragraph",
          "text": "new text",
          "style": { "bold": true }
        }
      },
      {
        "op": "remove",
        "path": "/body/table[0]"
      },
      {
        "op": "move",
        "from": "/body/paragraph[2]",
        "path": "/body/children/0"
      }
    ]
  }
}
```

### Supported Operations

| Op | Description |
|----|-------------|
| `add` | Insert element at path. `/body/children/N` for positional insert |
| `replace` | Replace element or property at path with new value |
| `remove` | Delete element at path |
| `move` | Move element from one location to another |
| `copy` | Duplicate element to another location |

### Value Types

The `value` field in `add` and `replace` is a typed JSON object:

```json
// Paragraph
{ "type": "paragraph", "text": "...", "style": { "bold": true, "font_size": 12 } }

// Heading
{ "type": "heading", "level": 2, "text": "..." }

// Table
{
  "type": "table",
  "rows": [["A", "B"], ["C", "D"]],
  "headers": ["Col1", "Col2"],
  "border_style": "single"
}

// Image
{ "type": "image", "path": "/tmp/photo.png", "width": 200, "height": 150, "alt": "..." }

// Hyperlink
{ "type": "hyperlink", "text": "Click here", "url": "https://..." }

// Page break
{ "type": "page_break" }

// List
{ "type": "list", "items": ["a", "b", "c"], "ordered": false }

// Style (for replace on /style segments)
{ "bold": true, "italic": false, "font_size": 14, "color": "FF0000" }
```

### Patch Engine Flow

```
1. Parse path string → DocxPath (typed)
2. Validate path structure → PathSchema
3. Resolve path → OpenXmlElement (or parent + index for "add")
4. Validate value type against target segment
5. Execute operation via Open XML SDK DOM
6. Return result (success or error with path context)
```

---

## ~~Part 5: XML Patch (Escape Hatch)~~ — NOT IMPLEMENTED

~~For advanced scenarios not covered by the JSON patch model, `apply_xml_patch` provides direct XPath-based access.~~

**Decision: Not implemented.** XPath-based raw OOXML manipulation is too fragile for production use, especially when the caller is an LLM. The JSON patch model covers all common document authoring scenarios. If gaps are discovered, the correct approach is to extend `ElementFactory` with new value types rather than expose raw XML manipulation.

---

## Part 6: Query Tool

The `query` tool uses the same typed path model to read document content:

```json
{
  "tool": "query",
  "input": {
    "doc_id": "abc-123",
    "path": "/body/table[0]",
    "format": "json"
  }
}
```

Query can return different formats:

| Format | Returns |
|--------|---------|
| `json` | Structured JSON (tables as arrays, paragraphs as objects) |
| `text` | Plain text extraction |
| `summary` | Element count and structure outline |

Special query paths:

| Path | Returns |
|------|---------|
| `/body` | Full document structure summary |
| `/body/paragraph[*]` | All paragraphs |
| `/body/table[*]` | All tables |
| `/body/heading[*]` | All headings (with levels) |
| `/styles` | Document style definitions |
| `/metadata` | Core properties (title, author, dates) |

---

## Part 7: Migration Path

### Phase 1 — Bootstrap (Done)

- Set up .NET 10 project with NativeAOT and MCP C# SDK
- Implement `DocxSession` (open/create/save/close)
- Implement `SessionManager` (thread-safe session lifecycle)
- Wire MCP server with stdio transport

### Phase 2 — Typed Paths (Done)

- Implement `DocxPath`, `PathSegment`, `Selector` types
- Implement `PathParser` (string to typed model)
- Implement `PathSchema` (structural validation)
- Implement `PathResolver` (path to Open XML element)
- Wire into `query` tool

### Phase 3 — JSON Patches (Done)

- Implement `PatchTool` with add/replace/remove/move/copy
- Implement `ElementFactory` (JSON value to Open XML element)
- Support all value types (paragraph, heading, table, image, hyperlink, style, list, page_break)
- 43 tests passing

### Phase 4 — Export (Done)

- PDF export via LibreOffice CLI
- HTML export (native)
- Markdown export (native)

### ~~Phase 4b — XML Patch~~ (Skipped)

- ~~Implement `apply_xml_patch` with XPath resolution~~
- **Not implemented** — too fragile, unnecessary given the JSON patch model

### Phase 5 — Parity and Deprecation (Done)

- Rust implementation removed
- All functionality migrated to .NET

---

## Risks and Mitigations

| Risk | Mitigation |
|------|------------|
| NativeAOT trim warnings | Open XML SDK 3.x is AOT-compatible; test early |
| PDF export | No native .NET solution; use LibreOffice CLI |
| Path model gaps | Extend JSON value types in `ElementFactory` |
| LLM path errors | Precise error messages enable self-correction in MCP |
| Binary size (~30-40 MB) | Acceptable for desktop MCP server |
