//! D1 client for reading OAuth connections via Cloudflare REST API.
//!
//! Mirrors the pattern from `docx-mcp-sse-proxy/src/auth.rs`.

use reqwest::Client;
use serde::{Deserialize, Serialize};
use tracing::warn;

/// An OAuth connection record from D1.
#[derive(Debug, Clone, Deserialize)]
#[allow(dead_code)]
pub struct OAuthConnection {
    pub id: String,
    #[serde(rename = "tenantId")]
    pub tenant_id: String,
    pub provider: String,
    #[serde(rename = "displayName")]
    pub display_name: String,
    #[serde(rename = "providerAccountId")]
    pub provider_account_id: Option<String>,
    #[serde(rename = "accessToken")]
    pub access_token: String,
    #[serde(rename = "refreshToken")]
    pub refresh_token: String,
    #[serde(rename = "tokenExpiresAt")]
    pub token_expires_at: Option<String>,
    pub scopes: String,
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
    results: Vec<serde_json::Value>,
}

#[derive(Deserialize)]
struct D1Error {
    message: String,
}

/// Client for querying D1 oauth_connection table via Cloudflare REST API.
pub struct D1Client {
    http: Client,
    account_id: String,
    api_token: String,
    database_id: String,
}

impl D1Client {
    pub fn new(account_id: String, api_token: String, database_id: String) -> Self {
        Self {
            http: Client::new(),
            account_id,
            api_token,
            database_id,
        }
    }

    fn query_url(&self) -> String {
        format!(
            "https://api.cloudflare.com/client/v4/accounts/{}/d1/database/{}/query",
            self.account_id, self.database_id
        )
    }

    /// Execute a D1 query and return raw results.
    async fn execute_query(
        &self,
        sql: &str,
        params: Vec<String>,
    ) -> anyhow::Result<Vec<serde_json::Value>> {
        let query = D1QueryRequest {
            sql: sql.to_string(),
            params,
        };

        let response = self
            .http
            .post(&self.query_url())
            .header("Authorization", format!("Bearer {}", self.api_token))
            .header("Content-Type", "application/json")
            .json(&query)
            .send()
            .await?;

        let status = response.status();
        let body = response.text().await?;

        if !status.is_success() {
            anyhow::bail!("D1 API returned {}: {}", status, body);
        }

        let d1_response: D1Response = serde_json::from_str(&body)?;

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
            anyhow::bail!("D1 query failed: {}", error_msg);
        }

        Ok(d1_response
            .result
            .and_then(|mut r| r.pop())
            .map(|qr| qr.results)
            .unwrap_or_default())
    }

    /// Get an OAuth connection by ID, scoped to the given tenant.
    pub async fn get_connection(
        &self,
        tenant_id: &str,
        connection_id: &str,
    ) -> anyhow::Result<Option<OAuthConnection>> {
        let results = self
            .execute_query(
                "SELECT id, tenantId, provider, displayName, providerAccountId, \
                 accessToken, refreshToken, tokenExpiresAt, scopes \
                 FROM oauth_connection WHERE id = ?1 AND tenantId = ?2",
                vec![connection_id.to_string(), tenant_id.to_string()],
            )
            .await?;

        match results.into_iter().next() {
            Some(row) => Ok(Some(serde_json::from_value(row)?)),
            None => Ok(None),
        }
    }

    /// List connections for a tenant and provider.
    #[allow(dead_code)]
    pub async fn list_connections(
        &self,
        tenant_id: &str,
        provider: &str,
    ) -> anyhow::Result<Vec<OAuthConnection>> {
        let results = self
            .execute_query(
                "SELECT id, tenantId, provider, displayName, providerAccountId, \
                 accessToken, refreshToken, tokenExpiresAt, scopes \
                 FROM oauth_connection WHERE tenantId = ?1 AND provider = ?2",
                vec![tenant_id.to_string(), provider.to_string()],
            )
            .await?;

        let mut connections = Vec::new();
        for row in results {
            match serde_json::from_value(row) {
                Ok(conn) => connections.push(conn),
                Err(e) => warn!("Failed to parse OAuth connection: {}", e),
            }
        }

        Ok(connections)
    }

    /// Update tokens after a refresh.
    pub async fn update_tokens(
        &self,
        connection_id: &str,
        access_token: &str,
        refresh_token: &str,
        expires_at: &str,
    ) -> anyhow::Result<()> {
        let now = chrono::Utc::now().to_rfc3339();
        self.execute_query(
            "UPDATE oauth_connection \
             SET accessToken = ?1, refreshToken = ?2, tokenExpiresAt = ?3, updatedAt = ?4 \
             WHERE id = ?5",
            vec![
                access_token.to_string(),
                refresh_token.to_string(),
                expires_at.to_string(),
                now,
                connection_id.to_string(),
            ],
        )
        .await?;

        Ok(())
    }
}
