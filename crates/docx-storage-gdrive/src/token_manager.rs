//! Per-connection OAuth token manager with automatic refresh.
//!
//! Reads tokens from D1 via `D1Client`, caches them in-memory,
//! and refreshes via Google OAuth2 when expired.

use std::sync::Arc;

use dashmap::DashMap;
use tracing::{debug, info, warn};

use crate::d1_client::D1Client;

/// Cached token with expiration.
#[derive(Debug, Clone)]
struct CachedToken {
    access_token: String,
    expires_at: Option<chrono::DateTime<chrono::Utc>>,
}

impl CachedToken {
    fn is_expired(&self) -> bool {
        match self.expires_at {
            Some(exp) => chrono::Utc::now() >= exp - chrono::Duration::minutes(5),
            None => true, // No expiration info → always refresh to be safe
        }
    }
}

/// Manages OAuth tokens per-connection with caching and automatic refresh.
pub struct TokenManager {
    d1: Arc<D1Client>,
    http: reqwest::Client,
    google_client_id: String,
    google_client_secret: String,
    cache: DashMap<String, CachedToken>,
}

impl TokenManager {
    pub fn new(
        d1: Arc<D1Client>,
        google_client_id: String,
        google_client_secret: String,
    ) -> Self {
        Self {
            d1,
            http: reqwest::Client::new(),
            google_client_id,
            google_client_secret,
            cache: DashMap::new(),
        }
    }

    /// Get a valid access token for a connection, refreshing if necessary.
    pub async fn get_valid_token(&self, connection_id: &str) -> anyhow::Result<String> {
        // 1. Check cache
        if let Some(cached) = self.cache.get(connection_id) {
            if !cached.is_expired() {
                debug!("Token cache hit for connection {}", connection_id);
                return Ok(cached.access_token.clone());
            }
            debug!("Token expired for connection {}, refreshing", connection_id);
        }

        // 2. Read from D1
        let conn = self
            .d1
            .get_connection(connection_id)
            .await?
            .ok_or_else(|| anyhow::anyhow!("OAuth connection not found: {}", connection_id))?;

        // 3. Check if token from D1 is still valid
        let expires_at = conn
            .token_expires_at
            .as_ref()
            .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
            .map(|dt| dt.with_timezone(&chrono::Utc));

        let cached = CachedToken {
            access_token: conn.access_token.clone(),
            expires_at,
        };

        if !cached.is_expired() {
            self.cache
                .insert(connection_id.to_string(), cached.clone());
            return Ok(cached.access_token);
        }

        // 4. Refresh the token
        info!(
            "Refreshing OAuth token for connection {} ({})",
            connection_id, conn.display_name
        );

        let new_token = self
            .refresh_token(&conn.refresh_token, connection_id)
            .await?;

        Ok(new_token)
    }

    /// Refresh an OAuth token using the refresh_token grant.
    async fn refresh_token(
        &self,
        refresh_token: &str,
        connection_id: &str,
    ) -> anyhow::Result<String> {
        let resp = self
            .http
            .post("https://oauth2.googleapis.com/token")
            .form(&[
                ("client_id", self.google_client_id.as_str()),
                ("client_secret", self.google_client_secret.as_str()),
                ("refresh_token", refresh_token),
                ("grant_type", "refresh_token"),
            ])
            .send()
            .await?;

        if !resp.status().is_success() {
            let status = resp.status();
            let body = resp.text().await.unwrap_or_default();
            anyhow::bail!(
                "OAuth token refresh failed for connection {}: {} {}",
                connection_id,
                status,
                body
            );
        }

        #[derive(serde::Deserialize)]
        struct RefreshResponse {
            access_token: String,
            expires_in: u64,
            refresh_token: Option<String>,
        }

        let token_resp: RefreshResponse = resp.json().await?;

        let expires_at = chrono::Utc::now() + chrono::Duration::seconds(token_resp.expires_in as i64);
        let expires_at_str = expires_at.to_rfc3339();

        // Google may rotate the refresh token
        let new_refresh = token_resp
            .refresh_token
            .as_deref()
            .unwrap_or(refresh_token);

        // Update D1
        if let Err(e) = self
            .d1
            .update_tokens(
                connection_id,
                &token_resp.access_token,
                new_refresh,
                &expires_at_str,
            )
            .await
        {
            warn!(
                "Failed to update tokens in D1 for connection {}: {}",
                connection_id, e
            );
        }

        // Update cache
        self.cache.insert(
            connection_id.to_string(),
            CachedToken {
                access_token: token_resp.access_token.clone(),
                expires_at: Some(expires_at),
            },
        );

        info!(
            "Refreshed OAuth token for connection {}, expires at {}",
            connection_id, expires_at_str
        );

        Ok(token_resp.access_token)
    }
}
