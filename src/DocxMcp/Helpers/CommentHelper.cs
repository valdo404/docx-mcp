using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using W15 = DocumentFormat.OpenXml.Office2013.Word;

namespace DocxMcp.Helpers;

/// <summary>
/// Core OOXML comment logic: add, delete, list, and anchor comments to document elements.
/// </summary>
public static class CommentHelper
{
    /// <summary>
    /// Ensure the document has a WordprocessingCommentsPart with a root Comments element.
    /// </summary>
    public static WordprocessingCommentsPart EnsureCommentsPart(WordprocessingDocument doc)
    {
        var mainPart = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Document has no MainDocumentPart.");

        var commentsPart = mainPart.WordprocessingCommentsPart;
        if (commentsPart is null)
        {
            commentsPart = mainPart.AddNewPart<WordprocessingCommentsPart>();
            commentsPart.Comments = new Comments();
        }
        else if (commentsPart.Comments is null)
        {
            commentsPart.Comments = new Comments();
        }

        return commentsPart;
    }

    /// <summary>
    /// Allocate the next comment ID (max existing + 1). Never reuses deleted IDs.
    /// </summary>
    public static int AllocateCommentId(WordprocessingDocument doc)
    {
        var commentsPart = doc.MainDocumentPart?.WordprocessingCommentsPart;
        if (commentsPart?.Comments is null)
            return 0;

        var maxId = -1;
        foreach (var c in commentsPart.Comments.Elements<Comment>())
        {
            var idStr = c.Id?.Value;
            if (idStr is not null && int.TryParse(idStr, out var id) && id > maxId)
                maxId = id;
        }

        return maxId + 1;
    }

    /// <summary>
    /// Add a paragraph-level comment: CommentRangeStart as first child (after ParagraphProperties),
    /// CommentRangeEnd + reference Run at end of paragraph.
    /// </summary>
    public static void AddCommentToElement(
        WordprocessingDocument doc,
        OpenXmlElement element,
        int commentId,
        string text,
        string author,
        string initials,
        DateTime date)
    {
        var commentsPart = EnsureCommentsPart(doc);

        // Create the Comment in comments.xml
        var comment = CreateComment(commentId, text, author, initials, date);
        commentsPart.Comments.AppendChild(comment);
        commentsPart.Comments.Save();

        // Add anchoring to the element
        if (element is Paragraph para)
        {
            AddCommentAnchorsToParagraph(para, commentId);
        }
        else
        {
            // For non-paragraph elements (e.g. table), wrap the whole element's content
            // Find the first and last paragraph descendant
            var paragraphs = element.Descendants<Paragraph>().ToList();
            if (paragraphs.Count > 0)
            {
                // Start marker in first paragraph, end marker + ref in last
                var firstPara = paragraphs[0];
                var lastPara = paragraphs[^1];

                var rangeStart = new CommentRangeStart { Id = commentId.ToString() };
                var pp = firstPara.GetFirstChild<ParagraphProperties>();
                if (pp is not null)
                    firstPara.InsertAfter(rangeStart, pp);
                else
                    firstPara.PrependChild(rangeStart);

                var rangeEnd = new CommentRangeEnd { Id = commentId.ToString() };
                lastPara.AppendChild(rangeEnd);
                lastPara.AppendChild(CreateCommentReferenceRun(commentId));
            }
        }
    }

    /// <summary>
    /// Add a text-level comment anchored to specific text within an element.
    /// Supports cross-run text matching with run splitting.
    /// </summary>
    public static void AddCommentToText(
        WordprocessingDocument doc,
        OpenXmlElement element,
        int commentId,
        string text,
        string author,
        string initials,
        DateTime date,
        string anchorText)
    {
        var commentsPart = EnsureCommentsPart(doc);

        // Create the Comment in comments.xml
        var comment = CreateComment(commentId, text, author, initials, date);
        commentsPart.Comments.AppendChild(comment);
        commentsPart.Comments.Save();

        // Find the paragraph containing the anchor text
        var paragraphs = element is Paragraph p
            ? new List<Paragraph> { p }
            : element.Descendants<Paragraph>().ToList();

        foreach (var para in paragraphs)
        {
            if (TryAnchorTextInParagraph(para, commentId, anchorText))
                return;
        }

        // If we get here, anchor_text was not found — clean up the comment
        commentsPart.Comments.RemoveChild(comment);
        commentsPart.Comments.Save();
        throw new InvalidOperationException($"anchor_text '{anchorText}' not found in element.");
    }

    /// <summary>
    /// Try to anchor a comment to specific text within a paragraph.
    /// Returns true if successful.
    /// </summary>
    private static bool TryAnchorTextInParagraph(Paragraph para, int commentId, string anchorText)
    {
        var runs = para.Elements<Run>().ToList();
        if (runs.Count == 0) return false;

        // Concatenate all run texts and find the anchor
        var runTexts = new List<(Run Run, string Text, int Start)>();
        int pos = 0;
        foreach (var run in runs)
        {
            var runText = run.InnerText;
            runTexts.Add((run, runText, pos));
            pos += runText.Length;
        }

        var allText = string.Concat(runTexts.Select(r => r.Text));
        var matchIdx = allText.IndexOf(anchorText, StringComparison.Ordinal);
        if (matchIdx < 0) return false;

        var matchEnd = matchIdx + anchorText.Length;

        // Find runs that overlap with the match
        var firstRunIdx = -1;
        var lastRunIdx = -1;

        for (int i = 0; i < runTexts.Count; i++)
        {
            var (_, runText, runStart) = runTexts[i];
            var runEnd = runStart + runText.Length;

            if (runEnd <= matchIdx || runStart >= matchEnd)
                continue;

            if (firstRunIdx < 0) firstRunIdx = i;
            lastRunIdx = i;
        }

        if (firstRunIdx < 0) return false;

        // Split first run if match doesn't start at run boundary
        var firstEntry = runTexts[firstRunIdx];
        var firstRunTextElem = firstEntry.Run.GetFirstChild<Text>();
        if (firstRunTextElem is not null && matchIdx > firstEntry.Start)
        {
            var splitAt = matchIdx - firstEntry.Start;
            var newRun = SplitRun(firstEntry.Run, splitAt);
            para.InsertAfter(newRun, firstEntry.Run);
            // Update: the new run is now our first matching run
            runTexts[firstRunIdx] = (firstEntry.Run, firstEntry.Text[..splitAt], firstEntry.Start);
            // Insert the new entry
            runTexts.Insert(firstRunIdx + 1, (newRun, firstEntry.Text[splitAt..], firstEntry.Start + splitAt));
            firstRunIdx++;
            lastRunIdx++;
        }

        // Split last run if match doesn't end at run boundary
        var lastEntry = runTexts[lastRunIdx];
        var lastRunTextElem = lastEntry.Run.GetFirstChild<Text>();
        if (lastRunTextElem is not null)
        {
            var lastRunEnd = lastEntry.Start + lastEntry.Text.Length;
            if (matchEnd < lastRunEnd)
            {
                var splitAt = matchEnd - lastEntry.Start;
                var newRun = SplitRun(lastEntry.Run, splitAt);
                para.InsertAfter(newRun, lastEntry.Run);
            }
        }

        // Insert CommentRangeStart before first matching run
        var rangeStart = new CommentRangeStart { Id = commentId.ToString() };
        para.InsertBefore(rangeStart, runTexts[firstRunIdx].Run);

        // Insert CommentRangeEnd after last matching run
        var rangeEnd = new CommentRangeEnd { Id = commentId.ToString() };
        para.InsertAfter(rangeEnd, runTexts[lastRunIdx].Run);

        // Insert reference run after CommentRangeEnd
        var refRun = CreateCommentReferenceRun(commentId);
        para.InsertAfter(refRun, rangeEnd);

        return true;
    }

    /// <summary>
    /// Split a run at the given character position. Returns a new run containing
    /// text from splitAt onwards, leaving text before splitAt in the original run.
    /// </summary>
    public static Run SplitRun(Run run, int splitAt)
    {
        var textElem = run.GetFirstChild<Text>()
            ?? throw new InvalidOperationException("Run has no Text element.");

        var originalText = textElem.Text;
        textElem.Text = originalText[..splitAt];
        textElem.Space = SpaceProcessingModeValues.Preserve;

        var newRun = (Run)run.CloneNode(true);
        var newText = newRun.GetFirstChild<Text>()!;
        newText.Text = originalText[splitAt..];
        newText.Space = SpaceProcessingModeValues.Preserve;

        return newRun;
    }

    /// <summary>
    /// Delete a comment by ID: removes Comment from comments.xml,
    /// CommentRangeStart, CommentRangeEnd, and CommentReference run from body.
    /// </summary>
    public static bool DeleteComment(WordprocessingDocument doc, int commentId)
    {
        var commentsPart = doc.MainDocumentPart?.WordprocessingCommentsPart;
        if (commentsPart?.Comments is null) return false;

        var idStr = commentId.ToString();

        // Remove Comment element
        var comment = commentsPart.Comments.Elements<Comment>()
            .FirstOrDefault(c => c.Id?.Value == idStr);
        if (comment is null) return false;

        commentsPart.Comments.RemoveChild(comment);
        commentsPart.Comments.Save();

        // Remove anchoring from body
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return true;

        RemoveCommentAnchors(body, idStr);

        return true;
    }

    /// <summary>
    /// Remove all comment anchoring elements (RangeStart, RangeEnd, Reference) for a given ID
    /// from the body and all its descendants.
    /// </summary>
    private static void RemoveCommentAnchors(OpenXmlElement root, string commentId)
    {
        // Remove CommentRangeStart
        foreach (var el in root.Descendants<CommentRangeStart>()
            .Where(e => e.Id?.Value == commentId).ToList())
        {
            el.Remove();
        }

        // Remove CommentRangeEnd
        foreach (var el in root.Descendants<CommentRangeEnd>()
            .Where(e => e.Id?.Value == commentId).ToList())
        {
            el.Remove();
        }

        // Remove CommentReference runs
        foreach (var el in root.Descendants<CommentReference>()
            .Where(e => e.Id?.Value == commentId).ToList())
        {
            // Remove the parent Run that contains the CommentReference
            var parentRun = el.Parent;
            if (parentRun is Run)
                parentRun.Remove();
            else
                el.Remove();
        }
    }

    /// <summary>
    /// List all comments in the document with metadata.
    /// </summary>
    public static List<CommentInfo> ListComments(WordprocessingDocument doc, string? authorFilter = null)
    {
        var results = new List<CommentInfo>();
        var mainPart = doc.MainDocumentPart;
        var commentsPart = mainPart?.WordprocessingCommentsPart;
        if (commentsPart?.Comments is null) return results;

        var body = mainPart?.Document?.Body;

        // Build paraId→Done lookup from CommentsExPart
        var resolvedParaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exPart = mainPart?.WordprocessingCommentsExPart;
        if (exPart?.CommentsEx is not null)
        {
            foreach (var ce in exPart.CommentsEx.Elements<W15.CommentEx>())
            {
                if (ce.Done?.Value == true && ce.ParaId?.Value is not null)
                    resolvedParaIds.Add(ce.ParaId.Value);
            }
        }

        foreach (var comment in commentsPart.Comments.Elements<Comment>())
        {
            var cAuthor = comment.Author?.Value ?? "";
            if (authorFilter is not null &&
                !cAuthor.Equals(authorFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var idStr = comment.Id?.Value ?? "";
            var commentText = string.Join("\n",
                comment.Elements<Paragraph>().Select(p => p.InnerText));

            string? anchoredText = null;
            if (body is not null && int.TryParse(idStr, out _))
            {
                anchoredText = GetAnchoredText(body, idStr);
            }

            // Check resolved state via first paragraph's paraId
            var firstPara = comment.Elements<Paragraph>().FirstOrDefault();
            var paraId = firstPara?.ParagraphId?.Value;
            var isResolved = paraId is not null && resolvedParaIds.Contains(paraId);

            results.Add(new CommentInfo
            {
                Id = int.TryParse(idStr, out var id) ? id : 0,
                Author = cAuthor,
                Initials = comment.Initials?.Value ?? "",
                Date = comment.Date?.Value,
                Text = commentText,
                AnchoredText = anchoredText,
                Resolved = isResolved
            });
        }

        return results;
    }

    /// <summary>
    /// Get the text between CommentRangeStart and CommentRangeEnd for a given comment ID.
    /// </summary>
    public static string? GetAnchoredText(OpenXmlElement root, string commentId)
    {
        var rangeStart = root.Descendants<CommentRangeStart>()
            .FirstOrDefault(e => e.Id?.Value == commentId);
        if (rangeStart is null) return null;

        var rangeEnd = root.Descendants<CommentRangeEnd>()
            .FirstOrDefault(e => e.Id?.Value == commentId);
        if (rangeEnd is null) return null;

        // Collect text between rangeStart and rangeEnd within the same parent
        var parent = rangeStart.Parent;
        if (parent is null) return null;

        var collecting = false;
        var text = new System.Text.StringBuilder();

        foreach (var child in parent.ChildElements)
        {
            if (child == rangeStart)
            {
                collecting = true;
                continue;
            }

            if (child == rangeEnd)
                break;

            if (collecting)
                text.Append(child.InnerText);
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>
    /// Create a Comment element with one or more paragraphs (split by \n).
    /// </summary>
    private static Comment CreateComment(int commentId, string text, string author, string initials, DateTime date)
    {
        var comment = new Comment
        {
            Id = commentId.ToString(),
            Author = author,
            Initials = initials,
            Date = date
        };

        var lines = text.Split('\n');
        foreach (var line in lines)
        {
            var para = new Paragraph(
                new Run(
                    new Text(line) { Space = SpaceProcessingModeValues.Preserve }));
            comment.AppendChild(para);
        }

        return comment;
    }

    /// <summary>
    /// Add CommentRangeStart/End + reference Run to a paragraph (paragraph-level anchoring).
    /// </summary>
    private static void AddCommentAnchorsToParagraph(Paragraph para, int commentId)
    {
        var idStr = commentId.ToString();

        // Insert CommentRangeStart after ParagraphProperties (or as first child)
        var rangeStart = new CommentRangeStart { Id = idStr };
        var pp = para.GetFirstChild<ParagraphProperties>();
        if (pp is not null)
            para.InsertAfter(rangeStart, pp);
        else
            para.PrependChild(rangeStart);

        // Append CommentRangeEnd + reference Run at end of paragraph
        para.AppendChild(new CommentRangeEnd { Id = idStr });
        para.AppendChild(CreateCommentReferenceRun(commentId));
    }

    /// <summary>
    /// Resolve or un-resolve a comment by ID.
    /// Uses WordprocessingCommentsExPart (commentsExtended.xml) with CommentEx.Done attribute.
    /// </summary>
    public static bool ResolveComment(WordprocessingDocument doc, int commentId, bool resolved)
    {
        var mainPart = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Document has no MainDocumentPart.");

        // Verify the comment exists
        var commentsPart = mainPart.WordprocessingCommentsPart;
        if (commentsPart?.Comments is null) return false;

        var idStr = commentId.ToString();
        var comment = commentsPart.Comments.Elements<Comment>()
            .FirstOrDefault(c => c.Id?.Value == idStr);
        if (comment is null) return false;

        // Get the first paragraph's paraId from the comment
        var firstPara = comment.Elements<Paragraph>().FirstOrDefault();
        if (firstPara is null) return false;

        var paraId = firstPara.ParagraphId?.Value;
        if (paraId is null)
        {
            // Generate a paraId if none exists
            paraId = GenerateParaId();
            firstPara.ParagraphId = new HexBinaryValue(paraId);
        }

        // Get or create CommentsExPart
        var exPart = mainPart.WordprocessingCommentsExPart;
        if (exPart is null)
        {
            exPart = mainPart.AddNewPart<WordprocessingCommentsExPart>();
            exPart.CommentsEx = new W15.CommentsEx();
        }
        else if (exPart.CommentsEx is null)
        {
            exPart.CommentsEx = new W15.CommentsEx();
        }

        // Find existing CommentEx for this paraId, or create one
        var commentEx = exPart.CommentsEx.Elements<W15.CommentEx>()
            .FirstOrDefault(ce => ce.ParaId?.Value == paraId);

        if (commentEx is null)
        {
            commentEx = new W15.CommentEx { ParaId = new HexBinaryValue(paraId) };
            exPart.CommentsEx.AppendChild(commentEx);
        }

        commentEx.Done = resolved ? true : false;
        exPart.CommentsEx.Save();

        return true;
    }

    /// <summary>
    /// Check if a comment is resolved.
    /// </summary>
    public static bool IsCommentResolved(WordprocessingDocument doc, int commentId)
    {
        var mainPart = doc.MainDocumentPart;
        var commentsPart = mainPart?.WordprocessingCommentsPart;
        if (commentsPart?.Comments is null) return false;

        var idStr = commentId.ToString();
        var comment = commentsPart.Comments.Elements<Comment>()
            .FirstOrDefault(c => c.Id?.Value == idStr);
        if (comment is null) return false;

        var firstPara = comment.Elements<Paragraph>().FirstOrDefault();
        var paraId = firstPara?.ParagraphId?.Value;
        if (paraId is null) return false;

        var exPart = mainPart?.WordprocessingCommentsExPart;
        if (exPart?.CommentsEx is null) return false;

        var commentEx = exPart.CommentsEx.Elements<W15.CommentEx>()
            .FirstOrDefault(ce => ce.ParaId?.Value == paraId);

        return commentEx?.Done?.Value == true;
    }

    /// <summary>
    /// Generate a random 8-character hex paraId (matching Word's format).
    /// </summary>
    private static string GenerateParaId()
    {
        var bytes = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Create the reference Run: contains CommentReference with CommentReference style.
    /// </summary>
    private static Run CreateCommentReferenceRun(int commentId)
    {
        var run = new Run();
        run.RunProperties = new RunProperties(
            new RunStyle { Val = "CommentReference" });
        run.AppendChild(new CommentReference { Id = commentId.ToString() });
        return run;
    }
}

/// <summary>
/// Data object for comment listing results.
/// </summary>
public class CommentInfo
{
    public int Id { get; set; }
    public string Author { get; set; } = "";
    public string Initials { get; set; } = "";
    public DateTime? Date { get; set; }
    public string Text { get; set; } = "";
    public string? AnchoredText { get; set; }
    public bool Resolved { get; set; }
}
