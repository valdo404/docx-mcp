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

[McpServerToolType]
public sealed class PatchTool
{
    [McpServerTool(Name = "apply_patch"), Description(
        "Modify a document using JSON patches (RFC 6902 adapted for OOXML).\n" +
        "Maximum 10 operations per call. Split larger changes into multiple calls.\n" +
        "Returns structured JSON with operation results and element IDs.\n\n" +
        "Parameters:\n" +
        "  dry_run — If true, simulates operations without applying changes.\n\n" +
        "Operations:\n" +
        "  add — Insert element at path. Use /body/children/N for positional insert.\n" +
        "        Result: {created_id: \"...\"}\n" +
        "  replace — Replace element or property at path.\n" +
        "        Result: {replaced_id: \"...\"}\n" +
        "  remove — Delete element at path.\n" +
        "        Result: {removed_id: \"...\"}\n" +
        "  move — Move element from one location to another.\n" +
        "        Result: {moved_id: \"...\", from: \"...\"}\n" +
        "  copy — Duplicate element to another location.\n" +
        "        Result: {source_id: \"...\", copy_id: \"...\"}\n" +
        "  replace_text — Find/replace text preserving run-level formatting.\n" +
        "        Options: max_count (default 1, use 0 to skip, higher values for multiple)\n" +
        "        Note: 'replace' cannot be empty (use remove operation instead)\n" +
        "        Result: {matches_found: N, replacements_made: N}\n" +
        "  remove_column — Remove a column from a table by index.\n" +
        "        Result: {column_index: N, rows_affected: N}\n\n" +
        "Paths support stable element IDs (preferred over indices for existing content):\n" +
        "  /body/paragraph[id='1A2B3C4D'] — target paragraph by ID\n" +
        "  /body/table[id='5E6F7A8B']/row[id='AABB1122'] — target row by ID\n\n" +
        "Value types (for add/replace):\n" +
        "  Paragraph with runs (preserves styling):\n" +
        "    {\"type\": \"paragraph\", \"runs\": [{\"text\": \"bold\", \"style\": {\"bold\": true}}, {\"tab\": true}, {\"text\": \"normal\"}]}\n" +
        "  Heading with runs:\n" +
        "    {\"type\": \"heading\", \"level\": 2, \"runs\": [{\"text\": \"Title\"}]}\n" +
        "  Table:\n" +
        "    {\"type\": \"table\", \"headers\": [\"Col1\",\"Col2\"], \"rows\": [[\"A\",\"B\"]]}\n\n" +
        "replace_text example:\n" +
        "  {\"op\": \"replace_text\", \"path\": \"/body/paragraph[0]\", \"find\": \"old\", \"replace\": \"new\", \"max_count\": 1}\n\n" +
        "Response format:\n" +
        "  {\"success\": true, \"applied\": 2, \"total\": 2, \"operations\": [...]}")]
    public static string ApplyPatch(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("JSON array of patch operations (max 10 per call).")] string patches,
        [Description("If true, simulates operations without applying changes.")] bool dry_run = false)
    {
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

        foreach (var patch in patchArray.EnumerateArray())
        {
            var op = patch.TryGetProperty("op", out var opEl) ? opEl.GetString()?.ToLowerInvariant() : null;
            var pathStr = patch.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";

            PatchOperationResult opResult;

            try
            {
                if (op is null)
                    throw new ArgumentException("Patch must have an 'op' field.");

                opResult = op switch
                {
                    "add" => ExecuteAdd(patch, wpDoc, mainPart, pathStr, dry_run),
                    "replace" => ExecuteReplace(patch, wpDoc, mainPart, pathStr, dry_run),
                    "remove" => ExecuteRemove(patch, wpDoc, pathStr, dry_run),
                    "move" => ExecuteMove(patch, wpDoc, pathStr, dry_run),
                    "copy" => ExecuteCopy(patch, wpDoc, pathStr, dry_run),
                    "replace_text" => ExecuteReplaceText(patch, wpDoc, pathStr, dry_run),
                    "remove_column" => ExecuteRemoveColumn(patch, wpDoc, pathStr, dry_run),
                    _ => new UnknownOperationResult
                    {
                        Path = pathStr,
                        Status = dry_run ? "would_fail" : "error",
                        Error = $"Unknown operation: '{op}'",
                        UnknownOp = op
                    }
                };

                if (opResult.Status is "success" or "would_succeed")
                {
                    if (!dry_run)
                    {
                        succeededPatches.Add(patch.GetRawText());
                        result.Applied++;
                    }
                    else
                    {
                        result.WouldApply++;
                    }
                }
            }
            catch (Exception ex)
            {
                opResult = CreateErrorResult(op ?? "unknown", pathStr, ex.Message, dry_run);
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
            }
            catch { /* persistence is best-effort */ }
        }

        result.Success = dry_run
            ? result.Operations.All(o => o.Status is "would_succeed")
            : result.Applied == result.Total;

        return result.ToJson();
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
        var pathStr = patch.GetProperty("path").GetString() ?? "";
        ExecuteAdd(patch, wpDoc, mainPart, pathStr, false);
    }

    internal static void ReplayReplace(JsonElement patch, WordprocessingDocument wpDoc, MainDocumentPart mainPart)
    {
        var pathStr = patch.GetProperty("path").GetString() ?? "";
        ExecuteReplace(patch, wpDoc, mainPart, pathStr, false);
    }

    internal static void ReplayRemove(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var pathStr = patch.GetProperty("path").GetString() ?? "";
        ExecuteRemove(patch, wpDoc, pathStr, false);
    }

    internal static void ReplayMove(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var pathStr = patch.GetProperty("path").GetString() ?? "";
        ExecuteMove(patch, wpDoc, pathStr, false);
    }

    internal static void ReplayCopy(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var pathStr = patch.GetProperty("path").GetString() ?? "";
        ExecuteCopy(patch, wpDoc, pathStr, false);
    }

    internal static void ReplayReplaceText(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var pathStr = patch.GetProperty("path").GetString() ?? "";
        ExecuteReplaceText(patch, wpDoc, pathStr, false);
    }

    internal static void ReplayRemoveColumn(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var pathStr = patch.GetProperty("path").GetString() ?? "";
        ExecuteRemoveColumn(patch, wpDoc, pathStr, false);
    }

    private static AddOperationResult ExecuteAdd(JsonElement patch, WordprocessingDocument wpDoc,
        MainDocumentPart mainPart, string pathStr, bool dryRun)
    {
        var result = new AddOperationResult { Path = pathStr };

        var value = patch.GetProperty("value");
        var path = DocxPath.Parse(pathStr);

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

        if (path.IsChildrenPath)
        {
            var (parent, index) = PathResolver.ResolveForInsert(path, wpDoc);

            if (value.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "list")
            {
                var items = ElementFactory.CreateListItems(value);
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    parent.InsertChildAt(items[i], index);
                }
                createdElement = items.FirstOrDefault();
            }
            else
            {
                var element = ElementFactory.CreateFromJson(value, mainPart);
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

            if (value.TryGetProperty("type", out var t) && t.GetString() == "list")
            {
                var items = ElementFactory.CreateListItems(value);
                foreach (var item in items)
                    parent.AppendChild(item);
                createdElement = items.FirstOrDefault();
            }
            else
            {
                var element = ElementFactory.CreateFromJson(value, mainPart);
                parent.AppendChild(element);
                createdElement = element;
            }
        }

        result.Status = "success";
        result.CreatedId = createdElement is not null ? GetId(createdElement) : null;
        return result;
    }

    private static ReplaceOperationResult ExecuteReplace(JsonElement patch, WordprocessingDocument wpDoc,
        MainDocumentPart mainPart, string pathStr, bool dryRun)
    {
        var result = new ReplaceOperationResult { Path = pathStr };

        var value = patch.GetProperty("value");
        var path = DocxPath.Parse(pathStr);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{pathStr}'.");

        if (dryRun)
        {
            result.Status = "would_succeed";
            result.ReplacedId = GetId(targets[0]);
            return result;
        }

        string? replacedId = null;

        if (path.Leaf is StyleSegment)
        {
            foreach (var target in targets)
            {
                replacedId ??= GetId(target.Parent as OpenXmlElement);

                if (target is ParagraphProperties)
                {
                    var newProps = ElementFactory.CreateParagraphProperties(value);
                    target.Parent?.ReplaceChild(newProps, target);
                }
                else if (target is RunProperties)
                {
                    var newProps = ElementFactory.CreateRunProperties(value);
                    target.Parent?.ReplaceChild(newProps, target);
                }
                else if (target is TableProperties)
                {
                    var newProps = ElementFactory.CreateTableProperties(value);
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

                var newElement = ElementFactory.CreateFromJson(value, mainPart);
                parent.ReplaceChild(newElement, target);
            }
        }

        result.Status = "success";
        result.ReplacedId = replacedId;
        return result;
    }

    private static RemoveOperationResult ExecuteRemove(JsonElement patch, WordprocessingDocument wpDoc,
        string pathStr, bool dryRun)
    {
        var result = new RemoveOperationResult { Path = pathStr };

        var path = DocxPath.Parse(pathStr);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{pathStr}'.");

        var removedId = GetId(targets[0]);

        if (dryRun)
        {
            result.Status = "would_succeed";
            result.RemovedId = removedId;
            return result;
        }

        foreach (var target in targets)
        {
            target.Parent?.RemoveChild(target);
        }

        result.Status = "success";
        result.RemovedId = removedId;
        return result;
    }

    private static MoveOperationResult ExecuteMove(JsonElement patch, WordprocessingDocument wpDoc,
        string pathStr, bool dryRun)
    {
        var fromStr = patch.GetProperty("from").GetString()
            ?? throw new ArgumentException("Move patch must have a 'from' field.");

        var result = new MoveOperationResult { Path = pathStr, From = fromStr };

        var fromPath = DocxPath.Parse(fromStr);
        var sources = PathResolver.Resolve(fromPath, wpDoc);
        if (sources.Count != 1)
            throw new InvalidOperationException("Move source must resolve to exactly one element.");

        var source = sources[0];
        var movedId = GetId(source);

        if (dryRun)
        {
            // Validate destination
            var toPath = DocxPath.Parse(pathStr);
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

        var destPath = DocxPath.Parse(pathStr);
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

    private static CopyOperationResult ExecuteCopy(JsonElement patch, WordprocessingDocument wpDoc,
        string pathStr, bool dryRun)
    {
        var fromStr = patch.GetProperty("from").GetString()
            ?? throw new ArgumentException("Copy patch must have a 'from' field.");

        var result = new CopyOperationResult { Path = pathStr };

        var fromPath = DocxPath.Parse(fromStr);
        var sources = PathResolver.Resolve(fromPath, wpDoc);
        if (sources.Count != 1)
            throw new InvalidOperationException("Copy source must resolve to exactly one element.");

        var sourceId = GetId(sources[0]);

        if (dryRun)
        {
            // Validate destination
            var toPath = DocxPath.Parse(pathStr);
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

        var destPath = DocxPath.Parse(pathStr);
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
    /// Works on paragraphs, headings, table cells, or any element containing runs.
    /// </summary>
    private static ReplaceTextOperationResult ExecuteReplaceText(JsonElement patch, WordprocessingDocument wpDoc,
        string pathStr, bool dryRun)
    {
        var result = new ReplaceTextOperationResult { Path = pathStr };

        var find = patch.GetProperty("find").GetString()
            ?? throw new ArgumentException("replace_text must have a 'find' field.");
        var replace = patch.GetProperty("replace").GetString()
            ?? throw new ArgumentException("replace_text must have a 'replace' field.");

        // Validation: empty replace is forbidden
        if (string.IsNullOrEmpty(replace))
            throw new ArgumentException("'replace' cannot be empty. Use 'remove' operation to delete content.");

        // Get max_count (default: 1)
        int maxCount = 1;
        if (patch.TryGetProperty("max_count", out var maxCountEl))
        {
            maxCount = maxCountEl.GetInt32();
            if (maxCount < 0)
                throw new ArgumentException("'max_count' must be >= 0.");
        }

        var path = DocxPath.Parse(pathStr);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{pathStr}'.");

        // Count all matches first
        int totalMatches = 0;
        foreach (var target in targets)
        {
            totalMatches += CountTextMatches(target, find);
        }

        result.MatchesFound = totalMatches;

        // Determine how many we would/will replace
        int toReplace = maxCount == 0 ? 0 : Math.Min(totalMatches, maxCount);

        if (dryRun)
        {
            result.Status = "would_succeed";
            result.WouldReplace = toReplace;
            return result;
        }

        // Actually perform replacements
        int replaced = 0;
        foreach (var target in targets)
        {
            if (maxCount > 0 && replaced >= maxCount)
                break;

            int remaining = maxCount == 0 ? 0 : maxCount - replaced;
            replaced += ReplaceTextInElement(target, find, replace, remaining);
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
    /// Removes the cell at the given column index from every row.
    /// </summary>
    private static RemoveColumnOperationResult ExecuteRemoveColumn(JsonElement patch, WordprocessingDocument wpDoc,
        string pathStr, bool dryRun)
    {
        var result = new RemoveColumnOperationResult { Path = pathStr };

        var column = patch.GetProperty("column").GetInt32();
        var path = DocxPath.Parse(pathStr);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{pathStr}'.");

        int totalRowsAffected = 0;

        foreach (var target in targets)
        {
            if (target is not Table table)
                throw new InvalidOperationException("remove_column target must be a table.");

            var rows = table.Elements<TableRow>().ToList();
            foreach (var row in rows)
            {
                var cells = row.Elements<TableCell>().ToList();
                if (column >= 0 && column < cells.Count)
                {
                    if (!dryRun)
                        row.RemoveChild(cells[column]);
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
                    if (column >= 0 && column < gridCols.Count)
                    {
                        grid.RemoveChild(gridCols[column]);
                    }
                }
            }
        }

        result.Status = dryRun ? "would_succeed" : "success";
        result.ColumnIndex = column;
        result.RowsAffected = totalRowsAffected;
        return result;
    }
}
