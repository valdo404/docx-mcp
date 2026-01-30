namespace DocxMcp.Paths;

/// <summary>
/// A typed, validated path into a DOCX document.
/// Consists of an ordered sequence of typed segments.
/// </summary>
public sealed class DocxPath
{
    public IReadOnlyList<PathSegment> Segments { get; }

    public DocxPath(IReadOnlyList<PathSegment> segments)
    {
        if (segments.Count == 0)
            throw new ArgumentException("Path must have at least one segment.");
        Segments = segments;
    }

    /// <summary>
    /// Parse a path string into a typed DocxPath.
    /// </summary>
    public static DocxPath Parse(string path) => PathParser.Parse(path);

    /// <summary>
    /// Whether this path targets the body itself.
    /// </summary>
    public bool IsBodyPath => Segments.Count == 1 && Segments[0] is BodySegment;

    /// <summary>
    /// Whether this path is a positional children path (for insertion).
    /// </summary>
    public bool IsChildrenPath => Segments.Count >= 2 && Segments[^1] is ChildrenSegment;

    /// <summary>
    /// The last segment in the path.
    /// </summary>
    public PathSegment Leaf => Segments[^1];

    public override string ToString()
    {
        return "/" + string.Join("/", Segments.Select(SegmentToString));
    }

    private static string SegmentToString(PathSegment seg) => seg switch
    {
        BodySegment => "body",
        ParagraphSegment p => $"paragraph{SelectorToString(p.Selector)}",
        HeadingSegment h => $"heading[level={h.Level}]{SelectorToString(h.Selector)}",
        TableSegment t => $"table{SelectorToString(t.Selector)}",
        RowSegment r => $"row{SelectorToString(r.Selector)}",
        CellSegment c => $"cell{SelectorToString(c.Selector)}",
        RunSegment r => $"run{SelectorToString(r.Selector)}",
        DrawingSegment d => $"drawing{SelectorToString(d.Selector)}",
        HyperlinkSegment h => $"hyperlink{SelectorToString(h.Selector)}",
        StyleSegment => "style",
        SectionSegment s => $"section{SelectorToString(s.Selector)}",
        HeaderFooterSegment hf => hf.Kind.ToString().ToLowerInvariant(),
        BookmarkSegment b => $"bookmark{SelectorToString(b.Selector)}",
        ChildrenSegment ch => $"children/{ch.Index}",
        _ => seg.GetType().Name
    };

    private static string SelectorToString(Selector sel) => sel switch
    {
        IndexSelector i => $"[{i.Index}]",
        TextContainsSelector t => $"[text~='{t.Text}']",
        TextEqualsSelector t => $"[text='{t.Text}']",
        StyleSelector s => $"[style='{s.StyleName}']",
        AllSelector => "[*]",
        _ => ""
    };
}
