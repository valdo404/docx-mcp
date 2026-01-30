namespace DocxMcp.Paths;

/// <summary>
/// Selectors for targeting elements within a segment type.
/// </summary>
public abstract record Selector;

/// <summary>Select by zero-based index. Negative indexes count from the end.</summary>
public record IndexSelector(int Index) : Selector;

/// <summary>Select elements whose text contains the given substring.</summary>
public record TextContainsSelector(string Text) : Selector;

/// <summary>Select elements whose text exactly equals the given string.</summary>
public record TextEqualsSelector(string Text) : Selector;

/// <summary>Select elements with the given style name.</summary>
public record StyleSelector(string StyleName) : Selector;

/// <summary>Select all elements of this type (wildcard).</summary>
public record AllSelector : Selector;
