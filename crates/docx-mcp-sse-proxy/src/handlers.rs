//! HTTP handlers for the reverse proxy.
//!
//! Implements:
//! - POST/GET/DELETE /mcp{/*rest} - Forward to .NET MCP backend
//! - GET /health - Health check endpoint
//!
//! Session recovery: when the backend returns 404 (session lost after restart),
//! the proxy transparently re-initializes the MCP session and retries the request.

use std::sync::Arc;
use std::time::Duration;

use axum::body::Body;
use axum::extract::{Request, State};
use axum::http::{header, HeaderMap, HeaderValue, Method};
use axum::response::{IntoResponse, Response};
use axum::Json;
use axum::body::Bytes;
use reqwest::Client as HttpClient;
use serde::Serialize;
use serde_json::Value;
use tracing::{debug, info, warn};

use crate::auth::SharedPatValidator;
use crate::error::{set_resource_metadata_url, ProxyError};
use crate::oauth::{OAuthValidator, SharedOAuthValidator};
use crate::session::SessionRegistry;

/// Application state shared across handlers.
#[derive(Clone)]
pub struct AppState {
    pub validator: Option<SharedPatValidator>,
    pub oauth_validator: Option<SharedOAuthValidator>,
    pub backend_url: String,
    pub http_client: HttpClient,
    pub sessions: Arc<SessionRegistry>,
    pub resource_url: Option<String>,
    pub auth_server_url: Option<String>,
}

/// Health check response.
#[derive(Serialize)]
pub struct HealthResponse {
    pub healthy: bool,
    pub version: &'static str,
    pub auth_enabled: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub backend_healthy: Option<bool>,
}

/// GET /health - Liveness check (proxy only, no upstream dependency).
pub async fn health_handler(State(state): State<AppState>) -> Json<HealthResponse> {
    Json(HealthResponse {
        healthy: true,
        version: env!("CARGO_PKG_VERSION"),
        auth_enabled: state.validator.is_some(),
        backend_healthy: None,
    })
}

/// GET /upstream-health - Deep health check (proxy + upstream mcp-http).
pub async fn upstream_health_handler(State(state): State<AppState>) -> Json<HealthResponse> {
    let backend_ok = state
        .http_client
        .get(format!("{}/health", state.backend_url))
        .timeout(Duration::from_secs(3))
        .send()
        .await
        .map(|r| r.status().is_success())
        .unwrap_or(false);

    Json(HealthResponse {
        healthy: backend_ok,
        version: env!("CARGO_PKG_VERSION"),
        auth_enabled: state.validator.is_some(),
        backend_healthy: Some(backend_ok),
    })
}

/// GET /.well-known/oauth-protected-resource - OAuth 2.0 Protected Resource Metadata.
pub async fn oauth_metadata_handler(
    State(state): State<AppState>,
) -> std::result::Result<Response, ProxyError> {
    let resource = state
        .resource_url
        .as_deref()
        .unwrap_or("https://mcp.docx.lapoule.dev");
    let auth_server = state
        .auth_server_url
        .as_deref()
        .unwrap_or("https://docx.lapoule.dev");

    let metadata = serde_json::json!({
        "resource": resource,
        "authorization_servers": [auth_server],
        "bearer_methods_supported": ["header"],
        "scopes_supported": ["mcp:tools"]
    });

    let body = serde_json::to_string(&metadata)
        .map_err(|e| ProxyError::Internal(format!("Failed to serialize metadata: {}", e)))?;

    let response = Response::builder()
        .status(axum::http::StatusCode::OK)
        .header(header::CONTENT_TYPE, "application/json")
        .header(header::CACHE_CONTROL, "public, max-age=3600")
        .body(Body::from(body))
        .map_err(|e| ProxyError::Internal(format!("Failed to build response: {}", e)))?;

    Ok(response)
}

/// Extract Bearer token from Authorization header.
fn extract_bearer_token(headers: &HeaderMap) -> Option<&str> {
    headers
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
}

/// Maximum number of retry attempts for transient backend errors.
/// Budget: 500+1000+2000+4000+5000×4 = ~27.5s (covers .NET cold start).
const MAX_RETRIES: u32 = 8;
/// Initial backoff delay in milliseconds.
const INITIAL_BACKOFF_MS: u64 = 500;
/// Maximum backoff delay in milliseconds (cap for exponential backoff).
const MAX_BACKOFF_MS: u64 = 5_000;
/// Timeout for each individual backend request.
const FORWARD_TIMEOUT_SECS: u64 = 30;

/// Headers to forward from the client to the backend.
const FORWARD_HEADERS: &[header::HeaderName] = &[header::CONTENT_TYPE, header::ACCEPT];

/// MCP-specific header for session tracking.
const MCP_SESSION_ID: &str = "mcp-session-id";
/// SSE resumption header (client sends this to resume from a specific event).
const LAST_EVENT_ID: &str = "last-event-id";
const X_TENANT_ID: &str = "x-tenant-id";

/// Check if a JSON body is an MCP `initialize` request.
fn is_initialize_request(body: &[u8]) -> bool {
    if body.is_empty() {
        return false;
    }
    // Fast path: check for the method string before full parse
    let Ok(val) = serde_json::from_slice::<Value>(body) else {
        return false;
    };
    val.get("method").and_then(|m| m.as_str()) == Some("initialize")
}

/// Outcome of forwarding a request to the backend.
struct BackendResponse {
    status: axum::http::StatusCode,
    headers: HeaderMap,
    is_sse: bool,
    /// The backend response (not yet consumed). Only available for non-SSE responses.
    body_bytes: Option<Bytes>,
    /// For SSE responses, we keep the raw reqwest response to stream from.
    raw_response: Option<reqwest::Response>,
}

/// Check if an HTTP status code is retryable (transient server error).
fn is_retryable_status(status: axum::http::StatusCode) -> bool {
    matches!(status.as_u16(), 502 | 503)
}

/// Check if a proxy error is retryable (connection errors).
fn is_retryable_error(err: &ProxyError) -> bool {
    match err {
        ProxyError::BackendError(msg) => {
            // Network-level failures: reqwest wraps the root cause in
            // "error sending request for url (...)" which may NOT contain
            // the inner "Connection refused" text depending on the platform.
            msg.contains("connection refused")
                || msg.contains("Connection refused")
                || msg.contains("connect error")
                || msg.contains("dns error")
                || msg.contains("timed out")
                || msg.contains("error sending request")
                || msg.contains("connection reset")
                || msg.contains("broken pipe")
        }
        _ => false,
    }
}

/// Send a request to the backend with retry for transient errors.
#[allow(clippy::too_many_arguments)]
async fn send_to_backend_with_retry(
    http_client: &HttpClient,
    backend_url: &str,
    method: &Method,
    path: &str,
    query: &str,
    client_headers: &HeaderMap,
    tenant_id: &str,
    session_id_override: Option<&str>,
    body: Bytes,
) -> Result<BackendResponse, ProxyError> {
    let started = std::time::Instant::now();
    let mut last_error = None;
    for attempt in 0..=MAX_RETRIES {
        if attempt > 0 {
            let delay = (INITIAL_BACKOFF_MS * 2u64.pow(attempt - 1)).min(MAX_BACKOFF_MS);
            warn!(
                "Retrying backend request ({}/{}) after {}ms",
                attempt, MAX_RETRIES, delay
            );
            tokio::time::sleep(Duration::from_millis(delay)).await;
        }
        match send_to_backend(
            http_client,
            backend_url,
            method,
            path,
            query,
            client_headers,
            tenant_id,
            session_id_override,
            body.clone(),
        )
        .await
        {
            Ok(resp) if is_retryable_status(resp.status) && attempt < MAX_RETRIES => {
                warn!(
                    "Backend returned {}, will retry ({}/{})",
                    resp.status,
                    attempt + 1,
                    MAX_RETRIES
                );
                last_error = Some(ProxyError::BackendUnavailable(
                    format!("Backend returned {}", resp.status),
                    attempt + 1,
                ));
            }
            Ok(resp) => {
                if attempt > 0 {
                    info!(
                        "Backend request succeeded after {} attempts in {:.1}s",
                        attempt + 1,
                        started.elapsed().as_secs_f64()
                    );
                }
                return Ok(resp);
            }
            Err(e) if is_retryable_error(&e) && attempt < MAX_RETRIES => {
                warn!(
                    "Backend error: {}, will retry ({}/{})",
                    e,
                    attempt + 1,
                    MAX_RETRIES
                );
                last_error = Some(e);
            }
            // Last attempt failed with retryable error → wrap as BackendUnavailable (503)
            Err(e) if is_retryable_error(&e) => {
                return Err(ProxyError::BackendUnavailable(
                    e.to_string(),
                    MAX_RETRIES,
                ));
            }
            Err(e) => return Err(e),
        }
    }
    warn!(
        "All {} retries exhausted after {:.1}s",
        MAX_RETRIES,
        started.elapsed().as_secs_f64()
    );
    Err(last_error.map_or_else(
        || ProxyError::BackendUnavailable("All retries exhausted".into(), MAX_RETRIES),
        |e| ProxyError::BackendUnavailable(e.to_string(), MAX_RETRIES),
    ))
}

/// Send a request to the backend, returning status + headers + body.
#[allow(clippy::too_many_arguments)]
async fn send_to_backend(
    http_client: &HttpClient,
    backend_url: &str,
    method: &Method,
    path: &str,
    query: &str,
    client_headers: &HeaderMap,
    tenant_id: &str,
    session_id_override: Option<&str>,
    body: Bytes,
) -> Result<BackendResponse, ProxyError> {
    let url = format!("{}{}{}", backend_url, path, query);

    debug!("Forwarding {} {} -> {}", method, path, url);

    let mut req = http_client.request(
        reqwest::Method::from_bytes(method.as_str().as_bytes())
            .map_err(|e| ProxyError::Internal(format!("Invalid method: {}", e)))?,
        &url,
    );

    // Forward relevant headers
    for header_name in FORWARD_HEADERS {
        if let Some(value) = client_headers.get(header_name) {
            if let Ok(s) = value.to_str() {
                req = req.header(header_name.as_str(), s);
            }
        }
    }

    // Use override session ID if provided, otherwise forward client's
    if let Some(sid) = session_id_override {
        req = req.header(MCP_SESSION_ID, sid);
    } else if let Some(value) = client_headers.get(MCP_SESSION_ID) {
        if let Ok(s) = value.to_str() {
            req = req.header(MCP_SESSION_ID, s);
        }
    }

    // Forward Last-Event-ID for SSE stream resumption
    if let Some(value) = client_headers.get(LAST_EVENT_ID) {
        if let Ok(s) = value.to_str() {
            req = req.header(LAST_EVENT_ID, s);
        }
    }

    // Inject tenant ID
    req = req.header(X_TENANT_ID, tenant_id);

    // Forward body
    if !body.is_empty() {
        debug!(
            "Request body ({} bytes): {}",
            body.len(),
            String::from_utf8_lossy(&body[..body.len().min(2048)])
        );
        req = req.body(body);
    }

    // Send with timeout
    let resp = req
        .timeout(Duration::from_secs(FORWARD_TIMEOUT_SECS))
        .send()
        .await
        .map_err(|e| ProxyError::BackendError(format!("Failed to reach backend: {}", e)))?;

    let status = axum::http::StatusCode::from_u16(resp.status().as_u16())
        .unwrap_or(axum::http::StatusCode::BAD_GATEWAY);

    debug!(
        "Backend response: {} (content-type: {:?})",
        status,
        resp.headers().get("content-type")
    );

    let mut response_headers = HeaderMap::new();

    // Forward content-type
    if let Some(ct) = resp.headers().get(reqwest::header::CONTENT_TYPE) {
        if let Ok(v) = HeaderValue::from_bytes(ct.as_bytes()) {
            response_headers.insert(header::CONTENT_TYPE, v);
        }
    }

    // Forward Mcp-Session-Id from backend
    if let Some(session_id) = resp.headers().get(MCP_SESSION_ID) {
        if let Ok(v) = HeaderValue::from_bytes(session_id.as_bytes()) {
            response_headers.insert(
                header::HeaderName::from_static("mcp-session-id"),
                v,
            );
        }
    }

    let is_sse = resp
        .headers()
        .get(reqwest::header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .map(|v| v.contains("text/event-stream"))
        .unwrap_or(false);

    if is_sse {
        Ok(BackendResponse {
            status,
            headers: response_headers,
            is_sse: true,
            body_bytes: None,
            raw_response: Some(resp),
        })
    } else {
        let body_bytes = resp
            .bytes()
            .await
            .map_err(|e| ProxyError::BackendError(format!("Failed to read backend response: {}", e)))?;

        debug!(
            "Response body ({} bytes): {}",
            body_bytes.len(),
            String::from_utf8_lossy(&body_bytes[..body_bytes.len().min(2048)])
        );

        Ok(BackendResponse {
            status,
            headers: response_headers,
            is_sse: false,
            body_bytes: Some(body_bytes),
            raw_response: None,
        })
    }
}

/// Convert a BackendResponse into an axum Response.
fn into_response(br: BackendResponse) -> Result<Response, ProxyError> {
    if br.is_sse {
        let raw = br.raw_response.expect("SSE response must have raw_response");
        debug!("Starting SSE stream forwarding");
        let stream = raw.bytes_stream();
        let body = Body::from_stream(stream);

        let mut response = Response::builder()
            .status(br.status)
            .body(body)
            .map_err(|e| ProxyError::Internal(format!("Failed to build response: {}", e)))?;

        *response.headers_mut() = br.headers;
        response.headers_mut().insert(
            header::CONTENT_TYPE,
            HeaderValue::from_static("text/event-stream"),
        );

        Ok(response)
    } else {
        let body_bytes = br.body_bytes.unwrap_or_default();
        let mut response = (br.status, body_bytes).into_response();

        for (name, value) in br.headers {
            if let Some(name) = name {
                response.headers_mut().insert(name, value);
            }
        }

        Ok(response)
    }
}

/// Extract the Mcp-Session-Id value from response headers.
fn extract_session_id_from_headers(headers: &HeaderMap) -> Option<String> {
    headers
        .get("mcp-session-id")
        .and_then(|v| v.to_str().ok())
        .map(|s| s.to_string())
}

/// Perform a synthetic MCP initialize + notifications/initialized handshake
/// against the backend to obtain a new session ID.
async fn reinitialize_session(
    http_client: &HttpClient,
    backend_url: &str,
    tenant_id: &str,
) -> Result<String, ProxyError> {
    info!("Sending synthetic initialize to backend for tenant {}", tenant_id);

    let init_body = serde_json::json!({
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {
                "name": "docx-mcp-sse-proxy",
                "version": env!("CARGO_PKG_VERSION")
            }
        }
    });

    let url = format!("{}/mcp", backend_url);

    let resp = http_client
        .post(&url)
        .header("Content-Type", "application/json")
        .header(X_TENANT_ID, tenant_id)
        .json(&init_body)
        .send()
        .await
        .map_err(|e| {
            ProxyError::SessionRecoveryFailed(format!("Initialize request failed: {}", e))
        })?;

    if !resp.status().is_success() {
        return Err(ProxyError::SessionRecoveryFailed(format!(
            "Initialize returned {}",
            resp.status()
        )));
    }

    let new_session_id = resp
        .headers()
        .get(MCP_SESSION_ID)
        .and_then(|v| v.to_str().ok())
        .map(|s| s.to_string())
        .ok_or_else(|| {
            ProxyError::SessionRecoveryFailed(
                "Initialize response missing Mcp-Session-Id header".into(),
            )
        })?;

    // Read the init response body (we don't need it, but must consume it)
    let _ = resp.bytes().await;

    // Send notifications/initialized
    let notif_body = serde_json::json!({
        "jsonrpc": "2.0",
        "method": "notifications/initialized"
    });

    let notif_resp = http_client
        .post(&url)
        .header("Content-Type", "application/json")
        .header(MCP_SESSION_ID, &new_session_id)
        .header(X_TENANT_ID, tenant_id)
        .json(&notif_body)
        .send()
        .await
        .map_err(|e| {
            ProxyError::SessionRecoveryFailed(format!(
                "notifications/initialized request failed: {}",
                e
            ))
        })?;

    if !notif_resp.status().is_success() {
        warn!(
            "notifications/initialized returned {} (non-fatal)",
            notif_resp.status()
        );
    }

    // Consume body
    let _ = notif_resp.bytes().await;

    info!(
        "Session recovered for tenant {}: new session ID {}",
        tenant_id, new_session_id
    );

    Ok(new_session_id)
}

/// Forward any request on /mcp (POST, GET, DELETE) to the .NET backend.
///
/// This is a transparent reverse proxy with session recovery:
/// 1. Validates PAT → extracts tenant_id
/// 2. Forwards the request to {MCP_BACKEND_URL}/mcp with X-Tenant-Id header
/// 3. If backend returns 404 (session lost), transparently re-initializes and retries
/// 4. Streams the response back (SSE or JSON)
pub async fn mcp_forward_handler(
    State(state): State<AppState>,
    req: Request,
) -> std::result::Result<Response, ProxyError> {
    // --- 1. Authenticate (PAT or OAuth) ---
    // Set resource metadata URL for WWW-Authenticate header on 401
    set_resource_metadata_url(state.resource_url.clone());

    let tenant_id = if state.validator.is_some() || state.oauth_validator.is_some() {
        let token = extract_bearer_token(req.headers()).ok_or(ProxyError::Unauthorized)?;

        if OAuthValidator::is_oauth_token(token) {
            // Try OAuth token (oat_...)
            let oauth_validator = state
                .oauth_validator
                .as_ref()
                .ok_or(ProxyError::InvalidToken)?;
            let validation = oauth_validator.validate(token).await?;
            info!(
                "Authenticated request for tenant {} (OAuth: {}...)",
                validation.tenant_id,
                &token[..12.min(token.len())]
            );
            validation.tenant_id
        } else {
            // Try PAT token (dxs_...)
            let pat_validator = state.validator.as_ref().ok_or(ProxyError::InvalidToken)?;
            let validation = pat_validator.validate(token).await?;
            info!(
                "Authenticated request for tenant {} (PAT: {}...)",
                validation.tenant_id,
                &validation.pat_id[..8.min(validation.pat_id.len())]
            );
            validation.tenant_id
        }
    } else {
        debug!("Auth not configured, using default tenant");
        String::new()
    };

    // --- 2. Capture request parts ---
    let method = req.method().clone();
    let uri = req.uri().clone();
    let path = uri.path().to_string();
    let query = uri.query().map(|q| format!("?{}", q)).unwrap_or_default();
    let client_headers = req.headers().clone();

    let body_bytes: Bytes = axum::body::to_bytes(req.into_body(), 10 * 1024 * 1024)
        .await
        .map_err(|e| ProxyError::Internal(format!("Failed to read body: {}", e)))?;

    let is_init = is_initialize_request(&body_bytes);
    let is_delete = method == Method::DELETE;

    // --- 3. Resolve session ID ---
    // For initialize: don't inject a session ID (backend creates a new one).
    // For other requests: use registry session ID if available, else fall through
    // to whatever the client sent.
    let registry_session_id = if !is_init {
        state.sessions.get_session_id(&tenant_id).await
    } else {
        None
    };

    // --- 4. Forward to backend ---
    let backend_resp = send_to_backend_with_retry(
        &state.http_client,
        &state.backend_url,
        &method,
        &path,
        &query,
        &client_headers,
        &tenant_id,
        registry_session_id.as_deref(),
        body_bytes.clone(),
    )
    .await?;

    // --- 5. Handle 404 → session recovery ---
    if backend_resp.status == axum::http::StatusCode::NOT_FOUND && !is_init && !is_delete {
        info!(
            "Session expired for tenant {}, attempting recovery",
            tenant_id
        );

        // Invalidate the stale session
        state.sessions.invalidate(&tenant_id).await;

        // Acquire per-tenant recovery lock (serializes concurrent recoveries)
        let _guard = state.sessions.acquire_recovery_lock(&tenant_id).await;

        // Double-check: another request may have already recovered
        if let Some(new_sid) = state.sessions.get_session_id(&tenant_id).await {
            debug!(
                "Session already recovered by another request for tenant {}",
                tenant_id
            );
            // Retry with the recovered session ID
            let retry_resp = send_to_backend(
                &state.http_client,
                &state.backend_url,
                &method,
                &path,
                &query,
                &client_headers,
                &tenant_id,
                Some(&new_sid),
                body_bytes,
            )
            .await?;

            // Cache any new session ID from the retry
            if let Some(sid) = extract_session_id_from_headers(&retry_resp.headers) {
                state.sessions.set_session_id(&tenant_id, sid).await;
            }

            return into_response(retry_resp);
        }

        // We are the first to recover: re-initialize
        let new_session_id = reinitialize_session(
            &state.http_client,
            &state.backend_url,
            &tenant_id,
        )
        .await?;

        state
            .sessions
            .set_session_id(&tenant_id, new_session_id.clone())
            .await;

        // Retry the original request with the new session ID
        let retry_resp = send_to_backend(
            &state.http_client,
            &state.backend_url,
            &method,
            &path,
            &query,
            &client_headers,
            &tenant_id,
            Some(&new_session_id),
            body_bytes,
        )
        .await?;

        // Cache any updated session ID
        if let Some(sid) = extract_session_id_from_headers(&retry_resp.headers) {
            state.sessions.set_session_id(&tenant_id, sid).await;
        }

        return into_response(retry_resp);
    }

    // --- 6. Normal path: cache session ID and return response ---
    if let Some(sid) = extract_session_id_from_headers(&backend_resp.headers) {
        state.sessions.set_session_id(&tenant_id, sid).await;
    }

    // On DELETE, clear the registry entry
    if is_delete && backend_resp.status.is_success() {
        state.sessions.invalidate(&tenant_id).await;
    }

    into_response(backend_resp)
}
