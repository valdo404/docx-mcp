using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using DocxMcp.Helpers;
using DocxMcp.Paths;
using DocxMcp.Tools;
using System.Text.Json;
using Xunit;

namespace DocxMcp.Tests;

public class TableModificationTests : IDisposable
{
    private readonly DocxSession _session;
    private readonly SessionManager _sessions;

    public TableModificationTests()
    {
        _sessions = TestHelpers.CreateSessionManager();
        _session = _sessions.Create();

        var body = _session.GetBody();

        // Add a heading
        body.AppendChild(new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new Text("Document"))));

        // Add a table with header row and 2 data rows
        var table = new Table(
            new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4 },
                    new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 },
                    new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        // Header row
        var headerRow = new TableRow(
            new TableRowProperties(new TableHeader()),
            new TableCell(new Paragraph(new Run(
                new RunProperties(new Bold()),
                new Text("Name")))),
            new TableCell(new Paragraph(new Run(
                new RunProperties(new Bold()),
                new Text("Age")))),
            new TableCell(new Paragraph(new Run(
                new RunProperties(new Bold()),
                new Text("City")))));
        table.AppendChild(headerRow);

        // Data rows
        table.AppendChild(new TableRow(
            new TableCell(new Paragraph(new Run(new Text("Alice")))),
            new TableCell(new Paragraph(new Run(new Text("30")))),
            new TableCell(new Paragraph(new Run(new Text("Paris"))))));

        table.AppendChild(new TableRow(
            new TableCell(new Paragraph(new Run(new Text("Bob")))),
            new TableCell(new Paragraph(new Run(new Text("25")))),
            new TableCell(new Paragraph(new Run(new Text("London"))))));

        body.AppendChild(table);
    }

    // ===========================
    // Table Creation Tests
    // ===========================

    [Fact]
    public void CreateTableWithRichHeaderCells()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "headers": [
                {"text": "Product", "shading": "E0E0E0", "style": {"bold": true}},
                {"text": "Price", "shading": "E0E0E0", "style": {"bold": true, "color": "FF0000"}}
            ],
            "rows": [["Widget", "$10"], ["Gadget", "$20"]]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var rows = table.Elements<TableRow>().ToList();

        Assert.Equal(3, rows.Count); // header + 2 data

        // Check header cell shading
        var headerCells = rows[0].Elements<TableCell>().ToList();
        Assert.Equal("Product", headerCells[0].InnerText);
        Assert.Equal("E0E0E0", headerCells[0].TableCellProperties?.Shading?.Fill?.Value);

        Assert.Equal("Price", headerCells[1].InnerText);
        Assert.Equal("FF0000",
            headerCells[1].Descendants<Run>().First().RunProperties?.Color?.Val?.Value);
    }

    [Fact]
    public void CreateTableWithRichRowObjects()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "headers": ["Name", "Value"],
            "rows": [
                {
                    "cells": [
                        {"text": "Total", "style": {"bold": true}},
                        {"text": "$100", "shading": "FFFF00"}
                    ]
                }
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var rows = table.Elements<TableRow>().ToList();

        Assert.Equal(2, rows.Count); // header + 1 data
        var dataCells = rows[1].Elements<TableCell>().ToList();

        Assert.Equal("Total", dataCells[0].InnerText);
        Assert.NotNull(dataCells[0].Descendants<Run>().First().RunProperties?.Bold);

        Assert.Equal("$100", dataCells[1].InnerText);
        Assert.Equal("FFFF00", dataCells[1].TableCellProperties?.Shading?.Fill?.Value);
    }

    [Fact]
    public void CreateTableWithCellSpanning()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "rows": [
                {
                    "cells": [
                        {"text": "Merged Header", "col_span": 3, "style": {"bold": true}}
                    ]
                },
                {
                    "cells": [
                        {"text": "A"},
                        {"text": "B"},
                        {"text": "C"}
                    ]
                }
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var rows = table.Elements<TableRow>().ToList();

        Assert.Equal(2, rows.Count);
        var mergedCell = rows[0].Elements<TableCell>().First();
        Assert.Equal(3, mergedCell.TableCellProperties?.GridSpan?.Val?.Value);
    }

    [Fact]
    public void CreateTableWithVerticalMerge()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "rows": [
                {
                    "cells": [
                        {"text": "Span Start", "row_span": "restart"},
                        {"text": "Row 1"}
                    ]
                },
                {
                    "cells": [
                        {"text": "", "row_span": "continue"},
                        {"text": "Row 2"}
                    ]
                }
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var rows = table.Elements<TableRow>().ToList();

        var firstCell = rows[0].Elements<TableCell>().First();
        Assert.Equal(MergedCellValues.Restart,
            firstCell.TableCellProperties?.VerticalMerge?.Val?.Value);

        var secondCell = rows[1].Elements<TableCell>().First();
        Assert.NotNull(secondCell.TableCellProperties?.VerticalMerge);
        // Continue has no Val attribute
        Assert.Null(secondCell.TableCellProperties?.VerticalMerge?.Val);
    }

    [Fact]
    public void CreateTableWithCellVerticalAlignment()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "rows": [
                {
                    "cells": [
                        {"text": "Top", "vertical_align": "top"},
                        {"text": "Center", "vertical_align": "center"},
                        {"text": "Bottom", "vertical_align": "bottom"}
                    ]
                }
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var cells = table.Descendants<TableCell>().ToList();

        Assert.Equal(TableVerticalAlignmentValues.Top,
            cells[0].TableCellProperties?.TableCellVerticalAlignment?.Val?.Value);
        Assert.Equal(TableVerticalAlignmentValues.Center,
            cells[1].TableCellProperties?.TableCellVerticalAlignment?.Val?.Value);
        Assert.Equal(TableVerticalAlignmentValues.Bottom,
            cells[2].TableCellProperties?.TableCellVerticalAlignment?.Val?.Value);
    }

    [Fact]
    public void CreateTableWithBorderStyle()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "border_style": "double",
            "border_size": 8,
            "rows": [["A"]]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var borders = table.GetFirstChild<TableProperties>()?.TableBorders;

        Assert.NotNull(borders);
        Assert.Equal(BorderValues.Double, borders!.TopBorder?.Val?.Value);
        Assert.Equal(8u, borders.TopBorder?.Size?.Value);
    }

    [Fact]
    public void CreateTableWithNoBorders()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "border_style": "none",
            "rows": [["A"]]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var borders = table.GetFirstChild<TableProperties>()?.TableBorders;

        Assert.Null(borders);
    }

    [Fact]
    public void CreateTableWithWidthAndAlignment()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "width": 5000,
            "width_type": "pct",
            "table_alignment": "center",
            "rows": [["A"]]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var tblProps = table.GetFirstChild<TableProperties>();

        Assert.Equal("5000", tblProps?.TableWidth?.Width?.Value);
        Assert.Equal(TableWidthUnitValues.Pct, tblProps?.TableWidth?.Type?.Value);
        Assert.Equal(TableRowAlignmentValues.Center, tblProps?.TableJustification?.Val?.Value);
    }

    [Fact]
    public void CreateTableWithCellBorders()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "rows": [
                {
                    "cells": [
                        {
                            "text": "Custom borders",
                            "borders": {
                                "top": "double",
                                "bottom": "single",
                                "left": "dotted",
                                "right": "dashed"
                            }
                        }
                    ]
                }
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var cell = table.Descendants<TableCell>().First();
        var cb = cell.TableCellProperties?.TableCellBorders;

        Assert.NotNull(cb);
        Assert.Equal(BorderValues.Double, cb!.TopBorder?.Val?.Value);
        Assert.Equal(BorderValues.Single, cb.BottomBorder?.Val?.Value);
        Assert.Equal(BorderValues.Dotted, cb.LeftBorder?.Val?.Value);
        Assert.Equal(BorderValues.Dashed, cb.RightBorder?.Val?.Value);
    }

    [Fact]
    public void CreateRowAsTopLevelType()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "row",
            "is_header": true,
            "cells": [
                {"text": "Col1", "style": {"bold": true}},
                {"text": "Col2", "style": {"bold": true}}
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var row = Assert.IsType<TableRow>(element);

        Assert.NotNull(row.TableRowProperties?.GetFirstChild<TableHeader>());
        var cells = row.Elements<TableCell>().ToList();
        Assert.Equal(2, cells.Count);
        Assert.Equal("Col1", cells[0].InnerText);
    }

    [Fact]
    public void CreateCellAsTopLevelType()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "cell",
            "text": "Cell content",
            "shading": "E0E0E0",
            "width": 2000,
            "vertical_align": "center"
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var cell = Assert.IsType<TableCell>(element);

        Assert.Equal("Cell content", cell.InnerText);
        Assert.Equal("E0E0E0", cell.TableCellProperties?.Shading?.Fill?.Value);
        Assert.Equal("2000", cell.TableCellProperties?.TableCellWidth?.Width?.Value);
        Assert.Equal(TableVerticalAlignmentValues.Center,
            cell.TableCellProperties?.TableCellVerticalAlignment?.Val?.Value);
    }

    [Fact]
    public void CreateCellWithRunsArray()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "cell",
            "runs": [
                {"text": "Bold ", "style": {"bold": true}},
                {"text": "normal"}
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var cell = Assert.IsType<TableCell>(element);

        var runs = cell.Descendants<Run>().ToList();
        Assert.Equal(2, runs.Count);
        Assert.NotNull(runs[0].RunProperties?.Bold);
        Assert.Equal("Bold ", runs[0].InnerText);
        Assert.Equal("normal", runs[1].InnerText);
    }

    [Fact]
    public void CreateCellWithMultipleParagraphs()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "cell",
            "paragraphs": [
                {"type": "paragraph", "text": "First paragraph"},
                {"type": "paragraph", "text": "Second paragraph", "style": {"bold": true}}
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var cell = Assert.IsType<TableCell>(element);
        var paragraphs = cell.Elements<Paragraph>().ToList();

        Assert.Equal(2, paragraphs.Count);
        Assert.Equal("First paragraph", paragraphs[0].InnerText);
        Assert.Equal("Second paragraph", paragraphs[1].InnerText);
    }

    [Fact]
    public void CreateTableWithRowHeight()
    {
        var mainPart = _session.Document.MainDocumentPart!;
        var value = JsonDocument.Parse("""
        {
            "type": "table",
            "rows": [
                {
                    "height": 500,
                    "cells": [{"text": "Tall row"}]
                }
            ]
        }
        """).RootElement;

        var element = ElementFactory.CreateFromJson(value, mainPart);
        var table = Assert.IsType<Table>(element);
        var row = table.Elements<TableRow>().First();

        Assert.Equal(500u,
            row.TableRowProperties?.GetFirstChild<TableRowHeight>()?.Val?.Value);
    }

    // ===========================
    // Patch Operations on Tables
    // ===========================

    [Fact]
    public void RemoveTableRow()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id,
            """[{"op": "remove", "path": "/body/table[0]/row[2]"}]""");

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var rows = table.Elements<TableRow>().ToList();
        Assert.Equal(2, rows.Count); // header + 1 data row (removed "Bob" row)
    }

    [Fact]
    public void RemoveTableCell()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id,
            """[{"op": "remove", "path": "/body/table[0]/row[1]/cell[2]"}]""");

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var row = table.Elements<TableRow>().ElementAt(1);
        var cells = row.Elements<TableCell>().ToList();
        Assert.Equal(2, cells.Count); // "Alice", "30" (removed "Paris")
    }

    [Fact]
    public void ReplaceTableCell()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "replace",
            "path": "/body/table[0]/row[1]/cell[0]",
            "value": {
                "type": "cell",
                "text": "Alice Smith",
                "style": {"bold": true},
                "shading": "E0FFE0"
            }
        }]
        """);

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var cell = table.Elements<TableRow>().ElementAt(1).Elements<TableCell>().First();
        Assert.Equal("Alice Smith", cell.InnerText);
        Assert.Equal("E0FFE0", cell.TableCellProperties?.Shading?.Fill?.Value);
    }

    [Fact]
    public void ReplaceTableRow()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "replace",
            "path": "/body/table[0]/row[2]",
            "value": {
                "type": "row",
                "cells": [
                    {"text": "Charlie", "style": {"italic": true}},
                    {"text": "35"},
                    {"text": "Berlin"}
                ]
            }
        }]
        """);

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var row = table.Elements<TableRow>().Last();
        var cells = row.Elements<TableCell>().ToList();
        Assert.Equal("Charlie", cells[0].InnerText);
        Assert.Equal("35", cells[1].InnerText);
        Assert.Equal("Berlin", cells[2].InnerText);
    }

    [Fact]
    public void RemoveColumn()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id,
            """[{"op": "remove_column", "path": "/body/table[0]", "column": 1}]""");

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        foreach (var row in table.Elements<TableRow>())
        {
            var cells = row.Elements<TableCell>().ToList();
            Assert.Equal(2, cells.Count); // removed "Age" column
        }

        // Verify header: Name, City
        var headerCells = table.Elements<TableRow>().First().Elements<TableCell>().ToList();
        Assert.Equal("Name", headerCells[0].InnerText);
        Assert.Equal("City", headerCells[1].InnerText);
    }

    [Fact]
    public void RemoveFirstColumn()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id,
            """[{"op": "remove_column", "path": "/body/table[0]", "column": 0}]""");

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var headerCells = table.Elements<TableRow>().First().Elements<TableCell>().ToList();
        Assert.Equal("Age", headerCells[0].InnerText);
        Assert.Equal("City", headerCells[1].InnerText);
    }

    [Fact]
    public void RemoveLastColumn()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id,
            """[{"op": "remove_column", "path": "/body/table[0]", "column": 2}]""");

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var headerCells = table.Elements<TableRow>().First().Elements<TableCell>().ToList();
        Assert.Equal("Name", headerCells[0].InnerText);
        Assert.Equal("Age", headerCells[1].InnerText);
    }

    [Fact]
    public void ReplaceTextPreservesFormatting()
    {
        // First, add a styled paragraph with multiple runs
        var body = _session.GetBody();
        var p = new Paragraph(
            new Run(
                new RunProperties(new Bold(), new Color { Val = "FF0000" }),
                new Text("Hello World") { Space = SpaceProcessingModeValues.Preserve }),
            new Run(
                new RunProperties(new Italic()),
                new Text(" is great") { Space = SpaceProcessingModeValues.Preserve }));
        body.AppendChild(p);

        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "replace_text",
            "path": "/body/paragraph[text~='Hello World']",
            "find": "World",
            "replace": "Universe"
        }]
        """);

        Assert.Contains("\"success\": true", result);

        // Find the paragraph that was modified
        var modified = body.Elements<Paragraph>()
            .FirstOrDefault(par => par.InnerText.Contains("Universe"));
        Assert.NotNull(modified);

        // Verify the formatting is preserved
        var runs = modified!.Elements<Run>().ToList();
        Assert.True(runs.Count >= 1);

        // First run should still be bold+red
        var firstRun = runs[0];
        Assert.NotNull(firstRun.RunProperties?.Bold);
        Assert.Equal("FF0000", firstRun.RunProperties?.Color?.Val?.Value);
        Assert.Contains("Universe", firstRun.InnerText);
    }

    [Fact]
    public void ReplaceTextInTableCell()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "replace_text",
            "path": "/body/table[0]/row[1]/cell[0]",
            "find": "Alice",
            "replace": "Eve"
        }]
        """);

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var cell = table.Elements<TableRow>().ElementAt(1).Elements<TableCell>().First();
        Assert.Equal("Eve", cell.InnerText);
    }

    [Fact]
    public void AddRowToExistingTable()
    {
        // Add a new row after the last row
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "add",
            "path": "/body/table[0]",
            "value": {
                "type": "row",
                "cells": ["Charlie", "35", "Berlin"]
            }
        }]
        """);

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var rows = table.Elements<TableRow>().ToList();
        Assert.Equal(4, rows.Count); // header + 3 data rows

        var lastRowCells = rows[3].Elements<TableCell>().ToList();
        Assert.Equal("Charlie", lastRowCells[0].InnerText);
    }

    [Fact]
    public void AddStyledCellToRow()
    {
        // Add a new cell to the first data row
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "add",
            "path": "/body/table[0]/row[1]",
            "value": {
                "type": "cell",
                "text": "France",
                "shading": "E0FFE0"
            }
        }]
        """);

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var row = table.Elements<TableRow>().ElementAt(1);
        var cells = row.Elements<TableCell>().ToList();
        Assert.Equal(4, cells.Count); // original 3 + new cell
        Assert.Equal("France", cells[3].InnerText);
    }

    // ===========================
    // Query Round-Trip Tests
    // ===========================

    [Fact]
    public void QueryTableReturnsRichRowData()
    {
        var result = QueryTool.Query(_sessions, _session.Id, "/body/table[0]");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.Equal("table", root.GetProperty("type").GetString());
        Assert.Equal(3, root.GetProperty("rows").GetInt32());
        Assert.Equal(3, root.GetProperty("cols").GetInt32());

        // Check rich_rows array
        Assert.True(root.TryGetProperty("rich_rows", out var richRows));
        Assert.Equal(3, richRows.GetArrayLength());

        // Check first row (header) has is_header property
        var firstRow = richRows[0];
        Assert.True(firstRow.TryGetProperty("properties", out var rowProps));
        Assert.True(rowProps.GetProperty("is_header").GetBoolean());
    }

    [Fact]
    public void QueryCellReturnsProperties()
    {
        // Add a cell with shading for testing
        var body = _session.GetBody();
        var table = body.Elements<Table>().First();
        var row = table.Elements<TableRow>().ElementAt(1);
        var cell = row.Elements<TableCell>().First();

        // Add cell properties
        cell.TableCellProperties = new TableCellProperties(
            new Shading { Fill = "AABBCC", Val = ShadingPatternValues.Clear },
            new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });

        var result = QueryTool.Query(_sessions, _session.Id,
            "/body/table[0]/row[1]/cell[0]");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.Equal("cell", root.GetProperty("type").GetString());
        Assert.Equal("Alice", root.GetProperty("text").GetString());

        Assert.True(root.TryGetProperty("properties", out var props));
        Assert.Equal("AABBCC", props.GetProperty("shading").GetString());
        Assert.Equal("center", props.GetProperty("vertical_align").GetString());
    }

    [Fact]
    public void QueryTableReturnsTableProperties()
    {
        // Add table width and alignment
        var table = _session.GetBody().Elements<Table>().First();
        var tblProps = table.GetFirstChild<TableProperties>()!;
        tblProps.TableWidth = new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct };
        tblProps.TableJustification = new TableJustification { Val = TableRowAlignmentValues.Center };

        var result = QueryTool.Query(_sessions, _session.Id, "/body/table[0]");
        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("properties", out var props));
        Assert.Equal("5000", props.GetProperty("width").GetString());
        Assert.Equal("center", props.GetProperty("table_alignment").GetString());
    }

    [Fact]
    public void ReplaceTableProperties()
    {
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [{
            "op": "replace",
            "path": "/body/table[0]/style",
            "value": {
                "border_style": "double",
                "width": 9000,
                "table_alignment": "center"
            }
        }]
        """);

        Assert.Contains("\"success\": true", result);

        var table = _session.GetBody().Elements<Table>().First();
        var tblProps = table.GetFirstChild<TableProperties>();
        Assert.NotNull(tblProps);
        Assert.Equal(BorderValues.Double, tblProps!.TableBorders?.TopBorder?.Val?.Value);
        Assert.Equal("9000", tblProps.TableWidth?.Width?.Value);
        Assert.Equal(TableRowAlignmentValues.Center, tblProps.TableJustification?.Val?.Value);
    }

    [Fact]
    public void MultiplePatchOperationsOnTable()
    {
        // Do multiple operations in one patch call:
        // 1. Replace header cell text
        // 2. Remove a column
        // 3. Add a new row
        var result = PatchTool.ApplyPatch(_sessions, null, _session.Id, """
        [
            {
                "op": "replace_text",
                "path": "/body/table[0]/row[0]/cell[0]",
                "find": "Name",
                "replace": "Full Name"
            },
            {
                "op": "remove_column",
                "path": "/body/table[0]",
                "column": 2
            }
        ]
        """);

        Assert.Contains("\"success\": true", result);
        Assert.Contains("\"applied\": 2", result);

        var table = _session.GetBody().Elements<Table>().First();

        // Verify header text changed
        var headerCells = table.Elements<TableRow>().First().Elements<TableCell>().ToList();
        Assert.Equal("Full Name", headerCells[0].InnerText);

        // Verify column removed (2 columns left)
        Assert.Equal(2, headerCells.Count);
    }

    public void Dispose()
    {
        _sessions.Close(_session.Id);
    }
}
