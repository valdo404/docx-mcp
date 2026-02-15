//! Google Drive API v3 client wrapper.
//!
//! Token is passed per-call by the caller (TokenManager resolves it from D1).
//! URI format: `gdrive://{connection_id}/{file_id}`

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
    #[allow(dead_code)]
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
}

/// Result of parsing a `gdrive://` URI.
#[derive(Debug, Clone, PartialEq)]
pub struct GDriveUri {
    pub connection_id: String,
    pub file_id: String,
}

/// Parse a `gdrive://{connection_id}/{file_id}` URI.
pub fn parse_gdrive_uri(uri: &str) -> Option<GDriveUri> {
    let rest = uri.strip_prefix("gdrive://")?;
    let (connection_id, file_id) = rest.split_once('/')?;
    if connection_id.is_empty() || file_id.is_empty() {
        return None;
    }
    Some(GDriveUri {
        connection_id: connection_id.to_string(),
        file_id: file_id.to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_gdrive_uri() {
        assert_eq!(
            parse_gdrive_uri("gdrive://conn-123/abc456"),
            Some(GDriveUri {
                connection_id: "conn-123".to_string(),
                file_id: "abc456".to_string(),
            })
        );
        assert_eq!(parse_gdrive_uri("gdrive://abc123"), None);
        assert_eq!(parse_gdrive_uri("gdrive:///file"), None);
        assert_eq!(parse_gdrive_uri("gdrive://conn/"), None);
        assert_eq!(parse_gdrive_uri("s3://bucket/key"), None);
        assert_eq!(parse_gdrive_uri(""), None);
    }
}
