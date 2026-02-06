mod config;
mod error;
mod lock;
mod service;
mod service_sync;
mod service_watch;
mod storage;
mod sync;
mod watch;

use std::sync::Arc;

use clap::Parser;
use tokio::signal;
use tokio::sync::watch as tokio_watch;
use tonic::transport::Server;
use tonic_reflection::server::Builder as ReflectionBuilder;
use tracing::info;
use tracing_subscriber::EnvFilter;

#[cfg(unix)]
use tokio::net::UnixListener;

use config::{Config, Transport};
use lock::FileLock;
use service::proto::storage_service_server::StorageServiceServer;
use service::proto::source_sync_service_server::SourceSyncServiceServer;
use service::proto::external_watch_service_server::ExternalWatchServiceServer;
use service::StorageServiceImpl;
use service_sync::SourceSyncServiceImpl;
use service_watch::ExternalWatchServiceImpl;
use storage::LocalStorage;
use sync::LocalFileSyncBackend;
use watch::NotifyWatchBackend;

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

    info!("Starting docx-storage-local server");
    info!("  Transport: {}", config.transport);
    info!("  Backend: {}", config.storage_backend);
    if let Some(ppid) = config.parent_pid {
        info!("  Parent PID: {} (will exit when parent dies)", ppid);
    }

    // Create storage backend (local only)
    let dir = config.effective_local_storage_dir();
    info!("  Local storage dir: {}", dir.display());
    let storage: Arc<dyn crate::storage::StorageBackend> = Arc::new(LocalStorage::new(&dir));

    // Create lock manager (using same base dir as storage)
    let lock_manager: Arc<dyn crate::lock::LockManager> = Arc::new(FileLock::new(&dir));

    // Create sync backend (shares storage for index persistence)
    let sync_backend: Arc<dyn docx_storage_core::SyncBackend> = Arc::new(LocalFileSyncBackend::new(storage.clone()));

    // Create watch backend (uses SHA256 hash for content change detection, like C# ExternalChangeTracker)
    let watch_backend: Arc<dyn docx_storage_core::WatchBackend> = Arc::new(NotifyWatchBackend::new());

    // Create gRPC services
    let storage_service = StorageServiceImpl::new(storage, lock_manager);
    let storage_svc = StorageServiceServer::new(storage_service);

    let sync_service = SourceSyncServiceImpl::new(sync_backend);
    let sync_svc = SourceSyncServiceServer::new(sync_service);

    let watch_service = ExternalWatchServiceImpl::new(watch_backend);
    let watch_svc = ExternalWatchServiceServer::new(watch_service);

    // Set up parent death signal using OS-native mechanisms
    setup_parent_death_signal(config.parent_pid);

    // Create shutdown signal (watches for Ctrl+C and SIGTERM)
    // Parent death is handled by OS-native signal delivery (prctl/kqueue)
    let mut shutdown_rx = create_shutdown_signal();
    let shutdown_future = async move {
        let _ = shutdown_rx.wait_for(|&v| v).await;
    };

    // Create reflection service
    let reflection_svc = ReflectionBuilder::configure()
        .register_encoded_file_descriptor_set(FILE_DESCRIPTOR_SET)
        .build_v1()?;

    // Start server based on transport
    match config.transport {
        Transport::Tcp => {
            let addr = format!("{}:{}", config.host, config.port).parse()?;
            info!("Listening on tcp://{}", addr);

            Server::builder()
                .add_service(reflection_svc)
                .add_service(storage_svc)
                .add_service(sync_svc)
                .add_service(watch_svc)
                .serve_with_shutdown(addr, shutdown_future)
                .await?;
        }
        #[cfg(unix)]
        Transport::Unix => {
            let socket_path = config.effective_unix_socket();

            // Remove existing socket file if it exists
            if socket_path.exists() {
                std::fs::remove_file(&socket_path)?;
            }

            // Ensure parent directory exists
            if let Some(parent) = socket_path.parent() {
                std::fs::create_dir_all(parent)?;
            }

            info!("Listening on unix://{}", socket_path.display());

            let uds = UnixListener::bind(&socket_path)?;
            let uds_stream = tokio_stream::wrappers::UnixListenerStream::new(uds);

            Server::builder()
                .add_service(reflection_svc)
                .add_service(storage_svc)
                .add_service(sync_svc)
                .add_service(watch_svc)
                .serve_with_incoming_shutdown(uds_stream, shutdown_future)
                .await?;

            // Clean up socket on shutdown
            if socket_path.exists() {
                let _ = std::fs::remove_file(&socket_path);
            }
        }
        #[cfg(not(unix))]
        Transport::Unix => {
            anyhow::bail!("Unix socket transport is not supported on Windows. Use TCP instead.");
        }
    }

    info!("Server shutdown complete");
    Ok(())
}

/// Set up parent death monitoring.
/// The parent process (.NET) will kill us on exit via ProcessExit event.
/// This is a fallback safety net that polls for parent death.
fn setup_parent_death_signal(parent_pid: Option<u32>) {
    let Some(ppid) = parent_pid else { return };

    #[cfg(target_os = "linux")]
    {
        // Linux: use prctl for immediate notification
        setup_parent_death_signal_linux(ppid);
    }

    #[cfg(not(target_os = "linux"))]
    {
        // macOS/Windows: poll as fallback (parent will kill us on exit)
        setup_parent_death_poll(ppid);
    }
}

/// Linux: Use prctl to receive SIGTERM when parent dies.
#[cfg(target_os = "linux")]
#[allow(unsafe_code)]
fn setup_parent_death_signal_linux(parent_pid: u32) {
    // SAFETY: prctl and kill are well-defined syscalls
    unsafe {
        // Check if parent is already dead
        if libc::kill(parent_pid as i32, 0) != 0 {
            info!("Parent process {} already dead at startup, terminating", parent_pid);
            std::process::exit(0);
        }

        // Set up parent death signal
        const PR_SET_PDEATHSIG: libc::c_int = 1;
        libc::prctl(PR_SET_PDEATHSIG, libc::SIGTERM);
    }
    info!("Configured prctl(PR_SET_PDEATHSIG, SIGTERM) for parent {} death notification", parent_pid);
}

/// Simple polling fallback for parent death detection.
/// The parent (.NET) will kill us via ProcessExit, this is just a safety net.
#[cfg(not(target_os = "linux"))]
#[allow(unsafe_code)]
fn setup_parent_death_poll(parent_pid: u32) {
    use std::thread;
    use std::time::Duration;

    thread::spawn(move || {
        info!("Monitoring parent process {} (poll fallback)", parent_pid);

        loop {
            thread::sleep(Duration::from_secs(2));

            #[cfg(unix)]
            let alive = unsafe { libc::kill(parent_pid as i32, 0) == 0 };

            #[cfg(windows)]
            let alive = {
                // SYNCHRONIZE = 0x00100000 - we need this to open process for synchronization
                const SYNCHRONIZE: u32 = 0x00100000;
                let handle = unsafe {
                    windows_sys::Win32::System::Threading::OpenProcess(
                        SYNCHRONIZE,
                        0,
                        parent_pid,
                    )
                };
                if handle != std::ptr::null_mut() {
                    unsafe { windows_sys::Win32::Foundation::CloseHandle(handle) };
                    true
                } else {
                    false
                }
            };

            if !alive {
                info!("Parent process {} exited, terminating", parent_pid);
                std::process::exit(0);
            }
        }
    });
}

/// Create a shutdown signal that triggers on Ctrl+C or SIGTERM.
/// Parent death is handled separately via OS-native mechanisms.
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
