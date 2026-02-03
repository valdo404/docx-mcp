using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;
using DocxMcp;
using DocxMcp.Auth;
using DocxMcp.Http;
using DocxMcp.Persistence;
using DocxMcp.Storage;
using DocxMcp.Tools;
using DocxMcp.ExternalChanges;
using DocxMcp.Transport;

// Determine transport mode from environment
var transportMode = Environment.GetEnvironmentVariable("DOCX_MCP_TRANSPORT")?.ToLowerInvariant() ?? "stdio";

if (transportMode == "sse" || transportMode == "http")
{
    await RunHttpServerAsync(args);
}
else
{
    await RunStdioServerAsync(args);
}

/// <summary>
/// Run the MCP server in stdio mode (traditional MCP, for local clients).
/// </summary>
static async Task RunStdioServerAsync(string[] args)
{
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
}

/// <summary>
/// Run the MCP server in HTTP/SSE mode (for cloud deployment like Koyeb).
/// </summary>
static async Task RunHttpServerAsync(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);

    // Configuration
    builder.Services.Configure<OAuthOptions>(options =>
    {
        options.TenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "common";
        options.ClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? "";
        options.ClientSecret = Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET") ?? "";
        options.BaseUrl = Environment.GetEnvironmentVariable("DOCX_MCP_BASE_URL") ?? "http://localhost:8080";
        options.ValidateTokens = Environment.GetEnvironmentVariable("DOCX_MCP_VALIDATE_TOKENS") != "false";
    });

    builder.Services.Configure<StorageOptions>(options =>
    {
        options.Provider = Environment.GetEnvironmentVariable("DOCX_MCP_STORAGE_PROVIDER") ?? "local";
        options.LocalBasePath = Environment.GetEnvironmentVariable("DOCX_MCP_SESSIONS_DIR") ?? "";
        options.GcsBucket = Environment.GetEnvironmentVariable("GCS_BUCKET") ?? "";
        options.GcsPrefix = Environment.GetEnvironmentVariable("GCS_PREFIX") ?? "docx-mcp/sessions/";
        options.GcsCredentialsPath = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS");
    });

    // Logging
    builder.Logging.AddConsole();

    // Storage provider (based on configuration)
    var storageProvider = Environment.GetEnvironmentVariable("DOCX_MCP_STORAGE_PROVIDER")?.ToLowerInvariant() ?? "local";
    if (storageProvider == "gcs")
    {
        builder.Services.AddSingleton<IStorageProvider, GcsStorageProvider>();
        builder.Services.AddSingleton<CloudSessionStore>();
    }
    else
    {
        builder.Services.AddSingleton<IStorageProvider, LocalStorageProvider>();
        builder.Services.AddSingleton<CloudSessionStore>();
    }

    // Also register the original SessionStore for compatibility
    builder.Services.AddSingleton<SessionStore>();
    builder.Services.AddSingleton<SessionManager>();
    builder.Services.AddHostedService<SessionRestoreService>();

    // HTTP client for OAuth
    builder.Services.AddHttpClient<TokenService>();

    // SSE Transport
    builder.Services.AddSingleton<SseTransport>();
    builder.Services.AddSingleton<McpSseServer>();

    // Authentication
    var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") ?? "";
    var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID") ?? "common";

    if (!string.IsNullOrEmpty(clientId))
    {
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
                options.Audience = clientId;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidAudiences = new[] { clientId, $"api://{clientId}" }
                };

                // Allow token in query string for SSE connections
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/sse"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });
    }
    else
    {
        // Development mode - no auth required
        builder.Services.AddAuthentication("Development")
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, DevelopmentAuthHandler>(
                "Development", null);
    }

    builder.Services.AddAuthorization();

    // CORS for web clients
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            var allowedOrigins = Environment.GetEnvironmentVariable("DOCX_MCP_CORS_ORIGINS")?.Split(',')
                ?? new[] { "*" };

            if (allowedOrigins.Contains("*"))
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            }
            else
            {
                policy.WithOrigins(allowedOrigins)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .AllowCredentials();
            }
        });
    });

    var app = builder.Build();

    // Configure pipeline
    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();

    // Map MCP endpoints
    app.MapMcpEndpoints();

    // Get port from environment (Koyeb uses PORT)
    var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
    app.Urls.Add($"http://0.0.0.0:{port}");

    Console.WriteLine($"docx-mcp HTTP/SSE server starting on port {port}");
    Console.WriteLine($"Storage provider: {storageProvider}");
    Console.WriteLine($"OAuth: {(string.IsNullOrEmpty(clientId) ? "disabled (development mode)" : "enabled")}");

    await app.RunAsync();
}

/// <summary>
/// Development authentication handler - accepts all requests as authenticated.
/// </summary>
public class DevelopmentAuthHandler : Microsoft.AspNetCore.Authentication.AuthenticationHandler<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions>
{
    public DevelopmentAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<Microsoft.AspNetCore.Authentication.AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new System.Security.Claims.Claim("oid", "dev-user-001"),
            new System.Security.Claims.Claim("preferred_username", "dev@localhost"),
            new System.Security.Claims.Claim("name", "Development User"),
            new System.Security.Claims.Claim("tid", "dev-tenant")
        };

        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Development");
        var principal = new System.Security.Claims.ClaimsPrincipal(identity);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal, "Development");

        return Task.FromResult(Microsoft.AspNetCore.Authentication.AuthenticateResult.Success(ticket));
    }
}
