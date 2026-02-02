using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using DocxMcp.Persistence;
using DocxMcp.Ui;
using DocxMcp.Ui.Models;
using DocxMcp.Ui.Services;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Add(UiJsonContext.Default));

var sessionsDir = Environment.GetEnvironmentVariable("DOCX_MCP_SESSIONS_DIR")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".docx-mcp", "sessions");

builder.Services.AddSingleton(sp =>
    new SessionStore(sp.GetRequiredService<ILogger<SessionStore>>(), sessionsDir));
builder.Services.AddSingleton<SessionBrowserService>();
builder.Services.AddSingleton(sp =>
    new EventBroadcaster(sessionsDir, sp.GetRequiredService<ILogger<EventBroadcaster>>()));

var port = builder.Configuration.GetValue("Port", 5200);
builder.WebHost.UseUrls($"http://localhost:{port}");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// --- REST Endpoints ---

app.MapGet("/api/sessions", (SessionBrowserService svc) =>
    Results.Ok(svc.ListSessions()));

app.MapGet("/api/sessions/{id}", (string id, SessionBrowserService svc) =>
{
    var detail = svc.GetSessionDetail(id);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

app.MapGet("/api/sessions/{id}/docx", (string id, int? position, SessionBrowserService svc) =>
{
    try
    {
        var pos = position ?? svc.GetCurrentPosition(id);
        var bytes = svc.GetDocxBytesAtPosition(id, pos);
        return Results.File(bytes,
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            $"{id}-pos{pos}.docx");
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/api/sessions/{id}/history",
    (string id, int? offset, int? limit, SessionBrowserService svc) =>
{
    try
    {
        return Results.Ok(svc.GetHistory(id, offset ?? 0, limit ?? 50));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

// --- SSE Endpoint ---

app.MapGet("/api/events", async (HttpContext ctx, EventBroadcaster broadcaster) =>
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";

    var channel = Channel.CreateUnbounded<SessionEvent>();
    broadcaster.Subscribe(channel.Writer);
    try
    {
        await foreach (var evt in channel.Reader.ReadAllAsync(ctx.RequestAborted))
        {
            var json = JsonSerializer.Serialize(evt, UiJsonContext.Default.SessionEvent);
            await ctx.Response.WriteAsync($"event: {evt.Type}\ndata: {json}\n\n");
            await ctx.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    finally
    {
        broadcaster.Unsubscribe(channel.Writer);
    }
});

// Start watching for session changes
app.Services.GetRequiredService<EventBroadcaster>().Start();

// Auto-open browser
_ = Task.Run(async () =>
{
    await Task.Delay(800);
    try { Process.Start(new ProcessStartInfo($"http://localhost:{port}") { UseShellExecute = true }); }
    catch { /* headless environment */ }
});

Console.Error.WriteLine($"docx-ui listening on http://localhost:{port}");
Console.Error.WriteLine($"Sessions directory: {sessionsDir}");

app.Run();
