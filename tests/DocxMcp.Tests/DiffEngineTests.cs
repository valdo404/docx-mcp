using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Diff;
using DocxMcp.Helpers;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class DiffEngineTests : IDisposable
{
    private readonly List<DocxSession> _sessions = [];

    private DocxSession CreateSession()
    {
        var session = DocxSession.Create();
        _sessions.Add(session);
        return session;
    }

    [Fact]
    public void DetectsNoChanges_WhenDocumentsAreIdentical()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Hello World"));
        body.AppendChild(CreateParagraph("Second paragraph"));

        // Create a copy
        var modified = CreateSessionFromBytes(original.ToBytes());

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.False(diff.HasChanges);
        Assert.Empty(diff.Changes);
        Assert.Equal(0, diff.Summary.TotalChanges);
    }

    [Fact]
    public void DetectsAddedParagraph()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First"));
        body.AppendChild(CreateParagraph("Second"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var newPara = CreateParagraph("Third");
        modBody.AppendChild(newPara);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);

        var change = diff.Changes[0];
        Assert.Equal(ChangeType.Added, change.ChangeType);
        Assert.Equal("paragraph", change.ElementType);
        Assert.Equal("Third", change.NewText);
        Assert.Equal(2, change.NewIndex);
    }

    [Fact]
    public void DetectsRemovedParagraph()
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
        modBody.RemoveChild(paragraphs[1]); // Remove "Second"

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);

        var change = diff.Changes[0];
        Assert.Equal(ChangeType.Removed, change.ChangeType);
        Assert.Equal("paragraph", change.ElementType);
        Assert.Equal("Second", change.OldText);
        Assert.Equal(1, change.OldIndex);
    }

    [Fact]
    public void DetectsModifiedParagraphText()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        var para = CreateParagraph("Original text");
        body.AppendChild(para);
        var paraId = ElementIdManager.GetId(para);

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var modPara = modBody.Elements<Paragraph>().First();

        // Modify the text
        var run = modPara.Elements<Run>().First();
        var text = run.GetFirstChild<Text>()!;
        text.Text = "Modified text";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);

        var change = diff.Changes[0];
        Assert.Equal(ChangeType.Modified, change.ChangeType);
        Assert.Equal("paragraph", change.ElementType);
        Assert.Equal("Original text", change.OldText);
        Assert.Equal("Modified text", change.NewText);
        Assert.Equal(paraId, change.ElementId);
    }

    [Fact]
    public void DetectsMovedParagraph()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First"));
        body.AppendChild(CreateParagraph("Second"));
        body.AppendChild(CreateParagraph("Third"));

        var originalParaIds = body.Elements<Paragraph>()
            .Select(p => ElementIdManager.GetId(p))
            .ToList();

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();

        // Move "Third" to the beginning
        var third = paragraphs[2];
        modBody.RemoveChild(third);
        modBody.InsertChildAt(third, 0);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);

        // We expect move changes for the reordered elements
        var moveChanges = diff.Changes.Where(c => c.ChangeType == ChangeType.Moved).ToList();
        Assert.NotEmpty(moveChanges);

        // "Third" should be detected as moved from index 2 to index 0
        var thirdMove = moveChanges.FirstOrDefault(c => c.OldText == "Third");
        Assert.NotNull(thirdMove);
        Assert.Equal(2, thirdMove.OldIndex);
        Assert.Equal(0, thirdMove.NewIndex);
    }

    [Fact]
    public void DetectsAddedTable()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Introduction"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var table = CreateTable(2, 2);
        modBody.AppendChild(table);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);

        var change = diff.Changes[0];
        Assert.Equal(ChangeType.Added, change.ChangeType);
        Assert.Equal("table", change.ElementType);
    }

    [Fact]
    public void DetectsMultipleChanges()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Keep this"));
        body.AppendChild(CreateParagraph("Remove this"));
        body.AppendChild(CreateParagraph("Modify this"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();

        // Remove second paragraph
        modBody.RemoveChild(paragraphs[1]);

        // Modify third paragraph (now at index 1)
        paragraphs = modBody.Elements<Paragraph>().ToList();
        var modifyPara = paragraphs[1];
        var run = modifyPara.Elements<Run>().First();
        run.GetFirstChild<Text>()!.Text = "Modified content";

        // Add new paragraph
        modBody.AppendChild(CreateParagraph("New paragraph"));

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Equal(3, diff.Summary.TotalChanges);
        Assert.Equal(1, diff.Summary.Added);
        Assert.Equal(1, diff.Summary.Removed);
        Assert.Equal(1, diff.Summary.Modified);
    }

    [Fact]
    public void GeneratesValidPatches_ForAddition()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        modified.GetBody().AppendChild(CreateParagraph("Second"));

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
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("First"));
        body.AppendChild(CreateParagraph("Second"));

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
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Original"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var para = modBody.Elements<Paragraph>().First();
        para.Elements<Run>().First().GetFirstChild<Text>()!.Text = "Modified";

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
        modBody.InsertChildAt(third, 0);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var patches = diff.ToPatches();

        // Assert
        var movePatches = patches.Where(p => p["op"]?.GetValue<string>() == "move").ToList();
        Assert.NotEmpty(movePatches);
    }

    [Fact]
    public void CompareFromBytes_WorksCorrectly()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello"));
        var originalBytes = original.ToBytes();

        var modified = CreateSessionFromBytes(originalBytes);
        modified.GetBody().AppendChild(CreateParagraph("World"));
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
        Assert.NotNull(parsed.RootElement.GetProperty("summary"));
        Assert.NotNull(parsed.RootElement.GetProperty("changes"));
        Assert.NotNull(parsed.RootElement.GetProperty("patches"));
    }

    [Fact]
    public void DetectsHeadingLevelChange()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        var heading = CreateHeading(1, "Title");
        body.AppendChild(heading);

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var modHeading = modBody.Elements<Paragraph>().First();

        // Change heading level
        modHeading.ParagraphProperties!.ParagraphStyleId!.Val = "Heading2";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Modified, diff.Changes[0].ChangeType);
    }

    [Fact]
    public void DetectsRunStyleChange()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        var para = CreateParagraph("Normal text");
        body.AppendChild(para);

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var modPara = modBody.Elements<Paragraph>().First();
        var run = modPara.Elements<Run>().First();

        // Add bold styling
        run.RunProperties = new RunProperties { Bold = new Bold() };

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Modified, diff.Changes[0].ChangeType);
    }

    [Fact]
    public void DetectsTableCellTextChange()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        var table = CreateTable(2, 2);
        body.AppendChild(table);

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var modTable = modBody.Elements<Table>().First();
        var firstCell = modTable.Descendants<TableCell>().First();
        var cellPara = firstCell.Elements<Paragraph>().First();
        var cellRun = cellPara.Elements<Run>().First();
        cellRun.GetFirstChild<Text>()!.Text = "Changed";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        Assert.True(diff.HasChanges);
        Assert.Single(diff.Changes);
        Assert.Equal(ChangeType.Modified, diff.Changes[0].ChangeType);
        Assert.Equal("table", diff.Changes[0].ElementType);
    }

    [Fact]
    public void ChangeDescription_IsHumanReadable()
    {
        // Arrange
        var original = CreateSession();
        original.GetBody().AppendChild(CreateParagraph("Hello World"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var para = modBody.Elements<Paragraph>().First();
        para.Elements<Run>().First().GetFirstChild<Text>()!.Text = "Hello Universe";

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);

        // Assert
        var change = diff.Changes[0];
        Assert.Contains("Modified", change.Description);
        Assert.Contains("Hello World", change.Description);
        Assert.Contains("Hello Universe", change.Description);
    }

    [Fact]
    public void Summary_CorrectlyCounts_AllChangeTypes()
    {
        // Arrange
        var original = CreateSession();
        var body = original.GetBody();
        body.AppendChild(CreateParagraph("Para 1"));
        body.AppendChild(CreateParagraph("Para 2"));
        body.AppendChild(CreateParagraph("Para 3"));
        body.AppendChild(CreateParagraph("Para 4"));

        var modified = CreateSessionFromBytes(original.ToBytes());
        var modBody = modified.GetBody();
        var paragraphs = modBody.Elements<Paragraph>().ToList();

        // Remove Para 2
        modBody.RemoveChild(paragraphs[1]);

        // Modify Para 3
        paragraphs = modBody.Elements<Paragraph>().ToList();
        paragraphs[1].Elements<Run>().First().GetFirstChild<Text>()!.Text = "Modified Para 3";

        // Add new paragraph
        modBody.AppendChild(CreateParagraph("New Para"));

        // Move Para 4 to beginning
        paragraphs = modBody.Elements<Paragraph>().ToList();
        var para4 = paragraphs[2];
        modBody.RemoveChild(para4);
        modBody.InsertChildAt(para4, 0);

        // Act
        var diff = DiffEngine.Compare(original.Document, modified.Document);
        var summary = diff.Summary;

        // Assert
        Assert.True(diff.HasChanges);
        Assert.True(summary.TotalChanges >= 3); // At least remove, modify, add
        Assert.True(summary.Removed >= 1);
        Assert.True(summary.Modified >= 1);
        Assert.True(summary.Added >= 1);
    }

    // Helper methods

    private Paragraph CreateParagraph(string text)
    {
        var para = new Paragraph();
        var run = new Run();
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        ElementIdManager.AssignId(run);
        para.AppendChild(run);
        ElementIdManager.AssignId(para);
        return para;
    }

    private Paragraph CreateHeading(int level, string text)
    {
        var para = new Paragraph();
        para.ParagraphProperties = new ParagraphProperties
        {
            ParagraphStyleId = new ParagraphStyleId { Val = $"Heading{level}" }
        };
        var run = new Run();
        run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        ElementIdManager.AssignId(run);
        para.AppendChild(run);
        ElementIdManager.AssignId(para);
        return para;
    }

    private Table CreateTable(int rows, int cols)
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
                ElementIdManager.AssignId(run);
                para.AppendChild(run);
                ElementIdManager.AssignId(para);
                cell.AppendChild(para);
                ElementIdManager.AssignId(cell);
                row.AppendChild(cell);
            }
            ElementIdManager.AssignId(row);
            table.AppendChild(row);
        }

        ElementIdManager.AssignId(table);
        return table;
    }

    private DocxSession CreateSessionFromBytes(byte[] bytes)
    {
        var session = DocxSession.FromBytes(bytes, Guid.NewGuid().ToString("N")[..12], null);
        _sessions.Add(session);
        return session;
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
        {
            session.Dispose();
        }
    }
}
