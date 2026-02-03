using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DocxMcp.Tools;

/// <summary>
/// Documentation tool for path syntax and selectors.
/// </summary>
[McpServerToolType]
public sealed class PathHelpTool
{
    [McpServerTool(Name = "path_syntax"), Description(
        "Get documentation about the path syntax used to navigate and select document elements.\n" +
        "Returns a comprehensive guide covering path segments, selectors, and examples.")]
    public static string PathSyntax()
    {
        return PathSyntaxDocumentation;
    }

    private const string PathSyntaxDocumentation = """
# DOCX Path Syntax Reference

Paths are used to navigate and select elements within a DOCX document.
Format: /segment[selector]/segment[selector]/...

## Root Segments

| Segment | Description |
|---------|-------------|
| /body | Document body (starting point for most paths) |
| /header | Document header (default) |
| /header[type=first] | First page header |
| /header[type=even] | Even page header |
| /footer | Document footer (default) |
| /footer[type=first] | First page footer |

## Element Segments

| Segment | Aliases | Description |
|---------|---------|-------------|
| paragraph | p | Text paragraphs |
| heading | | Heading paragraphs (Heading1, Heading2, etc.) |
| table | | Tables |
| row | | Table rows (inside table) |
| cell | | Table cells (inside row) |
| run | | Text runs (inside paragraph) |
| hyperlink | | Hyperlinks |
| drawing | | Images and drawings |
| bookmark | | Bookmarks |
| section | | Section properties |
| style | | Element style properties |

## Selectors

Selectors filter elements within a segment type.

### By Index (0-based)
```
/body/paragraph[0]      # First paragraph
/body/paragraph[1]      # Second paragraph
/body/paragraph[-1]     # Last paragraph
/body/table[0]/row[2]   # Third row of first table
```

### By Stable ID (PREFERRED for modifications)
```
/body/paragraph[id='1A2B3C4D']     # Paragraph with ID 1A2B3C4D
/body/table[id='AABB1122']         # Table with ID AABB1122
/body/table[id='1234']/row[id='5678']   # Nested selection by ID
```
IDs are 1-8 hexadecimal characters. Query the document to discover element IDs.
Using IDs is preferred because they remain stable even when document structure changes.

### By Text Content
```
/body/paragraph[text~='hello']     # Paragraphs containing "hello" (case-insensitive)
/body/paragraph[text='Hello World'] # Paragraphs with exact text (case-insensitive)
```

### By Style
```
/body/paragraph[style='Heading1']  # Paragraphs with Heading1 style
/body/table[style='TableGrid']     # Tables with TableGrid style
```

### Wildcard (All Elements)
```
/body/paragraph[*]      # All paragraphs
/body/table[0]/row[*]   # All rows in first table
```

## Special Paths

### Positional Insert (for add operations)
```
/body/children/0        # Insert at beginning of body
/body/children/5        # Insert at position 5
/body/table[0]/children/0   # Insert at beginning of table
```

### Style Properties
```
/body/paragraph[0]/style    # Style properties of first paragraph
/body/table[0]/style        # Table style properties
/body/paragraph[0]/run[0]/style  # Run style properties
```

### Headings with Level Filter
```
/body/heading              # All headings
/body/heading[level=1]     # Only Heading1 paragraphs
/body/heading[level=2]     # Only Heading2 paragraphs
/body/heading[0]           # First heading of any level
```

## Examples

### Common Operations

```json
// Add paragraph at beginning
{"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "Hello"}}

// Replace paragraph by stable ID
{"op": "replace", "path": "/body/paragraph[id='1A2B3C4D']", "value": {"type": "heading", "level": 1, "text": "New Title"}}

// Remove paragraph containing specific text
{"op": "remove", "path": "/body/paragraph[text~='delete me']"}

// Move paragraph by ID to beginning
{"op": "move", "from": "/body/paragraph[id='AABB1122']", "path": "/body/children/0"}

// Copy table by ID
{"op": "copy", "from": "/body/table[id='1234']", "path": "/body/children/999"}

// Replace text in specific paragraph
{"op": "replace_text", "path": "/body/paragraph[id='5678']", "find": "old", "replace": "new"}

// Remove column from table by ID
{"op": "remove_column", "path": "/body/table[id='ABCD']", "column": 1}
```

### Querying Elements

```
/body                      # Entire body
/body/paragraph[*]         # All paragraphs (for counting or listing)
/body/table[0]/row[*]/cell[0]  # First cell of each row
```

## Best Practices

1. **Use IDs for modifications**: Query first to get element IDs, then use IDs in patch operations.
2. **Use indexes for new documents**: When building a document from scratch, indexes are fine.
3. **Use text selectors carefully**: They may match multiple elements.
4. **Use dry_run**: Test your patches with dry_run=true before applying.
5. **Prefer specific paths**: /body/paragraph[id='X'] is more reliable than /body/paragraph[0].
""";
}
