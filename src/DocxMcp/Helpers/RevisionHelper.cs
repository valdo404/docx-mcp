using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxMcp.Helpers;

/// <summary>
/// Core OOXML revision (Track Changes) logic: list, accept, reject revisions.
/// Supports insertions (w:ins), deletions (w:del), moves, and formatting changes.
/// </summary>
public static class RevisionHelper
{
    /// <summary>
    /// Check if Track Changes is enabled in document settings.
    /// </summary>
    public static bool IsTrackChangesEnabled(WordprocessingDocument doc)
    {
        var settingsPart = doc.MainDocumentPart?.DocumentSettingsPart;
        if (settingsPart?.Settings is null)
            return false;

        return settingsPart.Settings.GetFirstChild<TrackRevisions>() is not null;
    }

    /// <summary>
    /// Enable or disable Track Changes in document settings.
    /// </summary>
    public static void SetTrackChangesEnabled(WordprocessingDocument doc, bool enabled)
    {
        var mainPart = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Document has no MainDocumentPart.");

        var settingsPart = mainPart.DocumentSettingsPart;
        if (settingsPart is null)
        {
            settingsPart = mainPart.AddNewPart<DocumentSettingsPart>();
            settingsPart.Settings = new Settings();
        }
        else if (settingsPart.Settings is null)
        {
            settingsPart.Settings = new Settings();
        }

        var trackRevisions = settingsPart.Settings.GetFirstChild<TrackRevisions>();

        if (enabled)
        {
            if (trackRevisions is null)
            {
                settingsPart.Settings.PrependChild(new TrackRevisions());
            }
        }
        else
        {
            trackRevisions?.Remove();
        }

        settingsPart.Settings.Save();
    }

    /// <summary>
    /// List all revisions in the document with metadata.
    /// </summary>
    public static List<RevisionInfo> ListRevisions(
        WordprocessingDocument doc,
        string? authorFilter = null,
        string? typeFilter = null)
    {
        var results = new List<RevisionInfo>();
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return results;

        // Collect insertions (w:ins)
        foreach (var ins in body.Descendants<InsertedRun>())
        {
            var info = CreateRevisionInfo(ins, "insertion", ins.Id?.Value, ins.Author?.Value, ins.Date?.Value);
            info.Content = ins.InnerText;
            info.ElementId = ElementIdManager.GetId(ins.Parent as OpenXmlElement);

            if (MatchesFilters(info, authorFilter, typeFilter))
                results.Add(info);
        }

        // Collect deletions (w:del)
        foreach (var del in body.Descendants<DeletedRun>())
        {
            var info = CreateRevisionInfo(del, "deletion", del.Id?.Value, del.Author?.Value, del.Date?.Value);
            // Deleted text is stored in DeletedText elements
            info.Content = string.Join("", del.Descendants<DeletedText>().Select(dt => dt.Text));
            info.ElementId = ElementIdManager.GetId(del.Parent as OpenXmlElement);

            if (MatchesFilters(info, authorFilter, typeFilter))
                results.Add(info);
        }

        // Collect paragraph insertions
        foreach (var para in body.Descendants<Paragraph>())
        {
            var pPr = para.ParagraphProperties;
            var pPrChange = pPr?.ParagraphPropertiesChange;
            if (pPrChange is not null)
            {
                var info = CreateRevisionInfo(pPrChange, "format_change", pPrChange.Id?.Value,
                    pPrChange.Author?.Value, pPrChange.Date?.Value);
                info.Content = "[Paragraph formatting change]";
                info.ElementId = ElementIdManager.GetId(para);

                if (MatchesFilters(info, authorFilter, typeFilter))
                    results.Add(info);
            }

            // Check for paragraph insertion marker (ins with rsidR)
            var paraProps = para.ParagraphProperties;
            if (paraProps is not null)
            {
                var ins = paraProps.GetFirstChild<Inserted>();
                if (ins is not null)
                {
                    var info = CreateRevisionInfo(ins, "paragraph_insertion", ins.Id?.Value,
                        ins.Author?.Value, ins.Date?.Value);
                    info.Content = para.InnerText;
                    info.ElementId = ElementIdManager.GetId(para);

                    if (MatchesFilters(info, authorFilter, typeFilter))
                        results.Add(info);
                }
            }
        }

        // Collect run property changes (w:rPrChange)
        foreach (var run in body.Descendants<Run>())
        {
            var rPr = run.RunProperties;
            var rPrChange = rPr?.RunPropertiesChange;
            if (rPrChange is not null)
            {
                var info = CreateRevisionInfo(rPrChange, "format_change", rPrChange.Id?.Value,
                    rPrChange.Author?.Value, rPrChange.Date?.Value);
                info.Content = $"[Run formatting change: '{run.InnerText}']";
                info.ElementId = ElementIdManager.GetId(run.Parent as OpenXmlElement);

                if (MatchesFilters(info, authorFilter, typeFilter))
                    results.Add(info);
            }
        }

        // Collect section property changes (w:sectPrChange)
        foreach (var sectPr in body.Descendants<SectionProperties>())
        {
            var sectPrChange = sectPr.GetFirstChild<SectionPropertiesChange>();
            if (sectPrChange is not null)
            {
                var info = CreateRevisionInfo(sectPrChange, "section_change", sectPrChange.Id?.Value,
                    sectPrChange.Author?.Value, sectPrChange.Date?.Value);
                info.Content = "[Section properties change]";

                if (MatchesFilters(info, authorFilter, typeFilter))
                    results.Add(info);
            }
        }

        // Collect table property changes
        foreach (var table in body.Descendants<Table>())
        {
            var tblPr = table.GetFirstChild<TableProperties>();
            var tblPrChange = tblPr?.GetFirstChild<TablePropertiesChange>();
            if (tblPrChange is not null)
            {
                var info = CreateRevisionInfo(tblPrChange, "table_change", tblPrChange.Id?.Value,
                    tblPrChange.Author?.Value, tblPrChange.Date?.Value);
                info.Content = "[Table properties change]";
                info.ElementId = ElementIdManager.GetId(table);

                if (MatchesFilters(info, authorFilter, typeFilter))
                    results.Add(info);
            }
        }

        // Collect table row property changes
        foreach (var row in body.Descendants<TableRow>())
        {
            var trPr = row.TableRowProperties;
            var trPrChange = trPr?.GetFirstChild<TableRowPropertiesChange>();
            if (trPrChange is not null)
            {
                var info = CreateRevisionInfo(trPrChange, "row_change", trPrChange.Id?.Value,
                    trPrChange.Author?.Value, trPrChange.Date?.Value);
                info.Content = "[Table row properties change]";
                info.ElementId = ElementIdManager.GetId(row);

                if (MatchesFilters(info, authorFilter, typeFilter))
                    results.Add(info);
            }
        }

        // Collect table cell property changes
        foreach (var cell in body.Descendants<TableCell>())
        {
            var tcPr = cell.GetFirstChild<TableCellProperties>();
            var tcPrChange = tcPr?.GetFirstChild<TableCellPropertiesChange>();
            if (tcPrChange is not null)
            {
                var info = CreateRevisionInfo(tcPrChange, "cell_change", tcPrChange.Id?.Value,
                    tcPrChange.Author?.Value, tcPrChange.Date?.Value);
                info.Content = "[Table cell properties change]";
                info.ElementId = ElementIdManager.GetId(cell);

                if (MatchesFilters(info, authorFilter, typeFilter))
                    results.Add(info);
            }
        }

        // Collect move-from (w:moveFrom)
        foreach (var moveFrom in body.Descendants<MoveFromRun>())
        {
            var info = CreateRevisionInfo(moveFrom, "move_from", moveFrom.Id?.Value,
                moveFrom.Author?.Value, moveFrom.Date?.Value);
            info.Content = moveFrom.InnerText;
            info.ElementId = ElementIdManager.GetId(moveFrom.Parent as OpenXmlElement);

            if (MatchesFilters(info, authorFilter, typeFilter))
                results.Add(info);
        }

        // Collect move-to (w:moveTo)
        foreach (var moveTo in body.Descendants<MoveToRun>())
        {
            var info = CreateRevisionInfo(moveTo, "move_to", moveTo.Id?.Value,
                moveTo.Author?.Value, moveTo.Date?.Value);
            info.Content = moveTo.InnerText;
            info.ElementId = ElementIdManager.GetId(moveTo.Parent as OpenXmlElement);

            if (MatchesFilters(info, authorFilter, typeFilter))
                results.Add(info);
        }

        // Sort by revision ID
        results.Sort((a, b) => a.Id.CompareTo(b.Id));
        return results;
    }

    /// <summary>
    /// Accept a single revision by ID.
    /// </summary>
    public static bool AcceptRevision(WordprocessingDocument doc, int revisionId)
    {
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return false;

        var idStr = revisionId.ToString();

        // Accept insertion: unwrap w:ins, keep content
        foreach (var ins in body.Descendants<InsertedRun>().Where(i => i.Id?.Value == idStr).ToList())
        {
            AcceptInsertedRun(ins);
            return true;
        }

        // Accept deletion: remove w:del and its content
        foreach (var del in body.Descendants<DeletedRun>().Where(d => d.Id?.Value == idStr).ToList())
        {
            del.Remove();
            return true;
        }

        // Accept paragraph property change: remove w:pPrChange
        foreach (var para in body.Descendants<Paragraph>())
        {
            var pPrChange = para.ParagraphProperties?.ParagraphPropertiesChange;
            if (pPrChange?.Id?.Value == idStr)
            {
                pPrChange.Remove();
                return true;
            }

            // Accept paragraph insertion marker
            var ins = para.ParagraphProperties?.GetFirstChild<Inserted>();
            if (ins?.Id?.Value == idStr)
            {
                ins.Remove();
                return true;
            }
        }

        // Accept run property change: remove w:rPrChange
        foreach (var run in body.Descendants<Run>())
        {
            var rPrChange = run.RunProperties?.RunPropertiesChange;
            if (rPrChange?.Id?.Value == idStr)
            {
                rPrChange.Remove();
                return true;
            }
        }

        // Accept section property change
        foreach (var sectPr in body.Descendants<SectionProperties>())
        {
            var sectPrChange = sectPr.GetFirstChild<SectionPropertiesChange>();
            if (sectPrChange?.Id?.Value == idStr)
            {
                sectPrChange.Remove();
                return true;
            }
        }

        // Accept table property changes
        foreach (var table in body.Descendants<Table>())
        {
            var tblPrChange = table.GetFirstChild<TableProperties>()?.GetFirstChild<TablePropertiesChange>();
            if (tblPrChange?.Id?.Value == idStr)
            {
                tblPrChange.Remove();
                return true;
            }
        }

        foreach (var row in body.Descendants<TableRow>())
        {
            var trPrChange = row.TableRowProperties?.GetFirstChild<TableRowPropertiesChange>();
            if (trPrChange?.Id?.Value == idStr)
            {
                trPrChange.Remove();
                return true;
            }
        }

        foreach (var cell in body.Descendants<TableCell>())
        {
            var tcPrChange = cell.GetFirstChild<TableCellProperties>()?.GetFirstChild<TableCellPropertiesChange>();
            if (tcPrChange?.Id?.Value == idStr)
            {
                tcPrChange.Remove();
                return true;
            }
        }

        // Accept move-from: remove content
        foreach (var moveFrom in body.Descendants<MoveFromRun>().Where(m => m.Id?.Value == idStr).ToList())
        {
            moveFrom.Remove();
            return true;
        }

        // Accept move-to: unwrap, keep content
        foreach (var moveTo in body.Descendants<MoveToRun>().Where(m => m.Id?.Value == idStr).ToList())
        {
            AcceptMoveToRun(moveTo);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reject a single revision by ID.
    /// </summary>
    public static bool RejectRevision(WordprocessingDocument doc, int revisionId)
    {
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return false;

        var idStr = revisionId.ToString();

        // Reject insertion: remove w:ins and its content
        foreach (var ins in body.Descendants<InsertedRun>().Where(i => i.Id?.Value == idStr).ToList())
        {
            ins.Remove();
            return true;
        }

        // Reject deletion: unwrap w:del, restore content
        foreach (var del in body.Descendants<DeletedRun>().Where(d => d.Id?.Value == idStr).ToList())
        {
            RejectDeletedRun(del);
            return true;
        }

        // Reject paragraph property change: restore previous properties
        foreach (var para in body.Descendants<Paragraph>())
        {
            var pPr = para.ParagraphProperties;
            var pPrChange = pPr?.ParagraphPropertiesChange;
            if (pPrChange?.Id?.Value == idStr)
            {
                // Restore previous properties from pPrChange
                var prevProps = pPrChange.PreviousParagraphProperties;
                if (prevProps is not null && pPr is not null)
                {
                    // Replace current properties with previous
                    var cloned = (ParagraphProperties)prevProps.CloneNode(true);
                    foreach (var child in pPr.ChildElements.ToList())
                    {
                        if (child is not ParagraphPropertiesChange)
                            child.Remove();
                    }
                    foreach (var child in cloned.ChildElements.ToList())
                    {
                        pPr.AppendChild(child.CloneNode(true));
                    }
                }
                pPrChange.Remove();
                return true;
            }

            // Reject paragraph insertion: remove the paragraph
            var ins = para.ParagraphProperties?.GetFirstChild<Inserted>();
            if (ins?.Id?.Value == idStr)
            {
                para.Remove();
                return true;
            }
        }

        // Reject run property change: restore previous properties
        foreach (var run in body.Descendants<Run>())
        {
            var rPr = run.RunProperties;
            var rPrChange = rPr?.RunPropertiesChange;
            if (rPrChange?.Id?.Value == idStr)
            {
                var prevProps = rPrChange.PreviousRunProperties;
                if (prevProps is not null && rPr is not null)
                {
                    var cloned = (RunProperties)prevProps.CloneNode(true);
                    foreach (var child in rPr.ChildElements.ToList())
                    {
                        if (child is not RunPropertiesChange)
                            child.Remove();
                    }
                    foreach (var child in cloned.ChildElements.ToList())
                    {
                        rPr.AppendChild(child.CloneNode(true));
                    }
                }
                rPrChange.Remove();
                return true;
            }
        }

        // Reject section property change
        foreach (var sectPr in body.Descendants<SectionProperties>())
        {
            var sectPrChange = sectPr.GetFirstChild<SectionPropertiesChange>();
            if (sectPrChange?.Id?.Value == idStr)
            {
                var prevProps = sectPrChange.PreviousSectionProperties;
                if (prevProps is not null)
                {
                    foreach (var child in sectPr.ChildElements.ToList())
                    {
                        if (child is not SectionPropertiesChange)
                            child.Remove();
                    }
                    foreach (var child in prevProps.ChildElements.ToList())
                    {
                        sectPr.AppendChild(child.CloneNode(true));
                    }
                }
                sectPrChange.Remove();
                return true;
            }
        }

        // Reject table property changes
        foreach (var table in body.Descendants<Table>())
        {
            var tblPr = table.GetFirstChild<TableProperties>();
            var tblPrChange = tblPr?.GetFirstChild<TablePropertiesChange>();
            if (tblPrChange?.Id?.Value == idStr)
            {
                var prevProps = tblPrChange.PreviousTableProperties;
                if (prevProps is not null && tblPr is not null)
                {
                    foreach (var child in tblPr.ChildElements.ToList())
                    {
                        if (child is not TablePropertiesChange)
                            child.Remove();
                    }
                    foreach (var child in prevProps.ChildElements.ToList())
                    {
                        tblPr.AppendChild(child.CloneNode(true));
                    }
                }
                tblPrChange.Remove();
                return true;
            }
        }

        foreach (var row in body.Descendants<TableRow>())
        {
            var trPr = row.TableRowProperties;
            var trPrChange = trPr?.GetFirstChild<TableRowPropertiesChange>();
            if (trPrChange?.Id?.Value == idStr)
            {
                var prevProps = trPrChange.PreviousTableRowProperties;
                if (prevProps is not null && trPr is not null)
                {
                    foreach (var child in trPr.ChildElements.ToList())
                    {
                        if (child is not TableRowPropertiesChange)
                            child.Remove();
                    }
                    foreach (var child in prevProps.ChildElements.ToList())
                    {
                        trPr.AppendChild(child.CloneNode(true));
                    }
                }
                trPrChange.Remove();
                return true;
            }
        }

        foreach (var cell in body.Descendants<TableCell>())
        {
            var tcPr = cell.GetFirstChild<TableCellProperties>();
            var tcPrChange = tcPr?.GetFirstChild<TableCellPropertiesChange>();
            if (tcPrChange?.Id?.Value == idStr)
            {
                var prevProps = tcPrChange.PreviousTableCellProperties;
                if (prevProps is not null && tcPr is not null)
                {
                    foreach (var child in tcPr.ChildElements.ToList())
                    {
                        if (child is not TableCellPropertiesChange)
                            child.Remove();
                    }
                    foreach (var child in prevProps.ChildElements.ToList())
                    {
                        tcPr.AppendChild(child.CloneNode(true));
                    }
                }
                tcPrChange.Remove();
                return true;
            }
        }

        // Reject move-from: unwrap, restore content at original location
        foreach (var moveFrom in body.Descendants<MoveFromRun>().Where(m => m.Id?.Value == idStr).ToList())
        {
            AcceptMoveFromAsReject(moveFrom);
            return true;
        }

        // Reject move-to: remove content at new location
        foreach (var moveTo in body.Descendants<MoveToRun>().Where(m => m.Id?.Value == idStr).ToList())
        {
            moveTo.Remove();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Get revision statistics for the document.
    /// </summary>
    public static RevisionStats GetRevisionStats(WordprocessingDocument doc)
    {
        var revisions = ListRevisions(doc);
        var stats = new RevisionStats
        {
            TotalCount = revisions.Count,
            TrackChangesEnabled = IsTrackChangesEnabled(doc)
        };

        foreach (var rev in revisions)
        {
            // Count by type
            if (!stats.ByType.ContainsKey(rev.Type))
                stats.ByType[rev.Type] = 0;
            stats.ByType[rev.Type]++;

            // Count by author
            var author = rev.Author ?? "(unknown)";
            if (!stats.ByAuthor.ContainsKey(author))
                stats.ByAuthor[author] = 0;
            stats.ByAuthor[author]++;
        }

        return stats;
    }

    // --- Private helpers ---

    private static RevisionInfo CreateRevisionInfo(OpenXmlElement element, string type, string? id, string? author, DateTime? date)
    {
        return new RevisionInfo
        {
            Id = int.TryParse(id, out var parsedId) ? parsedId : 0,
            Type = type,
            Author = author,
            Date = date
        };
    }

    private static bool MatchesFilters(RevisionInfo info, string? authorFilter, string? typeFilter)
    {
        if (authorFilter is not null &&
            !string.Equals(info.Author, authorFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        if (typeFilter is not null &&
            !string.Equals(info.Type, typeFilter, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Accept an InsertedRun: unwrap and keep content as normal runs.
    /// </summary>
    private static void AcceptInsertedRun(InsertedRun ins)
    {
        var parent = ins.Parent;
        if (parent is null)
        {
            ins.Remove();
            return;
        }

        // Move all children before the ins element, then remove ins
        var children = ins.ChildElements.ToList();
        foreach (var child in children)
        {
            var cloned = child.CloneNode(true);
            parent.InsertBefore(cloned, ins);
        }
        ins.Remove();
    }

    /// <summary>
    /// Reject a DeletedRun: unwrap and restore content as normal runs.
    /// </summary>
    private static void RejectDeletedRun(DeletedRun del)
    {
        var parent = del.Parent;
        if (parent is null)
        {
            del.Remove();
            return;
        }

        // Convert DeletedText elements back to Text elements in new runs
        foreach (var child in del.ChildElements.ToList())
        {
            if (child is Run run)
            {
                // Clone the run and convert DeletedText to Text
                var newRun = (Run)run.CloneNode(true);
                foreach (var dt in newRun.Descendants<DeletedText>().ToList())
                {
                    var text = new Text(dt.Text) { Space = SpaceProcessingModeValues.Preserve };
                    dt.Parent?.InsertBefore(text, dt);
                    dt.Remove();
                }
                parent.InsertBefore(newRun, del);
            }
        }
        del.Remove();
    }

    /// <summary>
    /// Accept a MoveToRun: unwrap and keep content.
    /// </summary>
    private static void AcceptMoveToRun(MoveToRun moveTo)
    {
        var parent = moveTo.Parent;
        if (parent is null)
        {
            moveTo.Remove();
            return;
        }

        var children = moveTo.ChildElements.ToList();
        foreach (var child in children)
        {
            var cloned = child.CloneNode(true);
            parent.InsertBefore(cloned, moveTo);
        }
        moveTo.Remove();
    }

    /// <summary>
    /// When rejecting a move, restore content at the original location (move-from).
    /// </summary>
    private static void AcceptMoveFromAsReject(MoveFromRun moveFrom)
    {
        var parent = moveFrom.Parent;
        if (parent is null)
        {
            moveFrom.Remove();
            return;
        }

        var children = moveFrom.ChildElements.ToList();
        foreach (var child in children)
        {
            var cloned = child.CloneNode(true);
            parent.InsertBefore(cloned, moveFrom);
        }
        moveFrom.Remove();
    }
}

/// <summary>
/// Data object for revision listing results.
/// </summary>
public class RevisionInfo
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string? Author { get; set; }
    public DateTime? Date { get; set; }
    public string? Content { get; set; }
    public string? ElementId { get; set; }
}

/// <summary>
/// Statistics about revisions in a document.
/// </summary>
public class RevisionStats
{
    public int TotalCount { get; set; }
    public bool TrackChangesEnabled { get; set; }
    public Dictionary<string, int> ByType { get; set; } = new();
    public Dictionary<string, int> ByAuthor { get; set; } = new();
}
