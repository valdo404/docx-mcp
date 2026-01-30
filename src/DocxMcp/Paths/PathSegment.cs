namespace DocxMcp.Paths;

/// <summary>
/// Base type for all path segments in the typed path model.
/// Each segment maps to a specific Open XML element kind.
/// </summary>
public abstract record PathSegment;

public record BodySegment : PathSegment;
public record ParagraphSegment(Selector Selector) : PathSegment;
public record HeadingSegment(int Level, Selector Selector) : PathSegment;
public record TableSegment(Selector Selector) : PathSegment;
public record RowSegment(Selector Selector) : PathSegment;
public record CellSegment(Selector Selector) : PathSegment;
public record RunSegment(Selector Selector) : PathSegment;
public record DrawingSegment(Selector Selector) : PathSegment;
public record HyperlinkSegment(Selector Selector) : PathSegment;
public record StyleSegment : PathSegment;
public record SectionSegment(Selector Selector) : PathSegment;
public record HeaderFooterSegment(HeaderFooterKind Kind) : PathSegment;
public record BookmarkSegment(Selector Selector) : PathSegment;
public record CommentSegment(Selector Selector) : PathSegment;
public record FootnoteSegment(Selector Selector) : PathSegment;

/// <summary>
/// Special segment for positional insertion: /body/children/N
/// </summary>
public record ChildrenSegment(int Index) : PathSegment;

public enum HeaderFooterKind
{
    DefaultHeader,
    FirstHeader,
    EvenHeader,
    DefaultFooter,
    FirstFooter,
    EvenFooter
}
