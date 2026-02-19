//! PAT token validation via Cloudflare D1 API.
//!
//! Validates Personal Access Tokens against a D1 database using the
//! Cloudflare REST API. Includes a moka cache for performance.

use std::sync::Arc;
use std::time::Duration;

use moka::future::Cache;
use reqwest::Client;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use tracing::{debug, warn};

use crate::error::{ProxyError, Result};

/// PAT token prefix expected by the system.
const TOKEN_PREFIX: &str = "dxs_";

/// Result of a PAT validation.
#[derive(Debug, Clone)]
pub struct PatValidationResult {
    pub tenant_id: String,
    pub pat_id: String,
}

/// Cached validation result (either success or known-invalid).
#[derive(Debug, Clone)]
enum CachedResult {
    Valid(PatValidationResult),
    Invalid,
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
    results: Vec<PatRecord>,
}

#[derive(Deserialize)]
struct D1Error {
    message: String,
}

/// PAT record from D1.
#[derive(Deserialize)]
struct PatRecord {
    id: String,
    #[serde(rename = "tenantId")]
    tenant_id: String,
    #[serde(rename = "expiresAt")]
    expires_at: Option<String>,
}

/// PAT validator with D1 backend and caching.
pub struct PatValidator {
    client: Client,
    account_id: String,
    api_token: String,
    database_id: String,
    cache: Cache<String, CachedResult>,
    negative_cache_ttl: Duration,
}

impl PatValidator {
    /// Create a new PAT validator.
    pub fn new(
        account_id: String,
        api_token: String,
        database_id: String,
        cache_ttl_secs: u64,
        negative_cache_ttl_secs: u64,
    ) -> Self {
        let cache = Cache::builder()
            .time_to_live(Duration::from_secs(cache_ttl_secs))
            .max_capacity(10_000)
            .build();

        Self {
            client: Client::new(),
            account_id,
            api_token,
            database_id,
            cache,
            negative_cache_ttl: Duration::from_secs(negative_cache_ttl_secs),
        }
    }

    /// Validate a PAT token.
    pub async fn validate(&self, token: &str) -> Result<PatValidationResult> {
        // Check token prefix
        if !token.starts_with(TOKEN_PREFIX) {
            return Err(ProxyError::InvalidToken);
        }

        // Compute token hash for cache key
        let token_hash = self.hash_token(token);

        // Check cache first
        if let Some(cached) = self.cache.get(&token_hash).await {
            match cached {
                CachedResult::Valid(result) => {
                    debug!("PAT validation cache hit (valid) for {}", &token[..12]);
                    return Ok(result);
                }
                CachedResult::Invalid => {
                    debug!("PAT validation cache hit (invalid) for {}", &token[..12]);
                    return Err(ProxyError::InvalidToken);
                }
            }
        }

        // Query D1
        debug!("PAT validation cache miss, querying D1 for {}", &token[..12]);
        match self.query_d1(&token_hash).await {
            Ok(Some(result)) => {
                self.cache
                    .insert(token_hash.clone(), CachedResult::Valid(result.clone()))
                    .await;
                Ok(result)
            }
            Ok(None) => {
                // Cache negative result with shorter TTL
                let cache_clone = self.cache.clone();
                let token_hash_clone = token_hash.clone();
                let ttl = self.negative_cache_ttl;
                tokio::spawn(async move {
                    cache_clone
                        .insert(token_hash_clone, CachedResult::Invalid)
                        .await;
                    tokio::time::sleep(ttl).await;
                    // Entry will auto-expire based on cache TTL
                });
                Err(ProxyError::InvalidToken)
            }
            Err(e) => {
                warn!("D1 query failed: {}", e);
                Err(e)
            }
        }
    }

    /// Hash a token using SHA-256.
    fn hash_token(&self, token: &str) -> String {
        let mut hasher = Sha256::new();
        hasher.update(token.as_bytes());
        hex::encode(hasher.finalize())
    }

    /// Query D1 for the PAT record.
    async fn query_d1(&self, token_hash: &str) -> Result<Option<PatValidationResult>> {
        let url = format!(
            "https://api.cloudflare.com/client/v4/accounts/{}/d1/database/{}/query",
            self.account_id, self.database_id
        );

        let query = D1QueryRequest {
            sql: "SELECT id, tenantId, expiresAt FROM personal_access_token WHERE tokenHash = ?1"
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
                .map(|errs| errs.into_iter().map(|e| e.message).collect::<Vec<_>>().join(", "))
                .unwrap_or_else(|| "Unknown D1 error".to_string());
            return Err(ProxyError::D1Error(error_msg));
        }

        // Extract the first result
        let record = d1_response
            .result
            .and_then(|mut results| results.pop())
            .and_then(|mut query_result| query_result.results.pop());

        match record {
            Some(pat) => {
                // Check expiration
                if let Some(expires_at) = &pat.expires_at {
                    if let Ok(expires) = chrono::DateTime::parse_from_rfc3339(expires_at) {
                        if expires < chrono::Utc::now() {
                            debug!("PAT {} is expired", &pat.id[..8]);
                            return Ok(None);
                        }
                    }
                }

                // Update last_used_at asynchronously
                self.update_last_used(&pat.id).await;

                Ok(Some(PatValidationResult {
                    tenant_id: pat.tenant_id,
                    pat_id: pat.id,
                }))
            }
            None => Ok(None),
        }
    }

    /// Update the last_used_at timestamp (fire-and-forget).
    async fn update_last_used(&self, pat_id: &str) {
        let url = format!(
            "https://api.cloudflare.com/client/v4/accounts/{}/d1/database/{}/query",
            self.account_id, self.database_id
        );

        let now = chrono::Utc::now().to_rfc3339();
        let query = D1QueryRequest {
            sql: "UPDATE personal_access_token SET lastUsedAt = ?1 WHERE id = ?2".to_string(),
            params: vec![now, pat_id.to_string()],
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
                warn!("Failed to update lastUsedAt: {}", e);
            }
        });
    }
}

/// Shared validator wrapped in Arc.
pub type SharedPatValidator = Arc<PatValidator>;

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_hash_token() {
        // Create a minimal validator just to test hash_token
        let validator = PatValidator::new(
            "test_account".to_string(),
            "test_token".to_string(),
            "test_db".to_string(),
            300,
            60,
        );

        let token = "dxs_abcdef1234567890";
        let hash = validator.hash_token(token);

        // Hash should be 64 hex chars (SHA-256)
        assert_eq!(hash.len(), 64);

        // Same token should produce same hash
        let hash2 = validator.hash_token(token);
        assert_eq!(hash, hash2);

        // Different token should produce different hash
        let hash3 = validator.hash_token("dxs_different");
        assert_ne!(hash, hash3);
    }

    #[tokio::test]
    async fn test_invalid_prefix() {
        let validator = PatValidator::new(
            "test_account".to_string(),
            "test_token".to_string(),
            "test_db".to_string(),
            300,
            60,
        );

        // Token without dxs_ prefix should fail
        let result = validator.validate("invalid_token").await;
        assert!(matches!(result, Err(ProxyError::InvalidToken)));
    }
}
