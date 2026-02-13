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

// Register gRPC storage clients and session management
var storageOptions = StorageClientOptions.FromEnvironment();

if (!string.IsNullOrEmpty(storageOptions.ServerUrl))
{
    // Dual mode — remote for history, local embedded for sync/watch
    builder.Services.AddSingleton<IHistoryStorage>(sp =>
    {
        var logger = sp.GetService<ILogger<HistoryStorageClient>>();
        var launcherLogger = sp.GetService<ILogger<GrpcLauncher>>();
        var launcher = new GrpcLauncher(storageOptions, launcherLogger);
        return HistoryStorageClient.CreateAsync(storageOptions, launcher, logger).GetAwaiter().GetResult();
    });

    // Local embedded for sync/watch
    NativeStorage.Init(storageOptions.GetEffectiveLocalStorageDir());
    builder.Services.AddSingleton<ISyncStorage>(sp =>
    {
        var logger = sp.GetService<ILogger<SyncStorageClient>>();
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = (_, _) =>
                new ValueTask<Stream>(new InMemoryPipeStream())
        };
        var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://in-memory", new Grpc.Net.Client.GrpcChannelOptions
        {
            HttpHandler = handler
        });
        return new SyncStorageClient(channel, logger);
    });
}
else
{
    // Embedded mode — single in-memory channel for both history and sync
    NativeStorage.Init(storageOptions.GetEffectiveLocalStorageDir());

    var handler = new System.Net.Http.SocketsHttpHandler
    {
        ConnectCallback = (_, _) =>
            new ValueTask<Stream>(new InMemoryPipeStream())
    };

    var channel = Grpc.Net.Client.GrpcChannel.ForAddress("http://in-memory", new Grpc.Net.Client.GrpcChannelOptions
    {
        HttpHandler = handler
    });

    builder.Services.AddSingleton<IHistoryStorage>(sp =>
        new HistoryStorageClient(channel, sp.GetService<ILogger<HistoryStorageClient>>()));
    builder.Services.AddSingleton<ISyncStorage>(sp =>
        new SyncStorageClient(channel, sp.GetService<ILogger<SyncStorageClient>>()));
}

builder.Services.AddSingleton<SyncManager>();
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
