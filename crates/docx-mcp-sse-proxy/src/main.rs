//! HTTP reverse proxy for docx-mcp multi-tenant architecture.
//!
//! This proxy:
//! - Receives MCP Streamable HTTP requests (POST/GET/DELETE /mcp)
//! - Validates PAT tokens via Cloudflare D1
//! - Extracts tenant_id from validated tokens
//! - Forwards requests to the .NET MCP HTTP backend with X-Tenant-Id header
//! - Streams responses (SSE or JSON) back to clients

use std::sync::Arc;

use axum::routing::{any, get};
use axum::Router;
use clap::Parser;
use tokio::net::TcpListener;
use tokio::signal;
use tower_http::cors::{Any, CorsLayer};
use tower_http::trace::TraceLayer;
use tracing::{info, warn};
use tracing_subscriber::EnvFilter;

mod auth;
mod config;
mod error;
mod handlers;
mod session;

use auth::{PatValidator, SharedPatValidator};
use config::Config;
use handlers::{health_handler, mcp_forward_handler, AppState};
use session::SessionRegistry;

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    // Initialize logging
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")),
        )
        .init();

    let config = Config::parse();

    info!(
        "Starting docx-mcp-sse-proxy v{}",
        env!("CARGO_PKG_VERSION")
    );
    info!("  Host: {}", config.host);
    info!("  Port: {}", config.port);
    info!("  Backend: {}", config.mcp_backend_url);

    // Create PAT validator if D1 credentials are configured
    let validator: Option<SharedPatValidator> = if config.cloudflare_account_id.is_some()
        && config.cloudflare_api_token.is_some()
        && config.d1_database_id.is_some()
    {
        info!("  Auth: D1 PAT validation enabled");
        info!(
            "  PAT cache TTL: {}s (negative: {}s)",
            config.pat_cache_ttl_secs, config.pat_negative_cache_ttl_secs
        );

        Some(Arc::new(PatValidator::new(
            config.cloudflare_account_id.clone().unwrap(),
            config.cloudflare_api_token.clone().unwrap(),
            config.d1_database_id.clone().unwrap(),
            config.pat_cache_ttl_secs,
            config.pat_negative_cache_ttl_secs,
        )))
    } else {
        warn!("  Auth: DISABLED (no D1 credentials configured)");
        warn!("  Set CLOUDFLARE_ACCOUNT_ID, CLOUDFLARE_API_TOKEN, and D1_DATABASE_ID to enable auth");
        None
    };

    // Create HTTP client for forwarding
    let http_client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()
        .expect("Failed to create HTTP client");

    // Normalize backend URL (strip trailing slash)
    let backend_url = config.mcp_backend_url.trim_end_matches('/').to_string();

    // Build application state
    let state = AppState {
        validator,
        backend_url,
        http_client,
        sessions: Arc::new(SessionRegistry::new()),
    };

    // Configure CORS
    let cors = CorsLayer::new()
        .allow_origin(Any)
        .allow_methods(Any)
        .allow_headers(Any);

    // Build router
    let app = Router::new()
        .route("/health", get(health_handler))
        .route("/mcp", any(mcp_forward_handler))
        .route("/mcp/{*rest}", any(mcp_forward_handler))
        .layer(cors)
        .layer(TraceLayer::new_for_http())
        .with_state(state);

    // Bind and serve
    let addr = format!("{}:{}", config.host, config.port);
    let listener = TcpListener::bind(&addr).await?;
    info!("Listening on http://{}", addr);

    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown_signal())
        .await?;

    info!("Server shutdown complete");
    Ok(())
}

async fn shutdown_signal() {
    let ctrl_c = async {
        signal::ctrl_c()
            .await
            .expect("Failed to install Ctrl+C handler");
        info!("Received Ctrl+C, initiating shutdown");
    };

    #[cfg(unix)]
    let terminate = async {
        signal::unix::signal(signal::unix::SignalKind::terminate())
            .expect("Failed to install SIGTERM handler")
            .recv()
            .await;
        info!("Received SIGTERM, initiating shutdown");
    };

    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        _ = ctrl_c => {},
        _ = terminate => {},
    }
}
