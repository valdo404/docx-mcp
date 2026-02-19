//! Google Drive API v3 client wrapper.
//!
//! Token is passed per-call by the caller (TokenManager resolves it from D1).

use reqwest::Client;
use serde::Deserialize;
use tracing::{debug, instrument};

/// Metadata returned by Google Drive API.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileMetadata {
    #[allow(dead_code)]
    pub id: String,
    #[serde(default)]
    pub size: Option<String>,
    #[serde(default)]
    pub modified_time: Option<String>,
    #[serde(default)]
    pub md5_checksum: Option<String>,
    #[serde(default)]
    pub head_revision_id: Option<String>,
}

/// A file entry from Drive API files.list.
#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DriveFileEntry {
    pub id: String,
    pub name: String,
    pub mime_type: String,
    #[serde(default)]
    pub size: Option<String>,
    #[serde(default)]
    pub modified_time: Option<String>,
}

/// Response from Drive API files.list.
#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct FileListResponse {
    #[serde(default)]
    files: Vec<DriveFileEntry>,
    #[serde(default)]
    next_page_token: Option<String>,
}

/// Google Drive API client (stateless — token provided per-call).
pub struct GDriveClient {
    http: Client,
}

impl GDriveClient {
    pub fn new() -> Self {
        Self {
            http: Client::new(),
        }
    }

    /// Get file metadata from Google Drive.
    #[instrument(skip(self, token), level = "debug")]
    pub async fn get_metadata(
        &self,
        token: &str,
        file_id: &str,
    ) -> anyhow::Result<Option<FileMetadata>> {
        let url = format!(
            "https://www.googleapis.com/drive/v3/files/{}?fields=id,size,modifiedTime,md5Checksum,headRevisionId",
            file_id
        );

        let resp = self.http.get(&url).bearer_auth(token).send().await?;

        if resp.status() == reqwest::StatusCode::NOT_FOUND {
            return Ok(None);
        }

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            anyhow::bail!("Google Drive API error {}: {}", status, body);
        }

        let metadata: FileMetadata = resp.json().await?;
        debug!("Got metadata for file {}: {:?}", file_id, metadata);
        Ok(Some(metadata))
    }

    /// Download file content from Google Drive.
    #[instrument(skip(self, token), level = "debug")]
    pub async fn download_file(
        &self,
        token: &str,
        file_id: &str,
    ) -> anyhow::Result<Option<Vec<u8>>> {
        let url = format!(
            "https://www.googleapis.com/drive/v3/files/{}?alt=media",
            file_id
        );

        let resp = self.http.get(&url).bearer_auth(token).send().await?;

        if resp.status() == reqwest::StatusCode::NOT_FOUND {
            return Ok(None);
        }

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            anyhow::bail!("Google Drive download error {}: {}", status, body);
        }

        let bytes = resp.bytes().await?;
        debug!("Downloaded {} bytes for file {}", bytes.len(), file_id);
        Ok(Some(bytes.to_vec()))
    }

    /// Upload (update) file content on Google Drive.
    #[instrument(skip(self, token, data), level = "debug", fields(data_len = data.len()))]
    pub async fn update_file(
        &self,
        token: &str,
        file_id: &str,
        data: &[u8],
    ) -> anyhow::Result<()> {
        let url = format!(
            "https://www.googleapis.com/upload/drive/v3/files/{}?uploadType=media",
            file_id
        );

        let resp = self
            .http
            .patch(&url)
            .bearer_auth(token)
            .header(
                "Content-Type",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            )
            .body(data.to_vec())
            .send()
            .await?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            anyhow::bail!("Google Drive upload error {}: {}", status, body);
        }

        debug!("Updated file {} ({} bytes)", file_id, data.len());
        Ok(())
    }

    /// Create a new file on Google Drive.
    /// Returns the new file's ID.
    #[instrument(skip(self, token, data), level = "debug", fields(data_len = data.len()))]
    pub async fn create_file(
        &self,
        token: &str,
        name: &str,
        parent_id: Option<&str>,
        data: &[u8],
    ) -> anyhow::Result<String> {
        // Build multipart/related body manually:
        // Google Drive v3 uploadType=multipart expects a multipart/related body
        // with a JSON metadata part and a file content part.
        let boundary = "docx_mcp_boundary";
        let mime_type =
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

        let parents_json = match parent_id {
            Some(pid) => format!(r#","parents":["{}"]"#, pid),
            None => String::new(),
        };

        let metadata = format!(
            r#"{{"name":"{}","mimeType":"{}"{}}}"#,
            name, mime_type, parents_json
        );

        let mut body = Vec::new();
        // Metadata part
        body.extend_from_slice(format!("--{}\r\n", boundary).as_bytes());
        body.extend_from_slice(b"Content-Type: application/json; charset=UTF-8\r\n\r\n");
        body.extend_from_slice(metadata.as_bytes());
        body.extend_from_slice(b"\r\n");
        // File content part
        body.extend_from_slice(format!("--{}\r\n", boundary).as_bytes());
        body.extend_from_slice(format!("Content-Type: {}\r\n\r\n", mime_type).as_bytes());
        body.extend_from_slice(data);
        body.extend_from_slice(b"\r\n");
        // Closing boundary
        body.extend_from_slice(format!("--{}--\r\n", boundary).as_bytes());

        let url =
            "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id";

        let resp = self
            .http
            .post(url)
            .bearer_auth(token)
            .header(
                "Content-Type",
                format!("multipart/related; boundary={}", boundary),
            )
            .body(body)
            .send()
            .await?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            anyhow::bail!("Google Drive create error {}: {}", status, body);
        }

        #[derive(Deserialize)]
        struct CreateResponse {
            id: String,
        }
        let created: CreateResponse = resp.json().await?;
        debug!(
            "Created file '{}' with ID {} ({} bytes)",
            name,
            created.id,
            data.len()
        );
        Ok(created.id)
    }

    /// List files in a folder on Google Drive.
    /// Only returns .docx files and folders.
    #[instrument(skip(self, token), level = "debug")]
    pub async fn list_files(
        &self,
        token: &str,
        parent_id: &str,
        page_token: Option<&str>,
        page_size: u32,
    ) -> anyhow::Result<(Vec<DriveFileEntry>, Option<String>)> {
        let query = format!(
            "'{}' in parents and trashed=false and (mimeType='application/vnd.google-apps.folder' or mimeType='application/vnd.openxmlformats-officedocument.wordprocessingml.document')",
            parent_id
        );

        let mut request = self
            .http
            .get("https://www.googleapis.com/drive/v3/files")
            .bearer_auth(token)
            .query(&[
                ("q", query.as_str()),
                ("fields", "nextPageToken,files(id,name,mimeType,size,modifiedTime)"),
                ("pageSize", &page_size.to_string()),
                ("orderBy", "folder,name"),
            ]);

        if let Some(pt) = page_token {
            request = request.query(&[("pageToken", pt)]);
        }

        let resp = request.send().await?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            anyhow::bail!("Google Drive list error {}: {}", status, body);
        }

        let list_response: FileListResponse = resp.json().await?;
        debug!(
            "Listed {} files in folder {}",
            list_response.files.len(),
            parent_id
        );

        Ok((list_response.files, list_response.next_page_token))
    }
}
