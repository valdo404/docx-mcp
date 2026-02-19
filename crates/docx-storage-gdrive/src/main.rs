mod browse;
mod config;
mod d1_client;
mod gdrive;
mod service_sync;
mod service_watch;
mod sync;
mod token_manager;
mod watch;

use std::sync::Arc;

use clap::Parser;
use tokio::signal;
use tokio::sync::watch as tokio_watch;
use tonic::transport::Server;
use tonic_reflection::server::Builder as ReflectionBuilder;
use tracing::info;
use tracing_subscriber::EnvFilter;

use browse::GDriveBrowsableBackend;
use config::Config;
use d1_client::D1Client;
use gdrive::GDriveClient;
use service_sync::SourceSyncServiceImpl;
use service_watch::ExternalWatchServiceImpl;
use sync::GDriveSyncBackend;
use token_manager::TokenManager;
use watch::GDriveWatchBackend;

/// Include generated protobuf code.
pub mod proto {
    tonic::include_proto!("docx.storage");
}

/// File descriptor set for gRPC reflection.
pub const FILE_DESCRIPTOR_SET: &[u8] = tonic::include_file_descriptor_set!("storage_descriptor");

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    // Initialize logging
    tracing_subscriber::fmt()
        .with_env_filter(
            EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new("info")),
        )
        .init();

    let config = Config::parse();

    info!("Starting docx-storage-gdrive server (multi-tenant)");
    info!("  Poll interval: {} secs", config.watch_poll_interval_secs);

    // Create D1 client for OAuth token storage
    let d1_client = Arc::new(D1Client::new(
        config.cloudflare_account_id.clone(),
        config.cloudflare_api_token.clone(),
        config.d1_database_id.clone(),
    ));
    info!("  D1 client initialized (database: {})", config.d1_database_id);

    // Create token manager (reads tokens from D1, refreshes via Google OAuth2)
    let token_manager = Arc::new(TokenManager::new(
        d1_client.clone(),
        config.google_client_id.clone(),
        config.google_client_secret.clone(),
    ));
    info!("  Token manager initialized");

    // Create Google Drive API client (stateless — tokens provided per-call)
    let gdrive_client = Arc::new(GDriveClient::new());

    // Create sync backend
    let sync_backend: Arc<dyn docx_storage_core::SyncBackend> = Arc::new(
        GDriveSyncBackend::new(gdrive_client.clone(), token_manager.clone()),
    );

    // Create browse backend
    let browse_backend: Arc<dyn docx_storage_core::BrowsableBackend> = Arc::new(
        GDriveBrowsableBackend::new(d1_client, gdrive_client.clone(), token_manager.clone()),
    );

    // Create watch backend
    let watch_backend = Arc::new(GDriveWatchBackend::new(
        gdrive_client,
        token_manager,
        config.watch_poll_interval_secs,
    ));

    // Create gRPC services (sync + watch only — no StorageService)
    let sync_service = SourceSyncServiceImpl::new(sync_backend, browse_backend);
    let sync_svc = proto::source_sync_service_server::SourceSyncServiceServer::new(sync_service);

    let watch_service = ExternalWatchServiceImpl::new(watch_backend);
    let watch_svc =
        proto::external_watch_service_server::ExternalWatchServiceServer::new(watch_service);

    // Create shutdown signal
    let mut shutdown_rx = create_shutdown_signal();
    let shutdown_future = async move {
        let _ = shutdown_rx.wait_for(|&v| v).await;
    };

    // Create reflection service
    let reflection_svc = ReflectionBuilder::configure()
        .register_encoded_file_descriptor_set(FILE_DESCRIPTOR_SET)
        .build_v1()?;

    // Start server
    let addr = format!("{}:{}", config.host, config.port).parse()?;
    info!("Listening on tcp://{}", addr);

    Server::builder()
        .add_service(reflection_svc)
        .add_service(sync_svc)
        .add_service(watch_svc)
        .serve_with_shutdown(addr, shutdown_future)
        .await?;

    info!("Server shutdown complete");
    Ok(())
}

/// Create a shutdown signal that triggers on Ctrl+C or SIGTERM.
fn create_shutdown_signal() -> tokio_watch::Receiver<bool> {
    let (tx, rx) = tokio_watch::channel(false);

    tokio::spawn(async move {
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

        let _ = tx.send(true);
    });

    rx
}
