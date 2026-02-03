using DocxMcp;
using DocxMcp.Persistence;
using DocxMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

// --- Bootstrap ---
var sessionsDir = Environment.GetEnvironmentVariable("DOCX_MCP_SESSIONS_DIR")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "docx-mcp", "sessions");

var store = new SessionStore(NullLogger<SessionStore>.Instance, sessionsDir);
var sessions = new SessionManager(store, NullLogger<SessionManager>.Instance);
sessions.RestoreSessions();

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

try
{
    var result = command switch
    {
        "open" => CmdOpen(args),
        "list" => DocumentTools.DocumentList(sessions),
        "close" => DocumentTools.DocumentClose(sessions, Require(args, 1, "doc_id")),
        "save" => DocumentTools.DocumentSave(sessions, Require(args, 1, "doc_id"), Opt(args, 2)),
        "snapshot" => DocumentTools.DocumentSnapshot(sessions, Require(args, 1, "doc_id"),
            HasFlag(args, "--discard-redo")),
        "query" => QueryTool.Query(sessions, Require(args, 1, "doc_id"), Require(args, 2, "path"),
            OptNamed(args, "--format") ?? "json",
            ParseIntOpt(OptNamed(args, "--offset")),
            ParseIntOpt(OptNamed(args, "--limit"))),
        "count" => CountTool.CountElements(sessions, Require(args, 1, "doc_id"), Require(args, 2, "path")),

        // Generic patch (multi-operation)
        "patch" => CmdPatch(args),

        // Individual element operations
        "add" => CmdAdd(args),
        "replace" => CmdReplace(args),
        "remove" => CmdRemove(args),
        "move" => CmdMove(args),
        "copy" => CmdCopy(args),
        "replace-text" => CmdReplaceText(args),
        "remove-column" => CmdRemoveColumn(args),

        // Style commands
        "style-element" => CmdStyleElement(args),
        "style-paragraph" => CmdStyleParagraph(args),
        "style-table" => CmdStyleTable(args),

        // History commands
        "undo" => HistoryTools.DocumentUndo(sessions, Require(args, 1, "doc_id"),
            ParseInt(Opt(args, 2), 1)),
        "redo" => HistoryTools.DocumentRedo(sessions, Require(args, 1, "doc_id"),
            ParseInt(Opt(args, 2), 1)),
        "history" => HistoryTools.DocumentHistory(sessions, Require(args, 1, "doc_id"),
            ParseInt(OptNamed(args, "--offset"), 0),
            ParseInt(OptNamed(args, "--limit"), 20)),
        "jump-to" => HistoryTools.DocumentJumpTo(sessions, Require(args, 1, "doc_id"),
            int.Parse(Require(args, 2, "position"))),

        // Comment commands
        "comment-add" => CmdCommentAdd(args),
        "comment-list" => CmdCommentList(args),
        "comment-delete" => CmdCommentDelete(args),

        // Export commands
        "export-html" => ExportTools.ExportHtml(sessions, Require(args, 1, "doc_id"),
            Require(args, 2, "output_path")),
        "export-markdown" => ExportTools.ExportMarkdown(sessions, Require(args, 1, "doc_id"),
            Require(args, 2, "output_path")),
        "export-pdf" => ExportTools.ExportPdf(sessions, Require(args, 1, "doc_id"),
            Require(args, 2, "output_path")).GetAwaiter().GetResult(),

        // Read commands
        "read-section" => CmdReadSection(args),
        "read-heading" => CmdReadHeading(args),

        "help" or "--help" or "-h" => Usage(),
        _ => $"Unknown command: '{command}'. Run 'docx-cli help' for usage."
    };

    Console.WriteLine(result);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

// --- Command handlers for complex argument parsing ---

string CmdOpen(string[] a)
{
    var path = Opt(a, 1);
    // Skip if it looks like a flag
    if (path is not null && path.StartsWith('-')) path = null;
    return DocumentTools.DocumentOpen(sessions, path);
}

string CmdPatch(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var dryRun = HasFlag(a, "--dry-run");
    // patches can be arg[2] or read from stdin
    var patches = GetNonFlagArg(a, 2) ?? ReadStdin();
    return PatchTool.ApplyPatch(sessions, docId, patches, dryRun);
}

string CmdAdd(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var path = Require(a, 2, "path");
    var value = GetNonFlagArg(a, 3) ?? ReadStdin();
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.AddElement(sessions, docId, path, value, dryRun);
}

string CmdReplace(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var path = Require(a, 2, "path");
    var value = GetNonFlagArg(a, 3) ?? ReadStdin();
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.ReplaceElement(sessions, docId, path, value, dryRun);
}

string CmdRemove(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var path = Require(a, 2, "path");
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.RemoveElement(sessions, docId, path, dryRun);
}

string CmdMove(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var from = Require(a, 2, "from");
    var to = Require(a, 3, "to");
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.MoveElement(sessions, docId, from, to, dryRun);
}

string CmdCopy(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var from = Require(a, 2, "from");
    var to = Require(a, 3, "to");
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.CopyElement(sessions, docId, from, to, dryRun);
}

string CmdReplaceText(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var path = Require(a, 2, "path");
    var find = Require(a, 3, "find");
    var replace = Require(a, 4, "replace");
    var maxCount = ParseInt(OptNamed(a, "--max-count"), 1);
    var dryRun = HasFlag(a, "--dry-run");
    return TextTools.ReplaceText(sessions, docId, path, find, replace, maxCount, dryRun);
}

string CmdRemoveColumn(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var path = Require(a, 2, "path");
    var column = int.Parse(Require(a, 3, "column"));
    var dryRun = HasFlag(a, "--dry-run");
    return TableTools.RemoveTableColumn(sessions, docId, path, column, dryRun);
}

string CmdStyleElement(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var style = Require(a, 2, "style");
    var path = OptNamed(a, "--path") ?? GetNonFlagArg(a, 3);
    return StyleTools.StyleElement(sessions, docId, style, path);
}

string CmdStyleParagraph(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var style = Require(a, 2, "style");
    var path = OptNamed(a, "--path") ?? GetNonFlagArg(a, 3);
    return StyleTools.StyleParagraph(sessions, docId, style, path);
}

string CmdStyleTable(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var style = OptNamed(a, "--style");
    var cellStyle = OptNamed(a, "--cell-style");
    var rowStyle = OptNamed(a, "--row-style");
    var path = OptNamed(a, "--path");
    return StyleTools.StyleTable(sessions, docId, style, cellStyle, rowStyle, path);
}

string CmdCommentAdd(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var path = Require(a, 2, "path");
    var text = Require(a, 3, "text");
    var anchorText = OptNamed(a, "--anchor-text");
    var author = OptNamed(a, "--author");
    var initials = OptNamed(a, "--initials");
    return CommentTools.CommentAdd(sessions, docId, path, text, anchorText, author, initials);
}

string CmdCommentList(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var author = OptNamed(a, "--author");
    var offset = ParseIntOpt(OptNamed(a, "--offset"));
    var limit = ParseIntOpt(OptNamed(a, "--limit"));
    return CommentTools.CommentList(sessions, docId, author, offset, limit);
}

string CmdCommentDelete(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var commentId = ParseIntOpt(OptNamed(a, "--id"));
    var author = OptNamed(a, "--author");
    return CommentTools.CommentDelete(sessions, docId, commentId, author);
}

string CmdReadSection(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var sectionIndex = ParseIntOpt(OptNamed(a, "--index"));
    var format = OptNamed(a, "--format");
    var offset = ParseIntOpt(OptNamed(a, "--offset"));
    var limit = ParseIntOpt(OptNamed(a, "--limit"));
    return ReadSectionTool.ReadSection(sessions, docId, sectionIndex, format, offset, limit);
}

string CmdReadHeading(string[] a)
{
    var docId = Require(a, 1, "doc_id");
    var headingText = OptNamed(a, "--text");
    var headingIndex = ParseIntOpt(OptNamed(a, "--index"));
    var headingLevel = ParseIntOpt(OptNamed(a, "--level"));
    var includeSubHeadings = !HasFlag(a, "--no-sub-headings");
    var format = OptNamed(a, "--format");
    var offset = ParseIntOpt(OptNamed(a, "--offset"));
    var limit = ParseIntOpt(OptNamed(a, "--limit"));
    return ReadHeadingContentTool.ReadHeadingContent(sessions, docId,
        headingText, headingIndex, headingLevel, includeSubHeadings, format, offset, limit);
}

// --- Argument helpers ---

static string Require(string[] a, int idx, string name)
{
    if (idx >= a.Length)
        throw new ArgumentException($"Missing required argument: <{name}>");
    var val = a[idx];
    if (val.StartsWith('-'))
        throw new ArgumentException($"Missing required argument: <{name}> (got flag '{val}')");
    return val;
}

static string? Opt(string[] a, int idx) =>
    idx < a.Length ? a[idx] : null;

static string? GetNonFlagArg(string[] a, int idx)
{
    if (idx >= a.Length) return null;
    var val = a[idx];
    return val.StartsWith('-') ? null : val;
}

static string? OptNamed(string[] a, string flag)
{
    for (int i = 0; i < a.Length - 1; i++)
    {
        if (a[i] == flag)
            return a[i + 1];
    }
    return null;
}

static bool HasFlag(string[] a, string flag) =>
    a.Any(x => x == flag);

static int ParseInt(string? s, int def) =>
    s is not null && int.TryParse(s, out var v) ? v : def;

static int? ParseIntOpt(string? s) =>
    s is not null && int.TryParse(s, out var v) ? v : null;

static string ReadStdin()
{
    if (Console.IsInputRedirected)
        return Console.In.ReadToEnd();
    throw new ArgumentException("Missing argument. Provide inline or pipe via stdin.");
}

static string Usage()
{
    PrintUsage();
    return "";
}

static void PrintUsage()
{
    Console.Error.WriteLine("""
    docx-cli — CLI for DOCX document manipulation

    Usage: docx-cli <command> [arguments] [options]

    Document commands:
      open [path]                          Open file or create new document
      list                                 List open sessions
      save <doc_id> [output_path]          Save document to disk

    Administrative commands (CLI-only, not exposed to MCP):
      close <doc_id>                       Close session and delete all persisted data
      snapshot <doc_id> [--discard-redo]   Force WAL compaction into new baseline

    Query commands:
      query <doc_id> <path> [--format json|text|summary] [--offset N] [--limit N]
      count <doc_id> <path>
      read-section <doc_id> [--index N] [--format fmt] [--offset N] [--limit N]
      read-heading <doc_id> [--text str] [--index N] [--level N] [--format fmt]
                            [--offset N] [--limit N] [--no-sub-headings]

    Element operations (all support --dry-run):
      add <doc_id> <path> <value_json>     Add element at path
      replace <doc_id> <path> <value_json> Replace element
      remove <doc_id> <path>               Remove element
      move <doc_id> <from> <to>            Move element
      copy <doc_id> <from> <to>            Copy element
      replace-text <doc_id> <path> <find> <replace> [--max-count N]
      remove-column <doc_id> <table_path> <column_index>

    Generic patch (multi-operation):
      patch <doc_id> <patches_json> [--dry-run]

    Style commands:
      style-element <doc_id> <style_json> [path | --path path]
      style-paragraph <doc_id> <style_json> [path | --path path]
      style-table <doc_id> --style json [--cell-style json] [--row-style json] [--path path]

    History commands:
      undo <doc_id> [steps]
      redo <doc_id> [steps]
      history <doc_id> [--offset N] [--limit N]
      jump-to <doc_id> <position>

    Comment commands:
      comment-add <doc_id> <path> <text> [--anchor-text str] [--author name] [--initials str]
      comment-list <doc_id> [--author name] [--offset N] [--limit N]
      comment-delete <doc_id> [--id N] [--author name]

    Export commands:
      export-html <doc_id> <output_path>
      export-markdown <doc_id> <output_path>
      export-pdf <doc_id> <output_path>

    Options:
      --dry-run    Simulate operation without applying changes

    Environment:
      DOCX_MCP_SESSIONS_DIR            Override sessions directory (shared with MCP server)
      DOCX_MCP_WAL_COMPACT_THRESHOLD   Auto-compact WAL after N entries (default: 50)
      DOCX_MCP_CHECKPOINT_INTERVAL     Create checkpoint every N entries (default: 10)

    Sessions persist between invocations and are shared with the MCP server.
    WAL history is preserved automatically; use 'close' to permanently delete a session.
    """);
}
