using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DocxMcp;
using DocxMcp.Persistence;
using DocxMcp.Tools;
using DocxMcp.ExternalChanges;

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

// Register external change tracking
builder.Services.AddSingleton<ExternalChangeTracker>();
builder.Services.AddHostedService<ExternalChangeNotificationService>();

// Register MCP server with stdio transport and explicit tool types (AOT-safe)
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "docx-mcp",
            Version = "2.2.0"
        };
    })
    .WithStdioServerTransport()
    // Document management
    .WithTools<DocumentTools>()
    // Query tools
    .WithTools<QueryTool>()
    .WithTools<CountTool>()
    .WithTools<ReadSectionTool>()
    .WithTools<ReadHeadingContentTool>()
    // Element operations (individual tools with focused documentation)
    .WithTools<ElementTools>()
    .WithTools<TextTools>()
    .WithTools<TableTools>()
    // Export, history, comments, styles
    .WithTools<ExportTools>()
    .WithTools<HistoryTools>()
    .WithTools<CommentTools>()
    .WithTools<StyleTools>()
    .WithTools<RevisionTools>()
    .WithTools<ExternalChangeTools>();

await builder.Build().RunAsync();
