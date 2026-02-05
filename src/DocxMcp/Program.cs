using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DocxMcp;
using DocxMcp.Grpc;
using DocxMcp.Tools;
using DocxMcp.ExternalChanges;

var builder = Host.CreateApplicationBuilder(args);

// MCP requirement: all logging goes to stderr
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register gRPC storage client and session management
builder.Services.AddSingleton<IStorageClient>(sp =>
{
    var logger = sp.GetService<ILogger<StorageClient>>();
    var options = new StorageClientOptions();
    var launcherLogger = sp.GetService<ILogger<GrpcLauncher>>();
    var launcher = new GrpcLauncher(options, launcherLogger);
    return StorageClient.CreateAsync(options, launcher, logger).GetAwaiter().GetResult();
});
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
            Version = "1.6.0"
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
