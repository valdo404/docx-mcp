mod config;
mod error;
mod service;
mod service_sync;
mod service_watch;
mod storage;
mod sync;
mod watch;

use std::sync::Arc;

use aws_config::Region;
use aws_sdk_s3::config::{BehaviorVersion, Credentials};
use clap::Parser;
use tokio::signal;
use tokio::sync::watch as tokio_watch;
use tonic::transport::Server;
use tonic_reflection::server::Builder as ReflectionBuilder;
use tracing::info;
use tracing_subscriber::EnvFilter;

use config::Config;
use service::proto::external_watch_service_server::ExternalWatchServiceServer;
use service::proto::source_sync_service_server::SourceSyncServiceServer;
use service::proto::storage_service_server::StorageServiceServer;
use service::StorageServiceImpl;
use service_sync::SourceSyncServiceImpl;
use service_watch::ExternalWatchServiceImpl;
use storage::R2Storage;
use sync::R2SyncBackend;
use watch::PollingWatchBackend;

/// File descriptor set for gRPC reflection
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

    info!("Starting docx-storage-cloudflare server");
    info!("  R2 bucket: {}", config.r2_bucket_name);
    info!("  Poll interval: {} secs", config.watch_poll_interval_secs);

    // Create S3 client for R2
    let credentials = Credentials::new(
        &config.r2_access_key_id,
        &config.r2_secret_access_key,
        None,
        None,
        "r2",
    );

    let s3_config = aws_sdk_s3::Config::builder()
        .behavior_version(BehaviorVersion::latest())
        .credentials_provider(credentials)
        .region(Region::new("auto"))
        .endpoint_url(config.r2_endpoint())
        .force_path_style(true)
        .build();

    let s3_client = aws_sdk_s3::Client::from_conf(s3_config);

    // Create storage backend (R2 only — no KV dependency)
    let storage = Arc::new(R2Storage::new(
        s3_client.clone(),
        config.r2_bucket_name.clone(),
    ));

    // Create sync backend (R2)
    let sync_backend: Arc<dyn docx_storage_core::SyncBackend> = Arc::new(R2SyncBackend::new(
        s3_client.clone(),
        config.r2_bucket_name.clone(),
        storage.clone(),
    ));

    // Create watch backend (polling-based)
    let watch_backend: Arc<dyn docx_storage_core::WatchBackend> =
        Arc::new(PollingWatchBackend::new(
            s3_client,
            config.r2_bucket_name.clone(),
            config.watch_poll_interval_secs,
        ));

    // Create gRPC services
    let storage_service = StorageServiceImpl::new(storage);
    let storage_svc = StorageServiceServer::new(storage_service);

    let sync_service = SourceSyncServiceImpl::new(sync_backend);
    let sync_svc = SourceSyncServiceServer::new(sync_service);

    let watch_service = ExternalWatchServiceImpl::new(watch_backend);
    let watch_svc = ExternalWatchServiceServer::new(watch_service);

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
        .add_service(storage_svc)
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
