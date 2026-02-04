using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace DocxMcp.Helpers;

/// <summary>
/// Computes content-based hash by stripping all ID attributes.
/// Used for detecting real content changes vs. ID reassignment noise.
///
/// When comparing documents for sync-external, raw byte comparison fails because:
/// - Session documents have dmcp:id attributes assigned
/// - External files don't have these attributes
/// - Even unchanged files appear "different" due to ID presence
///
/// This class strips all ID/revision attributes before hashing, enabling
/// true content-based comparison.
/// </summary>
public static class ContentHasher
{
    // Attributes to strip when computing content hash
    // These are all ID or revision-tracking attributes that don't affect content
    private static readonly string[] IdLocalNames =
    [
        "id",       // dmcp:id
        "paraId",   // w14:paraId
        "textId",   // w14:textId
        "rsidR",    // w:rsidR - revision save ID (run)
        "rsidRPr",  // w:rsidRPr - revision save ID (run properties)
        "rsidP",    // w:rsidP - revision save ID (paragraph)
        "rsidRDefault", // w:rsidRDefault - default revision save ID
        "rsidSect", // w:rsidSect - revision save ID (section)
        "rsidTr",   // w:rsidTr - revision save ID (table row)
        "rsidDel",  // w:rsidDel - revision save ID (deletion)
    ];

    // Namespaces that contain ID attributes we want to strip
    private static readonly string[] IdNamespaces =
    [
        ElementIdManager.DmcpNamespace,  // http://docx-mcp.dev/id
        "http://schemas.microsoft.com/office/word/2010/wordml",  // w14 (paraId, textId)
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main",  // w (rsid*)
    ];

    /// <summary>
    /// Compute a hash of the document content, ignoring all ID/revision attributes.
    /// </summary>
    /// <param name="documentBytes">The raw bytes of the DOCX file.</param>
    /// <returns>A 16-character hex hash string representing the content.</returns>
    public static string ComputeContentHash(byte[] documentBytes)
    {
        try
        {
            using var ms = new MemoryStream(documentBytes);
            using var doc = WordprocessingDocument.Open(ms, false);

            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
                return ComputeBytesHash(documentBytes); // Fallback to raw hash

            // Clone the body and strip ID attributes
            var clone = (OpenXmlElement)body.CloneNode(true);
            StripIdAttributes(clone);

            // Hash the stripped XML
            var xml = clone.OuterXml;
            return ComputeStringHash(xml);
        }
        catch
        {
            // If anything fails, fall back to raw bytes hash
            return ComputeBytesHash(documentBytes);
        }
    }

    /// <summary>
    /// Strip all ID and revision attributes from an element and its descendants.
    /// Also removes namespace declarations for dmcp namespace to ensure consistent hashing.
    /// </summary>
    internal static void StripIdAttributes(OpenXmlElement element)
    {
        // Get all attributes and find ones to remove
        var attributes = element.GetAttributes().ToList();

        foreach (var attr in attributes)
        {
            // Check if this is an ID attribute by local name
            if (IdLocalNames.Contains(attr.LocalName))
            {
                // Verify it's in a known ID namespace (to avoid stripping user content)
                if (string.IsNullOrEmpty(attr.NamespaceUri) || IdNamespaces.Any(ns => attr.NamespaceUri.Contains(ns)))
                {
                    element.RemoveAttribute(attr.LocalName, attr.NamespaceUri);
                }
            }
        }

        // Also remove Ignorable attribute that references dmcp
        var ignorableAttr = element.GetAttributes()
            .FirstOrDefault(a => a.LocalName == "Ignorable" && a.NamespaceUri.Contains("markup-compatibility"));
        if (!string.IsNullOrEmpty(ignorableAttr.Value))
        {
            // Remove or clean up the Ignorable attribute
            var cleanedValue = string.Join(" ",
                ignorableAttr.Value.Split(' ')
                    .Where(v => v != ElementIdManager.DmcpPrefix && v != "w14"));
            if (string.IsNullOrWhiteSpace(cleanedValue))
            {
                element.RemoveAttribute(ignorableAttr.LocalName, ignorableAttr.NamespaceUri);
            }
            else if (cleanedValue != ignorableAttr.Value)
            {
                element.SetAttribute(new OpenXmlAttribute(
                    ignorableAttr.Prefix,
                    ignorableAttr.LocalName,
                    ignorableAttr.NamespaceUri,
                    cleanedValue));
            }
        }

        // Remove namespace declarations for dmcp and w14
        var nsToRemove = element.NamespaceDeclarations
            .Where(ns => ns.Key == ElementIdManager.DmcpPrefix ||
                         ns.Key == "w14" ||
                         ns.Value == ElementIdManager.DmcpNamespace ||
                         ns.Value.Contains("word/2010/wordml"))
            .ToList();
        foreach (var ns in nsToRemove)
        {
            element.RemoveNamespaceDeclaration(ns.Key);
        }

        // Recurse into children
        foreach (var child in element.ChildElements)
        {
            StripIdAttributes(child);
        }
    }

    /// <summary>
    /// Compute SHA256 hash of a string, returning first 16 hex characters.
    /// </summary>
    private static string ComputeStringHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return ComputeBytesHash(bytes);
    }

    /// <summary>
    /// Compute SHA256 hash of bytes, returning first 16 hex characters.
    /// </summary>
    private static string ComputeBytesHash(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
