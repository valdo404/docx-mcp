using System.ComponentModel;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;
using DocxMcp.Models;
using DocxMcp.Paths;
using static DocxMcp.Helpers.ElementIdManager;

namespace DocxMcp.Tools;

/// <summary>
/// Individual element manipulation tools with detailed documentation.
/// These call the same underlying engine as apply_patch but provide
/// focused interfaces for each operation type.
/// </summary>
[McpServerToolType]
public sealed class ElementTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [McpServerTool(Name = "add_element"), Description(
        "Add a new element to the document at a specific position.\n\n" +
        "Path syntax:\n" +
        "  /body/children/N — insert at position N (0-indexed)\n" +
        "  /body/children/0 — insert at the beginning\n" +
        "  /body/table[0]/row — append a row to first table\n" +
        "  /body/table[id='1A2B3C4D']/row — append row to table by stable ID\n" +
        "  /body/paragraph[id='AABBCCDD'] — target paragraph by ID (use with replace/remove)\n\n" +
        "Stable IDs (preferred for existing elements):\n" +
        "  Use id='HEXVALUE' selector to target elements by their unique ID.\n" +
        "  IDs are 1-8 hex characters (e.g., id='1A2B3C4D').\n" +
        "  Query the document to discover element IDs.\n\n" +
        "Element types:\n" +
        "  paragraph — {\"type\": \"paragraph\", \"text\": \"Simple text\"}\n" +
        "              {\"type\": \"paragraph\", \"runs\": [{\"text\": \"bold\", \"style\": {\"bold\": true}}, {\"text\": \"normal\"}]}\n" +
        "              {\"type\": \"paragraph\", \"properties\": {\"alignment\": \"center\"}, \"runs\": [...]}\n\n" +
        "  heading   — {\"type\": \"heading\", \"level\": 1, \"text\": \"Chapter Title\"}\n" +
        "              {\"type\": \"heading\", \"level\": 2, \"runs\": [{\"text\": \"Section\"}]}\n\n" +
        "  table     — {\"type\": \"table\", \"headers\": [\"A\", \"B\"], \"rows\": [[\"1\", \"2\"], [\"3\", \"4\"]]}\n" +
        "              Rich: {\"type\": \"table\", \"headers\": [{\"text\": \"H1\", \"shading\": \"E0E0E0\"}], \"rows\": [...]}\n\n" +
        "  row       — {\"type\": \"row\", \"cells\": [\"Cell1\", \"Cell2\"], \"is_header\": true}\n\n" +
        "  list      — {\"type\": \"list\", \"items\": [\"Item 1\", \"Item 2\"], \"ordered\": true}\n\n" +
        "  image     — {\"type\": \"image\", \"path\": \"/path/to/image.png\", \"width\": 200, \"height\": 100}\n\n" +
        "  hyperlink — {\"type\": \"hyperlink\", \"text\": \"Click here\", \"url\": \"https://example.com\"}\n\n" +
        "  page_break    — {\"type\": \"page_break\"}\n" +
        "  section_break — {\"type\": \"section_break\"}\n\n" +
        "Run styles: bold, italic, underline, strike, font_size, font_name, color\n" +
        "Paragraph properties: alignment (left/center/right/justify), tabs, spacing_before, spacing_after")]
    public static string AddElement(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path where to add the element (e.g., /body/children/0, /body/table[0]/row).")] string path,
        [Description("JSON object describing the element to add.")] string value,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = JsonSerializer.Serialize(new[] {
            new { op = "add", path, value = JsonDocument.Parse(value).RootElement }
        });
        return PatchTool.ApplyPatch(sessions, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "replace_element"), Description(
        "Replace an existing element in the document.\n\n" +
        "Path selectors:\n" +
        "  By index: /body/paragraph[0], /body/table[1]\n" +
        "  By ID: /body/paragraph[id='1A2B3C4D'] (PREFERRED for stability)\n" +
        "  By text: /body/paragraph[text~='Hello'] (contains text)\n" +
        "  By text exact: /body/paragraph[text='Hello World']\n" +
        "  By style: /body/paragraph[style='Heading1']\n\n" +
        "Stable IDs:\n" +
        "  IDs are 1-8 hex characters (e.g., id='1A2B3C4D').\n" +
        "  Using IDs prevents issues when document structure changes.\n" +
        "  Query the document to discover element IDs.\n\n" +
        "The new element completely replaces the old one.\n" +
        "Element format is the same as add_element.\n\n" +
        "Example: Replace paragraph by ID with a heading:\n" +
        "  path: /body/paragraph[id='1A2B3C4D']\n" +
        "  value: {\"type\": \"heading\", \"level\": 1, \"text\": \"New Title\"}")]
    public static string ReplaceElement(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to replace.")] string path,
        [Description("JSON object describing the new element.")] string value,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = JsonSerializer.Serialize(new[] {
            new { op = "replace", path, value = JsonDocument.Parse(value).RootElement }
        });
        return PatchTool.ApplyPatch(sessions, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "remove_element"), Description(
        "Remove an element from the document.\n\n" +
        "Path selectors:\n" +
        "  By index: /body/paragraph[0], /body/table[1]\n" +
        "  By ID: /body/paragraph[id='1A2B3C4D'] (PREFERRED for stability)\n" +
        "  By text: /body/paragraph[text~='Delete me'] (contains)\n" +
        "  Wildcards: /body/paragraph[*] (remove all paragraphs)\n\n" +
        "Stable IDs (1-8 hex chars):\n" +
        "  Using IDs ensures you remove the exact intended element.\n" +
        "  Query the document first to get element IDs.\n\n" +
        "The element and all its contents are removed.\n" +
        "Returns the ID of the removed element for reference.")]
    public static string RemoveElement(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to remove.")] string path,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $"""[{{"op": "remove", "path": "{EscapeJson(path)}"}}]""";
        return PatchTool.ApplyPatch(sessions, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "move_element"), Description(
        "Move an element from one location to another.\n\n" +
        "The element is removed from its original location and inserted at the new location.\n\n" +
        "Path selectors (for 'from'):\n" +
        "  By index: /body/paragraph[2]\n" +
        "  By ID: /body/paragraph[id='1A2B3C4D'] (PREFERRED)\n" +
        "  By text: /body/paragraph[text~='Move this']\n\n" +
        "Destination (for 'to'):\n" +
        "  /body/children/0 — move to beginning\n" +
        "  /body/children/5 — move to position 5\n" +
        "  /body/table[id='AABB1122']/row — append as row to table by ID\n\n" +
        "Stable IDs (1-8 hex chars):\n" +
        "  Using IDs for 'from' ensures you move the exact intended element.\n" +
        "  The element keeps its ID after moving.\n\n" +
        "Example: Move paragraph by ID to beginning:\n" +
        "  from: /body/paragraph[id='1A2B3C4D']\n" +
        "  to: /body/children/0")]
    public static string MoveElement(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to move.")] string from,
        [Description("Destination path (use /body/children/N for position).")] string to,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $"""[{{"op": "move", "from": "{EscapeJson(from)}", "path": "{EscapeJson(to)}"}}]""";
        return PatchTool.ApplyPatch(sessions, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "copy_element"), Description(
        "Duplicate an element to another location.\n\n" +
        "The original element is preserved, and a copy is created at the destination.\n" +
        "The copy receives a NEW unique ID (returned as copy_id in result).\n\n" +
        "Path selectors (for 'from'):\n" +
        "  By index: /body/table[0]\n" +
        "  By ID: /body/table[id='1A2B3C4D'] (PREFERRED)\n" +
        "  By text: /body/paragraph[text~='Template']\n\n" +
        "Destination (for 'to'):\n" +
        "  /body/children/0 — copy to beginning\n" +
        "  /body/children/999 — copy to end (high index = append)\n" +
        "  /body/table[id='AABB1122']/row — copy as new row\n\n" +
        "Stable IDs (1-8 hex chars):\n" +
        "  Using IDs for 'from' ensures you copy the exact intended element.\n" +
        "  Result includes source_id and copy_id (new ID of the copy).\n\n" +
        "Example: Copy table by ID to end of document:\n" +
        "  from: /body/table[id='1A2B3C4D']\n" +
        "  to: /body/children/999")]
    public static string CopyElement(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to copy.")] string from,
        [Description("Destination path for the copy.")] string to,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $"""[{{"op": "copy", "from": "{EscapeJson(from)}", "path": "{EscapeJson(to)}"}}]""";
        return PatchTool.ApplyPatch(sessions, doc_id, patchJson, dry_run);
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

[McpServerToolType]
public sealed class TextTools
{
    [McpServerTool(Name = "replace_text"), Description(
        "Find and replace text in the document while preserving formatting.\n\n" +
        "This is a non-destructive text replacement that maintains:\n" +
        "  - Bold, italic, underline styling\n" +
        "  - Font size and font family\n" +
        "  - Text color\n" +
        "  - All other run-level formatting\n\n" +
        "Path selectors:\n" +
        "  /body — Search entire document body\n" +
        "  /body/paragraph[0] — First paragraph only\n" +
        "  /body/paragraph[id='1A2B3C4D'] — Specific paragraph by ID (PREFERRED)\n" +
        "  /body/table[id='AABB1122'] — Specific table by ID\n" +
        "  /body/paragraph[*] — All paragraphs\n\n" +
        "Parameters:\n" +
        "  find — Text to search for (case-sensitive, exact match)\n" +
        "  replace — Replacement text (CANNOT be empty)\n" +
        "  max_count — Maximum replacements (default: 1)\n" +
        "              0 = do nothing (useful for counting matches)\n" +
        "              1 = replace first occurrence only\n" +
        "              N = replace up to N occurrences\n\n" +
        "Returns:\n" +
        "  matches_found — Total occurrences of 'find' in target\n" +
        "  replacements_made — Actual replacements performed\n\n" +
        "Note: To delete text, use remove_element or manually replace with alternative content.")]
    public static string ReplaceText(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to element(s) to search in.")] string path,
        [Description("Text to find (case-sensitive).")] string find,
        [Description("Replacement text (cannot be empty).")] string replace,
        [Description("Maximum number of replacements (default: 1, 0 = none).")] int max_count = 1,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = JsonSerializer.Serialize(new[] {
            new { op = "replace_text", path, find, replace, max_count }
        });
        return PatchTool.ApplyPatch(sessions, doc_id, patchJson, dry_run);
    }
}

[McpServerToolType]
public sealed class TableTools
{
    [McpServerTool(Name = "remove_table_column"), Description(
        "Remove a column from a table by index.\n\n" +
        "This removes the cell at the specified column index from every row.\n" +
        "Column indices are 0-based (first column = 0).\n\n" +
        "Path selectors:\n" +
        "  /body/table[0] — First table by index\n" +
        "  /body/table[id='1A2B3C4D'] — Table by stable ID (PREFERRED)\n" +
        "  /body/table[*] — All tables (removes column from each)\n\n" +
        "Returns:\n" +
        "  column_index — The index of the removed column\n" +
        "  rows_affected — Number of rows that had cells removed\n\n" +
        "Example: Remove the second column from table by ID:\n" +
        "  path: /body/table[id='1A2B3C4D']\n" +
        "  column: 1")]
    public static string RemoveTableColumn(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the table.")] string path,
        [Description("Column index to remove (0-based).")] int column,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $"""[{{"op": "remove_column", "path": "{path.Replace("\\", "\\\\").Replace("\"", "\\\"")}", "column": {column}}}]""";
        return PatchTool.ApplyPatch(sessions, doc_id, patchJson, dry_run);
    }
}
