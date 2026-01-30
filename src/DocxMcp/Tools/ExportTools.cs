using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class ExportTools
{
    [McpServerTool(Name = "export_pdf"), Description(
        "Export a document to PDF using LibreOffice CLI (soffice). " +
        "LibreOffice must be installed on the system.")]
    public static async Task<string> ExportPdf(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Output path for the PDF file.")] string output_path)
    {
        var session = sessions.Get(doc_id);

        // Save to a temp .docx first
        var tempDocx = Path.Combine(Path.GetTempPath(), $"docx-mcp-{session.Id}.docx");
        try
        {
            session.Save(tempDocx);

            // Find LibreOffice
            var soffice = FindLibreOffice();
            if (soffice is null)
                return "Error: LibreOffice not found. Install it for PDF export. " +
                       "macOS: brew install --cask libreoffice";

            var outputDir = Path.GetDirectoryName(output_path) ?? Path.GetTempPath();

            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                Arguments = $"--headless --convert-to pdf --outdir \"{outputDir}\" \"{tempDocx}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start LibreOffice.");

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                return $"Error: LibreOffice failed (exit {process.ExitCode}): {stderr}";
            }

            // LibreOffice outputs to outputDir with the same base name
            var generatedPdf = Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(tempDocx) + ".pdf");

            if (File.Exists(generatedPdf) && generatedPdf != output_path)
            {
                File.Move(generatedPdf, output_path, overwrite: true);
            }

            return $"PDF exported to '{output_path}'.";
        }
        finally
        {
            if (File.Exists(tempDocx))
                File.Delete(tempDocx);
        }
    }

    [McpServerTool(Name = "export_html"), Description(
        "Export a document to HTML format.")]
    public static string ExportHtml(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Output path for the HTML file.")] string output_path)
    {
        var session = sessions.Get(doc_id);
        var body = session.GetBody();

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><meta charset=\"utf-8\"><style>");
        sb.AppendLine("body { font-family: Calibri, Arial, sans-serif; max-width: 800px; margin: 0 auto; padding: 20px; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 1em 0; }");
        sb.AppendLine("td, th { border: 1px solid #ccc; padding: 8px; }");
        sb.AppendLine("th { background-color: #f5f5f5; font-weight: bold; }");
        sb.AppendLine("</style></head><body>");

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph p:
                    RenderParagraphHtml(p, sb);
                    break;
                case Table t:
                    RenderTableHtml(t, sb);
                    break;
            }
        }

        sb.AppendLine("</body></html>");

        File.WriteAllText(output_path, sb.ToString(), Encoding.UTF8);
        return $"HTML exported to '{output_path}'.";
    }

    [McpServerTool(Name = "export_markdown"), Description(
        "Export a document to Markdown format.")]
    public static string ExportMarkdown(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Output path for the Markdown file.")] string output_path)
    {
        var session = sessions.Get(doc_id);
        var body = session.GetBody();

        var sb = new StringBuilder();

        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph p:
                    RenderParagraphMarkdown(p, sb);
                    break;
                case Table t:
                    RenderTableMarkdown(t, sb);
                    break;
            }
        }

        File.WriteAllText(output_path, sb.ToString(), Encoding.UTF8);
        return $"Markdown exported to '{output_path}'.";
    }

    private static void RenderParagraphHtml(Paragraph p, StringBuilder sb)
    {
        var text = p.InnerText;
        if (string.IsNullOrWhiteSpace(text) && !p.Elements<Run>().Any(r => r.Elements<Break>().Any()))
            return;

        if (p.IsHeading())
        {
            var level = p.GetHeadingLevel();
            sb.AppendLine($"<h{level}>{Escape(text)}</h{level}>");
        }
        else
        {
            var style = p.GetStyleId();
            if (style is "ListBullet" or "ListNumber")
            {
                // Simple list rendering
                sb.AppendLine($"<li>{Escape(text)}</li>");
            }
            else
            {
                sb.AppendLine($"<p>{RenderRunsHtml(p)}</p>");
            }
        }
    }

    private static string RenderRunsHtml(Paragraph p)
    {
        var sb = new StringBuilder();
        foreach (var child in p.ChildElements)
        {
            if (child is Run r)
            {
                var text = r.InnerText;
                var rp = r.RunProperties;

                if (rp?.Bold is not null) sb.Append("<strong>");
                if (rp?.Italic is not null) sb.Append("<em>");
                if (rp?.Underline is not null) sb.Append("<u>");

                sb.Append(Escape(text));

                if (rp?.Underline is not null) sb.Append("</u>");
                if (rp?.Italic is not null) sb.Append("</em>");
                if (rp?.Bold is not null) sb.Append("</strong>");
            }
            else if (child is Hyperlink h)
            {
                sb.Append($"<a href=\"#\">{Escape(h.InnerText)}</a>");
            }
        }
        return sb.ToString();
    }

    private static void RenderTableHtml(Table t, StringBuilder sb)
    {
        sb.AppendLine("<table>");
        bool first = true;
        foreach (var row in t.Elements<TableRow>())
        {
            sb.AppendLine("<tr>");
            var tag = first ? "th" : "td";
            foreach (var cell in row.Elements<TableCell>())
            {
                sb.AppendLine($"  <{tag}>{Escape(cell.InnerText)}</{tag}>");
            }
            sb.AppendLine("</tr>");
            first = false;
        }
        sb.AppendLine("</table>");
    }

    private static void RenderParagraphMarkdown(Paragraph p, StringBuilder sb)
    {
        var text = p.InnerText;
        if (string.IsNullOrWhiteSpace(text))
        {
            sb.AppendLine();
            return;
        }

        if (p.IsHeading())
        {
            var level = p.GetHeadingLevel();
            sb.Append(new string('#', level));
            sb.Append(' ');
            sb.AppendLine(text);
            sb.AppendLine();
        }
        else
        {
            var style = p.GetStyleId();
            if (style == "ListBullet")
                sb.AppendLine($"- {text}");
            else if (style == "ListNumber")
                sb.AppendLine($"1. {text}");
            else
                sb.AppendLine(text);
            sb.AppendLine();
        }
    }

    private static void RenderTableMarkdown(Table t, StringBuilder sb)
    {
        var rows = t.Elements<TableRow>().ToList();
        if (rows.Count == 0) return;

        // Header
        var headerCells = rows[0].Elements<TableCell>().Select(c => c.InnerText).ToList();
        sb.AppendLine("| " + string.Join(" | ", headerCells) + " |");
        sb.AppendLine("| " + string.Join(" | ", headerCells.Select(_ => "---")) + " |");

        // Data rows
        foreach (var row in rows.Skip(1))
        {
            var cells = row.Elements<TableCell>().Select(c => c.InnerText).ToList();
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        sb.AppendLine();
    }

    private static string? FindLibreOffice()
    {
        // macOS
        var macPaths = new[]
        {
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
            "/opt/homebrew/bin/soffice",
        };
        foreach (var p in macPaths)
            if (File.Exists(p)) return p;

        // Linux
        var linuxPaths = new[]
        {
            "/usr/bin/soffice",
            "/usr/bin/libreoffice",
        };
        foreach (var p in linuxPaths)
            if (File.Exists(p)) return p;

        // Try PATH
        try
        {
            var psi = new ProcessStartInfo("which", "soffice")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var path = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0 && !string.IsNullOrEmpty(path))
                    return path;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
}
