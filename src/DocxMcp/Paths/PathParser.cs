using System.Text.RegularExpressions;

namespace DocxMcp.Paths;

/// <summary>
/// Parses path strings like "/body/paragraph[0]/run[0]/style" into typed DocxPath objects.
/// </summary>
public static partial class PathParser
{
    // Matches: name[selector] or just name
    [GeneratedRegex(@"^(?<name>\w+)(?:\[(?<sel>[^\]]+)\])?$")]
    private static partial Regex SegmentPattern();

    // Matches: text~='value' or text='value'
    [GeneratedRegex(@"^text(?<op>[~]?)='(?<val>.*)'$")]
    private static partial Regex TextSelectorPattern();

    // Matches: style='value'
    [GeneratedRegex(@"^style='(?<val>.*)'$")]
    private static partial Regex StyleSelectorPattern();

    // Matches: level=N
    [GeneratedRegex(@"^level=(?<val>\d+)$")]
    private static partial Regex LevelPattern();

    // Matches: type=value (for header/footer)
    [GeneratedRegex(@"^type=(?<val>\w+)$")]
    private static partial Regex TypePattern();

    public static DocxPath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new FormatException("Path cannot be empty.");

        // Normalize: remove leading slash
        var normalized = path.TrimStart('/');
        if (normalized.Length == 0)
            throw new FormatException("Path cannot be just '/'.");

        // Special paths
        if (normalized is "styles" or "metadata" or "fields")
        {
            return new DocxPath([new BodySegment(), ParseSpecialSegment(normalized)]);
        }

        var parts = SplitPath(normalized);
        var segments = new List<PathSegment>();

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];

            // Handle /body/children/N
            if (part == "children" && i + 1 < parts.Count && int.TryParse(parts[i + 1], out var childIdx))
            {
                segments.Add(new ChildrenSegment(childIdx));
                i++; // skip the index part
                continue;
            }

            var segment = ParseSegment(part);
            segments.Add(segment);
        }

        // Validate structure
        PathSchema.Validate(segments);

        return new DocxPath(segments);
    }

    private static List<string> SplitPath(string path)
    {
        // Split on '/' but be careful about brackets
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        int bracketDepth = 0;

        foreach (var ch in path)
        {
            if (ch == '[') bracketDepth++;
            else if (ch == ']') bracketDepth--;

            if (ch == '/' && bracketDepth == 0)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }

    private static PathSegment ParseSegment(string part)
    {
        var match = SegmentPattern().Match(part);
        if (!match.Success)
            throw new FormatException($"Invalid path segment: '{part}'");

        var name = match.Groups["name"].Value;
        var selStr = match.Groups["sel"].Success ? match.Groups["sel"].Value : null;

        return name.ToLowerInvariant() switch
        {
            "body" => new BodySegment(),
            "paragraph" or "p" => new ParagraphSegment(ParseSelector(selStr)),
            "heading" => ParseHeading(selStr),
            "table" => new TableSegment(ParseSelector(selStr)),
            "row" => new RowSegment(ParseSelector(selStr)),
            "cell" => new CellSegment(ParseSelector(selStr)),
            "run" => new RunSegment(ParseSelector(selStr)),
            "drawing" => new DrawingSegment(ParseSelector(selStr)),
            "hyperlink" => new HyperlinkSegment(ParseSelector(selStr)),
            "style" => new StyleSegment(),
            "section" => new SectionSegment(ParseSelector(selStr)),
            "header" => ParseHeaderFooter(selStr, isHeader: true),
            "footer" => ParseHeaderFooter(selStr, isHeader: false),
            "bookmark" => new BookmarkSegment(ParseSelector(selStr)),
            "comment" => new CommentSegment(ParseSelector(selStr)),
            "footnote" => new FootnoteSegment(ParseSelector(selStr)),
            _ => throw new FormatException($"Unknown segment type: '{name}'")
        };
    }

    private static PathSegment ParseSpecialSegment(string name) => name switch
    {
        "styles" => new StyleSegment(),
        "metadata" => new StyleSegment(), // Reuse — handled at query level
        "fields" => new StyleSegment(),   // Reuse — handled at query level
        _ => throw new FormatException($"Unknown special path: '/{name}'")
    };

    private static PathSegment ParseHeading(string? selStr)
    {
        if (selStr is null)
            return new HeadingSegment(0, new AllSelector());

        // Check for level=N
        var levelMatch = LevelPattern().Match(selStr);
        if (levelMatch.Success)
        {
            var level = int.Parse(levelMatch.Groups["val"].Value);
            return new HeadingSegment(level, new AllSelector());
        }

        // Could be a combined selector like "level=2,0" — for now just index
        return new HeadingSegment(0, ParseSelector(selStr));
    }

    private static PathSegment ParseHeaderFooter(string? selStr, bool isHeader)
    {
        if (selStr is null)
        {
            return new HeaderFooterSegment(
                isHeader ? HeaderFooterKind.DefaultHeader : HeaderFooterKind.DefaultFooter);
        }

        var typeMatch = TypePattern().Match(selStr);
        if (!typeMatch.Success)
            throw new FormatException($"Invalid header/footer selector: [{selStr}]. Expected [type=default|first|even].");

        var typeVal = typeMatch.Groups["val"].Value.ToLowerInvariant();
        var kind = (isHeader, typeVal) switch
        {
            (true, "default") => HeaderFooterKind.DefaultHeader,
            (true, "first") => HeaderFooterKind.FirstHeader,
            (true, "even") => HeaderFooterKind.EvenHeader,
            (false, "default") => HeaderFooterKind.DefaultFooter,
            (false, "first") => HeaderFooterKind.FirstFooter,
            (false, "even") => HeaderFooterKind.EvenFooter,
            _ => throw new FormatException($"Unknown header/footer type: '{typeVal}'")
        };

        return new HeaderFooterSegment(kind);
    }

    private static Selector ParseSelector(string? selStr)
    {
        if (selStr is null)
            return new IndexSelector(0);

        if (selStr == "*")
            return new AllSelector();

        // Integer index
        if (int.TryParse(selStr, out var index))
            return new IndexSelector(index);

        // text~='...' or text='...'
        var textMatch = TextSelectorPattern().Match(selStr);
        if (textMatch.Success)
        {
            var op = textMatch.Groups["op"].Value;
            var val = textMatch.Groups["val"].Value;
            return op == "~"
                ? new TextContainsSelector(val)
                : new TextEqualsSelector(val);
        }

        // style='...'
        var styleMatch = StyleSelectorPattern().Match(selStr);
        if (styleMatch.Success)
            return new StyleSelector(styleMatch.Groups["val"].Value);

        throw new FormatException($"Invalid selector: [{selStr}]");
    }
}
