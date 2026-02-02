using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DocxMcp;
using DocxMcp.Persistence;
using DocxMcp.Tools;

var builder = Host.CreateApplicationBuilder(args);

// MCP requirement: all logging goes to stderr
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register persistence and session management
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<SessionManager>();
builder.Services.AddHostedService<SessionRestoreService>();

// Register MCP server with stdio transport and explicit tool types (AOT-safe)
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "docx-mcp",
            Version = "2.1.0"
        };
    })
    .WithStdioServerTransport()
    .WithTools<DocumentTools>()
    .WithTools<QueryTool>()
    .WithTools<CountTool>()
    .WithTools<ReadSectionTool>()
    .WithTools<ReadHeadingContentTool>()
    .WithTools<PatchTool>()
    .WithTools<ExportTools>()
    .WithTools<HistoryTools>()
    .WithTools<CommentTools>()
    .WithTools<StyleTools>();

await builder.Build().RunAsync();
