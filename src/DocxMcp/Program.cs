using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DocxMcp;
using DocxMcp.ExternalChanges;
using DocxMcp.Grpc;
using DocxMcp.Tools;

var transport = Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio";

if (transport == "http")
{
    // ─── HTTP mode: local dev / behind proxy (Koyeb) ───
    var builder = WebApplication.CreateBuilder(args);

    builder.Logging.AddConsole();
    builder.Logging.SetMinimumLevel(LogLevel.Debug);

    RegisterStorageServices(builder.Services);

    // Multi-tenant: pool of SessionManagers, one per tenant
    builder.Services.AddSingleton<SessionManagerPool>();
    builder.Services.AddSingleton<SyncManager>();
    builder.Services.AddSingleton<ExternalChangeGate>();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<TenantScope>();

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
        .WithTools<ExternalChangeTools>()
        .WithTools<ConnectionTools>();

    var app = builder.Build();
    app.MapMcp("/mcp");
    app.Use(async (context, next) =>
    {
        if (context.Request.Path == "/health" && context.Request.Method == "GET")
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"healthy":true,"version":"1.7.0"}""");
            return;
        }
        await next();
    });
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
    builder.Services.AddSingleton<ExternalChangeGate>();
    builder.Services.AddSingleton<SessionManager>();
    builder.Services.AddScoped<TenantScope>();
    builder.Services.AddHostedService<SessionRestoreService>();

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
        .WithTools<ExternalChangeTools>()
        .WithTools<ConnectionTools>();

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

    // ── IHistoryStorage ──
    if (!string.IsNullOrEmpty(storageOptions.ServerUrl))
    {
        // Remote history storage (Cloudflare R2, etc.)
        services.AddSingleton<IHistoryStorage>(sp =>
        {
            var logger = sp.GetService<ILogger<HistoryStorageClient>>();
            var launcherLogger = sp.GetService<ILogger<GrpcLauncher>>();
            var launcher = new GrpcLauncher(storageOptions, launcherLogger);
            return HistoryStorageClient.CreateAsync(storageOptions, launcher, logger).GetAwaiter().GetResult();
        });
    }
    else
    {
        // Local embedded history storage
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

        // If no remote sync either, use same embedded channel
        if (string.IsNullOrEmpty(storageOptions.SyncServerUrl))
        {
            services.AddSingleton<ISyncStorage>(sp =>
                new SyncStorageClient(channel, sp.GetService<ILogger<SyncStorageClient>>()));
        }
    }

    // ── ISyncStorage ──
    if (!string.IsNullOrEmpty(storageOptions.SyncServerUrl))
    {
        // Remote sync/watch (Google Drive, etc.)
        services.AddSingleton<ISyncStorage>(sp =>
        {
            var logger = sp.GetService<ILogger<SyncStorageClient>>();
            var syncChannel = Grpc.Net.Client.GrpcChannel.ForAddress(storageOptions.SyncServerUrl);
            return new SyncStorageClient(syncChannel, logger);
        });
    }
    else if (!string.IsNullOrEmpty(storageOptions.ServerUrl))
    {
        // Remote history but local embedded sync/watch
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
    // else: already registered above in the embedded block
}
