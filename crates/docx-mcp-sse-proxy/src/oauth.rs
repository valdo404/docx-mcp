//! OAuth access token validation via Cloudflare D1 API.
//!
//! Validates opaque OAuth access tokens (oat_...) against the D1 database
//! using the Cloudflare REST API. Always queries D1 directly (no cache) so that
//! token revocation takes effect immediately.

use std::sync::Arc;

use reqwest::Client;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tracing::{debug, warn};

use crate::error::{ProxyError, Result};

/// OAuth access token prefix.
const TOKEN_PREFIX: &str = "oat_";

/// Result of an OAuth token validation.
#[derive(Debug, Clone)]
pub struct OAuthValidationResult {
    pub tenant_id: String,
    #[allow(dead_code)]
    pub scope: String,
}

/// D1 query request body.
#[derive(Serialize)]
struct D1QueryRequest {
    sql: String,
    params: Vec<String>,
}

/// D1 API response structure.
#[derive(Deserialize)]
struct D1Response {
    success: bool,
    result: Option<Vec<D1QueryResult>>,
    errors: Option<Vec<D1Error>>,
}

#[derive(Deserialize)]
struct D1QueryResult {
    results: Vec<OAuthTokenRecord>,
}

#[derive(Deserialize)]
struct D1Error {
    message: String,
}

/// OAuth access token record from D1.
#[derive(Deserialize)]
struct OAuthTokenRecord {
    id: String,
    #[serde(rename = "tenantId")]
    tenant_id: String,
    scope: String,
    #[serde(rename = "expiresAt")]
    expires_at: String,
}

/// OAuth token validator with D1 backend.
pub struct OAuthValidator {
    client: Client,
    account_id: String,
    api_token: String,
    database_id: String,
}

impl OAuthValidator {
    /// Create a new OAuth validator.
    pub fn new(
        account_id: String,
        api_token: String,
        database_id: String,
        _cache_ttl_secs: u64,
        _negative_cache_ttl_secs: u64,
    ) -> Self {
        Self {
            client: Client::new(),
            account_id,
            api_token,
            database_id,
        }
    }

    /// Check if a token has the OAuth prefix.
    pub fn is_oauth_token(token: &str) -> bool {
        token.starts_with(TOKEN_PREFIX)
    }

    /// Validate an OAuth access token.
    pub async fn validate(&self, token: &str) -> Result<OAuthValidationResult> {
        if !token.starts_with(TOKEN_PREFIX) {
            return Err(ProxyError::InvalidToken);
        }

        let token_hash = self.hash_token(token);

        // Always validate against D1 (no cache for OAuth tokens — revocation must be immediate)
        debug!(
            "Validating OAuth token against D1 for {}",
            &token[..12.min(token.len())]
        );
        match self.query_d1(&token_hash).await {
            Ok(Some(result)) => Ok(result),
            Ok(None) => Err(ProxyError::InvalidToken),
            Err(e) => {
                warn!("D1 query failed for OAuth token: {}", e);
                Err(e)
            }
        }
    }

    fn hash_token(&self, token: &str) -> String {
        let mut hasher = Sha256::new();
        hasher.update(token.as_bytes());
        hex::encode(hasher.finalize())
    }

    async fn query_d1(&self, token_hash: &str) -> Result<Option<OAuthValidationResult>> {
        let url = format!(
            "https://api.cloudflare.com/client/v4/accounts/{}/d1/database/{}/query",
            self.account_id, self.database_id
        );

        let query = D1QueryRequest {
            sql: "SELECT id, tenantId, scope, expiresAt FROM oauth_access_token WHERE tokenHash = ?1"
                .to_string(),
            params: vec![token_hash.to_string()],
        };

        let response = self
            .client
            .post(&url)
            .header("Authorization", format!("Bearer {}", self.api_token))
            .header("Content-Type", "application/json")
            .json(&query)
            .send()
            .await
            .map_err(|e| ProxyError::D1Error(e.to_string()))?;

        let status = response.status();
        let body = response
            .text()
            .await
            .map_err(|e| ProxyError::D1Error(e.to_string()))?;

        if !status.is_success() {
            return Err(ProxyError::D1Error(format!(
                "D1 API returned {}: {}",
                status, body
            )));
        }

        let d1_response: D1Response =
            serde_json::from_str(&body).map_err(|e| ProxyError::D1Error(e.to_string()))?;

        if !d1_response.success {
            let error_msg = d1_response
                .errors
                .map(|errs| {
                    errs.into_iter()
                        .map(|e| e.message)
                        .collect::<Vec<_>>()
                        .join(", ")
                })
                .unwrap_or_else(|| "Unknown D1 error".to_string());
            return Err(ProxyError::D1Error(error_msg));
        }

        let record = d1_response
            .result
            .and_then(|mut results| results.pop())
            .and_then(|mut query_result| query_result.results.pop());

        match record {
            Some(token_record) => {
                // Check expiration
                if let Ok(expires) = chrono::DateTime::parse_from_rfc3339(&token_record.expires_at)
                {
                    if expires < chrono::Utc::now() {
                        debug!("OAuth token {} is expired", &token_record.id[..8]);
                        return Ok(None);
                    }
                }

                // Update last_used_at asynchronously
                self.update_last_used(&token_record.id).await;

                Ok(Some(OAuthValidationResult {
                    tenant_id: token_record.tenant_id,
                    scope: token_record.scope,
                }))
            }
            None => Ok(None),
        }
    }

    async fn update_last_used(&self, token_id: &str) {
        let url = format!(
            "https://api.cloudflare.com/client/v4/accounts/{}/d1/database/{}/query",
            self.account_id, self.database_id
        );

        let now = chrono::Utc::now().to_rfc3339();
        let query = D1QueryRequest {
            sql: "UPDATE oauth_access_token SET lastUsedAt = ?1 WHERE id = ?2".to_string(),
            params: vec![now, token_id.to_string()],
        };

        let client = self.client.clone();
        let api_token = self.api_token.clone();
        tokio::spawn(async move {
            if let Err(e) = client
                .post(&url)
                .header("Authorization", format!("Bearer {}", api_token))
                .header("Content-Type", "application/json")
                .json(&query)
                .send()
                .await
            {
                warn!("Failed to update OAuth token lastUsedAt: {}", e);
            }
        });
    }
}

pub type SharedOAuthValidator = Arc<OAuthValidator>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_is_oauth_token() {
        assert!(OAuthValidator::is_oauth_token("oat_abcdef1234567890"));
        assert!(!OAuthValidator::is_oauth_token("dxs_abcdef1234567890"));
        assert!(!OAuthValidator::is_oauth_token("invalid"));
    }

    #[tokio::test]
    async fn test_invalid_prefix() {
        let validator = OAuthValidator::new(
            "test_account".to_string(),
            "test_token".to_string(),
            "test_db".to_string(),
            300,
            60,
        );

        let result = validator.validate("invalid_token").await;
        assert!(matches!(result, Err(ProxyError::InvalidToken)));
    }
}
