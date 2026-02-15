use async_trait::async_trait;

use crate::error::StorageError;
use crate::sync::SourceType;

/// Information about an available storage connection.
#[derive(Debug, Clone)]
pub struct ConnectionInfo {
    /// Connection ID (empty string for local)
    pub connection_id: String,
    /// Source type
    pub source_type: SourceType,
    /// Display name ("Local filesystem", "Mon Drive perso", etc.)
    pub display_name: String,
    /// Provider account identifier (email for GDrive, empty for local)
    pub provider_account_id: Option<String>,
}

/// A file or folder entry from a connection.
#[derive(Debug, Clone)]
pub struct FileEntry {
    /// File/folder name
    pub name: String,
    /// Human-readable path (local: absolute, cloud: display path)
    pub path: String,
    /// Provider-specific file ID (GDrive file ID, empty for local)
    pub file_id: Option<String>,
    /// Whether this is a folder
    pub is_folder: bool,
    /// Size in bytes (0 for folders)
    pub size_bytes: u64,
    /// Last modified timestamp (Unix seconds)
    pub modified_at: i64,
    /// MIME type (if known)
    pub mime_type: Option<String>,
}

/// Result of listing files with pagination.
#[derive(Debug, Clone)]
pub struct FileListResult {
    pub files: Vec<FileEntry>,
    pub next_page_token: Option<String>,
}

/// Backend trait for browsing storage connections and their files.
#[async_trait]
pub trait BrowsableBackend: Send + Sync {
    /// List available connections for a tenant.
    async fn list_connections(&self, tenant_id: &str) -> Result<Vec<ConnectionInfo>, StorageError>;

    /// List files in a folder of a connection.
    async fn list_files(
        &self,
        tenant_id: &str,
        connection_id: &str,
        path: &str,
        page_token: Option<&str>,
        page_size: u32,
    ) -> Result<FileListResult, StorageError>;

    /// Download a file from a connection.
    async fn download_file(
        &self,
        tenant_id: &str,
        connection_id: &str,
        path: &str,
        file_id: Option<&str>,
    ) -> Result<Vec<u8>, StorageError>;
}
