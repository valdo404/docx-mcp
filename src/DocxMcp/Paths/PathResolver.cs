using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxMcp.Paths;

/// <summary>
/// Resolves a typed DocxPath to the corresponding Open XML element(s).
/// </summary>
public static class PathResolver
{
    /// <summary>
    /// Resolve a path to its target element(s).
    /// Returns a list because selectors like [*] can match multiple elements.
    /// </summary>
    public static List<OpenXmlElement> Resolve(DocxPath path, WordprocessingDocument doc)
    {
        var mainPart = doc.MainDocumentPart
            ?? throw new InvalidOperationException("Document has no MainDocumentPart.");
        var body = mainPart.Document?.Body
            ?? throw new InvalidOperationException("Document has no Body.");

        // Start resolution
        var current = new List<OpenXmlElement> { body };

        // Skip the first segment if it's BodySegment (we already have the body)
        var startIdx = path.Segments[0] is BodySegment ? 1 : 0;

        // Handle header/footer as root
        if (path.Segments[0] is HeaderFooterSegment hfs)
        {
            current = ResolveHeaderFooter(hfs, mainPart);
            startIdx = 1;
        }

        for (int i = startIdx; i < path.Segments.Count; i++)
        {
            var segment = path.Segments[i];
            var next = new List<OpenXmlElement>();

            foreach (var parent in current)
            {
                var resolved = ResolveSegment(segment, parent, doc);
                next.AddRange(resolved);
            }

            if (next.Count == 0)
                throw new InvalidOperationException(
                    $"Path resolution failed at segment {i}: no elements found for {segment}.");

            current = next;
        }

        return current;
    }

    /// <summary>
    /// Resolve a path for insertion — returns the parent element and the target index.
    /// Used by the "add" patch operation.
    /// </summary>
    public static (OpenXmlElement Parent, int Index) ResolveForInsert(DocxPath path, WordprocessingDocument doc)
    {
        if (!path.IsChildrenPath)
            throw new InvalidOperationException("Insert paths must end with /children/N.");

        var childrenSeg = (ChildrenSegment)path.Leaf;

        // Resolve everything except the last (children/N) segment
        var parentPath = new DocxPath(path.Segments.Take(path.Segments.Count - 1).ToList());
        var parents = Resolve(parentPath, doc);

        if (parents.Count != 1)
            throw new InvalidOperationException(
                $"Insert path must resolve to exactly one parent, got {parents.Count}.");

        return (parents[0], childrenSeg.Index);
    }

    private static List<OpenXmlElement> ResolveHeaderFooter(
        HeaderFooterSegment seg, MainDocumentPart mainPart)
    {
        // For now, get the default header/footer parts
        var results = new List<OpenXmlElement>();

        switch (seg.Kind)
        {
            case HeaderFooterKind.DefaultHeader:
            case HeaderFooterKind.FirstHeader:
            case HeaderFooterKind.EvenHeader:
                foreach (var hp in mainPart.HeaderParts)
                {
                    if (hp.Header is not null)
                        results.Add(hp.Header);
                }
                break;

            case HeaderFooterKind.DefaultFooter:
            case HeaderFooterKind.FirstFooter:
            case HeaderFooterKind.EvenFooter:
                foreach (var fp in mainPart.FooterParts)
                {
                    if (fp.Footer is not null)
                        results.Add(fp.Footer);
                }
                break;
        }

        if (results.Count == 0)
            throw new InvalidOperationException($"No {seg.Kind} found in document.");

        return results;
    }

    private static List<OpenXmlElement> ResolveSegment(
        PathSegment segment, OpenXmlElement parent, WordprocessingDocument doc)
    {
        return segment switch
        {
            ParagraphSegment ps => SelectElements<Paragraph>(parent, ps.Selector),
            HeadingSegment hs => SelectHeadings(parent, hs),
            TableSegment ts => SelectElements<Table>(parent, ts.Selector),
            RowSegment rs => SelectElements<TableRow>(parent, rs.Selector),
            CellSegment cs => SelectElements<TableCell>(parent, cs.Selector),
            RunSegment rs => SelectElements<Run>(parent, rs.Selector),
            HyperlinkSegment hs => SelectElements<Hyperlink>(parent, hs.Selector),
            DrawingSegment ds => SelectElements<Drawing>(parent, ds.Selector),
            StyleSegment => ResolveStyle(parent),
            SectionSegment ss => SelectElements<SectionProperties>(parent, ss.Selector),
            BookmarkSegment bs => SelectElements<BookmarkStart>(parent, bs.Selector),
            _ => throw new InvalidOperationException($"Cannot resolve segment type: {segment.GetType().Name}")
        };
    }

    private static List<OpenXmlElement> SelectElements<T>(
        OpenXmlElement parent, Selector selector) where T : OpenXmlElement
    {
        var all = parent.Elements<T>().ToList();
        return ApplySelector(all, selector);
    }

    private static List<OpenXmlElement> SelectHeadings(
        OpenXmlElement parent, HeadingSegment hs)
    {
        var paragraphs = parent.Elements<Paragraph>().ToList();

        // Filter to heading paragraphs
        var headings = paragraphs.Where(p =>
        {
            var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            if (style is null) return false;

            // Match "Heading1", "Heading2", etc.
            if (!style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
                return false;

            if (hs.Level > 0)
            {
                var levelStr = style["Heading".Length..];
                return int.TryParse(levelStr, out var level) && level == hs.Level;
            }

            return true;
        }).Cast<OpenXmlElement>().ToList();

        return ApplySelector(headings, hs.Selector);
    }

    private static List<OpenXmlElement> ResolveStyle(OpenXmlElement parent)
    {
        // Return paragraph or run properties
        if (parent is Paragraph p)
        {
            var props = p.ParagraphProperties;
            if (props is not null)
                return [props];
            // Create empty props if needed
            var newProps = new ParagraphProperties();
            p.PrependChild(newProps);
            return [newProps];
        }

        if (parent is Run r)
        {
            var props = r.RunProperties;
            if (props is not null)
                return [props];
            var newProps = new RunProperties();
            r.PrependChild(newProps);
            return [newProps];
        }

        if (parent is Table t)
        {
            var props = t.GetFirstChild<TableProperties>();
            if (props is not null)
                return [props];
            var newProps = new TableProperties();
            t.PrependChild(newProps);
            return [newProps];
        }

        throw new InvalidOperationException(
            $"Cannot resolve /style on element type: {parent.GetType().Name}");
    }

    private static List<OpenXmlElement> ApplySelector<T>(
        List<T> elements, Selector selector) where T : OpenXmlElement
    {
        return selector switch
        {
            AllSelector => elements.Cast<OpenXmlElement>().ToList(),

            IndexSelector idx => ApplyIndexSelector(elements, idx),

            TextContainsSelector tc => elements
                .Where(e => e.InnerText.Contains(tc.Text, StringComparison.OrdinalIgnoreCase))
                .Cast<OpenXmlElement>()
                .ToList(),

            TextEqualsSelector te => elements
                .Where(e => e.InnerText.Equals(te.Text, StringComparison.OrdinalIgnoreCase))
                .Cast<OpenXmlElement>()
                .ToList(),

            StyleSelector ss => elements
                .Where(e => MatchesStyle(e, ss.StyleName))
                .Cast<OpenXmlElement>()
                .ToList(),

            _ => throw new InvalidOperationException($"Unknown selector type: {selector.GetType().Name}")
        };
    }

    private static List<OpenXmlElement> ApplyIndexSelector<T>(
        List<T> elements, IndexSelector idx) where T : OpenXmlElement
    {
        var index = idx.Index;
        if (index < 0) index = elements.Count + index;

        if (index < 0 || index >= elements.Count)
            throw new InvalidOperationException(
                $"Index {idx.Index} out of range (0..{elements.Count - 1}).");

        return [elements[index]];
    }

    private static bool MatchesStyle(OpenXmlElement element, string styleName)
    {
        if (element is Paragraph p)
            return p.ParagraphProperties?.ParagraphStyleId?.Val?.Value == styleName;
        if (element is Run r)
            return r.RunProperties?.RunStyle?.Val?.Value == styleName;
        if (element is Table t)
            return t.GetFirstChild<TableProperties>()?.TableStyle?.Val?.Value == styleName;
        return false;
    }
}
