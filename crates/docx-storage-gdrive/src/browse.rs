//! Google Drive BrowsableBackend implementation (multi-tenant).
//!
//! Lists connections from D1, browses files via Drive API, downloads files.

use std::sync::Arc;

use async_trait::async_trait;
use docx_storage_core::{
    BrowsableBackend, ConnectionInfo, FileEntry, FileListResult, SourceType, StorageError,
};
use tracing::{debug, instrument};

use crate::d1_client::D1Client;
use crate::gdrive::GDriveClient;
use crate::token_manager::TokenManager;

/// Google Drive browsable backend (multi-tenant, token per-connection).
pub struct GDriveBrowsableBackend {
    d1: Arc<D1Client>,
    client: Arc<GDriveClient>,
    token_manager: Arc<TokenManager>,
}

impl GDriveBrowsableBackend {
    pub fn new(
        d1: Arc<D1Client>,
        client: Arc<GDriveClient>,
        token_manager: Arc<TokenManager>,
    ) -> Self {
        Self {
            d1,
            client,
            token_manager,
        }
    }
}

#[async_trait]
impl BrowsableBackend for GDriveBrowsableBackend {
    #[instrument(skip(self), level = "debug")]
    async fn list_connections(
        &self,
        tenant_id: &str,
    ) -> Result<Vec<ConnectionInfo>, StorageError> {
        let connections = self
            .d1
            .list_connections(tenant_id, "google_drive")
            .await
            .map_err(|e| StorageError::Sync(format!("D1 error listing connections: {}", e)))?;

        let result = connections
            .into_iter()
            .map(|c| ConnectionInfo {
                connection_id: c.id,
                source_type: SourceType::GoogleDrive,
                display_name: c.display_name,
                provider_account_id: c.provider_account_id,
            })
            .collect::<Vec<_>>();

        debug!(
            "Listed {} Google Drive connections for tenant {}",
            result.len(),
            tenant_id
        );

        Ok(result)
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_files(
        &self,
        tenant_id: &str,
        connection_id: &str,
        path: &str,
        page_token: Option<&str>,
        page_size: u32,
    ) -> Result<FileListResult, StorageError> {
        let token = self
            .token_manager
            .get_valid_token(tenant_id, connection_id)
            .await
            .map_err(|e| StorageError::Sync(format!("Token error: {}", e)))?;

        // Use "root" as parent ID when path is empty (Drive root)
        let parent_id = if path.is_empty() { "root" } else { path };

        let (entries, next_page_token) = self
            .client
            .list_files(&token, parent_id, page_token, page_size)
            .await
            .map_err(|e| StorageError::Sync(format!("Google Drive list error: {}", e)))?;

        let files = entries
            .into_iter()
            .map(|e| {
                let is_folder = e.mime_type == "application/vnd.google-apps.folder";
                let size_bytes = e
                    .size
                    .as_ref()
                    .and_then(|s| s.parse::<u64>().ok())
                    .unwrap_or(0);
                let modified_at = e
                    .modified_time
                    .as_ref()
                    .and_then(|t| chrono::DateTime::parse_from_rfc3339(t).ok())
                    .map(|dt| dt.timestamp())
                    .unwrap_or(0);

                FileEntry {
                    name: e.name,
                    path: e.id.clone(), // For Google Drive, path = file ID (used for navigation)
                    file_id: Some(e.id),
                    is_folder,
                    size_bytes,
                    modified_at,
                    mime_type: Some(e.mime_type),
                }
            })
            .collect();

        Ok(FileListResult {
            files,
            next_page_token,
        })
    }

    #[instrument(skip(self), level = "debug")]
    async fn download_file(
        &self,
        tenant_id: &str,
        connection_id: &str,
        _path: &str,
        file_id: Option<&str>,
    ) -> Result<Vec<u8>, StorageError> {
        let token = self
            .token_manager
            .get_valid_token(tenant_id, connection_id)
            .await
            .map_err(|e| StorageError::Sync(format!("Token error: {}", e)))?;

        // For Google Drive, file_id is the primary identifier
        let effective_id = file_id.ok_or_else(|| {
            StorageError::Sync("file_id is required for Google Drive downloads".to_string())
        })?;

        let data = self
            .client
            .download_file(&token, effective_id)
            .await
            .map_err(|e| StorageError::Sync(format!("Google Drive download error: {}", e)))?;

        match data {
            Some(bytes) => {
                debug!(
                    "Downloaded {} bytes from Google Drive file {}",
                    bytes.len(),
                    effective_id
                );
                Ok(bytes)
            }
            None => Err(StorageError::NotFound(format!(
                "Google Drive file not found: {}",
                effective_id
            ))),
        }
    }
}
