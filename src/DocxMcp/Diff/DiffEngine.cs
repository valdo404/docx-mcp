using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocxMcp.Helpers;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace DocxMcp.Diff;

/// <summary>
/// Computes structured diffs between two Word documents.
/// Uses content-based matching (LCS algorithm) - does NOT rely on element IDs.
/// Works with any Word document, including those created by Microsoft Word.
/// </summary>
public static class DiffEngine
{
    /// <summary>
    /// Minimum similarity threshold for fuzzy matching (0.0 to 1.0).
    /// </summary>
    public const double DefaultSimilarityThreshold = 0.6;

    /// <summary>
    /// Compare two documents and produce a diff result.
    /// </summary>
    /// <param name="original">The original/baseline document.</param>
    /// <param name="modified">The modified/new document.</param>
    /// <param name="similarityThreshold">Minimum similarity for fuzzy matching (default 0.6).</param>
    /// <returns>A DiffResult containing all detected changes and generated patches.</returns>
    public static DiffResult Compare(
        WordprocessingDocument original,
        WordprocessingDocument modified,
        double similarityThreshold = DefaultSimilarityThreshold)
    {
        var originalBody = original.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Original document has no body.");
        var modifiedBody = modified.MainDocumentPart?.Document?.Body
            ?? throw new InvalidOperationException("Modified document has no body.");

        // Take snapshots of all top-level content elements
        var originalSnapshots = CaptureSnapshots(originalBody, "/body");
        var modifiedSnapshots = CaptureSnapshots(modifiedBody, "/body");

        // Use LCS-based matching to find correspondences
        var matches = ComputeMatches(originalSnapshots, modifiedSnapshots, similarityThreshold);

        // Generate changes from matches
        var changes = GenerateChanges(originalSnapshots, modifiedSnapshots, matches);

        // Detect uncovered changes (headers, footers, images, etc.)
        var uncoveredChanges = DetectUncoveredChanges(original, modified);

        return new DiffResult { Changes = changes, UncoveredChanges = uncoveredChanges };
    }

    /// <summary>
    /// Compare two documents from byte arrays.
    /// </summary>
    public static DiffResult Compare(
        byte[] originalBytes,
        byte[] modifiedBytes,
        double similarityThreshold = DefaultSimilarityThreshold)
    {
        using var originalStream = new MemoryStream();
        originalStream.Write(originalBytes);
        originalStream.Position = 0;

        using var modifiedStream = new MemoryStream();
        modifiedStream.Write(modifiedBytes);
        modifiedStream.Position = 0;

        using var originalDoc = WordprocessingDocument.Open(originalStream, isEditable: false);
        using var modifiedDoc = WordprocessingDocument.Open(modifiedStream, isEditable: false);

        return Compare(originalDoc, modifiedDoc, similarityThreshold);
    }

    /// <summary>
    /// Compare two documents from file paths.
    /// </summary>
    public static DiffResult Compare(
        string originalPath,
        string modifiedPath,
        double similarityThreshold = DefaultSimilarityThreshold)
    {
        var originalBytes = File.ReadAllBytes(originalPath);
        var modifiedBytes = File.ReadAllBytes(modifiedPath);

        return Compare(originalBytes, modifiedBytes, similarityThreshold);
    }

    /// <summary>
    /// Compare a DocxSession's current state with a file on disk.
    /// Useful for detecting external modifications.
    /// </summary>
    public static DiffResult CompareSessionWithFile(
        DocxSession session,
        string filePath,
        double similarityThreshold = DefaultSimilarityThreshold)
    {
        var sessionBytes = session.ToBytes();
        var fileBytes = File.ReadAllBytes(filePath);

        // Session state = original, file = modified (external changes)
        return Compare(sessionBytes, fileBytes, similarityThreshold);
    }

    /// <summary>
    /// Capture snapshots of all top-level body elements (paragraphs, tables).
    /// </summary>
    private static List<ElementSnapshot> CaptureSnapshots(Body body, string basePath)
    {
        var snapshots = new List<ElementSnapshot>();
        int contentIndex = 0;

        foreach (var element in body.ChildElements)
        {
            // Only track content elements
            if (element is Paragraph or Table)
            {
                snapshots.Add(ElementSnapshot.FromElement(element, contentIndex, basePath));
                contentIndex++;
            }
        }

        return snapshots;
    }

    /// <summary>
    /// Compute matches between original and modified elements using LCS + fuzzy matching.
    /// Returns a dictionary mapping original index to (modified index, match type).
    /// </summary>
    private static MatchResult ComputeMatches(
        List<ElementSnapshot> original,
        List<ElementSnapshot> modified,
        double threshold)
    {
        var result = new MatchResult();

        // Step 1: Find exact matches by fingerprint using position-aware grouping.
        // Group elements by fingerprint, then pair in positional order (first↔first, second↔second)
        // to avoid incorrect pairings when multiple elements share the same fingerprint.
        var modifiedUsed = new HashSet<int>();
        var exactMatches = new Dictionary<int, int>(); // origIdx -> modIdx

        // Group original and modified indices by fingerprint
        var origByFingerprint = new Dictionary<string, List<int>>();
        for (int i = 0; i < original.Count; i++)
        {
            var fp = original[i].Fingerprint;
            if (!origByFingerprint.TryGetValue(fp, out var list))
            {
                list = [];
                origByFingerprint[fp] = list;
            }
            list.Add(i);
        }

        var modByFingerprint = new Dictionary<string, List<int>>();
        for (int j = 0; j < modified.Count; j++)
        {
            var fp = modified[j].Fingerprint;
            if (!modByFingerprint.TryGetValue(fp, out var list))
            {
                list = [];
                modByFingerprint[fp] = list;
            }
            list.Add(j);
        }

        // Pair in positional order within each fingerprint group
        foreach (var (fp, origIndices) in origByFingerprint)
        {
            if (!modByFingerprint.TryGetValue(fp, out var modIndices))
                continue;

            var pairCount = Math.Min(origIndices.Count, modIndices.Count);
            for (int k = 0; k < pairCount; k++)
            {
                exactMatches[origIndices[k]] = modIndices[k];
                modifiedUsed.Add(modIndices[k]);
            }
        }

        // Step 2: Use LCS on remaining elements to find position-based matches
        var unmatchedOrig = Enumerable.Range(0, original.Count)
            .Where(i => !exactMatches.ContainsKey(i))
            .ToList();
        var unmatchedMod = Enumerable.Range(0, modified.Count)
            .Where(j => !modifiedUsed.Contains(j))
            .ToList();

        var lcsMatches = ComputeLcsMatches(
            unmatchedOrig.Select(i => original[i]).ToList(),
            unmatchedMod.Select(j => modified[j]).ToList(),
            threshold);

        // Map LCS results back to original indices
        var fuzzyMatches = new Dictionary<int, (int modIdx, double similarity)>();
        foreach (var (localOrigIdx, localModIdx, similarity) in lcsMatches)
        {
            var origIdx = unmatchedOrig[localOrigIdx];
            var modIdx = unmatchedMod[localModIdx];
            fuzzyMatches[origIdx] = (modIdx, similarity);
            modifiedUsed.Add(modIdx);
        }

        // Build result
        for (int i = 0; i < original.Count; i++)
        {
            if (exactMatches.TryGetValue(i, out var exactModIdx))
            {
                result.Matches[i] = new ElementMatch
                {
                    OriginalIndex = i,
                    ModifiedIndex = exactModIdx,
                    MatchType = MatchType.Exact,
                    Similarity = 1.0
                };
            }
            else if (fuzzyMatches.TryGetValue(i, out var fuzzy))
            {
                result.Matches[i] = new ElementMatch
                {
                    OriginalIndex = i,
                    ModifiedIndex = fuzzy.modIdx,
                    MatchType = MatchType.Similar,
                    Similarity = fuzzy.similarity
                };
            }
            // Else: no match = deleted
        }

        // Track unmatched modified elements (additions)
        result.UnmatchedModified = Enumerable.Range(0, modified.Count)
            .Where(j => !modifiedUsed.Contains(j))
            .ToList();

        return result;
    }

    /// <summary>
    /// Compute LCS-based matches with similarity scoring.
    /// </summary>
    private static List<(int origIdx, int modIdx, double similarity)> ComputeLcsMatches(
        List<ElementSnapshot> original,
        List<ElementSnapshot> modified,
        double threshold)
    {
        var matches = new List<(int, int, double)>();

        if (original.Count == 0 || modified.Count == 0)
            return matches;

        // Build similarity matrix
        var simMatrix = new double[original.Count, modified.Count];
        for (int i = 0; i < original.Count; i++)
        {
            for (int j = 0; j < modified.Count; j++)
            {
                simMatrix[i, j] = original[i].SimilarityTo(modified[j]);
            }
        }

        // Greedy matching: find best matches above threshold
        var usedOrig = new HashSet<int>();
        var usedMod = new HashSet<int>();

        while (true)
        {
            // Find best remaining match
            double bestSim = 0;
            int bestI = -1, bestJ = -1;

            for (int i = 0; i < original.Count; i++)
            {
                if (usedOrig.Contains(i)) continue;

                for (int j = 0; j < modified.Count; j++)
                {
                    if (usedMod.Contains(j)) continue;

                    if (simMatrix[i, j] > bestSim)
                    {
                        bestSim = simMatrix[i, j];
                        bestI = i;
                        bestJ = j;
                    }
                }
            }

            // Stop if no match above threshold
            if (bestSim < threshold || bestI < 0)
                break;

            matches.Add((bestI, bestJ, bestSim));
            usedOrig.Add(bestI);
            usedMod.Add(bestJ);
        }

        return matches;
    }

    /// <summary>
    /// Generate change list from matches.
    /// </summary>
    private static List<ElementChange> GenerateChanges(
        List<ElementSnapshot> original,
        List<ElementSnapshot> modified,
        MatchResult matches)
    {
        var changes = new List<ElementChange>();

        // Build a list of matched pairs sorted by original index to detect relative order changes
        var sortedMatches = matches.Matches
            .OrderBy(kvp => kvp.Key)
            .Select(kvp => (origIdx: kvp.Key, modIdx: kvp.Value.ModifiedIndex, match: kvp.Value))
            .ToList();

        // Detect moved elements using Longest Increasing Subsequence (LIS).
        // Elements NOT in the LIS of modified indices are the ones that truly "moved".
        var movedElements = new HashSet<int>();
        if (sortedMatches.Count > 1)
        {
            var modifiedSequence = sortedMatches.Select(m => m.modIdx).ToList();
            var lisIndices = ComputeLisIndices(modifiedSequence);
            var lisSet = new HashSet<int>(lisIndices);

            for (int i = 0; i < sortedMatches.Count; i++)
            {
                if (!lisSet.Contains(i))
                    movedElements.Add(sortedMatches[i].origIdx);
            }
        }

        // Process matched elements
        foreach (var (origIdx, match) in matches.Matches)
        {
            var origSnap = original[origIdx];
            var modSnap = modified[match.ModifiedIndex];

            if (match.MatchType == MatchType.Exact)
            {
                // Only report as "moved" if relative order changed (not just absolute index)
                if (movedElements.Contains(origIdx))
                {
                    changes.Add(new ElementChange
                    {
                        ChangeType = ChangeType.Moved,
                        ElementId = origSnap.Fingerprint, // Use fingerprint as identifier
                        ElementType = origSnap.ElementType,
                        OldPath = origSnap.Path,
                        NewPath = modSnap.Path,
                        OldIndex = origIdx,
                        NewIndex = match.ModifiedIndex,
                        OldText = origSnap.Text,
                        NewText = modSnap.Text
                    });
                }
                // Else: no change (index shift due to additions/deletions is not a "move")
            }
            else // Similar match = modification
            {
                changes.Add(new ElementChange
                {
                    ChangeType = ChangeType.Modified,
                    ElementId = origSnap.Fingerprint,
                    ElementType = origSnap.ElementType,
                    OldPath = origSnap.Path,
                    NewPath = modSnap.Path,
                    OldIndex = origIdx,
                    NewIndex = match.ModifiedIndex,
                    OldText = origSnap.Text,
                    NewText = modSnap.Text,
                    OldValue = origSnap.JsonValue,
                    NewValue = CreateValueForPatch(modSnap)
                });
            }
        }

        // Process deletions (unmatched original elements)
        for (int i = 0; i < original.Count; i++)
        {
            if (!matches.Matches.ContainsKey(i))
            {
                var snap = original[i];
                changes.Add(new ElementChange
                {
                    ChangeType = ChangeType.Removed,
                    ElementId = snap.Fingerprint,
                    ElementType = snap.ElementType,
                    OldPath = snap.Path,
                    OldIndex = i,
                    OldText = snap.Text,
                    OldValue = snap.JsonValue
                });
            }
        }

        // Process additions (unmatched modified elements)
        foreach (var modIdx in matches.UnmatchedModified)
        {
            var snap = modified[modIdx];
            changes.Add(new ElementChange
            {
                ChangeType = ChangeType.Added,
                ElementId = snap.Fingerprint,
                ElementType = snap.ElementType,
                NewPath = snap.Path,
                NewIndex = modIdx,
                NewText = snap.Text,
                NewValue = CreateValueForPatch(snap)
            });
        }

        // Sort changes for consistent output
        return changes
            .OrderBy(c => c.ChangeType switch
            {
                ChangeType.Removed => 0,
                ChangeType.Modified => 1,
                ChangeType.Moved => 2,
                ChangeType.Added => 3,
                _ => 4
            })
            .ThenBy(c => c.OldIndex ?? c.NewIndex ?? 0)
            .ToList();
    }

    /// <summary>
    /// Create a JSON value suitable for a patch operation.
    /// </summary>
    private static JsonObject CreateValueForPatch(ElementSnapshot snapshot)
    {
        var value = new JsonObject
        {
            ["type"] = snapshot.ElementType
        };

        // Copy relevant properties from the snapshot's JSON
        foreach (var prop in snapshot.JsonValue)
        {
            if (prop.Key == "type")
                continue;

            value[prop.Key] = prop.Value is not null
                ? JsonNode.Parse(prop.Value.ToJsonString())
                : null;
        }

        return value;
    }

    /// <summary>
    /// Compute indices of the Longest Increasing Subsequence in O(n log n).
    /// Returns the set of indices in the input that form the LIS.
    /// </summary>
    private static List<int> ComputeLisIndices(List<int> sequence)
    {
        if (sequence.Count == 0) return [];

        var n = sequence.Count;
        // tails[i] = smallest tail element for increasing subsequence of length i+1
        var tails = new List<int>();
        // tailIndices[i] = index in 'sequence' of tails[i]
        var tailIndices = new List<int>();
        // predecessor[i] = index in 'sequence' of the element before sequence[i] in the LIS
        var predecessor = new int[n];
        Array.Fill(predecessor, -1);

        for (int i = 0; i < n; i++)
        {
            var val = sequence[i];
            // Binary search for the position
            int lo = 0, hi = tails.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (tails[mid] < val)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            if (lo == tails.Count)
            {
                tails.Add(val);
                tailIndices.Add(i);
            }
            else
            {
                tails[lo] = val;
                tailIndices[lo] = i;
            }

            if (lo > 0)
                predecessor[i] = tailIndices[lo - 1];
        }

        // Reconstruct the LIS indices
        var result = new List<int>();
        int idx = tailIndices[^1];
        while (idx >= 0)
        {
            result.Add(idx);
            idx = predecessor[idx];
        }
        result.Reverse();
        return result;
    }

    /// <summary>
    /// Internal result of the matching algorithm.
    /// </summary>
    private sealed class MatchResult
    {
        public Dictionary<int, ElementMatch> Matches { get; } = [];
        public List<int> UnmatchedModified { get; set; } = [];
    }

    /// <summary>
    /// Represents a match between an original and modified element.
    /// </summary>
    private sealed class ElementMatch
    {
        public required int OriginalIndex { get; init; }
        public required int ModifiedIndex { get; init; }
        public required MatchType MatchType { get; init; }
        public required double Similarity { get; init; }
    }

    /// <summary>
    /// Type of match found.
    /// </summary>
    private enum MatchType
    {
        Exact,  // Identical fingerprint
        Similar // Fuzzy match above threshold
    }

    /// <summary>
    /// Detect changes to document parts outside the main body.
    /// These are "uncovered" changes that can't be represented as body patches.
    /// </summary>
    public static List<UncoveredChange> DetectUncoveredChanges(
        WordprocessingDocument original,
        WordprocessingDocument modified)
    {
        var changes = new List<UncoveredChange>();

        var origMain = original.MainDocumentPart;
        var modMain = modified.MainDocumentPart;

        if (origMain is null || modMain is null)
            return changes;

        // Compare headers
        CompareHeaderFooterParts(
            origMain.HeaderParts.Select(h => (h.Uri, (OpenXmlElement?)h.Header)),
            modMain.HeaderParts.Select(h => (h.Uri, (OpenXmlElement?)h.Header)),
            UncoveredChangeType.Header, "header", changes);

        // Compare footers
        CompareHeaderFooterParts(
            origMain.FooterParts.Select(f => (f.Uri, (OpenXmlElement?)f.Footer)),
            modMain.FooterParts.Select(f => (f.Uri, (OpenXmlElement?)f.Footer)),
            UncoveredChangeType.Footer, "footer", changes);

        // Compare styles
        CompareSinglePart(
            origMain.StyleDefinitionsPart?.Styles,
            modMain.StyleDefinitionsPart?.Styles,
            UncoveredChangeType.StyleDefinition,
            "Style definitions",
            "/word/styles.xml",
            changes);

        // Compare numbering
        CompareSinglePart(
            origMain.NumberingDefinitionsPart?.Numbering,
            modMain.NumberingDefinitionsPart?.Numbering,
            UncoveredChangeType.Numbering,
            "Numbering definitions",
            "/word/numbering.xml",
            changes);

        // Compare settings
        CompareSinglePart(
            origMain.DocumentSettingsPart?.Settings,
            modMain.DocumentSettingsPart?.Settings,
            UncoveredChangeType.Settings,
            "Document settings",
            "/word/settings.xml",
            changes);

        // Compare footnotes
        CompareSinglePart(
            origMain.FootnotesPart?.Footnotes,
            modMain.FootnotesPart?.Footnotes,
            UncoveredChangeType.Footnote,
            "Footnotes",
            "/word/footnotes.xml",
            changes);

        // Compare endnotes
        CompareSinglePart(
            origMain.EndnotesPart?.Endnotes,
            modMain.EndnotesPart?.Endnotes,
            UncoveredChangeType.Endnote,
            "Endnotes",
            "/word/endnotes.xml",
            changes);

        // Compare comments
        CompareSinglePart(
            origMain.WordprocessingCommentsPart?.Comments,
            modMain.WordprocessingCommentsPart?.Comments,
            UncoveredChangeType.Comment,
            "Comments",
            "/word/comments.xml",
            changes);

        // Compare theme
        CompareSinglePart(
            origMain.ThemePart?.Theme,
            modMain.ThemePart?.Theme,
            UncoveredChangeType.Theme,
            "Document theme",
            "/word/theme/theme1.xml",
            changes);

        // Compare embedded images/media
        CompareImageParts(original, modified, changes);

        // Compare document properties
        CompareDocumentProperties(original, modified, changes);

        return changes;
    }

    private static void CompareHeaderFooterParts(
        IEnumerable<(Uri Uri, OpenXmlElement? Element)> originalParts,
        IEnumerable<(Uri Uri, OpenXmlElement? Element)> modifiedParts,
        UncoveredChangeType changeType,
        string partName,
        List<UncoveredChange> changes)
    {
        var origDict = originalParts
            .Where(p => p.Element is not null)
            .ToDictionary(p => p.Uri.ToString(), p => ComputeStrippedHash(p.Element!));
        var modDict = modifiedParts
            .Where(p => p.Element is not null)
            .ToDictionary(p => p.Uri.ToString(), p => ComputeStrippedHash(p.Element!));

        // Check for removed or modified
        foreach (var (uri, hash) in origDict)
        {
            if (!modDict.TryGetValue(uri, out var modHash))
            {
                changes.Add(new UncoveredChange
                {
                    Type = changeType,
                    Description = $"{char.ToUpper(partName[0])}{partName[1..]} removed",
                    PartUri = uri,
                    ChangeKind = "removed"
                });
            }
            else if (hash != modHash)
            {
                changes.Add(new UncoveredChange
                {
                    Type = changeType,
                    Description = $"{char.ToUpper(partName[0])}{partName[1..]} modified",
                    PartUri = uri,
                    ChangeKind = "modified"
                });
            }
        }

        // Check for added
        foreach (var (uri, _) in modDict)
        {
            if (!origDict.ContainsKey(uri))
            {
                changes.Add(new UncoveredChange
                {
                    Type = changeType,
                    Description = $"{char.ToUpper(partName[0])}{partName[1..]} added",
                    PartUri = uri,
                    ChangeKind = "added"
                });
            }
        }
    }

    private static void CompareSinglePart(
        OpenXmlElement? originalElement,
        OpenXmlElement? modifiedElement,
        UncoveredChangeType changeType,
        string description,
        string partUri,
        List<UncoveredChange> changes)
    {
        var origHash = originalElement is not null ? ComputeStrippedHash(originalElement) : null;
        var modHash = modifiedElement is not null ? ComputeStrippedHash(modifiedElement) : null;

        if (origHash is null && modHash is not null)
        {
            changes.Add(new UncoveredChange
            {
                Type = changeType,
                Description = $"{description} added",
                PartUri = partUri,
                ChangeKind = "added"
            });
        }
        else if (origHash is not null && modHash is null)
        {
            changes.Add(new UncoveredChange
            {
                Type = changeType,
                Description = $"{description} removed",
                PartUri = partUri,
                ChangeKind = "removed"
            });
        }
        else if (origHash is not null && modHash is not null && origHash != modHash)
        {
            changes.Add(new UncoveredChange
            {
                Type = changeType,
                Description = $"{description} modified",
                PartUri = partUri,
                ChangeKind = "modified"
            });
        }
    }

    private static void CompareImageParts(
        WordprocessingDocument original,
        WordprocessingDocument modified,
        List<UncoveredChange> changes)
    {
        var origImages = GetImagePartHashes(original);
        var modImages = GetImagePartHashes(modified);

        foreach (var (uri, hash) in origImages)
        {
            if (!modImages.TryGetValue(uri, out var modHash))
            {
                changes.Add(new UncoveredChange
                {
                    Type = UncoveredChangeType.Image,
                    Description = $"Image removed: {Path.GetFileName(uri)}",
                    PartUri = uri,
                    ChangeKind = "removed"
                });
            }
            else if (hash != modHash)
            {
                changes.Add(new UncoveredChange
                {
                    Type = UncoveredChangeType.Image,
                    Description = $"Image modified: {Path.GetFileName(uri)}",
                    PartUri = uri,
                    ChangeKind = "modified"
                });
            }
        }

        foreach (var (uri, _) in modImages)
        {
            if (!origImages.ContainsKey(uri))
            {
                changes.Add(new UncoveredChange
                {
                    Type = UncoveredChangeType.Image,
                    Description = $"Image added: {Path.GetFileName(uri)}",
                    PartUri = uri,
                    ChangeKind = "added"
                });
            }
        }
    }

    private static Dictionary<string, string> GetImagePartHashes(WordprocessingDocument doc)
    {
        var result = new Dictionary<string, string>();
        var mainPart = doc.MainDocumentPart;
        if (mainPart is null) return result;

        foreach (var imagePart in mainPart.ImageParts)
        {
            try
            {
                using var stream = imagePart.GetStream();
                var hash = SHA256.HashData(stream);
                result[imagePart.Uri.ToString()] = Convert.ToHexString(hash);
            }
            catch
            {
                // Skip parts that can't be read
            }
        }

        return result;
    }

    private static void CompareDocumentProperties(
        WordprocessingDocument original,
        WordprocessingDocument modified,
        List<UncoveredChange> changes)
    {
        // Compare core properties
        var origCore = original.PackageProperties;
        var modCore = modified.PackageProperties;

        var coreChanged = false;
        if (origCore.Title != modCore.Title ||
            origCore.Subject != modCore.Subject ||
            origCore.Creator != modCore.Creator ||
            origCore.Keywords != modCore.Keywords ||
            origCore.Description != modCore.Description ||
            origCore.Category != modCore.Category)
        {
            coreChanged = true;
        }

        if (coreChanged)
        {
            changes.Add(new UncoveredChange
            {
                Type = UncoveredChangeType.DocumentProperty,
                Description = "Document properties modified",
                PartUri = "/docProps/core.xml",
                ChangeKind = "modified"
            });
        }

        // Compare extended properties
        var origExtProps = original.ExtendedFilePropertiesPart?.Properties?.OuterXml;
        var modExtProps = modified.ExtendedFilePropertiesPart?.Properties?.OuterXml;

        if (origExtProps != modExtProps)
        {
            var kind = origExtProps is null ? "added" : modExtProps is null ? "removed" : "modified";
            changes.Add(new UncoveredChange
            {
                Type = UncoveredChangeType.DocumentProperty,
                Description = $"Extended document properties {kind}",
                PartUri = "/docProps/app.xml",
                ChangeKind = kind
            });
        }
    }

    /// <summary>
    /// Compute hash of an element's XML after stripping ID/revision attributes.
    /// </summary>
    private static string ComputeStrippedHash(OpenXmlElement element)
    {
        var clone = (OpenXmlElement)element.CloneNode(true);
        ContentHasher.StripIdAttributes(clone);
        return ComputeHash(clone.OuterXml);
    }

    private static string ComputeHash(string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}

/// <summary>
/// Extension methods for comparing documents.
/// </summary>
public static class DiffExtensions
{
    /// <summary>
    /// Compare this session with another session.
    /// </summary>
    public static DiffResult CompareTo(this DocxSession session, DocxSession other)
    {
        return DiffEngine.Compare(session.Document, other.Document);
    }

    /// <summary>
    /// Compare this session with a file on disk.
    /// </summary>
    public static DiffResult CompareToFile(this DocxSession session, string filePath)
    {
        return DiffEngine.CompareSessionWithFile(session, filePath);
    }

    /// <summary>
    /// Check if the source file has been modified externally.
    /// </summary>
    public static bool HasExternalChanges(this DocxSession session)
    {
        if (session.SourcePath is null)
            return false;

        if (!File.Exists(session.SourcePath))
            return false;

        var diff = session.CompareToFile(session.SourcePath);
        return diff.HasChanges;
    }
}
