use clap::Parser;

/// Configuration for the docx-storage-gdrive server.
#[derive(Parser, Debug, Clone)]
#[command(name = "docx-storage-gdrive")]
#[command(about = "Google Drive sync/watch gRPC server for docx-mcp (multi-tenant, tokens from D1)")]
pub struct Config {
    /// TCP host to bind to
    #[arg(long, default_value = "0.0.0.0", env = "GRPC_HOST")]
    pub host: String,

    /// TCP port to bind to
    #[arg(long, default_value = "50052", env = "GRPC_PORT")]
    pub port: u16,

    /// Cloudflare Account ID (for D1 API access)
    #[arg(long, env = "CLOUDFLARE_ACCOUNT_ID")]
    pub cloudflare_account_id: String,

    /// Cloudflare API Token (for D1 API access)
    #[arg(long, env = "CLOUDFLARE_API_TOKEN")]
    pub cloudflare_api_token: String,

    /// D1 Database ID (stores oauth_connection table)
    #[arg(long, env = "D1_DATABASE_ID")]
    pub d1_database_id: String,

    /// Google OAuth2 Client ID (for token refresh)
    #[arg(long, env = "GOOGLE_CLIENT_ID")]
    pub google_client_id: String,

    /// Google OAuth2 Client Secret (for token refresh)
    #[arg(long, env = "GOOGLE_CLIENT_SECRET")]
    pub google_client_secret: String,

    /// Polling interval for external watch (seconds)
    #[arg(long, default_value = "60", env = "WATCH_POLL_INTERVAL")]
    pub watch_poll_interval_secs: u32,
}
