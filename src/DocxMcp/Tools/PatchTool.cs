using System.ComponentModel;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.ExternalChanges;
using DocxMcp.Helpers;
using DocxMcp.Models;
using DocxMcp.Paths;
using static DocxMcp.Helpers.ElementIdManager;

namespace DocxMcp.Tools;

/// <summary>
/// Internal patch engine used by element tools.
/// Not exposed as an MCP tool - use individual element tools instead.
/// </summary>
public sealed class PatchTool
{
    /// <summary>
    /// Apply JSON patches to a document. Used internally by element tools and CLI.
    /// </summary>
    public static string ApplyPatch(
        TenantScope tenant,
        SyncManager sync,
        ExternalChangeGate gate,
        [Description("Session ID of the document.")] string doc_id,
        [Description("JSON array of patch operations (max 10 per call).")] string patches,
        [Description("If true, simulates operations without applying changes.")] bool dry_run = false)
    {
        // Check for pending external changes — block edits until acknowledged
        if (!dry_run && gate.HasPendingChanges(tenant.TenantId, doc_id))
        {
            return new PatchResult
            {
                Success = false,
                Error = "External changes detected. " +
                        "Call get_external_changes to review and acknowledge before editing, " +
                        "or use sync_external_changes to reload the document."
            }.ToJson();
        }

        var sessions = tenant.Sessions;
        var session = sessions.Get(doc_id);
        var wpDoc = session.Document;
        var mainPart = wpDoc.MainDocumentPart
            ?? throw new InvalidOperationException("Document has no MainDocumentPart.");

        JsonElement patchArray;
        try
        {
            patchArray = JsonDocument.Parse(patches).RootElement;
        }
        catch (JsonException ex)
        {
            return new PatchResult
            {
                Success = false,
                Error = $"Invalid JSON — {ex.Message}"
            }.ToJson();
        }

        if (patchArray.ValueKind != JsonValueKind.Array)
        {
            return new PatchResult
            {
                Success = false,
                Error = "patches must be a JSON array."
            }.ToJson();
        }

        var patchCount = patchArray.GetArrayLength();
        if (patchCount > 10)
        {
            return new PatchResult
            {
                Success = false,
                Total = patchCount,
                Error = $"Too many operations ({patchCount}). Maximum is 10 per call. Split into multiple calls."
            }.ToJson();
        }

        var result = new PatchResult
        {
            DryRun = dry_run,
            Total = patchCount
        };

        var succeededPatches = new List<string>();

        foreach (var patchElement in patchArray.EnumerateArray())
        {
            PatchOperationResult opResult;
            PatchOperation? operation = null;

            try
            {
                // Deserialize to typed operation
                operation = JsonSerializer.Deserialize(patchElement.GetRawText(), DocxJsonContext.Default.PatchOperation);
                if (operation is null)
                    throw new ArgumentException("Failed to parse patch operation.");

                // Validate the operation
                operation.Validate();

                // Execute based on type
                opResult = operation switch
                {
                    AddPatchOperation add => ExecuteAdd(add, wpDoc, mainPart, dry_run),
                    ReplacePatchOperation replace => ExecuteReplace(replace, wpDoc, mainPart, dry_run),
                    RemovePatchOperation remove => ExecuteRemove(remove, wpDoc, dry_run),
                    MovePatchOperation move => ExecuteMove(move, wpDoc, dry_run),
                    CopyPatchOperation copy => ExecuteCopy(copy, wpDoc, dry_run),
                    ReplaceTextPatchOperation replaceText => ExecuteReplaceText(replaceText, wpDoc, dry_run),
                    RemoveColumnPatchOperation removeColumn => ExecuteRemoveColumn(removeColumn, wpDoc, dry_run),
                    _ => throw new ArgumentException($"Unknown operation type: {operation.GetType().Name}")
                };

                if (opResult.Status is "success" or "would_succeed")
                {
                    if (!dry_run)
                    {
                        succeededPatches.Add(patchElement.GetRawText());
                        result.Applied++;
                    }
                    else
                    {
                        result.WouldApply++;
                    }
                }
            }
            catch (JsonException ex)
            {
                var pathStr = patchElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var opStr = patchElement.TryGetProperty("op", out var o) ? o.GetString() ?? "unknown" : "unknown";
                opResult = CreateErrorResult(opStr, pathStr, $"Invalid patch format: {ex.Message}", dry_run);
            }
            catch (Exception ex)
            {
                var pathStr = operation?.Path ?? (patchElement.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "");
                var opStr = GetOpString(operation, patchElement);
                opResult = CreateErrorResult(opStr, pathStr, ex.Message, dry_run);
            }

            result.Operations.Add(opResult);
        }

        // Append only successful patches to WAL for replay fidelity
        if (!dry_run && succeededPatches.Count > 0)
        {
            try
            {
                var walPatches = $"[{string.Join(",", succeededPatches)}]";
                sessions.AppendWal(doc_id, walPatches);
                sync.MaybeAutoSave(tenant.TenantId, doc_id, sessions.Get(doc_id).ToBytes());
            }
            catch { /* persistence is best-effort */ }
        }

        result.Success = dry_run
            ? result.Operations.All(o => o.Status is "would_succeed")
            : result.Applied == result.Total;

        return result.ToJson();
    }

    private static string GetOpString(PatchOperation? operation, JsonElement element)
    {
        if (operation is not null)
        {
            return operation switch
            {
                AddPatchOperation => "add",
                ReplacePatchOperation => "replace",
                RemovePatchOperation => "remove",
                MovePatchOperation => "move",
                CopyPatchOperation => "copy",
                ReplaceTextPatchOperation => "replace_text",
                RemoveColumnPatchOperation => "remove_column",
                _ => "unknown"
            };
        }
        return element.TryGetProperty("op", out var o) ? o.GetString() ?? "unknown" : "unknown";
    }

    private static PatchOperationResult CreateErrorResult(string op, string path, string error, bool dryRun)
    {
        var status = dryRun ? "would_fail" : "error";
        return op switch
        {
            "add" => new AddOperationResult { Path = path, Status = status, Error = error },
            "replace" => new ReplaceOperationResult { Path = path, Status = status, Error = error },
            "remove" => new RemoveOperationResult { Path = path, Status = status, Error = error },
            "move" => new MoveOperationResult { Path = path, Status = status, Error = error },
            "copy" => new CopyOperationResult { Path = path, Status = status, Error = error },
            "replace_text" => new ReplaceTextOperationResult { Path = path, Status = status, Error = error },
            "remove_column" => new RemoveColumnOperationResult { Path = path, Status = status, Error = error },
            _ => new UnknownOperationResult { Path = path, Status = status, Error = error, UnknownOp = op }
        };
    }

    // Replay methods for WAL (kept for backwards compatibility)
    internal static void ReplayAdd(JsonElement patch, WordprocessingDocument wpDoc, MainDocumentPart mainPart)
    {
        var op = JsonSerializer.Deserialize(patch.GetRawText(), DocxJsonContext.Default.AddPatchOperation)!;
        ExecuteAdd(op, wpDoc, mainPart, false);
    }

    internal static void ReplayReplace(JsonElement patch, WordprocessingDocument wpDoc, MainDocumentPart mainPart)
    {
        var op = JsonSerializer.Deserialize(patch.GetRawText(), DocxJsonContext.Default.ReplacePatchOperation)!;
        ExecuteReplace(op, wpDoc, mainPart, false);
    }

    internal static void ReplayRemove(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var op = JsonSerializer.Deserialize(patch.GetRawText(), DocxJsonContext.Default.RemovePatchOperation)!;
        ExecuteRemove(op, wpDoc, false);
    }

    internal static void ReplayMove(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var op = JsonSerializer.Deserialize(patch.GetRawText(), DocxJsonContext.Default.MovePatchOperation)!;
        ExecuteMove(op, wpDoc, false);
    }

    internal static void ReplayCopy(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var op = JsonSerializer.Deserialize(patch.GetRawText(), DocxJsonContext.Default.CopyPatchOperation)!;
        ExecuteCopy(op, wpDoc, false);
    }

    internal static void ReplayReplaceText(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var op = JsonSerializer.Deserialize(patch.GetRawText(), DocxJsonContext.Default.ReplaceTextPatchOperation)!;
        ExecuteReplaceText(op, wpDoc, false);
    }

    internal static void ReplayRemoveColumn(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var op = JsonSerializer.Deserialize(patch.GetRawText(), DocxJsonContext.Default.RemoveColumnPatchOperation)!;
        ExecuteRemoveColumn(op, wpDoc, false);
    }

    private static AddOperationResult ExecuteAdd(AddPatchOperation op, WordprocessingDocument wpDoc,
        MainDocumentPart mainPart, bool dryRun)
    {
        var result = new AddOperationResult { Path = op.Path };
        var path = DocxPath.Parse(op.Path);

        if (dryRun)
        {
            // Validate path exists
            if (path.IsChildrenPath)
            {
                PathResolver.ResolveForInsert(path, wpDoc);
            }
            else
            {
                var parents = PathResolver.Resolve(new DocxPath(path.Segments.ToList()), wpDoc);
                if (parents.Count != 1)
                    throw new InvalidOperationException("Add path must resolve to exactly one parent.");
            }
            result.Status = "would_succeed";
            result.CreatedId = "(new)";
            return result;
        }

        OpenXmlElement? createdElement = null;
        var trackChanges = RevisionHelper.IsTrackChangesEnabled(wpDoc);

        if (path.IsChildrenPath)
        {
            var (parent, index) = PathResolver.ResolveForInsert(path, wpDoc);

            if (op.Value.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "list")
            {
                var items = ElementFactory.CreateListItems(op.Value);
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    if (trackChanges)
                        RevisionHelper.InsertElementWithTracking(wpDoc, parent, items[i], index);
                    else
                        parent.InsertChildAt(items[i], index);
                }
                createdElement = items.FirstOrDefault();
            }
            else
            {
                var element = ElementFactory.CreateFromJson(op.Value, mainPart);
                if (trackChanges)
                    RevisionHelper.InsertElementWithTracking(wpDoc, parent, element, index);
                else
                    parent.InsertChildAt(element, index);
                createdElement = element;
            }
        }
        else
        {
            var parentPath = new DocxPath(path.Segments.ToList());
            var parents = PathResolver.Resolve(parentPath, wpDoc);

            if (parents.Count != 1)
                throw new InvalidOperationException("Add path must resolve to exactly one parent.");

            var parent = parents[0];

            if (op.Value.TryGetProperty("type", out var t) && t.GetString() == "list")
            {
                var items = ElementFactory.CreateListItems(op.Value);
                foreach (var item in items)
                {
                    if (trackChanges)
                        RevisionHelper.InsertElementWithTracking(wpDoc, parent, item);
                    else
                        parent.AppendChild(item);
                }
                createdElement = items.FirstOrDefault();
            }
            else
            {
                var element = ElementFactory.CreateFromJson(op.Value, mainPart);
                if (trackChanges)
                    RevisionHelper.InsertElementWithTracking(wpDoc, parent, element);
                else
                    parent.AppendChild(element);
                createdElement = element;
            }
        }

        result.Status = "success";
        result.CreatedId = createdElement is not null ? GetId(createdElement) : null;
        return result;
    }

    private static ReplaceOperationResult ExecuteReplace(ReplacePatchOperation op, WordprocessingDocument wpDoc,
        MainDocumentPart mainPart, bool dryRun)
    {
        var result = new ReplaceOperationResult { Path = op.Path };
        var path = DocxPath.Parse(op.Path);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{op.Path}'.");

        if (dryRun)
        {
            result.Status = "would_succeed";
            result.ReplacedId = GetId(targets[0]);
            return result;
        }

        string? replacedId = null;
        var trackChanges = RevisionHelper.IsTrackChangesEnabled(wpDoc);

        if (path.Leaf is StyleSegment)
        {
            foreach (var target in targets)
            {
                if (target.Parent is OpenXmlElement parentEl)
                    replacedId ??= GetId(parentEl);

                if (target is ParagraphProperties pPr && pPr.Parent is Paragraph para)
                {
                    var newProps = ElementFactory.CreateParagraphProperties(op.Value);
                    if (trackChanges)
                        RevisionHelper.ApplyParagraphPropertiesWithTracking(wpDoc, para, newProps);
                    else
                        target.Parent?.ReplaceChild(newProps, target);
                }
                else if (target is RunProperties rPr && rPr.Parent is Run run)
                {
                    var newProps = ElementFactory.CreateRunProperties(op.Value);
                    if (trackChanges)
                        RevisionHelper.ApplyRunPropertiesWithTracking(wpDoc, run, newProps);
                    else
                        target.Parent?.ReplaceChild(newProps, target);
                }
                else if (target is TableProperties)
                {
                    // Table properties change tracking not fully supported
                    var newProps = ElementFactory.CreateTableProperties(op.Value);
                    target.Parent?.ReplaceChild(newProps, target);
                }
            }
        }
        else
        {
            foreach (var target in targets)
            {
                replacedId ??= GetId(target);

                var parent = target.Parent
                    ?? throw new InvalidOperationException("Target element has no parent.");

                var newElement = ElementFactory.CreateFromJson(op.Value, mainPart);
                if (trackChanges)
                    RevisionHelper.ReplaceElementWithTracking(wpDoc, target, newElement);
                else
                    parent.ReplaceChild(newElement, target);
            }
        }

        result.Status = "success";
        result.ReplacedId = replacedId;
        return result;
    }

    private static RemoveOperationResult ExecuteRemove(RemovePatchOperation op, WordprocessingDocument wpDoc, bool dryRun)
    {
        var result = new RemoveOperationResult { Path = op.Path };
        var path = DocxPath.Parse(op.Path);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{op.Path}'.");

        var removedId = GetId(targets[0]);

        if (dryRun)
        {
            result.Status = "would_succeed";
            result.RemovedId = removedId;
            return result;
        }

        var trackChanges = RevisionHelper.IsTrackChangesEnabled(wpDoc);

        foreach (var target in targets)
        {
            if (trackChanges)
                RevisionHelper.DeleteElementWithTracking(wpDoc, target);
            else
                target.Parent?.RemoveChild(target);
        }

        result.Status = "success";
        result.RemovedId = removedId;
        return result;
    }

    private static MoveOperationResult ExecuteMove(MovePatchOperation op, WordprocessingDocument wpDoc, bool dryRun)
    {
        var result = new MoveOperationResult { Path = op.Path, From = op.From };

        var fromPath = DocxPath.Parse(op.From);
        var sources = PathResolver.Resolve(fromPath, wpDoc);
        if (sources.Count != 1)
            throw new InvalidOperationException("Move source must resolve to exactly one element.");

        var source = sources[0];
        var movedId = GetId(source);

        if (dryRun)
        {
            // Validate destination
            var toPath = DocxPath.Parse(op.Path);
            if (toPath.IsChildrenPath)
            {
                PathResolver.ResolveForInsert(toPath, wpDoc);
            }
            else
            {
                var targets = PathResolver.Resolve(toPath, wpDoc);
                if (targets.Count != 1)
                    throw new InvalidOperationException("Move target must resolve to exactly one location.");
            }
            result.Status = "would_succeed";
            result.MovedId = movedId;
            return result;
        }

        source.Parent?.RemoveChild(source);

        var destPath = DocxPath.Parse(op.Path);
        if (destPath.IsChildrenPath)
        {
            var (parent, index) = PathResolver.ResolveForInsert(destPath, wpDoc);
            parent.InsertChildAt(source, index);
        }
        else
        {
            var targets = PathResolver.Resolve(destPath, wpDoc);
            if (targets.Count != 1)
                throw new InvalidOperationException("Move target must resolve to exactly one location.");

            var target = targets[0];
            target.Parent?.InsertAfter(source, target);
        }

        result.Status = "success";
        result.MovedId = movedId;
        return result;
    }

    private static CopyOperationResult ExecuteCopy(CopyPatchOperation op, WordprocessingDocument wpDoc, bool dryRun)
    {
        var result = new CopyOperationResult { Path = op.Path };

        var fromPath = DocxPath.Parse(op.From);
        var sources = PathResolver.Resolve(fromPath, wpDoc);
        if (sources.Count != 1)
            throw new InvalidOperationException("Copy source must resolve to exactly one element.");

        var sourceId = GetId(sources[0]);

        if (dryRun)
        {
            // Validate destination
            var toPath = DocxPath.Parse(op.Path);
            if (toPath.IsChildrenPath)
            {
                PathResolver.ResolveForInsert(toPath, wpDoc);
            }
            else
            {
                var targets = PathResolver.Resolve(toPath, wpDoc);
                if (targets.Count != 1)
                    throw new InvalidOperationException("Copy target must resolve to exactly one location.");
            }
            result.Status = "would_succeed";
            result.SourceId = sourceId;
            result.CopyId = "(new)";
            return result;
        }

        var clone = sources[0].CloneNode(true);

        var destPath = DocxPath.Parse(op.Path);
        if (destPath.IsChildrenPath)
        {
            var (parent, index) = PathResolver.ResolveForInsert(destPath, wpDoc);
            parent.InsertChildAt(clone, index);
        }
        else
        {
            var targets = PathResolver.Resolve(destPath, wpDoc);
            if (targets.Count != 1)
                throw new InvalidOperationException("Copy target must resolve to exactly one location.");

            var target = targets[0];
            target.Parent?.InsertAfter(clone, target);
        }

        result.Status = "success";
        result.SourceId = sourceId;
        result.CopyId = GetId(clone);
        return result;
    }

    /// <summary>
    /// Find and replace text within runs, preserving all run-level formatting.
    /// </summary>
    private static ReplaceTextOperationResult ExecuteReplaceText(ReplaceTextPatchOperation op,
        WordprocessingDocument wpDoc, bool dryRun)
    {
        var result = new ReplaceTextOperationResult { Path = op.Path };

        var path = DocxPath.Parse(op.Path);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{op.Path}'.");

        // Count all matches first
        int totalMatches = 0;
        foreach (var target in targets)
        {
            totalMatches += CountTextMatches(target, op.Find);
        }

        result.MatchesFound = totalMatches;

        // Determine how many we would/will replace
        int toReplace = op.MaxCount == 0 ? 0 : Math.Min(totalMatches, op.MaxCount);

        if (dryRun)
        {
            result.Status = "would_succeed";
            result.WouldReplace = toReplace;
            return result;
        }

        // Actually perform replacements
        var trackChanges = RevisionHelper.IsTrackChangesEnabled(wpDoc);
        int replaced = 0;
        foreach (var target in targets)
        {
            if (op.MaxCount > 0 && replaced >= op.MaxCount)
                break;

            int remaining = op.MaxCount == 0 ? 0 : op.MaxCount - replaced;
            if (trackChanges)
            {
                // Track changes: count matches before, ReplaceTextWithTracking handles one match per paragraph
                int matchesBefore = CountTextMatches(target, op.Find);
                RevisionHelper.ReplaceTextWithTracking(wpDoc, target, op.Find, op.Replace);
                int matchesAfter = CountTextMatches(target, op.Find);
                replaced += matchesBefore - matchesAfter;
            }
            else
            {
                replaced += ReplaceTextInElement(target, op.Find, op.Replace, remaining);
            }
        }

        result.Status = "success";
        result.ReplacementsMade = replaced;
        return result;
    }

    /// <summary>
    /// Count occurrences of search text within an element.
    /// </summary>
    private static int CountTextMatches(OpenXmlElement element, string find)
    {
        var paragraphs = element is Paragraph p
            ? new List<Paragraph> { p }
            : element.Descendants<Paragraph>().ToList();

        int count = 0;
        foreach (var para in paragraphs)
        {
            var allText = string.Concat(para.Elements<Run>().Select(r => r.InnerText));
            int idx = 0;
            while ((idx = allText.IndexOf(find, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += find.Length;
            }
        }
        return count;
    }

    /// <summary>
    /// Replace text within an element's runs, preserving formatting.
    /// Returns the number of replacements made.
    /// </summary>
    private static int ReplaceTextInElement(OpenXmlElement element, string find, string replace, int maxCount)
    {
        if (maxCount == 0)
            return 0;

        var paragraphs = element is Paragraph p
            ? new List<Paragraph> { p }
            : element.Descendants<Paragraph>().ToList();

        int totalReplaced = 0;

        foreach (var para in paragraphs)
        {
            if (maxCount > 0 && totalReplaced >= maxCount)
                break;

            var runs = para.Elements<Run>().ToList();
            if (runs.Count == 0) continue;

            // Try simple per-run replacement first
            bool foundInRun = false;
            foreach (var run in runs)
            {
                if (maxCount > 0 && totalReplaced >= maxCount)
                    break;

                var textElem = run.GetFirstChild<Text>();
                if (textElem is null) continue;

                var text = textElem.Text;
                int idx = 0;
                while ((idx = text.IndexOf(find, idx, StringComparison.Ordinal)) >= 0)
                {
                    text = text[..idx] + replace + text[(idx + find.Length)..];
                    idx += replace.Length;
                    totalReplaced++;
                    foundInRun = true;

                    if (maxCount > 0 && totalReplaced >= maxCount)
                        break;
                }
                textElem.Text = text;
            }

            if (foundInRun) continue;

            // Cross-run replacement: concatenate all run texts, find the match,
            // then adjust the runs that contain the match
            var allText = string.Concat(runs.Select(r => r.InnerText));
            var matchIdx = allText.IndexOf(find, StringComparison.Ordinal);
            if (matchIdx < 0) continue;

            // Map character positions to runs
            int pos = 0;
            foreach (var run in runs)
            {
                if (maxCount > 0 && totalReplaced >= maxCount)
                    break;

                var textElem = run.GetFirstChild<Text>();
                if (textElem is null)
                {
                    // Tab or break: count as 1 char (\t or empty)
                    var runText = run.InnerText;
                    pos += runText.Length;
                    continue;
                }

                var runStart = pos;
                var runEnd = pos + textElem.Text.Length;

                // Check if this run overlaps with the find range
                var findEnd = matchIdx + find.Length;

                if (runEnd <= matchIdx || runStart >= findEnd)
                {
                    // No overlap
                    pos = runEnd;
                    continue;
                }

                // This run overlaps with the search text
                var overlapStart = Math.Max(matchIdx, runStart) - runStart;
                var overlapEnd = Math.Min(findEnd, runEnd) - runStart;

                var before = textElem.Text[..overlapStart];
                var after = textElem.Text[overlapEnd..];

                // First overlapping run gets the replacement text
                if (runStart <= matchIdx)
                {
                    textElem.Text = before + replace + after;
                    textElem.Space = SpaceProcessingModeValues.Preserve;
                    totalReplaced++;
                }
                else
                {
                    // Subsequent overlapping runs: remove the overlapping portion
                    textElem.Text = after;
                    textElem.Space = SpaceProcessingModeValues.Preserve;
                }

                pos = runEnd;
            }
        }

        return totalReplaced;
    }

    /// <summary>
    /// Remove a column from a table by index (0-based).
    /// </summary>
    private static RemoveColumnOperationResult ExecuteRemoveColumn(RemoveColumnPatchOperation op,
        WordprocessingDocument wpDoc, bool dryRun)
    {
        var result = new RemoveColumnOperationResult { Path = op.Path };

        var path = DocxPath.Parse(op.Path);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{op.Path}'.");

        int totalRowsAffected = 0;

        foreach (var target in targets)
        {
            if (target is not Table table)
                throw new InvalidOperationException("remove_column target must be a table.");

            var rows = table.Elements<TableRow>().ToList();
            foreach (var row in rows)
            {
                var cells = row.Elements<TableCell>().ToList();
                if (op.Column >= 0 && op.Column < cells.Count)
                {
                    if (!dryRun)
                        row.RemoveChild(cells[op.Column]);
                    totalRowsAffected++;
                }
            }

            if (!dryRun)
            {
                // Update grid columns if present
                var grid = table.GetFirstChild<TableGrid>();
                if (grid is not null)
                {
                    var gridCols = grid.Elements<GridColumn>().ToList();
                    if (op.Column >= 0 && op.Column < gridCols.Count)
                    {
                        grid.RemoveChild(gridCols[op.Column]);
                    }
                }
            }
        }

        result.Status = dryRun ? "would_succeed" : "success";
        result.ColumnIndex = op.Column;
        result.RowsAffected = totalRowsAffected;
        return result;
    }
}
