using System.ComponentModel;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ModelContextProtocol.Server;
using DocxMcp.Helpers;
using DocxMcp.Paths;

namespace DocxMcp.Tools;

[McpServerToolType]
public sealed class PatchTool
{
    [McpServerTool(Name = "apply_patch"), Description(
        "Modify a document using JSON patches (RFC 6902 adapted for OOXML).\n\n" +
        "Operations:\n" +
        "  add — Insert element at path. Use /body/children/N for positional insert.\n" +
        "  replace — Replace element or property at path.\n" +
        "  remove — Delete element at path.\n" +
        "  move — Move element from one location to another.\n" +
        "  copy — Duplicate element to another location.\n\n" +
        "Value types (for add/replace):\n" +
        "  {\"type\": \"paragraph\", \"text\": \"...\", \"style\": {\"bold\": true}}\n" +
        "  {\"type\": \"heading\", \"level\": 2, \"text\": \"...\"}\n" +
        "  {\"type\": \"table\", \"rows\": [[\"A\",\"B\"]], \"headers\": [\"Col1\",\"Col2\"]}\n" +
        "  {\"type\": \"image\", \"path\": \"/tmp/img.png\", \"width\": 200, \"height\": 150}\n" +
        "  {\"type\": \"hyperlink\", \"text\": \"Click\", \"url\": \"https://...\"}\n" +
        "  {\"type\": \"page_break\"}\n" +
        "  {\"type\": \"list\", \"items\": [\"a\",\"b\",\"c\"], \"ordered\": false}\n\n" +
        "Style properties (for replace on /style paths):\n" +
        "  {\"bold\": true, \"italic\": false, \"font_size\": 14, \"color\": \"FF0000\"}")]
    public static string ApplyPatch(
        SessionManager sessions,
        [Description("Session ID of the document.")] string doc_id,
        [Description("JSON array of patch operations.")] string patches)
    {
        var session = sessions.Get(doc_id);
        var wpDoc = session.Document;
        var mainPart = wpDoc.MainDocumentPart
            ?? throw new InvalidOperationException("Document has no MainDocumentPart.");

        JsonElement patchArray;
        try
        {
            patchArray = JsonDocument.Parse(patches).RootElement;
        }
        catch (JsonException ex)
        {
            return $"Error: Invalid JSON — {ex.Message}";
        }

        if (patchArray.ValueKind != JsonValueKind.Array)
            return "Error: patches must be a JSON array.";

        var results = new List<string>();
        int applied = 0;

        foreach (var patch in patchArray.EnumerateArray())
        {
            try
            {
                var op = patch.GetProperty("op").GetString()?.ToLowerInvariant()
                    ?? throw new ArgumentException("Patch must have an 'op' field.");

                switch (op)
                {
                    case "add":
                        ApplyAdd(patch, wpDoc, mainPart);
                        break;
                    case "replace":
                        ApplyReplace(patch, wpDoc, mainPart);
                        break;
                    case "remove":
                        ApplyRemove(patch, wpDoc);
                        break;
                    case "move":
                        ApplyMove(patch, wpDoc);
                        break;
                    case "copy":
                        ApplyCopy(patch, wpDoc);
                        break;
                    default:
                        results.Add($"Unknown operation: '{op}'");
                        continue;
                }

                applied++;
            }
            catch (Exception ex)
            {
                var pathStr = patch.TryGetProperty("path", out var p) ? p.GetString() : "(no path)";
                results.Add($"Error at '{pathStr}': {ex.Message}");
            }
        }

        if (results.Count > 0)
            return $"Applied {applied}/{patchArray.GetArrayLength()} patches.\n" +
                   string.Join("\n", results);

        return $"Applied {applied} patch(es) successfully.";
    }

    private static void ApplyAdd(JsonElement patch, WordprocessingDocument wpDoc, MainDocumentPart mainPart)
    {
        var pathStr = patch.GetProperty("path").GetString()
            ?? throw new ArgumentException("Add patch must have a 'path' field.");
        var value = patch.GetProperty("value");

        var path = DocxPath.Parse(pathStr);

        if (path.IsChildrenPath)
        {
            var (parent, index) = PathResolver.ResolveForInsert(path, wpDoc);

            if (value.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "list")
            {
                var items = ElementFactory.CreateListItems(value);
                for (int i = items.Count - 1; i >= 0; i--)
                {
                    parent.InsertChildAt(items[i], index);
                }
            }
            else
            {
                var element = ElementFactory.CreateFromJson(value, mainPart);
                parent.InsertChildAt(element, index);
            }
        }
        else
        {
            var parentPath = new DocxPath(path.Segments.ToList());
            var parents = PathResolver.Resolve(parentPath, wpDoc);

            if (parents.Count != 1)
                throw new InvalidOperationException("Add path must resolve to exactly one parent.");

            var parent = parents[0];

            if (value.TryGetProperty("type", out var t) && t.GetString() == "list")
            {
                var items = ElementFactory.CreateListItems(value);
                foreach (var item in items)
                    parent.AppendChild(item);
            }
            else
            {
                var element = ElementFactory.CreateFromJson(value, mainPart);
                parent.AppendChild(element);
            }
        }
    }

    private static void ApplyReplace(JsonElement patch, WordprocessingDocument wpDoc, MainDocumentPart mainPart)
    {
        var pathStr = patch.GetProperty("path").GetString()
            ?? throw new ArgumentException("Replace patch must have a 'path' field.");
        var value = patch.GetProperty("value");

        var path = DocxPath.Parse(pathStr);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{pathStr}'.");

        if (path.Leaf is StyleSegment)
        {
            foreach (var target in targets)
            {
                if (target is ParagraphProperties)
                {
                    var newProps = ElementFactory.CreateParagraphProperties(value);
                    target.Parent?.ReplaceChild(newProps, target);
                }
                else if (target is RunProperties)
                {
                    var newProps = ElementFactory.CreateRunProperties(value);
                    target.Parent?.ReplaceChild(newProps, target);
                }
            }
            return;
        }

        foreach (var target in targets)
        {
            var parent = target.Parent
                ?? throw new InvalidOperationException("Target element has no parent.");

            var newElement = ElementFactory.CreateFromJson(value, mainPart);
            parent.ReplaceChild(newElement, target);
        }
    }

    private static void ApplyRemove(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var pathStr = patch.GetProperty("path").GetString()
            ?? throw new ArgumentException("Remove patch must have a 'path' field.");

        var path = DocxPath.Parse(pathStr);
        var targets = PathResolver.Resolve(path, wpDoc);

        if (targets.Count == 0)
            throw new InvalidOperationException($"No elements found at path '{pathStr}'.");

        foreach (var target in targets)
        {
            target.Parent?.RemoveChild(target);
        }
    }

    private static void ApplyMove(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var fromStr = patch.GetProperty("from").GetString()
            ?? throw new ArgumentException("Move patch must have a 'from' field.");
        var toStr = patch.GetProperty("path").GetString()
            ?? throw new ArgumentException("Move patch must have a 'path' field.");

        var fromPath = DocxPath.Parse(fromStr);
        var sources = PathResolver.Resolve(fromPath, wpDoc);
        if (sources.Count != 1)
            throw new InvalidOperationException("Move source must resolve to exactly one element.");

        var source = sources[0];
        source.Parent?.RemoveChild(source);

        var toPath = DocxPath.Parse(toStr);
        if (toPath.IsChildrenPath)
        {
            var (parent, index) = PathResolver.ResolveForInsert(toPath, wpDoc);
            parent.InsertChildAt(source, index);
        }
        else
        {
            var targets = PathResolver.Resolve(toPath, wpDoc);
            if (targets.Count != 1)
                throw new InvalidOperationException("Move target must resolve to exactly one location.");

            var target = targets[0];
            target.Parent?.InsertAfter(source, target);
        }
    }

    private static void ApplyCopy(JsonElement patch, WordprocessingDocument wpDoc)
    {
        var fromStr = patch.GetProperty("from").GetString()
            ?? throw new ArgumentException("Copy patch must have a 'from' field.");
        var toStr = patch.GetProperty("path").GetString()
            ?? throw new ArgumentException("Copy patch must have a 'path' field.");

        var fromPath = DocxPath.Parse(fromStr);
        var sources = PathResolver.Resolve(fromPath, wpDoc);
        if (sources.Count != 1)
            throw new InvalidOperationException("Copy source must resolve to exactly one element.");

        var clone = sources[0].CloneNode(true);

        var toPath = DocxPath.Parse(toStr);
        if (toPath.IsChildrenPath)
        {
            var (parent, index) = PathResolver.ResolveForInsert(toPath, wpDoc);
            parent.InsertChildAt(clone, index);
        }
        else
        {
            var targets = PathResolver.Resolve(toPath, wpDoc);
            if (targets.Count != 1)
                throw new InvalidOperationException("Copy target must resolve to exactly one location.");

            var target = targets[0];
            target.Parent?.InsertAfter(clone, target);
        }
    }
}
