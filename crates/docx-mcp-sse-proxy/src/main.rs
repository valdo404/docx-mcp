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
use hyper_util::rt::{TokioExecutor, TokioIo};
use hyper_util::server::conn::auto::Builder;
use tokio::net::TcpListener;
use tokio::signal;
use tower::Service;
use tower_http::cors::{Any, CorsLayer};
use tower_http::trace::TraceLayer;
use tracing::{info, warn};
use tracing_subscriber::EnvFilter;

mod auth;
mod config;
mod error;
mod handlers;
mod oauth;
mod session;

use auth::{PatValidator, SharedPatValidator};
use config::Config;
use handlers::{health_handler, mcp_forward_handler, oauth_metadata_handler, upstream_health_handler, AppState};
use oauth::{OAuthValidator, SharedOAuthValidator};
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

    // Create PAT and OAuth validators if D1 credentials are configured
    let (validator, oauth_validator): (Option<SharedPatValidator>, Option<SharedOAuthValidator>) =
        if config.cloudflare_account_id.is_some()
            && config.cloudflare_api_token.is_some()
            && config.d1_database_id.is_some()
        {
            let account_id = config.cloudflare_account_id.clone().unwrap();
            let api_token = config.cloudflare_api_token.clone().unwrap();
            let database_id = config.d1_database_id.clone().unwrap();

            info!("  Auth: D1 PAT + OAuth validation enabled");
            info!(
                "  Cache TTL: {}s (negative: {}s)",
                config.pat_cache_ttl_secs, config.pat_negative_cache_ttl_secs
            );

            let pat = Arc::new(PatValidator::new(
                account_id.clone(),
                api_token.clone(),
                database_id.clone(),
                config.pat_cache_ttl_secs,
                config.pat_negative_cache_ttl_secs,
            ));

            let oauth = Arc::new(OAuthValidator::new(
                account_id,
                api_token,
                database_id,
                config.pat_cache_ttl_secs,
                config.pat_negative_cache_ttl_secs,
            ));

            (Some(pat), Some(oauth))
        } else {
            warn!("  Auth: DISABLED (no D1 credentials configured)");
            warn!("  Set CLOUDFLARE_ACCOUNT_ID, CLOUDFLARE_API_TOKEN, and D1_DATABASE_ID to enable auth");
            (None, None)
        };

    // Create HTTP client for forwarding
    let http_client = reqwest::Client::builder()
        .timeout(std::time::Duration::from_secs(300))
        .build()
        .expect("Failed to create HTTP client");

    // Normalize backend URL (strip trailing slash)
    let backend_url = config.mcp_backend_url.trim_end_matches('/').to_string();

    // OAuth resource metadata config
    let resource_url = config.resource_url.clone();
    let auth_server_url = config.auth_server_url.clone();
    if let Some(ref url) = resource_url {
        info!("  Resource URL: {}", url);
    }
    if let Some(ref url) = auth_server_url {
        info!("  Auth Server URL: {}", url);
    }

    // Build application state
    let state = AppState {
        validator,
        oauth_validator,
        backend_url,
        http_client,
        sessions: Arc::new(SessionRegistry::new()),
        resource_url,
        auth_server_url,
    };

    // Configure CORS
    let cors = CorsLayer::new()
        .allow_origin(Any)
        .allow_methods(Any)
        .allow_headers(Any);

    // Build router
    let app = Router::new()
        .route("/health", get(health_handler))
        .route("/upstream-health", get(upstream_health_handler))
        .route(
            "/.well-known/oauth-protected-resource",
            get(oauth_metadata_handler),
        )
        .route("/mcp", any(mcp_forward_handler))
        .route("/mcp/{*rest}", any(mcp_forward_handler))
        .layer(cors)
        .layer(TraceLayer::new_for_http())
        .with_state(state);

    // Bind and serve (HTTP/1.1 + HTTP/2 h2c dual-stack)
    let addr = format!("{}:{}", config.host, config.port);
    let listener = TcpListener::bind(&addr).await?;
    info!("Listening on http://{} (HTTP/1.1 + h2c)", addr);

    let shutdown = shutdown_signal();
    tokio::pin!(shutdown);

    loop {
        tokio::select! {
            result = listener.accept() => {
                let (stream, _remote_addr) = result?;
                let tower_service = app.clone();
                tokio::spawn(async move {
                    let hyper_service = hyper::service::service_fn(move |req| {
                        tower_service.clone().call(req)
                    });
                    if let Err(err) = Builder::new(TokioExecutor::new())
                        .serve_connection_with_upgrades(TokioIo::new(stream), hyper_service)
                        .await
                    {
                        tracing::debug!("connection error: {err}");
                    }
                });
            }
            _ = &mut shutdown => {
                info!("Shutting down");
                break;
            }
        }
    }

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
