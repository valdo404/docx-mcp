//! SSE/HTTP proxy for docx-mcp multi-tenant architecture.
//!
//! This proxy:
//! - Receives HTTP Streamable MCP requests
//! - Validates PAT tokens via Cloudflare D1
//! - Extracts tenant_id from validated tokens
//! - Forwards requests to MCP .NET process via stdio
//! - Streams responses back to clients

use clap::Parser;
use tracing::info;
use tracing_subscriber::EnvFilter;

mod config;

use config::Config;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    // Initialize logging
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")),
        )
        .init();

    let config = Config::parse();

    info!("Starting docx-mcp-proxy");
    info!("  Host: {}", config.host);
    info!("  Port: {}", config.port);

    // TODO: Implement proxy server
    // - D1 client for PAT validation
    // - MCP process spawning and stdio bridge
    // - Streamable HTTP endpoint

    info!("Proxy not yet implemented");

    Ok(())
}
