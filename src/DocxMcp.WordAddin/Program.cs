using System.Text.Json;
using DocxMcp.WordAddin.Models;
using DocxMcp.WordAddin.Services;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure JSON serialization for AOT
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(WordAddinJsonContext.Default));

// Register services
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<UserChangeService>();

// Configure CORS for Office.js add-in
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyOrigin() // Office.js runs from various origins
            .AllowAnyMethod()
            .AllowAnyHeader()
            .WithExposedHeaders("Content-Type");
    });
});

var port = builder.Configuration.GetValue("Port", 5300);
builder.WebHost.UseUrls($"http://localhost:{port}");

var app = builder.Build();

app.UseCors();

// --- Health Check ---
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "doccy" }));

// --- LLM Streaming Endpoint (SSE) ---
app.MapPost("/api/llm/stream", async (
    HttpContext ctx,
    ClaudeService claude,
    UserChangeService userChanges) =>
{
    // Parse request body
    LlmEditRequest request;
    try
    {
        request = await ctx.Request.ReadFromJsonAsync(WordAddinJsonContext.Default.LlmEditRequest)
            ?? throw new ArgumentException("Invalid request body");
    }
    catch (JsonException ex)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(
            new { error = $"Invalid JSON: {ex.Message}" },
            WordAddinJsonContext.Default.Options);
        return;
    }

    // Add recent user changes for context
    request = request with
    {
        RecentChanges = userChanges.GetRecentChanges(request.SessionId)
    };

    // Set up SSE response
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    try
    {
        await foreach (var evt in claude.StreamPatchesAsync(request, ctx.RequestAborted))
        {
            var json = JsonSerializer.Serialize(evt, WordAddinJsonContext.Default.LlmStreamEvent);
            await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {json}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected - this is normal
    }
    catch (Exception ex)
    {
        var errorEvt = new LlmStreamEvent
        {
            Type = "error",
            Error = ex.Message
        };
        var json = JsonSerializer.Serialize(errorEvt, WordAddinJsonContext.Default.LlmStreamEvent);
        await ctx.Response.WriteAsync($"event: error\ndata: {json}\n\n");
    }
});

// --- User Change Tracking ---
app.MapPost("/api/changes/report", async (
    HttpContext ctx,
    UserChangeService userChanges) =>
{
    UserChangeReport report;
    try
    {
        report = await ctx.Request.ReadFromJsonAsync(WordAddinJsonContext.Default.UserChangeReport)
            ?? throw new ArgumentException("Invalid request body");
    }
    catch (JsonException ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }

    var result = userChanges.ProcessChanges(report);
    return Results.Ok(result);
});

// --- Get Recent Changes (for debugging/UI) ---
app.MapGet("/api/changes/{sessionId}", (string sessionId, UserChangeService userChanges) =>
{
    var changes = userChanges.GetRecentChanges(sessionId, 20);
    return Results.Ok(new { session_id = sessionId, changes });
});

// --- Clear Session Changes ---
app.MapDelete("/api/changes/{sessionId}", (string sessionId, UserChangeService userChanges) =>
{
    userChanges.ClearSession(sessionId);
    return Results.Ok(new { message = $"Cleared change history for session {sessionId}" });
});

Console.Error.WriteLine($"📎 Doccy backend listening on http://localhost:{port}");
Console.Error.WriteLine("Endpoints:");
Console.Error.WriteLine("  POST /api/llm/stream     - Stream LLM patches (SSE)");
Console.Error.WriteLine("  POST /api/changes/report - Report user changes");
Console.Error.WriteLine("  GET  /api/changes/:id    - Get recent changes");
Console.Error.WriteLine("  GET  /health             - Health check");

app.Run();
