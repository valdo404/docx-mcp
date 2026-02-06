use clap::Parser;

/// Configuration for the docx-mcp-proxy server.
#[derive(Parser, Debug, Clone)]
#[command(name = "docx-mcp-proxy")]
#[command(about = "SSE/HTTP proxy for docx-mcp multi-tenant architecture")]
pub struct Config {
    /// Host to bind to
    #[arg(long, default_value = "0.0.0.0", env = "PROXY_HOST")]
    pub host: String,

    /// Port to bind to
    #[arg(long, default_value = "8080", env = "PROXY_PORT")]
    pub port: u16,

    /// Path to docx-mcp binary
    #[arg(long, env = "DOCX_MCP_BINARY")]
    pub docx_mcp_binary: Option<String>,

    /// Cloudflare Account ID
    #[arg(long, env = "CLOUDFLARE_ACCOUNT_ID")]
    pub cloudflare_account_id: Option<String>,

    /// Cloudflare API Token (with D1 read permission)
    #[arg(long, env = "CLOUDFLARE_API_TOKEN")]
    pub cloudflare_api_token: Option<String>,

    /// D1 Database ID
    #[arg(long, env = "D1_DATABASE_ID")]
    pub d1_database_id: Option<String>,

    /// PAT cache TTL in seconds
    #[arg(long, default_value = "300", env = "PAT_CACHE_TTL_SECS")]
    pub pat_cache_ttl_secs: u64,

    /// Negative cache TTL for invalid PATs
    #[arg(long, default_value = "60", env = "PAT_NEGATIVE_CACHE_TTL_SECS")]
    pub pat_negative_cache_ttl_secs: u64,

    /// gRPC storage server URL
    #[arg(long, env = "STORAGE_GRPC_URL")]
    pub storage_grpc_url: Option<String>,
}
