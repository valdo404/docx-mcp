use std::path::PathBuf;

use clap::Parser;

/// Configuration for the docx-storage-local server.
#[derive(Parser, Debug, Clone)]
#[command(name = "docx-storage-local")]
#[command(about = "Local filesystem gRPC storage server for docx-mcp")]
pub struct Config {
    /// Transport type: tcp or unix
    #[arg(long, default_value = "tcp", env = "GRPC_TRANSPORT")]
    pub transport: Transport,

    /// TCP host to bind to (only used with --transport tcp)
    #[arg(long, default_value = "0.0.0.0", env = "GRPC_HOST")]
    pub host: String,

    /// TCP port to bind to (only used with --transport tcp)
    #[arg(long, default_value = "50051", env = "GRPC_PORT")]
    pub port: u16,

    /// Unix socket path (only used with --transport unix)
    #[arg(long, env = "GRPC_UNIX_SOCKET")]
    pub unix_socket: Option<PathBuf>,

    /// Storage backend (always local for this binary)
    #[arg(long, default_value = "local", env = "STORAGE_BACKEND")]
    pub storage_backend: StorageBackend,

    /// Base directory for local storage
    #[arg(long, env = "LOCAL_STORAGE_DIR")]
    pub local_storage_dir: Option<PathBuf>,

    /// Parent process PID to watch. If set, server will exit when parent dies.
    /// This enables fork/join semantics where the child server follows the parent lifecycle.
    #[arg(long)]
    pub parent_pid: Option<u32>,
}

impl Config {
    /// Get the effective local storage directory.
    pub fn effective_local_storage_dir(&self) -> PathBuf {
        self.local_storage_dir.clone().unwrap_or_else(|| {
            dirs::data_local_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join("docx-mcp")
                .join("sessions")
        })
    }

    /// Get the effective Unix socket path.
    #[cfg(unix)]
    pub fn effective_unix_socket(&self) -> PathBuf {
        self.unix_socket.clone().unwrap_or_else(|| {
            std::env::var("XDG_RUNTIME_DIR")
                .map(PathBuf::from)
                .unwrap_or_else(|_| PathBuf::from("/tmp"))
                .join("docx-storage-local.sock")
        })
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub enum Transport {
    Tcp,
    Unix,
}

impl std::fmt::Display for Transport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Transport::Tcp => write!(f, "tcp"),
            Transport::Unix => write!(f, "unix"),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, clap::ValueEnum)]
pub enum StorageBackend {
    Local,
}

impl std::fmt::Display for StorageBackend {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            StorageBackend::Local => write!(f, "local"),
        }
    }
}
