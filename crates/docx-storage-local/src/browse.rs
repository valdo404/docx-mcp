use async_trait::async_trait;
use docx_storage_core::{
    BrowsableBackend, ConnectionInfo, FileEntry, FileListResult, SourceType, StorageError,
};
use tracing::debug;

/// Local filesystem browsable backend.
///
/// Lists .docx files and folders on the local filesystem.
/// Returns a single "Local filesystem" connection.
pub struct LocalBrowsableBackend;

impl LocalBrowsableBackend {
    pub fn new() -> Self {
        Self
    }
}

#[async_trait]
impl BrowsableBackend for LocalBrowsableBackend {
    async fn list_connections(&self, _tenant_id: &str) -> Result<Vec<ConnectionInfo>, StorageError> {
        Ok(vec![ConnectionInfo {
            connection_id: String::new(),
            source_type: SourceType::LocalFile,
            display_name: "Local filesystem".to_string(),
            provider_account_id: None,
        }])
    }

    async fn list_files(
        &self,
        _tenant_id: &str,
        _connection_id: &str,
        path: &str,
        page_token: Option<&str>,
        page_size: u32,
    ) -> Result<FileListResult, StorageError> {
        let dir_path = if path.is_empty() {
            // Default to home directory
            dirs::home_dir().unwrap_or_else(|| std::path::PathBuf::from("/"))
        } else {
            std::path::PathBuf::from(path)
        };

        if !dir_path.is_dir() {
            return Err(StorageError::Sync(format!(
                "Path is not a directory: {}",
                dir_path.display()
            )));
        }

        let mut entries: Vec<FileEntry> = Vec::new();

        let read_dir = std::fs::read_dir(&dir_path).map_err(|e| {
            StorageError::Sync(format!(
                "Failed to read directory {}: {}",
                dir_path.display(),
                e
            ))
        })?;

        for entry in read_dir {
            let entry = match entry {
                Ok(e) => e,
                Err(_) => continue,
            };

            let metadata = match entry.metadata() {
                Ok(m) => m,
                Err(_) => continue,
            };

            let name = entry.file_name().to_string_lossy().to_string();

            // Skip hidden files
            if name.starts_with('.') {
                continue;
            }

            let is_folder = metadata.is_dir();

            // Only include folders and .docx files
            if !is_folder && !name.to_lowercase().ends_with(".docx") {
                continue;
            }

            let full_path = entry.path().to_string_lossy().to_string();
            let modified_at = metadata
                .modified()
                .ok()
                .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
                .map(|d| d.as_secs() as i64)
                .unwrap_or(0);

            let mime_type = if is_folder {
                None
            } else {
                Some("application/vnd.openxmlformats-officedocument.wordprocessingml.document".to_string())
            };

            entries.push(FileEntry {
                name,
                path: full_path,
                file_id: None,
                is_folder,
                size_bytes: if is_folder { 0 } else { metadata.len() },
                modified_at,
                mime_type,
            });
        }

        // Sort: folders first, then by name
        entries.sort_by(|a, b| {
            match (a.is_folder, b.is_folder) {
                (true, false) => std::cmp::Ordering::Less,
                (false, true) => std::cmp::Ordering::Greater,
                _ => a.name.to_lowercase().cmp(&b.name.to_lowercase()),
            }
        });

        // Pagination: offset-based (page_token = offset as string)
        let offset: usize = page_token
            .and_then(|t| t.parse().ok())
            .unwrap_or(0);

        let page_size = page_size as usize;
        let total = entries.len();
        let end = std::cmp::min(offset + page_size, total);
        let page = entries[offset..end].to_vec();

        let next_page_token = if end < total {
            Some(end.to_string())
        } else {
            None
        };

        debug!(
            "Listed {} files in {} (total {}, offset {}, page_size {})",
            page.len(),
            dir_path.display(),
            total,
            offset,
            page_size
        );

        Ok(FileListResult {
            files: page,
            next_page_token,
        })
    }

    async fn download_file(
        &self,
        _tenant_id: &str,
        _connection_id: &str,
        path: &str,
        _file_id: Option<&str>,
    ) -> Result<Vec<u8>, StorageError> {
        let file_path = std::path::PathBuf::from(path);

        if !file_path.exists() {
            return Err(StorageError::Sync(format!(
                "File not found: {}",
                file_path.display()
            )));
        }

        std::fs::read(&file_path).map_err(|e| {
            StorageError::Sync(format!(
                "Failed to read file {}: {}",
                file_path.display(),
                e
            ))
        })
    }
}
