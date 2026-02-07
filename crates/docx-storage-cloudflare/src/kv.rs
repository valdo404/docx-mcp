use std::time::Duration;

use docx_storage_core::StorageError;
use reqwest::{Client as HttpClient, RequestBuilder, Response, StatusCode};
use tracing::{debug, instrument, warn};

const MAX_RETRIES: u32 = 5;
const BASE_DELAY_MS: u64 = 200;

/// Cloudflare KV REST API client.
///
/// Uses the Cloudflare API v4 to interact with KV namespaces.
/// All requests use exponential backoff retry on 429 (rate limit).
pub struct KvClient {
    http_client: HttpClient,
    account_id: String,
    namespace_id: String,
    api_token: String,
}

impl KvClient {
    /// Create a new KV client.
    pub fn new(account_id: String, namespace_id: String, api_token: String) -> Self {
        Self {
            http_client: HttpClient::new(),
            account_id,
            namespace_id,
            api_token,
        }
    }

    /// Base URL for KV API.
    fn base_url(&self) -> String {
        format!(
            "https://api.cloudflare.com/client/v4/accounts/{}/storage/kv/namespaces/{}",
            self.account_id, self.namespace_id
        )
    }

    /// Send a request with exponential backoff retry on 429.
    async fn send_with_retry(
        &self,
        build_request: impl Fn() -> RequestBuilder,
    ) -> Result<Response, StorageError> {
        let mut delay = Duration::from_millis(BASE_DELAY_MS);

        for attempt in 0..=MAX_RETRIES {
            let response = build_request()
                .send()
                .await
                .map_err(|e| StorageError::Io(format!("KV request failed: {}", e)))?;

            if response.status() != StatusCode::TOO_MANY_REQUESTS {
                return Ok(response);
            }

            if attempt == MAX_RETRIES {
                let text = response.text().await.unwrap_or_default();
                return Err(StorageError::Io(format!(
                    "KV rate limited after {} retries: {}",
                    MAX_RETRIES, text
                )));
            }

            warn!(
                attempt = attempt + 1,
                delay_ms = delay.as_millis() as u64,
                "KV rate limited (429), retrying"
            );
            tokio::time::sleep(delay).await;
            delay *= 2;
        }

        unreachable!()
    }

    /// Get a value from KV.
    #[instrument(skip(self), level = "debug")]
    pub async fn get(&self, key: &str) -> Result<Option<String>, StorageError> {
        let url = format!("{}/values/{}", self.base_url(), urlencoding::encode(key));

        let response = self
            .send_with_retry(|| {
                self.http_client
                    .get(&url)
                    .header("Authorization", format!("Bearer {}", self.api_token))
            })
            .await?;

        let status = response.status();
        if status == StatusCode::NOT_FOUND {
            debug!("KV key not found: {}", key);
            return Ok(None);
        }

        if !status.is_success() {
            let text = response.text().await.unwrap_or_default();
            return Err(StorageError::Io(format!(
                "KV GET failed with status {}: {}",
                status, text
            )));
        }

        let value = response
            .text()
            .await
            .map_err(|e| StorageError::Io(format!("Failed to read KV response: {}", e)))?;

        debug!("KV GET {} ({} bytes)", key, value.len());
        Ok(Some(value))
    }

    /// Put a value to KV.
    #[instrument(skip(self, value), level = "debug", fields(value_len = value.len()))]
    pub async fn put(&self, key: &str, value: &str) -> Result<(), StorageError> {
        let url = format!("{}/values/{}", self.base_url(), urlencoding::encode(key));
        let value = value.to_string();

        let response = self
            .send_with_retry(|| {
                self.http_client
                    .put(&url)
                    .header("Authorization", format!("Bearer {}", self.api_token))
                    .header("Content-Type", "text/plain")
                    .body(value.clone())
            })
            .await?;

        let status = response.status();
        if !status.is_success() {
            let text = response.text().await.unwrap_or_default();
            return Err(StorageError::Io(format!(
                "KV PUT failed with status {}: {}",
                status, text
            )));
        }

        debug!("KV PUT {} ({} bytes)", key, value.len());
        Ok(())
    }

    /// Delete a value from KV.
    #[instrument(skip(self), level = "debug")]
    pub async fn delete(&self, key: &str) -> Result<bool, StorageError> {
        let url = format!("{}/values/{}", self.base_url(), urlencoding::encode(key));

        let response = self
            .send_with_retry(|| {
                self.http_client
                    .delete(&url)
                    .header("Authorization", format!("Bearer {}", self.api_token))
            })
            .await?;

        let status = response.status();
        if status == StatusCode::NOT_FOUND {
            return Ok(false);
        }

        if !status.is_success() {
            let text = response.text().await.unwrap_or_default();
            return Err(StorageError::Io(format!(
                "KV DELETE failed with status {}: {}",
                status, text
            )));
        }

        debug!("KV DELETE {}", key);
        Ok(true)
    }
}
