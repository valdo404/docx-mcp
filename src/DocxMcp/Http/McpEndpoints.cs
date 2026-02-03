using System.Security.Claims;
using System.Text.Json;
using DocxMcp.Auth;
using DocxMcp.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocxMcp.Http;

/// <summary>
/// HTTP endpoint definitions for the MCP SSE server.
/// </summary>
public static class McpEndpoints
{
    /// <summary>
    /// Map all MCP-related HTTP endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Health check
        endpoints.MapGet("/health", HealthCheck)
            .AllowAnonymous()
            .WithName("Health")
            .WithTags("System");

        // OAuth endpoints
        endpoints.MapGet("/auth/login", AuthLogin)
            .AllowAnonymous()
            .WithName("AuthLogin")
            .WithTags("Auth");

        endpoints.MapGet("/auth/callback", AuthCallback)
            .AllowAnonymous()
            .WithName("AuthCallback")
            .WithTags("Auth");

        endpoints.MapGet("/auth/me", AuthMe)
            .RequireAuthorization()
            .WithName("AuthMe")
            .WithTags("Auth");

        // SSE endpoint for MCP
        endpoints.MapGet("/sse", SseConnect)
            .RequireAuthorization()
            .WithName("SseConnect")
            .WithTags("MCP");

        // Message endpoint for client→server communication
        endpoints.MapPost("/message", PostMessage)
            .RequireAuthorization()
            .WithName("PostMessage")
            .WithTags("MCP");

        return endpoints;
    }

    /// <summary>
    /// Health check endpoint for Koyeb and load balancers.
    /// </summary>
    private static IResult HealthCheck(IServiceProvider services)
    {
        var sessionManager = services.GetRequiredService<SessionManager>();
        var activeSessions = sessionManager.List().Count;

        return Results.Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            version = "2.1.0",
            transport = "sse",
            activeSessions
        });
    }

    /// <summary>
    /// Initiate OAuth login flow.
    /// </summary>
    private static IResult AuthLogin(
        HttpContext context,
        TokenService tokenService)
    {
        // Generate state for CSRF protection
        var state = Guid.NewGuid().ToString("N");
        context.Response.Cookies.Append("oauth_state", state, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromMinutes(10)
        });

        var authUrl = tokenService.GetAuthorizationUrl(state);
        return Results.Redirect(authUrl);
    }

    /// <summary>
    /// OAuth callback handler.
    /// </summary>
    private static async Task<IResult> AuthCallback(
        HttpContext context,
        TokenService tokenService,
        ILogger<TokenService> logger)
    {
        var code = context.Request.Query["code"].FirstOrDefault();
        var state = context.Request.Query["state"].FirstOrDefault();
        var error = context.Request.Query["error"].FirstOrDefault();

        if (!string.IsNullOrEmpty(error))
        {
            var errorDescription = context.Request.Query["error_description"].FirstOrDefault();
            logger.LogError("OAuth error: {Error} - {Description}", error, errorDescription);
            return Results.BadRequest(new { error, description = errorDescription });
        }

        // Verify state
        var storedState = context.Request.Cookies["oauth_state"];
        if (string.IsNullOrEmpty(state) || state != storedState)
        {
            return Results.BadRequest(new { error = "invalid_state", description = "State mismatch - possible CSRF attack" });
        }

        if (string.IsNullOrEmpty(code))
        {
            return Results.BadRequest(new { error = "missing_code", description = "Authorization code not provided" });
        }

        // Exchange code for tokens
        var tokenInfo = await tokenService.ExchangeCodeAsync(code);
        if (tokenInfo is null)
        {
            return Results.BadRequest(new { error = "token_exchange_failed", description = "Failed to exchange authorization code" });
        }

        // Clear state cookie
        context.Response.Cookies.Delete("oauth_state");

        // Return token to client (in production, use secure cookies or return to SPA)
        return Results.Ok(new
        {
            access_token = tokenInfo.AccessToken,
            expires_at = tokenInfo.ExpiresAt,
            message = "Authentication successful. Use this token in the Authorization header for SSE connections."
        });
    }

    /// <summary>
    /// Get current user info.
    /// </summary>
    private static IResult AuthMe(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(new
        {
            userId = user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
            email = user.FindFirstValue("preferred_username") ?? user.FindFirstValue(ClaimTypes.Email),
            name = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name),
            tenantId = user.FindFirstValue("tid")
        });
    }

    /// <summary>
    /// Establish SSE connection for MCP protocol.
    /// </summary>
    private static async Task SseConnect(
        HttpContext context,
        SseTransport transport,
        McpSseServer mcpServer,
        ILogger<SseTransport> logger)
    {
        // Get user context
        var accessToken = context.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "") ?? "";
        var userContext = UserContext.FromClaimsPrincipal(context.User, accessToken);

        if (!userContext.IsAuthenticated)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }

        // Create connection
        var connectionId = Guid.NewGuid().ToString("N")[..12];
        var connection = transport.CreateConnection(connectionId, userContext);

        // Set SSE headers
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering

        logger.LogInformation("SSE connection established: {ConnectionId} for user {UserId}",
            connectionId, userContext.UserId);

        try
        {
            // Start processing inbound messages in background
            _ = ProcessInboundMessagesAsync(connection, mcpServer, context.RequestAborted);

            // Stream SSE messages to client
            await connection.StreamToResponseAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("SSE connection closed: {ConnectionId}", connectionId);
        }
        finally
        {
            await transport.RemoveConnectionAsync(connectionId);
        }
    }

    /// <summary>
    /// Process inbound messages for a connection.
    /// </summary>
    private static async Task ProcessInboundMessagesAsync(
        SseConnection connection,
        McpSseServer mcpServer,
        CancellationToken ct)
    {
        await foreach (var message in connection.ReadInboundAsync(ct))
        {
            var response = await mcpServer.ProcessMessageAsync(connection, message, ct);
            if (response is not null)
            {
                await connection.SendJsonRpcAsync(response, ct);
            }
        }
    }

    /// <summary>
    /// Receive client→server message (paired with SSE).
    /// </summary>
    private static async Task<IResult> PostMessage(
        HttpContext context,
        SseTransport transport,
        ILogger<SseTransport> logger)
    {
        // Get connection ID from header or query
        var connectionId = context.Request.Headers["X-Connection-Id"].FirstOrDefault()
            ?? context.Request.Query["connectionId"].FirstOrDefault();

        if (string.IsNullOrEmpty(connectionId))
        {
            return Results.BadRequest(new { error = "missing_connection_id" });
        }

        var connection = transport.GetConnection(connectionId);
        if (connection is null)
        {
            return Results.NotFound(new { error = "connection_not_found" });
        }

        // Verify user owns this connection
        var userId = context.User.FindFirstValue("oid") ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (connection.User.UserId != userId)
        {
            return Results.Forbid();
        }

        // Read message body
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);

        // Queue message for processing
        await connection.ReceiveAsync(document, context.RequestAborted);

        return Results.Accepted();
    }
}
