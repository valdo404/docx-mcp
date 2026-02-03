using System.ComponentModel;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;
using DocxMcp.Models;
using DocxMcp.Paths;
using DocxMcp.ExternalChanges;
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
    [McpServerTool(Name = "add_element"), Description(
        "Add a new element to the document at a specific position.\n\n" +
        "PATH SYNTAX FOR INSERTION:\n" +
        "  /body/children/N — insert at position N (0-indexed)\n" +
        "  /body/children/0 — insert at the beginning\n" +
        "  /body/children/999 — append to end (any large number works)\n" +
        "  /body/table[0]/row — append a row to first table\n" +
        "  /body/table[id='1A2B3C4D']/row — append row to table by stable ID\n" +
        "  /body/table[0]/row[0]/cell — append a cell to first row\n\n" +
        "STABLE IDs (PREFERRED for targeting existing elements):\n" +
        "  Use id='HEXVALUE' selector (1-8 hex chars, e.g., id='1A2B3C4D').\n" +
        "  IDs survive document edits; indexes shift when elements change.\n" +
        "  Use query_document or read_section to discover element IDs first.\n\n" +
        "ELEMENT TYPES AND FORMATS:\n\n" +
        "  paragraph (basic):\n" +
        "    {\"type\": \"paragraph\", \"text\": \"Simple text content\"}\n\n" +
        "  paragraph (with formatting):\n" +
        "    {\"type\": \"paragraph\", \"runs\": [\n" +
        "      {\"text\": \"Bold \", \"style\": {\"bold\": true}},\n" +
        "      {\"text\": \"and italic\", \"style\": {\"italic\": true}}\n" +
        "    ]}\n\n" +
        "  paragraph (with properties):\n" +
        "    {\"type\": \"paragraph\", \"properties\": {\n" +
        "      \"alignment\": \"center\",\n" +
        "      \"spacing_before\": 120,\n" +
        "      \"spacing_after\": 240\n" +
        "    }, \"runs\": [{\"text\": \"Centered\"}]}\n\n" +
        "  heading:\n" +
        "    {\"type\": \"heading\", \"level\": 1, \"text\": \"Chapter Title\"}\n" +
        "    {\"type\": \"heading\", \"level\": 2, \"runs\": [{\"text\": \"Section\", \"style\": {\"color\": \"2E5496\"}}]}\n\n" +
        "  table (simple):\n" +
        "    {\"type\": \"table\", \"headers\": [\"Name\", \"Age\"], \"rows\": [[\"Alice\", \"30\"], [\"Bob\", \"25\"]]}\n\n" +
        "  table (with styling):\n" +
        "    {\"type\": \"table\", \"headers\": [\n" +
        "      {\"text\": \"Name\", \"shading\": \"E0E0E0\", \"style\": {\"bold\": true}}\n" +
        "    ], \"rows\": [[{\"text\": \"Alice\", \"shading\": \"F5F5F5\"}]]}\n\n" +
        "  row (for adding to existing table):\n" +
        "    {\"type\": \"row\", \"cells\": [\"Cell1\", \"Cell2\", \"Cell3\"]}\n" +
        "    {\"type\": \"row\", \"cells\": [{\"text\": \"Bold\", \"style\": {\"bold\": true}}], \"is_header\": true}\n\n" +
        "  cell (for adding to existing row):\n" +
        "    {\"type\": \"cell\", \"text\": \"Content\"}\n" +
        "    {\"type\": \"cell\", \"text\": \"Styled\", \"style\": {\"bold\": true}, \"shading\": \"FFFF00\"}\n\n" +
        "  list:\n" +
        "    {\"type\": \"list\", \"items\": [\"First\", \"Second\", \"Third\"], \"ordered\": false}\n" +
        "    {\"type\": \"list\", \"items\": [\"Step 1\", \"Step 2\"], \"ordered\": true}\n\n" +
        "  image:\n" +
        "    {\"type\": \"image\", \"path\": \"/absolute/path/to/image.png\", \"width\": 200, \"height\": 100}\n" +
        "    {\"type\": \"image\", \"path\": \"./relative/image.jpg\"}\n\n" +
        "  hyperlink:\n" +
        "    {\"type\": \"hyperlink\", \"text\": \"Click here\", \"url\": \"https://example.com\"}\n\n" +
        "  page_break / section_break:\n" +
        "    {\"type\": \"page_break\"}\n" +
        "    {\"type\": \"section_break\"}\n\n" +
        "RUN STYLE OPTIONS:\n" +
        "  bold: true/false, italic: true/false, underline: true/false, strike: true/false\n" +
        "  font_size: integer (half-points, e.g., 24 = 12pt)\n" +
        "  font_name: string (e.g., \"Arial\", \"Times New Roman\")\n" +
        "  color: hex string without # (e.g., \"FF0000\" for red)\n\n" +
        "PARAGRAPH PROPERTIES:\n" +
        "  alignment: \"left\", \"center\", \"right\", \"justify\"\n" +
        "  spacing_before/spacing_after: integer (twips, 1440 = 1 inch)\n" +
        "  tabs: array of tab stop positions\n\n" +
        "RESPONSE FORMAT:\n" +
        "  {\"success\": true, \"applied\": 1, \"total\": 1,\n" +
        "   \"operations\": [{\"op\": \"add\", \"path\": \"...\", \"status\": \"success\", \"created_id\": \"1A2B3C4D\"}]}\n\n" +
        "  The created_id is the stable ID of the new element—save it for future operations.\n\n" +
        "BEST PRACTICES:\n" +
        "  1. Use dry_run=true first to validate your operation\n" +
        "  2. Save the created_id from response for subsequent edits\n" +
        "  3. Use /body/children/999 to append (avoids counting elements)\n" +
        "  4. For tables: add the table first, then add rows using the table's ID")]
    public static string AddElement(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path where to add the element (e.g., /body/children/0, /body/table[0]/row).")] string path,
        [Description("JSON object describing the element to add.")] string value,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patches = new[] { new AddPatchInput { Path = path, Value = JsonDocument.Parse(value).RootElement } };
        var patchJson = JsonSerializer.Serialize(patches, DocxJsonContext.Default.AddPatchInputArray);
        return PatchTool.ApplyPatch(sessions, externalChangeTracker, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "replace_element"), Description(
        "Replace an existing element in the document with a new element.\n\n" +
        "PATH SELECTORS (how to target the element to replace):\n" +
        "  By index:      /body/paragraph[0]          — first paragraph\n" +
        "                 /body/table[1]              — second table\n" +
        "                 /body/paragraph[-1]         — last paragraph\n" +
        "  By ID:         /body/paragraph[id='1A2B3C4D']  — PREFERRED, stable\n" +
        "  By text:       /body/paragraph[text~='Hello']  — contains 'Hello' (case-insensitive)\n" +
        "  By exact text: /body/paragraph[text='Hello World']  — exact match\n" +
        "  By style:      /body/paragraph[style='Heading1']    — by Word style name\n" +
        "  Nested:        /body/table[0]/row[1]/cell[0]        — specific cell\n\n" +
        "STABLE IDs (ALWAYS PREFERRED):\n" +
        "  IDs are 1-8 hex characters (e.g., id='1A2B3C4D').\n" +
        "  IDs don't change when other elements are added/removed.\n" +
        "  Indexes shift after edits—IDs remain constant.\n" +
        "  Use query_document to discover element IDs before replacing.\n\n" +
        "BEHAVIOR:\n" +
        "  The new element COMPLETELY replaces the old one.\n" +
        "  The new element gets the same ID as the replaced element.\n" +
        "  Element format is the same as add_element (see add_element for all types).\n\n" +
        "COMMON USE CASES:\n" +
        "  1. Convert paragraph to heading:\n" +
        "     path: /body/paragraph[id='ABC123']\n" +
        "     value: {\"type\": \"heading\", \"level\": 1, \"text\": \"New Title\"}\n\n" +
        "  2. Update table cell content:\n" +
        "     path: /body/table[id='DEF456']/row[1]/cell[2]\n" +
        "     value: {\"type\": \"cell\", \"text\": \"Updated\", \"style\": {\"bold\": true}}\n\n" +
        "  3. Replace plain text with formatted text:\n" +
        "     path: /body/paragraph[text~='replace me']\n" +
        "     value: {\"type\": \"paragraph\", \"runs\": [{\"text\": \"NEW\", \"style\": {\"bold\": true, \"color\": \"FF0000\"}}]}\n\n" +
        "RESPONSE FORMAT:\n" +
        "  {\"success\": true, \"applied\": 1, \"total\": 1,\n" +
        "   \"operations\": [{\"op\": \"replace\", \"path\": \"...\", \"status\": \"success\", \"replaced_id\": \"1A2B3C4D\"}]}\n\n" +
        "BEST PRACTICES:\n" +
        "  1. Query for the element's ID first, then use id selector\n" +
        "  2. Use dry_run=true to validate before replacing\n" +
        "  3. For partial text changes, use replace_text instead (preserves formatting)")]
    public static string ReplaceElement(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to replace.")] string path,
        [Description("JSON object describing the new element.")] string value,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patches = new[] { new ReplacePatchInput { Path = path, Value = JsonDocument.Parse(value).RootElement } };
        var patchJson = JsonSerializer.Serialize(patches, DocxJsonContext.Default.ReplacePatchInputArray);
        return PatchTool.ApplyPatch(sessions, externalChangeTracker, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "remove_element"), Description(
        "Remove an element from the document.\n\n" +
        "PATH SELECTORS (how to target the element to remove):\n" +
        "  By index:      /body/paragraph[0]              — first paragraph\n" +
        "                 /body/table[1]                  — second table\n" +
        "                 /body/paragraph[-1]             — last paragraph\n" +
        "  By ID:         /body/paragraph[id='1A2B3C4D']  — PREFERRED, most reliable\n" +
        "  By text:       /body/paragraph[text~='delete'] — contains text (case-insensitive)\n" +
        "  By exact text: /body/paragraph[text='Delete Me'] — exact match\n" +
        "  Wildcard:      /body/paragraph[*]              — ALL paragraphs (use carefully!)\n" +
        "  Nested:        /body/table[0]/row[2]           — specific table row\n" +
        "                 /body/table[0]/row[1]/cell[0]   — specific cell\n\n" +
        "STABLE IDs (ALWAYS PREFERRED):\n" +
        "  IDs are 1-8 hex characters (e.g., id='1A2B3C4D').\n" +
        "  Using IDs ensures you remove the EXACT intended element.\n" +
        "  Indexes can shift after other edits—IDs remain constant.\n" +
        "  Use query_document to get element IDs before removing.\n\n" +
        "BEHAVIOR:\n" +
        "  The element and ALL its contents are permanently removed.\n" +
        "  For tables: removing a row removes all its cells.\n" +
        "  For paragraphs: all text and formatting is removed.\n" +
        "  Use undo_patch to restore if needed.\n\n" +
        "COMMON USE CASES:\n" +
        "  1. Remove specific paragraph by ID:\n" +
        "     path: /body/paragraph[id='ABC123']\n\n" +
        "  2. Remove a table row:\n" +
        "     path: /body/table[id='DEF456']/row[2]\n\n" +
        "  3. Remove all paragraphs containing 'DRAFT':\n" +
        "     path: /body/paragraph[text~='DRAFT']\n\n" +
        "  4. Clear all content (remove all body children):\n" +
        "     path: /body/paragraph[*]  — then /body/table[*]\n\n" +
        "RESPONSE FORMAT:\n" +
        "  {\"success\": true, \"applied\": 1, \"total\": 1,\n" +
        "   \"operations\": [{\"op\": \"remove\", \"path\": \"...\", \"status\": \"success\", \"removed_id\": \"1A2B3C4D\"}]}\n\n" +
        "  The removed_id confirms which element was removed.\n" +
        "  For wildcard removals, multiple operations are returned.\n\n" +
        "BEST PRACTICES:\n" +
        "  1. Always query for ID first when removing specific elements\n" +
        "  2. Use dry_run=true to see what will be removed\n" +
        "  3. Be cautious with [*] wildcard—it removes ALL matching elements\n" +
        "  4. Remember you can undo with undo_patch if needed")]
    public static string RemoveElement(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to remove.")] string path,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $$"""[{"op": "remove", "path": "{{EscapeJson(path)}}"}]""";
        return PatchTool.ApplyPatch(sessions, externalChangeTracker, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "move_element"), Description(
        "Move an element from one location to another within the document.\n\n" +
        "The element is removed from its original location and inserted at the new location.\n" +
        "The element retains its original ID after moving.\n\n" +
        "SOURCE PATH SELECTORS ('from' parameter):\n" +
        "  By index:      /body/paragraph[2]              — third paragraph\n" +
        "                 /body/table[0]/row[3]           — fourth row of first table\n" +
        "                 /body/paragraph[-1]             — last paragraph\n" +
        "  By ID:         /body/paragraph[id='1A2B3C4D']  — PREFERRED, most reliable\n" +
        "  By text:       /body/paragraph[text~='move me'] — contains text\n" +
        "  By style:      /body/paragraph[style='Quote']  — by style name\n\n" +
        "DESTINATION PATH SYNTAX ('to' parameter):\n" +
        "  /body/children/0   — move to beginning of document\n" +
        "  /body/children/N   — move to position N (0-indexed)\n" +
        "  /body/children/999 — move to end (any large number = append)\n" +
        "  /body/table[id='XYZ']/row — move element as new row in table\n\n" +
        "BEHAVIOR:\n" +
        "  The element keeps its ID after moving (for future reference).\n" +
        "  All content and formatting is preserved.\n" +
        "  The source location becomes empty (element is truly moved, not copied).\n\n" +
        "COMMON USE CASES:\n" +
        "  1. Move paragraph to top of document:\n" +
        "     from: /body/paragraph[id='ABC123']\n" +
        "     to: /body/children/0\n\n" +
        "  2. Move paragraph to end:\n" +
        "     from: /body/paragraph[id='ABC123']\n" +
        "     to: /body/children/999\n\n" +
        "  3. Reorder table rows:\n" +
        "     from: /body/table[id='DEF456']/row[3]\n" +
        "     to: /body/table[id='DEF456']/row[0]  — move to first row position\n\n" +
        "  4. Move content before a specific element:\n" +
        "     from: /body/paragraph[text~='conclusion']\n" +
        "     to: /body/children/2\n\n" +
        "RESPONSE FORMAT:\n" +
        "  {\"success\": true, \"applied\": 1, \"total\": 1,\n" +
        "   \"operations\": [{\"op\": \"move\", \"path\": \"...\", \"status\": \"success\",\n" +
        "                   \"moved_id\": \"1A2B3C4D\", \"from\": \"/body/paragraph[id='1A2B3C4D']\"}]}\n\n" +
        "  The moved_id confirms the element's ID (unchanged after move).\n" +
        "  The 'from' field shows the original path used.\n\n" +
        "BEST PRACTICES:\n" +
        "  1. Use IDs for the 'from' path to ensure correct element is moved\n" +
        "  2. Use /body/children/N for precise positioning\n" +
        "  3. Use dry_run=true to verify the operation first\n" +
        "  4. For duplicating (not moving), use copy_element instead")]
    public static string MoveElement(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to move.")] string from,
        [Description("Destination path (use /body/children/N for position).")] string to,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $$"""[{"op": "move", "from": "{{EscapeJson(from)}}", "path": "{{EscapeJson(to)}}"}]""";
        return PatchTool.ApplyPatch(sessions, externalChangeTracker, doc_id, patchJson, dry_run);
    }

    [McpServerTool(Name = "copy_element"), Description(
        "Duplicate an element to another location (original is preserved).\n\n" +
        "The original element stays in place, and a complete copy is created.\n" +
        "The copy receives a NEW unique ID (different from the original).\n\n" +
        "SOURCE PATH SELECTORS ('from' parameter):\n" +
        "  By index:      /body/table[0]                  — first table\n" +
        "                 /body/paragraph[2]              — third paragraph\n" +
        "                 /body/table[0]/row[0]           — first row of first table\n" +
        "  By ID:         /body/table[id='1A2B3C4D']      — PREFERRED, most reliable\n" +
        "  By text:       /body/paragraph[text~='template'] — find by content\n" +
        "  By style:      /body/paragraph[style='Quote']  — by style name\n\n" +
        "DESTINATION PATH SYNTAX ('to' parameter):\n" +
        "  /body/children/0   — copy to beginning of document\n" +
        "  /body/children/N   — copy to position N (0-indexed)\n" +
        "  /body/children/999 — copy to end (any large number = append)\n" +
        "  /body/table[id='XYZ']/row — copy element as new row in specified table\n\n" +
        "BEHAVIOR:\n" +
        "  Original element is UNCHANGED (this is copy, not move).\n" +
        "  Copy receives a NEW unique ID (returned as copy_id).\n" +
        "  All content, formatting, and nested elements are copied.\n" +
        "  For tables: entire table structure including all rows/cells is copied.\n\n" +
        "COMMON USE CASES:\n" +
        "  1. Duplicate a template paragraph:\n" +
        "     from: /body/paragraph[id='TEMPLATE']\n" +
        "     to: /body/children/999\n\n" +
        "  2. Copy a table for reuse:\n" +
        "     from: /body/table[id='ABC123']\n" +
        "     to: /body/children/999\n" +
        "     (then modify the copy using copy_id)\n\n" +
        "  3. Duplicate a table row:\n" +
        "     from: /body/table[id='DEF456']/row[0]  — copy header row\n" +
        "     to: /body/table[id='DEF456']/row       — append as new row\n\n" +
        "  4. Copy formatted content as template:\n" +
        "     from: /body/paragraph[text~='[TEMPLATE]']\n" +
        "     to: /body/children/5\n\n" +
        "RESPONSE FORMAT:\n" +
        "  {\"success\": true, \"applied\": 1, \"total\": 1,\n" +
        "   \"operations\": [{\"op\": \"copy\", \"path\": \"...\", \"status\": \"success\",\n" +
        "                   \"source_id\": \"ORIGINAL_ID\", \"copy_id\": \"NEW_UNIQUE_ID\"}]}\n\n" +
        "  source_id: ID of the original element (unchanged)\n" +
        "  copy_id: NEW ID of the copied element (use this for subsequent edits to the copy)\n\n" +
        "BEST PRACTICES:\n" +
        "  1. Save the copy_id from the response for future operations on the copy\n" +
        "  2. Use IDs for the 'from' path to ensure correct element is copied\n" +
        "  3. Use dry_run=true to verify the operation first\n" +
        "  4. For moving (not copying), use move_element instead")]
    public static string CopyElement(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the element to copy.")] string from,
        [Description("Destination path for the copy.")] string to,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $$"""[{"op": "copy", "from": "{{EscapeJson(from)}}", "path": "{{EscapeJson(to)}}"}]""";
        return PatchTool.ApplyPatch(sessions, externalChangeTracker, doc_id, patchJson, dry_run);
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

[McpServerToolType]
public sealed class TextTools
{
    [McpServerTool(Name = "replace_text"), Description(
        "Find and replace text while preserving all formatting (bold, italic, fonts, etc.).\n\n" +
        "This is the PREFERRED way to change text content because it:\n" +
        "  - Preserves bold, italic, underline, strikethrough\n" +
        "  - Preserves font size, font family, and text color\n" +
        "  - Preserves paragraph alignment and spacing\n" +
        "  - Works across run boundaries (formatting can span matches)\n\n" +
        "PATH SELECTORS (scope of search):\n" +
        "  /body                         — entire document body\n" +
        "  /body/paragraph[0]            — first paragraph only\n" +
        "  /body/paragraph[-1]           — last paragraph only\n" +
        "  /body/paragraph[id='ABC123']  — specific paragraph by ID (PREFERRED)\n" +
        "  /body/table[id='DEF456']      — all text within a table\n" +
        "  /body/table[0]/row[1]/cell[2] — specific cell\n" +
        "  /body/paragraph[*]            — all paragraphs\n" +
        "  /body/heading[*]              — all headings\n\n" +
        "SEARCH BEHAVIOR:\n" +
        "  - Case-sensitive exact match (\"Hello\" won't match \"hello\")\n" +
        "  - Searches across formatting runs (finds \"bold text\" even if split)\n" +
        "  - Processes matches in document order (first occurrence first)\n\n" +
        "MAX_COUNT PARAMETER:\n" +
        "  0 = Count only (no replacements made, useful for finding occurrences)\n" +
        "  1 = Replace first occurrence only (DEFAULT—prevents unexpected bulk changes)\n" +
        "  N = Replace up to N occurrences\n" +
        "  Large number (e.g., 9999) = Replace all occurrences\n\n" +
        "COMMON USE CASES:\n" +
        "  1. Update a specific value (replace first only):\n" +
        "     path: /body, find: \"[DATE]\", replace: \"January 15, 2026\", max_count: 1\n\n" +
        "  2. Replace all placeholders:\n" +
        "     path: /body, find: \"{{name}}\", replace: \"John Smith\", max_count: 9999\n\n" +
        "  3. Fix typo everywhere:\n" +
        "     path: /body, find: \"teh\", replace: \"the\", max_count: 9999\n\n" +
        "  4. Count occurrences without changing:\n" +
        "     path: /body, find: \"TODO\", replace: \"TODO\", max_count: 0\n" +
        "     (returns matches_found count)\n\n" +
        "  5. Update text in specific paragraph:\n" +
        "     path: /body/paragraph[id='ABC123'], find: \"old\", replace: \"new\"\n\n" +
        "RESPONSE FORMAT:\n" +
        "  {\"success\": true, \"applied\": 1, \"total\": 1,\n" +
        "   \"operations\": [{\"op\": \"replace_text\", \"path\": \"...\", \"status\": \"success\",\n" +
        "                   \"matches_found\": 5, \"replacements_made\": 2}]}\n\n" +
        "  matches_found: Total occurrences of 'find' text in the scope\n" +
        "  replacements_made: How many were actually replaced (limited by max_count)\n\n" +
        "IMPORTANT NOTES:\n" +
        "  - Replacement text CANNOT be empty (use remove_element to delete content)\n" +
        "  - Default max_count is 1 to prevent accidental bulk changes\n" +
        "  - Use dry_run=true to see matches_found before committing\n\n" +
        "BEST PRACTICES:\n" +
        "  1. Use dry_run=true first to see how many matches exist\n" +
        "  2. Keep max_count=1 unless you specifically need bulk replacement\n" +
        "  3. Target specific elements with IDs for precise control\n" +
        "  4. For structural changes (add/remove paragraphs), use other tools")]
    public static string ReplaceText(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to element(s) to search in.")] string path,
        [Description("Text to find (case-sensitive).")] string find,
        [Description("Replacement text (cannot be empty).")] string replace,
        [Description("Maximum number of replacements (default: 1, 0 = none).")] int max_count = 1,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patches = new[] { new ReplaceTextPatchInput { Path = path, Find = find, Replace = replace, MaxCount = max_count } };
        var patchJson = JsonSerializer.Serialize(patches, DocxJsonContext.Default.ReplaceTextPatchInputArray);
        return PatchTool.ApplyPatch(sessions, externalChangeTracker, doc_id, patchJson, dry_run);
    }
}

[McpServerToolType]
public sealed class TableTools
{
    [McpServerTool(Name = "remove_table_column"), Description(
        "Remove an entire column from a table by column index.\n\n" +
        "This removes the cell at the specified column index from EVERY row,\n" +
        "including header rows. The table structure is preserved.\n\n" +
        "PATH SELECTORS (which table to modify):\n" +
        "  /body/table[0]               — first table by index\n" +
        "  /body/table[-1]              — last table in document\n" +
        "  /body/table[id='1A2B3C4D']   — PREFERRED, table by stable ID\n" +
        "  /body/table[*]               — ALL tables (removes column from each)\n\n" +
        "COLUMN INDEX:\n" +
        "  0-based: first column = 0, second column = 1, etc.\n" +
        "  To remove the last column, first query the table to count columns.\n\n" +
        "BEHAVIOR:\n" +
        "  - Removes the cell at position 'column' from every row\n" +
        "  - Header rows are affected (column header is removed too)\n" +
        "  - Remaining columns shift left to fill the gap\n" +
        "  - Table width may adjust automatically\n\n" +
        "COMMON USE CASES:\n" +
        "  1. Remove middle column from a table:\n" +
        "     path: /body/table[id='ABC123'], column: 1  — removes 2nd column\n\n" +
        "  2. Remove first column:\n" +
        "     path: /body/table[id='ABC123'], column: 0\n\n" +
        "  3. Remove column from all tables:\n" +
        "     path: /body/table[*], column: 2  — removes 3rd column from every table\n\n" +
        "RESPONSE FORMAT:\n" +
        "  {\"success\": true, \"applied\": 1, \"total\": 1,\n" +
        "   \"operations\": [{\"op\": \"remove_column\", \"path\": \"...\", \"status\": \"success\",\n" +
        "                   \"column_index\": 1, \"rows_affected\": 5}]}\n\n" +
        "  column_index: The index of the column that was removed\n" +
        "  rows_affected: Number of rows that had cells removed (includes headers)\n\n" +
        "BEST PRACTICES:\n" +
        "  1. Use table ID instead of index for reliability\n" +
        "  2. Query the table first to verify column count and content\n" +
        "  3. Use dry_run=true to see rows_affected before committing\n" +
        "  4. For removing specific cells only, use remove_element on individual cells")]
    public static string RemoveTableColumn(
        SessionManager sessions,
        ExternalChangeTracker? externalChangeTracker,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Path to the table.")] string path,
        [Description("Column index to remove (0-based).")] int column,
        [Description("If true, simulates the operation without applying changes.")] bool dry_run = false)
    {
        var patchJson = $$"""[{"op": "remove_column", "path": "{{path.Replace("\\", "\\\\").Replace("\"", "\\\"")}}", "column": {{column}}}]""";
        return PatchTool.ApplyPatch(sessions, externalChangeTracker, doc_id, patchJson, dry_run);
    }
}
