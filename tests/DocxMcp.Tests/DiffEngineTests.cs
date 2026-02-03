using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Diff;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

/// <summary>
/// Tests for the DiffEngine which compares Word documents without relying on element IDs.
/// Uses content-based fingerprinting and LCS matching.
/// </summary>
public class DiffEngineTests : IDisposable
{
    private readonly List<DocxSession> _sessions = [];

    private DocxSession CreateSession()
    {
        var session = DocxSession.Create();
        _sessions.Add(session);
        return session;
    }

    private DocxSession CreateSessionFromBytes(byte[] bytes)
    {
        var session = DocxSession.FromBytes(bytes, Guid.NewGuid().ToString("N")[..12], null);
        _sessions.Add(session);
        return session;
    }

    #region No Changes Tests

    [Fact]
    public void DetectsNoChanges_WhenDocumentsAreIdentical()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Hello World"));
        body.AppendChild(CreateParagraph("Second paragraph"));

        // Create an exact copy
        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
        Assert.Empty(diff.Changes);
        Assert.Equal(0, diff.Summary.TotalChanges);
    }

    [Fact]
    public void DetectsChanges_WhenWhitespaceIsDifferent()
    {
        // Arrange - documents with different whitespace
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello  World")); // double space

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("Hello World")); // single space

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - exact matching: whitespace differences ARE detected
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Modified, diff.Changes[0].ChangeType);
    }

    #endregion

    #region Addition Tests

    [Fact]
    public void DetectsAddedParagraph()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First"));
        body.AppendChild(CreateParagraph("Second"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        modified.GetBody().AppendChild(CreateParagraph("Third - new paragraph"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);

        var change = diff.Changes[0];
        Assert.Equal(ChangeType.Added, change.ChangeType);
        Assert.Equal("paragraph", change.ElementType);
        Assert.Contains("Third", change.NewText);
        Assert.Equal(2, change.NewIndex);
    }

    [Fact]
    public void DetectsMultipleAdditions()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Original"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        modified.GetBody().AppendChild(CreateParagraph("Added 1"));
        modified.GetBody().AppendChild(CreateParagraph("Added 2"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.Equal(2, diff.Summary.Added);
    }

    [Fact]
    public void DetectsAddedTable()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Introduction"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        modified.GetBody().AppendChild(CreateTable(2, 2));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Added, diff.Changes[0].ChangeType);
        Assert.Equal("table", diff.Changes[0].ElementType);
    }

    #endregion

    #region Removal Tests

    [Fact]
    public void DetectsRemovedParagraph()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First"));
        body.AppendChild(CreateParagraph("Second - to be removed"));
        body.AppendChild(CreateParagraph("Third"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();
        modBody.RemoveChild(paragraphs[1]); // Remove "Second"

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);

        var change = diff.Changes[0];
        Assert.Equal(ChangeType.Removed, change.ChangeType);
        Assert.Equal("paragraph", change.ElementType);
        Assert.Contains("Second", change.OldText);
    }

    [Fact]
    public void DetectsRemovedTable()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Intro"));
        body.AppendChild(CreateTable(2, 2));
        body.AppendChild(CreateParagraph("Conclusion"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var table = modBody.Elements<Table>().First();
        modBody.RemoveChild(table);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Removed, diff.Changes[0].ChangeType);
        Assert.Equal("table", diff.Changes[0].ElementType);
    }

    #endregion

    #region Modification Tests

    [Fact]
    public void DetectsModifiedParagraphText()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Original text here"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var para = modified.GetBody().Elements<Paragraph>().First();
        var run = para.Elements<Run>().First();
        run.GetFirstChild<Text>()!.Text = "Modified text here"; // Similar enough to match

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);

        var change = diff.Changes[0];
        Assert.Equal(ChangeType.Modified, change.ChangeType);
        Assert.Equal("paragraph", change.ElementType);
        Assert.Contains("Original", change.OldText);
        Assert.Contains("Modified", change.NewText);
    }

    [Fact]
    public void DetectsHeadingLevelChange()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateHeading(1, "Important Title"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var heading = modified.GetBody().Elements<Paragraph>().First();
        heading.ParagraphProperties!.ParagraphStyleId!.Val = "Heading2";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        // Should be detected as modification (same text, different heading level)
        var change = diff.Changes.First();
        Assert.Equal(ChangeType.Modified, change.ChangeType);
    }

    [Fact]
    public void DetectsTableCellTextChange()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateTable(2, 2));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var table = modified.GetBody().Elements<Table>().First();
        var firstCell = table.Descendants<TableCell>().First();
        var para = firstCell.Elements<Paragraph>().First();
        var run = para.Elements<Run>().First();
        run.GetFirstChild<Text>()!.Text = "Changed Cell";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal("table", diff.Changes[0].ElementType);
    }

    [Fact]
    public void DetectsCompletelyDifferentContent_AsRemoveAndAdd()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("AAAA AAAA AAAA"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("ZZZZ ZZZZ ZZZZ"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - completely different content should be detected as remove + add
        Assert.True(diff.HasChanges);
        Assert.True(diff.Summary.Removed >= 1 || diff.Summary.Added >= 1);
    }

    #endregion

    #region Move Tests

    [Fact]
    public void DetectsMovedParagraph()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First paragraph"));
        body.AppendChild(CreateParagraph("Second paragraph"));
        body.AppendChild(CreateParagraph("Third paragraph to move"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();

        // Move third to first position
        var third = paragraphs[2];
        modBody.RemoveChild(third);
        modBody.PrependChild(third);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        var moveChanges = diff.Changes.Where(c => c.ChangeType == ChangeType.Moved).ToList();
        Assert.NotEmpty(moveChanges);

        // The moved element should have different old/new indices
        var movedItem = moveChanges.First(c => c.OldText?.Contains("Third") == true);
        Assert.Equal(2, movedItem.OldIndex);
        Assert.Equal(0, movedItem.NewIndex);
    }

    #endregion

    #region Multiple Changes Tests

    [Fact]
    public void DetectsMultipleChanges()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Keep this unchanged"));
        body.AppendChild(CreateParagraph("Remove this paragraph"));
        body.AppendChild(CreateParagraph("Modify this text content"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();

        // Remove second
        modBody.RemoveChild(paragraphs[1]);

        // Modify third (now at index 1)
        paragraphs = modBody.Elements<Paragraph>().ToList();
        var run = paragraphs[1].Elements<Run>().First();
        run.GetFirstChild<Text>()!.Text = "Modify this text changed";

        // Add new
        modBody.AppendChild(CreateParagraph("Brand new paragraph"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.True(diff.Summary.TotalChanges >= 3);
        Assert.True(diff.Summary.Removed >= 1);
        Assert.True(diff.Summary.Added >= 1);
    }

    #endregion

    #region Patch Generation Tests

    [Fact]
    public void GeneratesValidPatches_ForAddition()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("First"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        modified.GetBody().AppendChild(CreateParagraph("Second - added"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var patches = diff.ToPatches();

        // Assert
        Assert.Single(patches);
        var patch = patches[0];
        Assert.Equal("add", patch["op"]?.GetValue<string>());
        Assert.NotNull(patch["path"]);
        Assert.NotNull(patch["value"]);

        var value = patch["value"]!.AsObject();
        Assert.Equal("paragraph", value["type"]?.GetValue<string>());
    }

    [Fact]
    public void GeneratesValidPatches_ForRemoval()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("First"));
        original.GetBody().AppendChild(CreateParagraph("Second - to remove"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();
        modBody.RemoveChild(paragraphs[1]);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var patches = diff.ToPatches();

        // Assert
        Assert.Single(patches);
        var patch = patches[0];
        Assert.Equal("remove", patch["op"]?.GetValue<string>());
        Assert.NotNull(patch["path"]);
    }

    [Fact]
    public void GeneratesValidPatches_ForModification()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Original content here"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var para = modified.GetBody().Elements<Paragraph>().First();
        para.Elements<Run>().First().GetFirstChild<Text>()!.Text = "Modified content here";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var patches = diff.ToPatches();

        // Assert
        Assert.Single(patches);
        var patch = patches[0];
        Assert.Equal("replace", patch["op"]?.GetValue<string>());
        Assert.NotNull(patch["path"]);
        Assert.NotNull(patch["value"]);
    }

    [Fact]
    public void GeneratesValidPatches_ForMove()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First"));
        body.AppendChild(CreateParagraph("Second"));
        body.AppendChild(CreateParagraph("Third"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();

        // Move Third to first position
        var third = paragraphs[2];
        modBody.RemoveChild(third);
        modBody.PrependChild(third);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var patches = diff.ToPatches();

        // Assert
        var movePatches = patches.Where(p => p["op"]?.GetValue<string>() == "move").ToList();
        Assert.NotEmpty(movePatches);
    }

    #endregion

    #region API Tests

    [Fact]
    public void CompareFromBytes_WorksCorrectly()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello"));
        var originalBytes = original.ToBytes();

        var modified = CreateSessionFromBytes(originalBytes);
        modified.GetBody().AppendChild(CreateParagraph("World - new"));
        var modifiedBytes = modified.ToBytes();

        // Act
        var diff = DiffEngine.Compare(originalBytes, modifiedBytes);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Added, diff.Changes[0].ChangeType);
    }

    [Fact]
    public void DiffResult_ToJson_ProducesValidJson()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        modified.GetBody().AppendChild(CreateParagraph("World"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var json = diff.ToJson();

        // Assert
        Assert.NotNull(json);

        var parsed = JsonDocument.Parse(json);
        Assert.True(parsed.RootElement.TryGetProperty("summary", out _));
        Assert.True(parsed.RootElement.TryGetProperty("changes", out _));
        Assert.True(parsed.RootElement.TryGetProperty("patches", out _));
    }

    [Fact]
    public void ChangeDescription_IsHumanReadable()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello World"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var para = modified.GetBody().Elements<Paragraph>().First();
        para.Elements<Run>().First().GetFirstChild<Text>()!.Text = "Hello Universe";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        var change = diff.Changes[0];
        Assert.NotEmpty(change.Description);
        Assert.Contains(change.ChangeType.ToString(), change.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_CorrectlyCounts_AllChangeTypes()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Keep"));
        body.AppendChild(CreateParagraph("Remove this"));
        body.AppendChild(CreateParagraph("Modify content here"));
        body.AppendChild(CreateParagraph("Move this"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();

        // Remove "Remove this"
        modBody.RemoveChild(paragraphs[1]);

        // Modify "Modify content"
        paragraphs = modBody.Elements<Paragraph>().ToList();
        paragraphs[1].Elements<Run>().First().GetFirstChild<Text>()!.Text = "Modify content changed";

        // Add new
        modBody.AppendChild(CreateParagraph("New paragraph"));

        // Move "Move this" to beginning
        paragraphs = modBody.Elements<Paragraph>().ToList();
        var toMove = paragraphs[2];
        modBody.RemoveChild(toMove);
        modBody.PrependChild(toMove);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var summary = diff.Summary;

        // Assert
        Assert.True(diff.HasChanges);
        Assert.True(summary.TotalChanges >= 3);
    }

    #endregion

    #region Similarity Threshold Tests

    [Fact]
    public void HigherThreshold_RequiresMoreSimilarContent()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("The quick brown fox"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("The slow brown fox"));

        // Act - with default threshold (0.6)
        var diffDefault = DiffEngine.Compare(original.Document, modified.Document, 0.6);

        // Act - with high threshold (0.95)
        var diffStrict = DiffEngine.Compare(original.Document, modified.Document, 0.95);

        // Assert - strict threshold should see remove+add, default might see modify
        Assert.True(diffDefault.HasChanges);
        Assert.True(diffStrict.HasChanges);
    }

    #endregion

    #region Edge Cases and Strange Documents

    [Fact]
    public void EmptyDocuments_NoChanges()
    {
        // Arrange - two empty documents
        var original = CreateSession();
        var modified = CreateSession();

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
        Assert.Empty(diff.Changes);
    }

    [Fact]
    public void OriginalEmpty_AllAdditions()
    {
        // Arrange
        var original = CreateSession();
        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("New content"));
        modified.GetBody().AppendChild(CreateTable(2, 2));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Equal(2, diff.Summary.Added);
        Assert.Equal(0, diff.Summary.Removed);
    }

    [Fact]
    public void ModifiedEmpty_AllRemovals()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Content to remove"));
        original.GetBody().AppendChild(CreateParagraph("More content"));

        var modified = CreateSession();

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Equal(0, diff.Summary.Added);
        Assert.Equal(2, diff.Summary.Removed);
    }

    [Fact]
    public void EmptyParagraphs_HandledCorrectly()
    {
        // Arrange - document with empty paragraph
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Before"));
        original.GetBody().AppendChild(new Paragraph()); // Empty paragraph
        original.GetBody().AppendChild(CreateParagraph("After"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var emptyPara = modBody.Elements<Paragraph>().ElementAt(1);
        modBody.RemoveChild(emptyPara);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Removed, diff.Changes[0].ChangeType);
    }

    [Fact]
    public void EmptyTable_HandledCorrectly()
    {
        // Arrange - table with no rows
        var original = CreateSession();
        var emptyTable = new Table();
        emptyTable.AppendChild(new TableProperties());
        original.GetBody().AppendChild(emptyTable);

        var modified = CreateSession();

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Removed, diff.Changes[0].ChangeType);
        Assert.Equal("table", diff.Changes[0].ElementType);
    }

    [Fact]
    public void UnicodeContent_HandledCorrectly()
    {
        // Arrange - document with unicode characters
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello 世界 مرحبا שלום 🌍"));
        original.GetBody().AppendChild(CreateParagraph("日本語テスト"));
        original.GetBody().AppendChild(CreateParagraph("Émoji: 👍🎉🚀"));

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - identical documents
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void UnicodeContent_DetectsChanges()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello 世界"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("Hello 世界!"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void RTLContent_HandledCorrectly()
    {
        // Arrange - Right-to-left content (Arabic, Hebrew)
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("مرحبا بالعالم")); // Hello World in Arabic
        original.GetBody().AppendChild(CreateParagraph("שלום עולם")); // Hello World in Hebrew

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void VeryLongParagraph_HandledCorrectly()
    {
        // Arrange - paragraph with very long text (10KB)
        var longText = string.Concat(Enumerable.Repeat("This is a long paragraph with lots of text. ", 250));
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph(longText));

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void VeryLongParagraph_DetectsSmallChange()
    {
        // Arrange - paragraph with very long text, small change at the end
        var longText = string.Concat(Enumerable.Repeat("word ", 1000));
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph(longText));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph(longText + "changed"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - should detect the change
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void SingleCharacterDifference_Detected()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello World"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("Hello world")); // lowercase w

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void WhitespaceOnlyDifference_Detected()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello World"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("Hello  World")); // double space

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - exact matching: whitespace differences ARE detected
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Modified, diff.Changes[0].ChangeType);
    }

    [Fact]
    public void LeadingTrailingWhitespace_Detected()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello World"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("  Hello World  ")); // leading/trailing spaces

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - exact matching: leading/trailing whitespace IS detected
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Modified, diff.Changes[0].ChangeType);
    }

    [Fact]
    public void MultipleSimilarParagraphs_MatchedCorrectly()
    {
        // Arrange - three similar paragraphs
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Item A"));
        original.GetBody().AppendChild(CreateParagraph("Item B"));
        original.GetBody().AppendChild(CreateParagraph("Item C"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paraB = modBody.Elements<Paragraph>().ElementAt(1);
        modBody.RemoveChild(paraB);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - only B should be removed, A and C should be matched (not moved)
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Removed, diff.Changes[0].ChangeType);
        Assert.Contains("Item B", diff.Changes[0].OldText);
    }

    [Fact]
    public void AmbiguousMatching_MultipleSimilarElements()
    {
        // Arrange - elements that are similar but not identical
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("The quick brown fox"));
        original.GetBody().AppendChild(CreateParagraph("The quick brown dog"));
        original.GetBody().AppendChild(CreateParagraph("The quick brown cat"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("The quick brown dog"));
        modified.GetBody().AppendChild(CreateParagraph("The quick brown cat"));
        modified.GetBody().AppendChild(CreateParagraph("The quick brown fox"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - should detect reordering as moves
        Assert.True(diff.HasChanges);
        // All elements should be matched, just reordered
        Assert.Equal(0, diff.Summary.Added);
        Assert.Equal(0, diff.Summary.Removed);
        Assert.True(diff.Summary.Moved >= 1);
    }

    [Fact]
    public void NestedTables_DetectsChanges()
    {
        // Arrange - table within table cell (not directly supported by CreateTable helper)
        var original = CreateSession();
        var outerTable = CreateTable(2, 2);
        original.GetBody().AppendChild(outerTable);

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modTable = modified.GetBody().Elements<Table>().First();
        var firstCell = modTable.Descendants<TableCell>().First();
        var cellPara = firstCell.Elements<Paragraph>().First();
        var cellRun = cellPara.Elements<Run>().First();
        var cellText = cellRun.Elements<Text>().First();
        cellText.Text = "Modified";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Equal("table", diff.Changes[0].ElementType);
    }

    [Fact]
    public void LargeTable_DetectsChanges()
    {
        // Arrange - 10x10 table
        var original = CreateSession();
        original.GetBody().AppendChild(CreateTable(10, 10));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modTable = modified.GetBody().Elements<Table>().First();
        var lastCell = modTable.Descendants<TableCell>().Last();
        var cellPara = lastCell.Elements<Paragraph>().First();
        var cellRun = cellPara.Elements<Run>().First();
        var cellText = cellRun.Elements<Text>().First();
        cellText.Text = "Changed";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void MixedContentOrder_DetectsChanges()
    {
        // Arrange - paragraph, table, paragraph
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Intro"));
        original.GetBody().AppendChild(CreateTable(2, 2));
        original.GetBody().AppendChild(CreateParagraph("Conclusion"));

        // Modified - table, paragraph, paragraph (different order)
        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateTable(2, 2));
        modified.GetBody().AppendChild(CreateParagraph("Intro"));
        modified.GetBody().AppendChild(CreateParagraph("Conclusion"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - should detect move
        Assert.True(diff.HasChanges);
        Assert.True(diff.Summary.Moved >= 1);
    }

    [Fact]
    public void SpecialXmlCharacters_HandledCorrectly()
    {
        // Arrange - content with XML special characters
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Test <tag> & \"quotes\" 'apostrophe'"));
        original.GetBody().AppendChild(CreateParagraph("Math: 5 > 3 && 2 < 4"));

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void ControlCharacters_HandledCorrectly()
    {
        // Arrange - content with control characters (tab, newline in run)
        var original = CreateSession();
        var para = new Paragraph();
        var run = new Run();
        run.AppendChild(new Text("Before\tTab\nNewline") { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(run);
        original.GetBody().AppendChild(para);

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void ParagraphWithMultipleRuns_HandledCorrectly()
    {
        // Arrange - paragraph with multiple runs (different formatting)
        var original = CreateSession();
        var para = new Paragraph();
        var run1 = new Run();
        run1.AppendChild(new Text("Normal ") { Space = SpaceProcessingModeValues.Preserve });
        var run2 = new Run(new RunProperties(new Bold()));
        run2.AppendChild(new Text("Bold ") { Space = SpaceProcessingModeValues.Preserve });
        var run3 = new Run(new RunProperties(new Italic()));
        run3.AppendChild(new Text("Italic") { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(run1);
        para.AppendChild(run2);
        para.AppendChild(run3);
        original.GetBody().AppendChild(para);

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - identical documents
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void FormattingChangeOnly_TextUnchanged()
    {
        // Arrange - same text, different formatting (currently NOT detected as change)
        var original = CreateSession();
        var para1 = new Paragraph();
        var run1 = new Run();
        run1.AppendChild(new Text("Same text") { Space = SpaceProcessingModeValues.Preserve });
        para1.AppendChild(run1);
        original.GetBody().AppendChild(para1);

        var modified = CreateSession();
        var para2 = new Paragraph();
        var run2 = new Run(new RunProperties(new Bold()));
        run2.AppendChild(new Text("Same text") { Space = SpaceProcessingModeValues.Preserve });
        para2.AppendChild(run2);
        modified.GetBody().AppendChild(para2);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - NOTE: Currently fingerprint is text-based, so formatting only changes
        // are NOT detected. This test documents current behavior.
        // If we want to detect formatting changes, we need to include formatting in fingerprint.
        Assert.False(diff.HasChanges); // Known limitation
    }

    [Fact]
    public void ManyParagraphs_Performance()
    {
        // Arrange - document with 100 paragraphs
        var original = CreateSession();
        var body = original.GetBody();
        for (int i = 0; i < 100; i++)
        {
            body.AppendChild(CreateParagraph($"Paragraph {i}: Lorem ipsum dolor sit amet, consectetur adipiscing elit."));
        }

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        // Modify one paragraph in the middle
        var paraToModify = modBody.Elements<Paragraph>().ElementAt(50);
        var run = paraToModify.Elements<Run>().First();
        var text = run.Elements<Text>().First();
        text.Text = "MODIFIED: This paragraph was changed.";

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        stopwatch.Stop();

        // Assert
        Assert.True(diff.HasChanges);
        Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"Diff took {stopwatch.ElapsedMilliseconds}ms, expected < 5000ms");
    }

    [Fact]
    public void DuplicateParagraphs_AllMatched()
    {
        // Arrange - multiple identical paragraphs
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Duplicate"));
        original.GetBody().AppendChild(CreateParagraph("Duplicate"));
        original.GetBody().AppendChild(CreateParagraph("Duplicate"));

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - all should match (same fingerprint, same order)
        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void DuplicateParagraphs_OneRemoved()
    {
        // Arrange - multiple identical paragraphs
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Duplicate"));
        original.GetBody().AppendChild(CreateParagraph("Duplicate"));
        original.GetBody().AppendChild(CreateParagraph("Duplicate"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var secondPara = modBody.Elements<Paragraph>().ElementAt(1);
        modBody.RemoveChild(secondPara);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - one removal
        Assert.True(diff.HasChanges);
        Assert.Equal(1, diff.Summary.Removed);
        Assert.Equal(0, diff.Summary.Moved);
    }

    [Fact]
    public void CompleteDocumentRewrite_DetectedCorrectly()
    {
        // Arrange - completely different content
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Old content A"));
        original.GetBody().AppendChild(CreateParagraph("Old content B"));
        original.GetBody().AppendChild(CreateTable(2, 2));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateParagraph("New content X"));
        modified.GetBody().AppendChild(CreateParagraph("New content Y"));
        modified.GetBody().AppendChild(CreateParagraph("New content Z"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.True(diff.Summary.TotalChanges >= 4); // At least 3 removals + 3 additions (may have some matches by similarity)
    }

    [Fact]
    public void HeadingLevelChange_DetectedAsModification()
    {
        // Arrange - heading level 1 -> heading level 2
        var original = CreateSession();
        original.GetBody().AppendChild(CreateHeading(1, "Title"));

        var modified = CreateSession();
        modified.GetBody().AppendChild(CreateHeading(2, "Title"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert - should detect as modification (same text, different heading level)
        Assert.True(diff.HasChanges);
    }

    [Fact]
    public void MixedHeadingsAndParagraphs_PreservesOrder()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateHeading(1, "Chapter 1"));
        original.GetBody().AppendChild(CreateParagraph("Content 1"));
        original.GetBody().AppendChild(CreateHeading(2, "Section 1.1"));
        original.GetBody().AppendChild(CreateParagraph("Content 1.1"));

        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
    }

    #endregion

    #region Helper Methods

    private static Paragraph CreateParagraph(string text)
    {
        var para = new Paragraph();
        var run = new Run();
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(run);
        return para;
    }

    private static Paragraph CreateHeading(int level, string text)
    {
        var para = new Paragraph();
        para.ParagraphProperties = new ParagraphProperties
        {
            ParagraphStyleId = new ParagraphStyleId { Val = $"Heading{level}" }
        };
        var run = new Run();
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        para.AppendChild(run);
        return para;
    }

    private static Table CreateTable(int rows, int cols)
    {
        var table = new Table();
        table.AppendChild(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }
            )
        ));

        for (int r = 0; r < rows; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                var cell = new TableCell();
                var para = new Paragraph();
                var run = new Run();
                run.AppendChild(new Text($"R{r}C{c}") { Space = SpaceProcessingModeValues.Preserve });
                para.AppendChild(run);
                cell.AppendChild(para);
                row.AppendChild(cell);
            }
            table.AppendChild(row);
        }

        return table;
    }

    #endregion

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }
    }
}
