namespace DocxMcp.Paths;

/// <summary>
/// Validates structural constraints on typed paths.
/// Ensures segments follow the OOXML parent-child hierarchy.
/// </summary>
public static class PathSchema
{
    /// <summary>
    /// Defines which segment types can follow a given parent segment type.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<Type>> AllowedChildren = new()
    {
        [typeof(BodySegment)] = [
            typeof(ParagraphSegment),
            typeof(HeadingSegment),
            typeof(TableSegment),
            typeof(DrawingSegment),
            typeof(SectionSegment),
            typeof(ChildrenSegment),
            typeof(StyleSegment),        // /body/style — document defaults
            typeof(HeaderFooterSegment),
            typeof(BookmarkSegment),
        ],
        [typeof(TableSegment)] = [
            typeof(RowSegment),
            typeof(StyleSegment),
        ],
        [typeof(RowSegment)] = [
            typeof(CellSegment),
        ],
        [typeof(CellSegment)] = [
            typeof(ParagraphSegment),
            typeof(HeadingSegment),
            typeof(TableSegment), // nested tables
        ],
        [typeof(ParagraphSegment)] = [
            typeof(RunSegment),
            typeof(HyperlinkSegment),
            typeof(DrawingSegment),
            typeof(StyleSegment),
            typeof(BookmarkSegment),
        ],
        [typeof(HeadingSegment)] = [
            typeof(RunSegment),
            typeof(StyleSegment),
            typeof(BookmarkSegment),
        ],
        [typeof(RunSegment)] = [
            typeof(StyleSegment),
            typeof(DrawingSegment),
        ],
        [typeof(HyperlinkSegment)] = [
            typeof(RunSegment),
        ],
        [typeof(HeaderFooterSegment)] = [
            typeof(ParagraphSegment),
            typeof(TableSegment),
        ],
    };

    /// <summary>
    /// Validate that each segment is a valid child of its predecessor.
    /// Throws FormatException with a precise error message on invalid structure.
    /// </summary>
    public static void Validate(IReadOnlyList<PathSegment> segments)
    {
        if (segments.Count == 0)
            throw new FormatException("Path must have at least one segment.");

        // First segment must be body or header/footer
        if (segments[0] is not BodySegment and not HeaderFooterSegment)
            throw new FormatException(
                $"Path must start with /body or /header or /footer, got /{segments[0].GetType().Name}.");

        for (int i = 1; i < segments.Count; i++)
        {
            var parent = segments[i - 1];
            var child = segments[i];
            var parentType = parent.GetType();
            var childType = child.GetType();

            if (!AllowedChildren.TryGetValue(parentType, out var allowed) || !allowed.Contains(childType))
            {
                var parentName = SegmentTypeName(parentType);
                var childName = SegmentTypeName(childType);
                throw new FormatException(
                    $"{childName} cannot be a direct child of {parentName}. " +
                    $"Allowed children: {(allowed is not null ? string.Join(", ", allowed.Select(SegmentTypeName)) : "none")}.");
            }
        }
    }

    private static string SegmentTypeName(Type t) =>
        t.Name.Replace("Segment", "");
}
