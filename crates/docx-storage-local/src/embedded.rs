use std::path::Path;
use std::pin::Pin;
use std::sync::{Mutex, OnceLock};
use std::task::{Context, Poll};

use tokio::io::{AsyncRead, AsyncWrite, DuplexStream, ReadBuf, ReadHalf, WriteHalf};
use tokio::runtime::Runtime;
use tokio::task::AbortHandle;
use tonic::transport::server::Connected;
use tonic::transport::Server;

use crate::server;
use crate::service::proto::external_watch_service_server::ExternalWatchServiceServer;
use crate::service::proto::source_sync_service_server::SourceSyncServiceServer;
use crate::service::proto::storage_service_server::StorageServiceServer;
use crate::service::StorageServiceImpl;
use crate::service_sync::SourceSyncServiceImpl;
use crate::service_watch::ExternalWatchServiceImpl;

/// Returns true if DEBUG environment variable is set.
fn is_debug() -> bool {
    std::env::var("DEBUG").is_ok()
}

/// Wrapper around DuplexStream that implements tonic's Connected trait.
struct InMemoryStream(DuplexStream);

impl Connected for InMemoryStream {
    type ConnectInfo = ();
    fn connect_info(&self) -> Self::ConnectInfo {}
}

impl AsyncRead for InMemoryStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<std::io::Result<()>> {
        Pin::new(&mut self.0).poll_read(cx, buf)
    }
}

impl AsyncWrite for InMemoryStream {
    fn poll_write(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<std::io::Result<usize>> {
        Pin::new(&mut self.0).poll_write(cx, buf)
    }

    fn poll_flush(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<std::io::Result<()>> {
        Pin::new(&mut self.0).poll_flush(cx)
    }

    fn poll_shutdown(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
    ) -> Poll<std::io::Result<()>> {
        Pin::new(&mut self.0).poll_shutdown(cx)
    }
}

/// Global state for the embedded gRPC server.
/// Read and write halves have separate mutexes so HTTP/2 full-duplex works
/// (one .NET thread reads, another writes, concurrently).
struct EmbeddedState {
    runtime: Runtime,
    read_half: Mutex<ReadHalf<DuplexStream>>,
    write_half: Mutex<WriteHalf<DuplexStream>>,
    server_abort: AbortHandle,
}

static STATE: OnceLock<EmbeddedState> = OnceLock::new();

/// Initialize the embedded gRPC server with in-memory DuplexStream transport.
///
/// Creates storage backends, starts tonic server on a background tokio task,
/// and splits the client half of the DuplexStream for FFI read/write access.
pub fn init(storage_dir: &Path) -> Result<(), String> {
    let debug = is_debug();
    if debug {
        eprintln!("[embedded] init: creating runtime...");
    }
    let runtime = Runtime::new().map_err(|e| e.to_string())?;

    // Enter the runtime context so create_backends() can call tokio::spawn()
    // (needed by NotifyWatchBackend which spawns an event processing task)
    let _guard = runtime.enter();

    // Create backends (shared with main.rs via server module)
    let (storage, lock, sync, watch) = server::create_backends(storage_dir);

    // Create gRPC services
    let storage_svc = StorageServiceServer::new(StorageServiceImpl::new(storage, lock));
    let sync_svc = SourceSyncServiceServer::new(SourceSyncServiceImpl::new(sync));
    let watch_svc = ExternalWatchServiceServer::new(ExternalWatchServiceImpl::new(watch));

    // Create in-memory transport (256KB buffer — matches StorageClient chunk size)
    if debug {
        eprintln!("[embedded] init: creating DuplexStream...");
    }
    let (client, server_stream) = tokio::io::duplex(256 * 1024);

    // Start tonic server on the server half (runs on tokio worker threads)
    if debug {
        eprintln!("[embedded] init: spawning tonic server...");
    }
    let server_handle = runtime.spawn(async move {
        if is_debug() {
            eprintln!("[embedded] server task: starting serve_with_incoming...");
        }
        let result = Server::builder()
            .add_service(storage_svc)
            .add_service(sync_svc)
            .add_service(watch_svc)
            .serve_with_incoming(tokio_stream::once(Ok::<_, std::io::Error>(
                InMemoryStream(server_stream),
            )))
            .await;
        if is_debug() {
            eprintln!("[embedded] server task: serve_with_incoming ended: {result:?}");
        }
    });

    // Split client for concurrent read/write (HTTP/2 is full-duplex)
    if debug {
        eprintln!("[embedded] init: splitting client DuplexStream...");
    }
    let (read_half, write_half) = tokio::io::split(client);

    STATE
        .set(EmbeddedState {
            runtime,
            read_half: Mutex::new(read_half),
            write_half: Mutex::new(write_half),
            server_abort: server_handle.abort_handle(),
        })
        .map_err(|_| "Already initialized".to_string())
}

/// Read from the client side of the in-memory gRPC transport.
/// Called by .NET via P/Invoke from a non-tokio thread.
/// Returns bytes read (>0), 0 = EOF, -1 = error.
pub fn pipe_read(buf: &mut [u8]) -> i64 {
    let state = match STATE.get() {
        Some(s) => s,
        None => return -1,
    };
    let debug = is_debug();
    if debug {
        eprintln!("[embedded] pipe_read: waiting for lock (buf_len={})...", buf.len());
    }
    let mut reader = state.read_half.lock().unwrap();
    if debug {
        eprintln!("[embedded] pipe_read: got lock, calling block_on...");
    }
    state.runtime.block_on(async {
        use tokio::io::AsyncReadExt;
        match reader.read(buf).await {
            Ok(n) => {
                if debug {
                    eprintln!("[embedded] pipe_read: read {n} bytes");
                }
                n as i64
            }
            Err(e) => {
                eprintln!("[embedded] pipe_read: error: {e}");
                -1
            }
        }
    })
}

/// Write to the client side of the in-memory gRPC transport.
/// Called by .NET via P/Invoke from a non-tokio thread.
/// Returns bytes written, -1 = error.
pub fn pipe_write(data: &[u8]) -> i64 {
    let state = match STATE.get() {
        Some(s) => s,
        None => return -1,
    };
    let debug = is_debug();
    if debug {
        eprintln!(
            "[embedded] pipe_write: waiting for lock (data_len={})...",
            data.len()
        );
    }
    let mut writer = state.write_half.lock().unwrap();
    if debug {
        eprintln!("[embedded] pipe_write: got lock, calling block_on...");
    }
    state.runtime.block_on(async {
        use tokio::io::AsyncWriteExt;
        match writer.write_all(data).await {
            Ok(()) => {
                if debug {
                    eprintln!("[embedded] pipe_write: wrote {} bytes", data.len());
                }
                data.len() as i64
            }
            Err(e) => {
                eprintln!("[embedded] pipe_write: error: {e}");
                -1
            }
        }
    })
}

/// Flush the write side of the transport.
/// Returns 0 on success, -1 on error.
pub fn pipe_flush() -> i32 {
    let state = match STATE.get() {
        Some(s) => s,
        None => return -1,
    };
    let mut writer = state.write_half.lock().unwrap();
    state.runtime.block_on(async {
        use tokio::io::AsyncWriteExt;
        match writer.flush().await {
            Ok(()) => 0,
            Err(_) => -1,
        }
    })
}

/// Shutdown the embedded gRPC server.
/// Aborts the server task. The runtime and pipe state remain in memory
/// (leaked via OnceLock) but the process is expected to exit shortly after.
pub fn shutdown() {
    if let Some(state) = STATE.get() {
        state.server_abort.abort();
    }
}
