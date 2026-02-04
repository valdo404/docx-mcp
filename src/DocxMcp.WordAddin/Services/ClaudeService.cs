using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using DocxMcp.WordAddin.Models;

namespace DocxMcp.WordAddin.Services;

/// <summary>
/// Service for interacting with Claude API to generate document patches.
/// </summary>
public sealed class ClaudeService
{
    private readonly AnthropicClient _client;
    private readonly ILogger<ClaudeService> _logger;

    private const string MODEL = "claude-sonnet-4-20250514";
    private const int MAX_TOKENS = 4096;

    public ClaudeService(ILogger<ClaudeService> logger)
    {
        _logger = logger;

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException(
                "ANTHROPIC_API_KEY environment variable is not set. " +
                "Get your API key from https://console.anthropic.com/");

        _client = new AnthropicClient(apiKey);
    }

    /// <summary>
    /// Stream document patches from Claude based on user instruction.
    /// </summary>
    public async IAsyncEnumerable<LlmStreamEvent> StreamPatchesAsync(
        LlmEditRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var patchCount = 0;
        var inputTokens = 0;
        var outputTokens = 0;

        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(request);

        _logger.LogInformation("Starting Claude stream for session {SessionId}", request.SessionId);

        var messages = new List<Message>
        {
            new(RoleType.User, userPrompt)
        };

        var parameters = new MessageParameters
        {
            Model = MODEL,
            MaxTokens = MAX_TOKENS,
            System = [new SystemMessage(systemPrompt)],
            Messages = messages,
            Stream = true
        };

        var patchBuffer = new StringBuilder();

        await foreach (var evt in _client.Messages.StreamClaudeMessageAsync(parameters, cancellationToken))
        {
            if (evt.Delta?.Text is { } text)
            {
                // Parse streaming text for patches
                patchBuffer.Append(text);
                var bufferStr = patchBuffer.ToString();

                // Look for complete patch JSON blocks
                while (TryExtractPatch(ref bufferStr, out var patchJson))
                {
                    patchBuffer.Clear();
                    patchBuffer.Append(bufferStr);

                    if (TryParsePatch(patchJson, out var patch))
                    {
                        patchCount++;
                        _logger.LogDebug("Extracted patch {Count}: {Op} {Path}",
                            patchCount, patch.Op, patch.Path);

                        yield return new LlmStreamEvent
                        {
                            Type = "patch",
                            Patch = patch
                        };
                    }
                }

                // Also emit raw content for UI display
                yield return new LlmStreamEvent
                {
                    Type = "content",
                    Content = text
                };
            }

            if (evt.Usage is { OutputTokens: > 0 } usage)
            {
                outputTokens = usage.OutputTokens;
            }

            if (evt.StreamStartMessage?.Usage is { InputTokens: > 0 } startUsage)
            {
                inputTokens = startUsage.InputTokens;
            }
        }

        var duration = (DateTime.UtcNow - startTime).TotalMilliseconds;

        _logger.LogInformation(
            "Claude stream completed: {Patches} patches, {InputTokens}/{OutputTokens} tokens, {Duration}ms",
            patchCount, inputTokens, outputTokens, duration);

        yield return new LlmStreamEvent
        {
            Type = "done",
            Stats = new LlmStreamStats
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                PatchesGenerated = patchCount,
                DurationMs = duration
            }
        };
    }

    private static string BuildSystemPrompt()
    {
        return """
            You are a document editing assistant integrated into Microsoft Word.
            Your task is to modify documents based on user instructions.

            ## Output Format

            When making changes, output JSON patches in this exact format:
            ```patch
            {"op": "replace", "path": "/body/paragraph[0]", "value": {"type": "paragraph", "text": "New text here"}}
            ```

            Each patch must be on its own line within ```patch``` blocks.

            ## Available Operations

            - **add**: Insert a new element
              ```patch
              {"op": "add", "path": "/body/children/0", "value": {"type": "paragraph", "text": "New paragraph"}}
              ```

            - **replace**: Replace existing content
              ```patch
              {"op": "replace", "path": "/body/paragraph[id='ABC123']", "value": {"type": "paragraph", "text": "Updated text"}}
              ```

            - **remove**: Delete an element
              ```patch
              {"op": "remove", "path": "/body/paragraph[2]"}
              ```

            - **replace_text**: Find and replace text (preserves formatting)
              ```patch
              {"op": "replace_text", "path": "/body/paragraph[0]", "find": "old", "replace": "new"}
              ```

            ## Path Syntax

            - By index: `/body/paragraph[0]`, `/body/heading[1]`
            - By ID: `/body/paragraph[id='ABC123']` (preferred for existing elements)
            - By text: `/body/paragraph[text~='contains this']`
            - Wildcards: `/body/paragraph[*]` (all paragraphs)

            ## Element Types

            - `paragraph`: Regular text
            - `heading`: With `level` (1-6)
            - `table`: With `headers` and `rows`
            - `list`: With `items` and optional `ordered`

            ## Guidelines

            1. Prefer `replace_text` for small text changes (preserves formatting)
            2. Use element IDs when available (more stable than indices)
            3. Make minimal changes - don't rewrite unchanged content
            4. Output patches incrementally as you determine each change
            5. Explain your reasoning briefly before each patch
            """;
    }

    private static string BuildUserPrompt(LlmEditRequest request)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## Current Document");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(request.Document.Text);
        sb.AppendLine("```");

        if (request.Document.Elements?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Document Structure");
            sb.AppendLine();
            foreach (var elem in request.Document.Elements.Take(50))
            {
                var text = elem.Text.Length > 100
                    ? elem.Text[..100] + "..."
                    : elem.Text;
                sb.AppendLine($"- [{elem.Index}] {elem.Type} (id={elem.Id}): \"{text}\"");
            }
        }

        if (request.Document.Selection is { } sel && !string.IsNullOrEmpty(sel.Text))
        {
            sb.AppendLine();
            sb.AppendLine("## Current Selection");
            sb.AppendLine($"Text: \"{sel.Text}\"");
            if (sel.ContainingElementId is not null)
            {
                sb.AppendLine($"In element: {sel.ContainingElementId}");
            }
        }

        if (request.RecentChanges?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## User's Recent Changes");
            foreach (var change in request.RecentChanges.Take(10))
            {
                sb.AppendLine($"- {change.Description}");
            }
        }

        if (request.FocusPath is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"## Focus Area: {request.FocusPath}");
        }

        sb.AppendLine();
        sb.AppendLine("## Instruction");
        sb.AppendLine();
        sb.AppendLine(request.Instruction);

        return sb.ToString();
    }

    /// <summary>
    /// Try to extract a complete patch JSON from the buffer.
    /// </summary>
    private static bool TryExtractPatch(ref string buffer, out string patchJson)
    {
        patchJson = "";

        // Look for ```patch ... ``` blocks
        const string startMarker = "```patch";
        const string endMarker = "```";

        var startIdx = buffer.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIdx < 0) return false;

        var contentStart = startIdx + startMarker.Length;
        var endIdx = buffer.IndexOf(endMarker, contentStart, StringComparison.Ordinal);
        if (endIdx < 0) return false;

        patchJson = buffer[contentStart..endIdx].Trim();
        buffer = buffer[(endIdx + endMarker.Length)..];

        return !string.IsNullOrWhiteSpace(patchJson);
    }

    /// <summary>
    /// Try to parse a patch JSON string into an LlmPatch.
    /// </summary>
    private static bool TryParsePatch(string json, out LlmPatch patch)
    {
        patch = null!;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var op = root.GetProperty("op").GetString();
            var path = root.GetProperty("path").GetString();

            if (op is null || path is null) return false;

            patch = new LlmPatch
            {
                Op = op,
                Path = path,
                Value = root.TryGetProperty("value", out var v) ? v.Clone() : null,
                From = root.TryGetProperty("from", out var f) ? f.GetString() : null
            };

            // Handle replace_text special properties
            if (op == "replace_text")
            {
                var find = root.TryGetProperty("find", out var findEl) ? findEl.GetString() : null;
                var replace = root.TryGetProperty("replace", out var replaceEl) ? replaceEl.GetString() : null;

                patch = patch with
                {
                    Value = new { find, replace }
                };
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
