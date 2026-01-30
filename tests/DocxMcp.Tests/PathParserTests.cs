using DocxMcp.Paths;
using Xunit;

namespace DocxMcp.Tests;

public class PathParserTests
{
    [Fact]
    public void ParseBody()
    {
        var path = DocxPath.Parse("/body");
        Assert.Single(path.Segments);
        Assert.IsType<BodySegment>(path.Segments[0]);
    }

    [Fact]
    public void ParseParagraphByIndex()
    {
        var path = DocxPath.Parse("/body/paragraph[0]");
        Assert.Equal(2, path.Segments.Count);
        Assert.IsType<BodySegment>(path.Segments[0]);
        var p = Assert.IsType<ParagraphSegment>(path.Segments[1]);
        var sel = Assert.IsType<IndexSelector>(p.Selector);
        Assert.Equal(0, sel.Index);
    }

    [Fact]
    public void ParseNegativeIndex()
    {
        var path = DocxPath.Parse("/body/paragraph[-1]");
        var p = Assert.IsType<ParagraphSegment>(path.Segments[1]);
        var sel = Assert.IsType<IndexSelector>(p.Selector);
        Assert.Equal(-1, sel.Index);
    }

    [Fact]
    public void ParseTextContainsSelector()
    {
        var path = DocxPath.Parse("/body/paragraph[text~='hello world']");
        var p = Assert.IsType<ParagraphSegment>(path.Segments[1]);
        var sel = Assert.IsType<TextContainsSelector>(p.Selector);
        Assert.Equal("hello world", sel.Text);
    }

    [Fact]
    public void ParseTextEqualsSelector()
    {
        var path = DocxPath.Parse("/body/paragraph[text='exact']");
        var p = Assert.IsType<ParagraphSegment>(path.Segments[1]);
        var sel = Assert.IsType<TextEqualsSelector>(p.Selector);
        Assert.Equal("exact", sel.Text);
    }

    [Fact]
    public void ParseStyleSelector()
    {
        var path = DocxPath.Parse("/body/paragraph[style='Heading1']");
        var p = Assert.IsType<ParagraphSegment>(path.Segments[1]);
        Assert.IsType<StyleSelector>(p.Selector);
    }

    [Fact]
    public void ParseAllSelector()
    {
        var path = DocxPath.Parse("/body/paragraph[*]");
        var p = Assert.IsType<ParagraphSegment>(path.Segments[1]);
        Assert.IsType<AllSelector>(p.Selector);
    }

    [Fact]
    public void ParseHeadingWithLevel()
    {
        var path = DocxPath.Parse("/body/heading[level=2]");
        var h = Assert.IsType<HeadingSegment>(path.Segments[1]);
        Assert.Equal(2, h.Level);
    }

    [Fact]
    public void ParseTableRowCell()
    {
        var path = DocxPath.Parse("/body/table[0]/row[1]/cell[0]");
        Assert.Equal(4, path.Segments.Count);
        Assert.IsType<BodySegment>(path.Segments[0]);
        Assert.IsType<TableSegment>(path.Segments[1]);
        Assert.IsType<RowSegment>(path.Segments[2]);
        Assert.IsType<CellSegment>(path.Segments[3]);
    }

    [Fact]
    public void ParseRunStyle()
    {
        var path = DocxPath.Parse("/body/paragraph[0]/run[0]/style");
        Assert.Equal(4, path.Segments.Count);
        Assert.IsType<StyleSegment>(path.Segments[3]);
    }

    [Fact]
    public void ParseHeaderFooter()
    {
        var path = DocxPath.Parse("/header[type=default]");
        Assert.Single(path.Segments);
        var hf = Assert.IsType<HeaderFooterSegment>(path.Segments[0]);
        Assert.Equal(HeaderFooterKind.DefaultHeader, hf.Kind);
    }

    [Fact]
    public void ParseChildrenPath()
    {
        var path = DocxPath.Parse("/body/children/3");
        Assert.True(path.IsChildrenPath);
        var ch = Assert.IsType<ChildrenSegment>(path.Segments[1]);
        Assert.Equal(3, ch.Index);
    }

    [Fact]
    public void RejectInvalidHierarchy()
    {
        // Cell cannot be direct child of body
        var ex = Assert.Throws<FormatException>(() => DocxPath.Parse("/body/cell[0]"));
        Assert.Contains("cannot be a direct child", ex.Message);
    }

    [Fact]
    public void RejectTableParagraphDirect()
    {
        // Paragraph cannot be direct child of table (must go through row/cell)
        var ex = Assert.Throws<FormatException>(() => DocxPath.Parse("/body/table[0]/paragraph[0]"));
        Assert.Contains("cannot be a direct child", ex.Message);
    }

    [Fact]
    public void RejectEmptyPath()
    {
        Assert.Throws<FormatException>(() => DocxPath.Parse(""));
    }

    [Fact]
    public void ParseRoundTrip()
    {
        var original = "/body/table[0]/row[1]/cell[0]";
        var path = DocxPath.Parse(original);
        var str = path.ToString();
        // Should produce a valid path (exact format may differ but should re-parse)
        var reparsed = DocxPath.Parse(str);
        Assert.Equal(path.Segments.Count, reparsed.Segments.Count);
    }
}
