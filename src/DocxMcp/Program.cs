using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DocxMcp;
using DocxMcp.Grpc;
using DocxMcp.Tools;
using DocxMcp.ExternalChanges;

var transport = Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio";

if (transport == "http")
{
    // ─── HTTP mode: local dev / behind proxy (Koyeb) ───
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.AddConsole();

    RegisterStorageServices(builder.Services);

    // Multi-tenant: pool of SessionManagers, one per tenant
    builder.Services.AddSingleton<SessionManagerPool>();
    builder.Services.AddSingleton<SyncManager>();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<TenantScope>();

    // No ExternalChangeTracker in HTTP mode (no local files to watch)
    // No SessionRestoreService (tenants are lazy-created on first request)

    builder.Services
        .AddMcpServer(ConfigureMcpServer)
        .WithHttpTransport()
        .WithTools<DocumentTools>()
        .WithTools<QueryTool>()
        .WithTools<CountTool>()
        .WithTools<ReadSectionTool>()
        .WithTools<ReadHeadingContentTool>()
        .WithTools<ElementTools>()
        .WithTools<TextTools>()
        .WithTools<TableTools>()
        .WithTools<ExportTools>()
        .WithTools<HistoryTools>()
        .WithTools<CommentTools>()
        .WithTools<StyleTools>()
        .WithTools<RevisionTools>()
        .WithTools<ExternalChangeTools>();

    var app = builder.Build();
    app.MapMcp();
    await app.RunAsync();
}
else
{
    // ─── Stdio mode: Claude Code local, single tenant (unchanged behavior) ───
    var builder = Host.CreateApplicationBuilder(args);

    // MCP requirement: all logging goes to stderr
    builder.Logging.AddConsole(options =>
    {
        options.LogToStandardErrorThreshold = LogLevel.Trace;
    });

    RegisterStorageServices(builder.Services);

    builder.Services.AddSingleton<SyncManager>();
    builder.Services.AddSingleton<SessionManager>();
    builder.Services.AddScoped<TenantScope>();
    builder.Services.AddHostedService<SessionRestoreService>();

    // External change tracking (local files)
    builder.Services.AddSingleton<ExternalChangeTracker>();
    builder.Services.AddHostedService<ExternalChangeNotificationService>();

    builder.Services
        .AddMcpServer(ConfigureMcpServer)
        .WithStdioServerTransport()
        .WithTools<DocumentTools>()
        .WithTools<QueryTool>()
        .WithTools<CountTool>()
        .WithTools<ReadSectionTool>()
        .WithTools<ReadHeadingContentTool>()
        .WithTools<ElementTools>()
        .WithTools<TextTools>()
        .WithTools<TableTools>()
        .WithTools<ExportTools>()
        .WithTools<HistoryTools>()
        .WithTools<CommentTools>()
        .WithTools<StyleTools>()
        .WithTools<RevisionTools>()
        .WithTools<ExternalChangeTools>();

    await builder.Build().RunAsync();
}

// ─── Shared helpers ───

static void ConfigureMcpServer(McpServerOptions options)
{
    options.ServerInfo = new()
    {
        Name = "docx-mcp",
        Version = "1.7.0"
    };
}

static void RegisterStorageServices(IServiceCollection services)
{
    var storageOptions = StorageClientOptions.FromEnvironment();

    if (!string.IsNullOrEmpty(storageOptions.ServerUrl))
    {
        // Dual mode — remote for history, local embedded for sync/watch
        services.AddSingleton<IHistoryStorage>(sp =>
        {
            var logger = sp.GetService<ILogger<HistoryStorageClient>>();
            var launcherLogger = sp.GetService<ILogger<GrpcLauncher>>();
            var launcher = new GrpcLauncher(storageOptions, launcherLogger);
            return HistoryStorageClient.CreateAsync(storageOptions, launcher, logger).GetAwaiter().GetResult();
        });

        // Local embedded for sync/watch
        NativeStorage.Init(storageOptions.GetEffectiveLocalStorageDir());
        services.AddSingleton<ISyncStorage>(sp =>
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

        services.AddSingleton<IHistoryStorage>(sp =>
            new HistoryStorageClient(channel, sp.GetService<ILogger<HistoryStorageClient>>()));
        services.AddSingleton<ISyncStorage>(sp =>
            new SyncStorageClient(channel, sp.GetService<ILogger<SyncStorageClient>>()));
    }
}
