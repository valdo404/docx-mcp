using System.Text.Json;
using DocxMcp.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol.Messages;
using ModelContextProtocol.Protocol.Types;
using ModelContextProtocol.Server;

namespace DocxMcp.Transport;

/// <summary>
/// MCP server implementation that works over SSE transport.
/// Bridges the SSE connection with the MCP protocol handlers.
/// </summary>
public sealed class McpSseServer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<McpSseServer> _logger;
    private readonly SseTransport _transport;
    private readonly Dictionary<string, Func<JsonElement, UserContext, CancellationToken, Task<object?>>> _methodHandlers = new();

    public McpSseServer(
        IServiceProvider serviceProvider,
        SseTransport transport,
        ILogger<McpSseServer> logger)
    {
        _serviceProvider = serviceProvider;
        _transport = transport;
        _logger = logger;

        RegisterHandlers();
    }

    private void RegisterHandlers()
    {
        _methodHandlers["initialize"] = HandleInitializeAsync;
        _methodHandlers["initialized"] = HandleInitializedAsync;
        _methodHandlers["tools/list"] = HandleToolsListAsync;
        _methodHandlers["tools/call"] = HandleToolCallAsync;
        _methodHandlers["ping"] = HandlePingAsync;
    }

    /// <summary>
    /// Process an incoming JSON-RPC message from a client.
    /// </summary>
    public async Task<JsonDocument?> ProcessMessageAsync(
        SseConnection connection,
        JsonDocument message,
        CancellationToken ct)
    {
        try
        {
            var root = message.RootElement;

            // Check if it's a request or notification
            var hasId = root.TryGetProperty("id", out var idElement);
            var method = root.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString()
                : null;

            if (string.IsNullOrEmpty(method))
            {
                _logger.LogWarning("Received message without method");
                return CreateErrorResponse(idElement, -32600, "Invalid Request: missing method");
            }

            _logger.LogDebug("Processing MCP method: {Method} for connection {ConnectionId}", method, connection.ConnectionId);

            // Get params
            var paramsElement = root.TryGetProperty("params", out var p) ? p : default;

            // Find handler
            if (!_methodHandlers.TryGetValue(method, out var handler))
            {
                _logger.LogWarning("Unknown method: {Method}", method);
                return CreateErrorResponse(idElement, -32601, $"Method not found: {method}");
            }

            // Execute handler
            var result = await handler(paramsElement, connection.User, ct);

            // If it's a notification (no id), don't send response
            if (!hasId)
                return null;

            // Build response
            return CreateSuccessResponse(idElement, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MCP message");
            return CreateErrorResponse(default, -32603, $"Internal error: {ex.Message}");
        }
    }

    private Task<object?> HandleInitializeAsync(JsonElement paramsEl, UserContext user, CancellationToken ct)
    {
        var result = new InitializeResult
        {
            ProtocolVersion = "2024-11-05",
            ServerInfo = new Implementation
            {
                Name = "docx-mcp",
                Version = "2.1.0"
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability
                {
                    ListChanged = false
                }
            }
        };

        return Task.FromResult<object?>(result);
    }

    private Task<object?> HandleInitializedAsync(JsonElement paramsEl, UserContext user, CancellationToken ct)
    {
        // Notification - no response needed
        return Task.FromResult<object?>(null);
    }

    private async Task<object?> HandleToolsListAsync(JsonElement paramsEl, UserContext user, CancellationToken ct)
    {
        // Get tools from the registered tool types
        var tools = GetRegisteredTools();

        return await Task.FromResult<object?>(new { tools });
    }

    private async Task<object?> HandleToolCallAsync(JsonElement paramsEl, UserContext user, CancellationToken ct)
    {
        var toolName = paramsEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        var arguments = paramsEl.TryGetProperty("arguments", out var argsEl) ? argsEl : default;

        if (string.IsNullOrEmpty(toolName))
        {
            throw new InvalidOperationException("Tool name is required");
        }

        _logger.LogInformation("Calling tool: {ToolName} for user {UserId}", toolName, user.UserId);

        // Execute the tool using the service provider
        var result = await ExecuteToolAsync(toolName, arguments, user, ct);

        return new
        {
            content = new[]
            {
                new { type = "text", text = result }
            }
        };
    }

    private Task<object?> HandlePingAsync(JsonElement paramsEl, UserContext user, CancellationToken ct)
    {
        return Task.FromResult<object?>(new { });
    }

    private async Task<string> ExecuteToolAsync(string toolName, JsonElement arguments, UserContext user, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var sessionManager = scope.ServiceProvider.GetRequiredService<SessionManager>();

        // Map tool names to handlers
        // This is a simplified version - in production, use reflection or a registry
        return toolName switch
        {
            "document_open" => ExecuteDocumentOpen(sessionManager, arguments, user),
            "document_create" => ExecuteDocumentCreate(sessionManager),
            "document_save" => ExecuteDocumentSave(sessionManager, arguments),
            "document_close" => ExecuteDocumentClose(sessionManager, arguments),
            "document_list" => ExecuteDocumentList(sessionManager),
            "query" => await ExecuteQueryAsync(sessionManager, arguments, ct),
            "count_elements" => ExecuteCountElements(sessionManager, arguments),
            "apply_patch" => ExecuteApplyPatch(sessionManager, arguments),
            "document_undo" => ExecuteUndo(sessionManager, arguments),
            "document_redo" => ExecuteRedo(sessionManager, arguments),
            "document_history" => ExecuteHistory(sessionManager, arguments),
            "document_jump_to" => ExecuteJumpTo(sessionManager, arguments),
            _ => throw new InvalidOperationException($"Unknown tool: {toolName}")
        };
    }

    private string ExecuteDocumentOpen(SessionManager sessionManager, JsonElement args, UserContext user)
    {
        var path = args.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;

        if (string.IsNullOrEmpty(path))
        {
            var session = sessionManager.Create();
            return JsonSerializer.Serialize(new { session_id = session.Id, message = "Created new empty document" });
        }

        // For cloud storage, we'll need to fetch the document first
        // This is a placeholder - actual implementation will use IDocumentProvider
        var openedSession = sessionManager.Open(path);
        return JsonSerializer.Serialize(new { session_id = openedSession.Id, source_path = path });
    }

    private string ExecuteDocumentCreate(SessionManager sessionManager)
    {
        var session = sessionManager.Create();
        return JsonSerializer.Serialize(new { session_id = session.Id, message = "Created new empty document" });
    }

    private string ExecuteDocumentSave(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var path = args.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
        sessionManager.Save(sessionId, path);
        return JsonSerializer.Serialize(new { success = true, message = $"Document saved{(path != null ? $" to {path}" : "")}" });
    }

    private string ExecuteDocumentClose(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        sessionManager.Close(sessionId);
        return JsonSerializer.Serialize(new { success = true, message = "Document closed" });
    }

    private string ExecuteDocumentList(SessionManager sessionManager)
    {
        var sessions = sessionManager.List();
        return JsonSerializer.Serialize(new
        {
            sessions = sessions.Select(s => new { id = s.Id, path = s.Path })
        });
    }

    private Task<string> ExecuteQueryAsync(SessionManager sessionManager, JsonElement args, CancellationToken ct)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var path = args.GetProperty("path").GetString()!;
        var format = args.TryGetProperty("format", out var fEl) ? fEl.GetString() : "json";
        var offset = args.TryGetProperty("offset", out var oEl) ? oEl.GetInt32() : (int?)null;
        var limit = args.TryGetProperty("limit", out var lEl) ? lEl.GetInt32() : (int?)null;

        // Call the existing tool method directly
        var result = Tools.QueryTool.Query(sessionManager, sessionId, path, format, offset, limit);
        return Task.FromResult(result);
    }

    private string ExecuteCountElements(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var path = args.GetProperty("path").GetString()!;

        // Call the existing tool method directly
        return Tools.CountTool.CountElements(sessionManager, sessionId, path);
    }

    private string ExecuteApplyPatch(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var patches = args.GetProperty("patches").GetRawText();

        // Call the existing tool method directly
        return Tools.PatchTool.ApplyPatch(sessionManager, sessionId, patches);
    }

    private string ExecuteUndo(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var steps = args.TryGetProperty("steps", out var stepsEl) ? stepsEl.GetInt32() : 1;

        var result = sessionManager.Undo(sessionId, steps);
        return JsonSerializer.Serialize(result);
    }

    private string ExecuteRedo(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var steps = args.TryGetProperty("steps", out var stepsEl) ? stepsEl.GetInt32() : 1;

        var result = sessionManager.Redo(sessionId, steps);
        return JsonSerializer.Serialize(result);
    }

    private string ExecuteHistory(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var offset = args.TryGetProperty("offset", out var oEl) ? oEl.GetInt32() : 0;
        var limit = args.TryGetProperty("limit", out var lEl) ? lEl.GetInt32() : 20;

        var result = sessionManager.GetHistory(sessionId, offset, limit);
        return JsonSerializer.Serialize(result);
    }

    private string ExecuteJumpTo(SessionManager sessionManager, JsonElement args)
    {
        var sessionId = args.GetProperty("session_id").GetString()!;
        var position = args.GetProperty("position").GetInt32();

        var result = sessionManager.JumpTo(sessionId, position);
        return JsonSerializer.Serialize(result);
    }

    private static List<object> GetRegisteredTools()
    {
        // Return tool definitions - this should ideally be built from reflection
        return new List<object>
        {
            new { name = "document_open", description = "Open a DOCX document or create a new one", inputSchema = new { type = "object", properties = new { path = new { type = "string", description = "Path to DOCX file (optional)" } } } },
            new { name = "document_create", description = "Create a new empty DOCX document", inputSchema = new { type = "object", properties = new { } } },
            new { name = "document_save", description = "Save a document", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, path = new { type = "string" } }, required = new[] { "session_id" } } },
            new { name = "document_close", description = "Close a document session", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" } }, required = new[] { "session_id" } } },
            new { name = "document_list", description = "List all open document sessions", inputSchema = new { type = "object", properties = new { } } },
            new { name = "query", description = "Query document elements using typed paths", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, path = new { type = "string" }, offset = new { type = "integer" }, limit = new { type = "integer" } }, required = new[] { "session_id", "path" } } },
            new { name = "count_elements", description = "Count elements matching a path", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, path = new { type = "string" } }, required = new[] { "session_id", "path" } } },
            new { name = "apply_patch", description = "Apply JSON patches to the document", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, patches = new { type = "array" } }, required = new[] { "session_id", "patches" } } },
            new { name = "document_undo", description = "Undo recent changes", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, steps = new { type = "integer" } }, required = new[] { "session_id" } } },
            new { name = "document_redo", description = "Redo undone changes", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, steps = new { type = "integer" } }, required = new[] { "session_id" } } },
            new { name = "document_history", description = "Get edit history", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, offset = new { type = "integer" }, limit = new { type = "integer" } }, required = new[] { "session_id" } } },
            new { name = "document_jump_to", description = "Jump to a specific history position", inputSchema = new { type = "object", properties = new { session_id = new { type = "string" }, position = new { type = "integer" } }, required = new[] { "session_id", "position" } } }
        };
    }

    private static JsonDocument CreateSuccessResponse(JsonElement id, object? result)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id.ValueKind != JsonValueKind.Undefined ? id.Clone() : null,
            result
        };
        var json = JsonSerializer.Serialize(response);
        return JsonDocument.Parse(json);
    }

    private static JsonDocument CreateErrorResponse(JsonElement id, int code, string message)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id.ValueKind != JsonValueKind.Undefined ? (object?)id.Clone() : null,
            error = new { code, message }
        };
        var json = JsonSerializer.Serialize(response);
        return JsonDocument.Parse(json);
    }
}
