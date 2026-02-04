using System.Text.Json;
using DocxMcp;
using DocxMcp.Cli;
using DocxMcp.Diff;
using DocxMcp.ExternalChanges;
using DocxMcp.Persistence;
using DocxMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;

// --- Bootstrap ---
var sessionsDir = Environment.GetEnvironmentVariable("DOCX_SESSIONS_DIR");

var store = new SessionStore(NullLogger<SessionStore>.Instance, sessionsDir);
var sessions = new SessionManager(store, NullLogger<SessionManager>.Instance);
var externalTracker = new ExternalChangeTracker(sessions, NullLogger<ExternalChangeTracker>.Instance);
sessions.SetExternalChangeTracker(externalTracker);
sessions.RestoreSessions();

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

// Helper to resolve doc_id or path to session ID
string ResolveDocId(string idOrPath)
{
    var session = sessions.ResolveSession(idOrPath);
    return session.Id;
}

try
{
    var result = command switch
    {
        "open" => CmdOpen(args),
        "list" => DocumentTools.DocumentList(sessions),
        "close" => DocumentTools.DocumentClose(sessions, null, ResolveDocId(Require(args, 1, "doc_id_or_path"))),
        "save" => DocumentTools.DocumentSave(sessions, null, ResolveDocId(Require(args, 1, "doc_id_or_path")), GetNonFlagArg(args, 2)),
        "snapshot" => DocumentTools.DocumentSnapshot(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            HasFlag(args, "--discard-redo")),
        "query" => QueryTool.Query(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")), Require(args, 2, "path"),
            OptNamed(args, "--format") ?? "json",
            ParseIntOpt(OptNamed(args, "--offset")),
            ParseIntOpt(OptNamed(args, "--limit"))),
        "count" => CountTool.CountElements(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")), Require(args, 2, "path")),

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
        "undo" => HistoryTools.DocumentUndo(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            ParseInt(GetNonFlagArg(args, 2), 1)),
        "redo" => HistoryTools.DocumentRedo(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            ParseInt(GetNonFlagArg(args, 2), 1)),
        "history" => HistoryTools.DocumentHistory(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            ParseInt(OptNamed(args, "--offset"), 0),
            ParseInt(OptNamed(args, "--limit"), 20)),
        "jump-to" => HistoryTools.DocumentJumpTo(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            int.Parse(Require(args, 2, "position"))),

        // Comment commands
        "comment-add" => CmdCommentAdd(args),
        "comment-list" => CmdCommentList(args),
        "comment-delete" => CmdCommentDelete(args),

        // Export commands
        "export-html" => ExportTools.ExportHtml(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            Require(args, 2, "output_path")),
        "export-markdown" => ExportTools.ExportMarkdown(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            Require(args, 2, "output_path")),
        "export-pdf" => ExportTools.ExportPdf(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            Require(args, 2, "output_path")).GetAwaiter().GetResult(),

        // Read commands
        "read-section" => CmdReadSection(args),
        "read-heading" => CmdReadHeading(args),

        // Revision (Track Changes) commands
        "revision-list" => CmdRevisionList(args),
        "revision-accept" => RevisionTools.RevisionAccept(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            int.Parse(Require(args, 2, "revision_id"))),
        "revision-reject" => RevisionTools.RevisionReject(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            int.Parse(Require(args, 2, "revision_id"))),
        "track-changes-enable" => RevisionTools.TrackChangesEnable(sessions, ResolveDocId(Require(args, 1, "doc_id_or_path")),
            ParseBool(Require(args, 2, "enabled"))),

        // Diff commands
        "diff" => CmdDiff(args),
        "diff-files" => CmdDiffFiles(args),

        // External change commands
        "check-external" => CmdCheckExternal(args),
        "sync-external" => CmdSyncExternal(args),
        "watch" => CmdWatch(args),

        // Session inspection
        "inspect" => CmdInspect(args),

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
    var path = GetNonFlagArg(a, 1);
    return DocumentTools.DocumentOpen(sessions, null, path);
}

string CmdPatch(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var dryRun = HasFlag(a, "--dry-run");
    // patches can be arg[2] or read from stdin
    var patches = GetNonFlagArg(a, 2) ?? ReadStdin();
    return PatchTool.ApplyPatch(sessions, null, docId, patches, dryRun);
}

string CmdAdd(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var path = Require(a, 2, "path");
    var value = GetNonFlagArg(a, 3) ?? ReadStdin();
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.AddElement(sessions, null, docId, path, value, dryRun);
}

string CmdReplace(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var path = Require(a, 2, "path");
    var value = GetNonFlagArg(a, 3) ?? ReadStdin();
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.ReplaceElement(sessions, null, docId, path, value, dryRun);
}

string CmdRemove(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var path = Require(a, 2, "path");
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.RemoveElement(sessions, null, docId, path, dryRun);
}

string CmdMove(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var from = Require(a, 2, "from");
    var to = Require(a, 3, "to");
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.MoveElement(sessions, null, docId, from, to, dryRun);
}

string CmdCopy(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var from = Require(a, 2, "from");
    var to = Require(a, 3, "to");
    var dryRun = HasFlag(a, "--dry-run");
    return ElementTools.CopyElement(sessions, null, docId, from, to, dryRun);
}

string CmdReplaceText(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var path = Require(a, 2, "path");
    var find = Require(a, 3, "find");
    var replace = Require(a, 4, "replace");
    var maxCount = ParseInt(OptNamed(a, "--max-count"), 1);
    var dryRun = HasFlag(a, "--dry-run");
    return TextTools.ReplaceText(sessions, null, docId, path, find, replace, maxCount, dryRun);
}

string CmdRemoveColumn(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var path = Require(a, 2, "path");
    var column = int.Parse(Require(a, 3, "column"));
    var dryRun = HasFlag(a, "--dry-run");
    return TableTools.RemoveTableColumn(sessions, null, docId, path, column, dryRun);
}

string CmdStyleElement(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var style = Require(a, 2, "style");
    var path = OptNamed(a, "--path") ?? GetNonFlagArg(a, 3);
    return StyleTools.StyleElement(sessions, docId, style, path);
}

string CmdStyleParagraph(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var style = Require(a, 2, "style");
    var path = OptNamed(a, "--path") ?? GetNonFlagArg(a, 3);
    return StyleTools.StyleParagraph(sessions, docId, style, path);
}

string CmdStyleTable(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var style = OptNamed(a, "--style");
    var cellStyle = OptNamed(a, "--cell-style");
    var rowStyle = OptNamed(a, "--row-style");
    var path = OptNamed(a, "--path");
    return StyleTools.StyleTable(sessions, docId, style, cellStyle, rowStyle, path);
}

string CmdCommentAdd(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var path = Require(a, 2, "path");
    var text = Require(a, 3, "text");
    var anchorText = OptNamed(a, "--anchor-text");
    var author = OptNamed(a, "--author");
    var initials = OptNamed(a, "--initials");
    return CommentTools.CommentAdd(sessions, docId, path, text, anchorText, author, initials);
}

string CmdCommentList(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var author = OptNamed(a, "--author");
    var offset = ParseIntOpt(OptNamed(a, "--offset"));
    var limit = ParseIntOpt(OptNamed(a, "--limit"));
    return CommentTools.CommentList(sessions, docId, author, offset, limit);
}

string CmdCommentDelete(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var commentId = ParseIntOpt(OptNamed(a, "--id"));
    var author = OptNamed(a, "--author");
    return CommentTools.CommentDelete(sessions, docId, commentId, author);
}

string CmdReadSection(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var sectionIndex = ParseIntOpt(OptNamed(a, "--index"));
    var format = OptNamed(a, "--format");
    var offset = ParseIntOpt(OptNamed(a, "--offset"));
    var limit = ParseIntOpt(OptNamed(a, "--limit"));
    return ReadSectionTool.ReadSection(sessions, docId, sectionIndex, format, offset, limit);
}

string CmdReadHeading(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
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

string CmdRevisionList(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var author = OptNamed(a, "--author");
    var type = OptNamed(a, "--type");
    var offset = ParseIntOpt(OptNamed(a, "--offset"));
    var limit = ParseIntOpt(OptNamed(a, "--limit"));
    return RevisionTools.RevisionList(sessions, docId, author, type, offset, limit);
}

string CmdDiff(string[] a)
{
    // diff <doc_id_or_path> [file_path] - compare session with file (default: source file)
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var filePath = GetNonFlagArg(a, 2);
    var threshold = ParseDouble(OptNamed(a, "--threshold"), DiffEngine.DefaultSimilarityThreshold);
    var format = OptNamed(a, "--format") ?? "text";

    var session = sessions.Get(docId);
    var targetPath = filePath ?? session.SourcePath
        ?? throw new ArgumentException("No file path specified and session has no source file.");

    if (!File.Exists(targetPath))
        throw new ArgumentException($"File not found: {targetPath}");

    var diff = DiffEngine.CompareSessionWithFile(session, targetPath, threshold);
    return FormatDiffResult(diff, format, $"Session '{docId}'", targetPath);
}

string CmdDiffFiles(string[] a)
{
    // diff-files <file1> <file2> - compare two files on disk
    var file1 = Require(a, 1, "file1");
    var file2 = Require(a, 2, "file2");
    var threshold = ParseDouble(OptNamed(a, "--threshold"), DiffEngine.DefaultSimilarityThreshold);
    var format = OptNamed(a, "--format") ?? "text";

    if (!File.Exists(file1))
        throw new ArgumentException($"File not found: {file1}");
    if (!File.Exists(file2))
        throw new ArgumentException($"File not found: {file2}");

    var diff = DiffEngine.Compare(file1, file2, threshold);
    return FormatDiffResult(diff, format, file1, file2);
}

string FormatDiffResult(DiffResult diff, string format, string original, string modified)
{
    if (format == "json")
        return diff.ToJson();

    if (format == "patch")
    {
        var patches = diff.ToPatches();
        var arr = new System.Text.Json.Nodes.JsonArray(patches.Select(p => (System.Text.Json.Nodes.JsonNode?)p).ToArray());
        return arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // Text format
    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Diff: {original} → {modified}");
    sb.AppendLine(new string('=', 60));

    if (!diff.HasAnyChanges)
    {
        sb.AppendLine("No changes detected.");
        return sb.ToString();
    }

    if (diff.Changes.Count > 0)
    {
        sb.AppendLine($"Body changes: {diff.Changes.Count}");
        sb.AppendLine($"  Removed: {diff.Changes.Count(c => c.ChangeType == ChangeType.Removed)}");
        sb.AppendLine($"  Added: {diff.Changes.Count(c => c.ChangeType == ChangeType.Added)}");
        sb.AppendLine($"  Modified: {diff.Changes.Count(c => c.ChangeType == ChangeType.Modified)}");
        sb.AppendLine($"  Moved: {diff.Changes.Count(c => c.ChangeType == ChangeType.Moved)}");
        sb.AppendLine();

        foreach (var change in diff.Changes)
        {
            var symbol = change.ChangeType switch
            {
                ChangeType.Removed => "[-]",
                ChangeType.Added => "[+]",
                ChangeType.Modified => "[~]",
                ChangeType.Moved => "[>]",
                _ => "[?]"
            };

            sb.AppendLine($"{symbol} {change.ChangeType}: {change.ElementType}");

            if (change.OldIndex.HasValue)
                sb.AppendLine($"    Old index: {change.OldIndex}");
            if (change.NewIndex.HasValue)
                sb.AppendLine($"    New index: {change.NewIndex}");

            if (!string.IsNullOrEmpty(change.OldText))
            {
                var oldText = change.OldText.Length > 80
                    ? change.OldText[..77] + "..."
                    : change.OldText;
                sb.AppendLine($"    Old: \"{oldText.Replace("\n", "\\n")}\"");
            }

            if (!string.IsNullOrEmpty(change.NewText))
            {
                var newText = change.NewText.Length > 80
                    ? change.NewText[..77] + "..."
                    : change.NewText;
                sb.AppendLine($"    New: \"{newText.Replace("\n", "\\n")}\"");
            }

            sb.AppendLine();
        }
    }

    if (diff.UncoveredChanges.Count > 0)
    {
        sb.AppendLine($"Uncovered changes: {diff.UncoveredChanges.Count}");
        foreach (var uc in diff.UncoveredChanges)
        {
            sb.AppendLine($"  [{uc.ChangeKind}] {uc.Type}: {uc.Description}");
            if (uc.PartUri is not null)
                sb.AppendLine($"         Part: {uc.PartUri}");
        }
        sb.AppendLine();
    }

    return sb.ToString();
}

string CmdCheckExternal(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var acknowledge = HasFlag(a, "--acknowledge");

    // Check for pending changes first, then check for new changes
    var pending = externalTracker.GetLatestUnacknowledgedChange(docId);
    if (pending is null)
    {
        pending = externalTracker.CheckForChanges(docId);
    }

    if (pending is null)
    {
        return "No external changes detected. The document is in sync with the source file.";
    }

    // Acknowledge if requested
    if (acknowledge)
    {
        externalTracker.AcknowledgeChange(docId, pending.Id);
    }

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"External changes detected in '{Path.GetFileName(pending.SourcePath)}'");
    sb.AppendLine($"Detected at: {pending.DetectedAt:yyyy-MM-dd HH:mm:ss UTC}");
    sb.AppendLine();
    sb.AppendLine($"Summary: +{pending.Summary.Added} -{pending.Summary.Removed} ~{pending.Summary.Modified}");
    sb.AppendLine();
    sb.AppendLine($"Change ID: {pending.Id}");
    sb.AppendLine($"Source: {pending.SourcePath}");
    sb.AppendLine($"Status: {(pending.Acknowledged || acknowledge ? "Acknowledged" : "Pending")}");

    if (!pending.Acknowledged && !acknowledge)
    {
        sb.AppendLine();
        sb.AppendLine("Use --acknowledge to acknowledge, or use 'sync-external' to sync.");
    }

    return sb.ToString();
}

string CmdSyncExternal(string[] a)
{
    var docId = ResolveDocId(Require(a, 1, "doc_id_or_path"));
    var changeId = OptNamed(a, "--change-id");

    var result = externalTracker.SyncExternalChanges(docId, changeId);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine(result.Message);

    if (result.Success && result.HasChanges)
    {
        sb.AppendLine();
        sb.AppendLine($"WAL Position: {result.WalPosition}");

        if (result.Summary is not null)
        {
            sb.AppendLine();
            sb.AppendLine("Body Changes:");
            sb.AppendLine($"  Added: {result.Summary.Added}");
            sb.AppendLine($"  Removed: {result.Summary.Removed}");
            sb.AppendLine($"  Modified: {result.Summary.Modified}");
        }

        if (result.UncoveredChanges?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Uncovered Changes ({result.UncoveredChanges.Count}):");
            foreach (var uc in result.UncoveredChanges.Take(10))
            {
                sb.AppendLine($"  [{uc.ChangeKind}] {uc.Type}: {uc.Description}");
            }
            if (result.UncoveredChanges.Count > 10)
            {
                sb.AppendLine($"  ... and {result.UncoveredChanges.Count - 10} more");
            }
        }
    }

    return sb.ToString();
}

string CmdWatch(string[] a)
{
    var path = Require(a, 1, "path");
    var autoSync = HasFlag(a, "--auto-sync");
    var debounceMs = ParseInt(OptNamed(a, "--debounce"), 500);
    var pattern = OptNamed(a, "--pattern") ?? "*.docx";
    var recursive = HasFlag(a, "--recursive");

    using var daemon = new WatchDaemon(sessions, externalTracker, debounceMs, autoSync);

    var fullPath = Path.GetFullPath(path);
    if (File.Exists(fullPath))
    {
        // Watch a single file
        var sessionId = FindOrCreateSession(fullPath);
        daemon.WatchFile(sessionId, fullPath);
    }
    else if (Directory.Exists(fullPath))
    {
        // Watch a folder
        daemon.WatchFolder(fullPath, pattern, recursive);
    }
    else
    {
        return $"Path not found: {fullPath}";
    }

    // Handle Ctrl+C
    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    try
    {
        daemon.RunAsync(cts.Token).GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
        // Expected on Ctrl+C
    }

    return "[DAEMON] Stopped.";
}

string CmdInspect(string[] a)
{
    var idOrPath = Require(a, 1, "doc_id_or_path");
    var session = sessions.ResolveSession(idOrPath);
    var history = sessions.GetHistory(session.Id);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine($"Session: {session.Id}");
    sb.AppendLine($"  Source Path: {session.SourcePath ?? "(none)"}");

    if (session.SourcePath is not null)
    {
        var sourceExists = File.Exists(session.SourcePath);
        sb.AppendLine($"  Source Exists: {(sourceExists ? "Yes" : "No")}");
        if (sourceExists)
        {
            var fileInfo = new FileInfo(session.SourcePath);
            sb.AppendLine($"  Source Modified: {fileInfo.LastWriteTimeUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"  Source Size: {fileInfo.Length:N0} bytes");
        }
    }

    sb.AppendLine();
    sb.AppendLine("WAL Status:");
    sb.AppendLine($"  Total Entries: {history.TotalEntries}");
    sb.AppendLine($"  Current Position: {history.CursorPosition}");
    sb.AppendLine($"  Can Undo: {(history.CanUndo ? $"Yes ({history.CursorPosition} steps)" : "No")}");
    sb.AppendLine($"  Can Redo: {(history.CanRedo ? $"Yes ({history.TotalEntries - 1 - history.CursorPosition} steps)" : "No")}");

    // Find last external sync
    var lastSync = history.Entries
        .Where(e => e.IsExternalSync)
        .OrderByDescending(e => e.Position)
        .FirstOrDefault();

    if (lastSync is not null)
    {
        sb.AppendLine();
        sb.AppendLine("Last External Sync:");
        sb.AppendLine($"  Position: {lastSync.Position}");
        sb.AppendLine($"  Timestamp: {lastSync.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
        if (lastSync.SyncSummary is not null)
        {
            sb.AppendLine($"  Changes: +{lastSync.SyncSummary.Added} -{lastSync.SyncSummary.Removed} ~{lastSync.SyncSummary.Modified}");
            if (lastSync.SyncSummary.UncoveredCount > 0)
            {
                sb.AppendLine($"  Uncovered: {lastSync.SyncSummary.UncoveredCount} ({string.Join(", ", lastSync.SyncSummary.UncoveredTypes)})");
            }
        }
    }

    // Check for pending external changes
    var pending = externalTracker.GetLatestUnacknowledgedChange(session.Id);
    if (pending is not null)
    {
        sb.AppendLine();
        sb.AppendLine("Pending External Change:");
        sb.AppendLine($"  Change ID: {pending.Id}");
        sb.AppendLine($"  Detected: {pending.DetectedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"  Summary: +{pending.Summary.Added} -{pending.Summary.Removed} ~{pending.Summary.Modified}");
    }

    return sb.ToString();
}

string FindOrCreateSession(string filePath)
{
    // Check if session already exists for this file
    foreach (var (id, sessPath) in sessions.List())
    {
        if (sessPath is not null && Path.GetFullPath(sessPath) == Path.GetFullPath(filePath))
        {
            return id;
        }
    }

    // Create new session (use EnsureTracked instead of StartWatching
    // to avoid creating an FSW that competes with the WatchDaemon)
    var session = sessions.Open(filePath);
    externalTracker.EnsureTracked(session.Id);
    Console.WriteLine($"[SESSION] Created session {session.Id} for {Path.GetFileName(filePath)}");
    return session.Id;
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

static bool ParseBool(string s) =>
    s.ToLowerInvariant() is "true" or "1" or "yes" or "on";

static double ParseDouble(string? s, double def) =>
    s is not null && double.TryParse(s, out var v) ? v : def;

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

    Note: Most commands accept either a session ID or a file path.
          When using a file path, an existing session is reused if one exists,
          otherwise a new session is auto-opened.

    Document commands:
      open [path]                          Open file or create new document
      list                                 List open sessions
      save <doc_id|path> [output_path]     Save document to disk
      inspect <doc_id|path>                Show detailed session information

    Administrative commands (CLI-only, not exposed to MCP):
      close <doc_id|path>                  Close session and delete all persisted data
      snapshot <doc_id|path> [--discard-redo]   Force WAL compaction into new baseline

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

    Revision (Track Changes) commands:
      revision-list <doc_id> [--author name] [--type type] [--offset N] [--limit N]
      revision-accept <doc_id> <revision_id>     Accept a single revision by ID
      revision-reject <doc_id> <revision_id>     Reject a single revision by ID
      track-changes-enable <doc_id> <true|false> Enable/disable Track Changes

    Export commands:
      export-html <doc_id> <output_path>
      export-markdown <doc_id> <output_path>
      export-pdf <doc_id> <output_path>

    Diff commands:
      diff <doc_id> [file_path] [--threshold 0.6] [--format text|json|patch]
                                 Compare session with file (default: source file)
      diff-files <file1> <file2> [--threshold 0.6] [--format text|json|patch]
                                 Compare two DOCX files on disk

    External change commands:
      check-external <doc_id|path> [--acknowledge]
                                 Check for external changes and optionally acknowledge
      sync-external <doc_id|path> [--change-id id]
                                 Sync session with external file (records in WAL)
      watch <path> [--auto-sync] [--debounce ms] [--pattern *.docx] [--recursive]
                                 Watch file or folder for changes (daemon mode)

    Options:
      --dry-run    Simulate operation without applying changes

    Environment:
      DOCX_SESSIONS_DIR            Override sessions directory (shared with MCP server)
      DOCX_WAL_COMPACT_THRESHOLD   Auto-compact WAL after N entries (default: 50)
      DOCX_CHECKPOINT_INTERVAL     Create checkpoint every N entries (default: 10)
      DOCX_AUTO_SAVE               Auto-save to source file after each edit (default: true)
      DEBUG                            Enable debug logging for sync operations

    Sessions persist between invocations and are shared with the MCP server.
    WAL history is preserved automatically; use 'close' to permanently delete a session.
    """);
}
