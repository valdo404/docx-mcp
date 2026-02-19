using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Grpc.Core;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class ExportTools
{
    [McpServerTool(Name = "export"), Description(
        "Export a document to another format. Returns the content as text (html, markdown) " +
        "or as base64-encoded binary (pdf, docx).\n\n" +
        "Formats:\n" +
        "  html — returns HTML string\n" +
        "  markdown — returns Markdown string\n" +
        "  pdf — returns base64-encoded PDF (requires LibreOffice on the server)\n" +
        "  docx — returns base64-encoded DOCX bytes")]
    public static async Task<string> Export(
        TenantScope tenant,
        [Description("Session ID of the document.")] string doc_id,
        [Description("Export format: html, markdown, pdf, docx.")] string format)
    {
        try
        {
            var session = tenant.Sessions.Get(doc_id);

            return format.ToLowerInvariant() switch
            {
                "html" => ExportHtml(session),
                "markdown" or "md" => ExportMarkdown(session),
                "pdf" => await ExportPdf(session),
                "docx" => ExportDocx(session),
                _ => throw new McpException(
                    $"Unknown export format '{format}'. Supported: html, markdown, pdf, docx."),
            };
        }
        catch (RpcException ex) { throw GrpcErrorHelper.Wrap(ex, $"exporting '{doc_id}' to {format}"); }
        catch (KeyNotFoundException) { throw GrpcErrorHelper.WrapNotFound(doc_id); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message, ex); }
    }

    private static string ExportHtml(DocxSession session)
    {
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
        return sb.ToString();
    }

    private static string ExportMarkdown(DocxSession session)
    {
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

        return sb.ToString();
    }

    private static async Task<string> ExportPdf(DocxSession session)
    {
        var tempDocx = Path.Combine(Path.GetTempPath(), $"docx-mcp-{session.Id}.docx");
        var tempDir = Path.GetTempPath();
        string? generatedPdf = null;
        try
        {
            session.Save(tempDocx);

            var soffice = FindLibreOffice()
                ?? throw new McpException(
                    "LibreOffice not found. PDF export requires LibreOffice. " +
                    "macOS: brew install --cask libreoffice");

            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                Arguments = $"--headless --convert-to pdf --outdir \"{tempDir}\" \"{tempDocx}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi)
                ?? throw new McpException("Failed to start LibreOffice.");

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                throw new McpException($"LibreOffice failed (exit {process.ExitCode}): {stderr}");
            }

            generatedPdf = Path.Combine(tempDir,
                Path.GetFileNameWithoutExtension(tempDocx) + ".pdf");

            if (!File.Exists(generatedPdf))
                throw new McpException("LibreOffice did not produce a PDF file.");

            var pdfBytes = await File.ReadAllBytesAsync(generatedPdf);

            return Convert.ToBase64String(pdfBytes);
        }
        finally
        {
            if (File.Exists(tempDocx))
                File.Delete(tempDocx);
            if (generatedPdf is not null && File.Exists(generatedPdf))
                File.Delete(generatedPdf);
        }
    }

    private static string ExportDocx(DocxSession session)
    {
        var bytes = session.ToBytes();
        return Convert.ToBase64String(bytes);
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

        var headerCells = rows[0].Elements<TableCell>().Select(c => c.InnerText).ToList();
        sb.AppendLine("| " + string.Join(" | ", headerCells) + " |");
        sb.AppendLine("| " + string.Join(" | ", headerCells.Select(_ => "---")) + " |");

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
