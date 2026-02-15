//! HTTP handlers for the reverse proxy.
//!
//! Implements:
//! - POST/GET/DELETE /mcp{/*rest} - Forward to .NET MCP backend
//! - GET /health - Health check endpoint

use axum::body::Body;
use axum::extract::{Request, State};
use axum::http::{header, HeaderMap, HeaderValue};
use axum::response::{IntoResponse, Response};
use axum::Json;
use reqwest::Client as HttpClient;
use serde::Serialize;
use tracing::{debug, info};

use crate::auth::SharedPatValidator;
use crate::error::ProxyError;

/// Application state shared across handlers.
#[derive(Clone)]
pub struct AppState {
    pub validator: Option<SharedPatValidator>,
    pub backend_url: String,
    pub http_client: HttpClient,
}

/// Health check response.
#[derive(Serialize)]
pub struct HealthResponse {
    pub healthy: bool,
    pub version: &'static str,
    pub auth_enabled: bool,
}

/// GET /health - Health check endpoint.
pub async fn health_handler(State(state): State<AppState>) -> Json<HealthResponse> {
    Json(HealthResponse {
        healthy: true,
        version: env!("CARGO_PKG_VERSION"),
        auth_enabled: state.validator.is_some(),
    })
}

/// Extract Bearer token from Authorization header.
fn extract_bearer_token(headers: &HeaderMap) -> Option<&str> {
    headers
        .get(header::AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "))
}

/// Headers to forward from the client to the backend.
const FORWARD_HEADERS: &[header::HeaderName] = &[
    header::CONTENT_TYPE,
    header::ACCEPT,
];

/// MCP-specific header for session tracking.
const MCP_SESSION_ID: &str = "mcp-session-id";
const X_TENANT_ID: &str = "x-tenant-id";

/// Forward any request on /mcp (POST, GET, DELETE) to the .NET backend.
///
/// This is a transparent reverse proxy:
/// 1. Validates PAT → extracts tenant_id
/// 2. Forwards the request to {MCP_BACKEND_URL}/mcp with X-Tenant-Id header
/// 3. Streams the response back (SSE or JSON)
pub async fn mcp_forward_handler(
    State(state): State<AppState>,
    req: Request,
) -> std::result::Result<Response, ProxyError> {
    // Authenticate if validator is configured
    let tenant_id = if let Some(ref validator) = state.validator {
        let token = extract_bearer_token(req.headers()).ok_or(ProxyError::Unauthorized)?;
        let validation = validator.validate(token).await?;
        info!(
            "Authenticated request for tenant {} (PAT: {}...)",
            validation.tenant_id,
            &validation.pat_id[..8.min(validation.pat_id.len())]
        );
        validation.tenant_id
    } else {
        debug!("Auth not configured, using default tenant");
        String::new()
    };

    // Build the backend URL preserving the path
    let uri = req.uri();
    let path = uri.path();
    let query = uri.query().map(|q| format!("?{}", q)).unwrap_or_default();
    let backend_url = format!("{}{}{}", state.backend_url, path, query);

    debug!(
        "Forwarding {} {} -> {}",
        req.method(),
        path,
        backend_url
    );

    // Build the forwarded request
    let method = req.method().clone();
    let mut backend_req = state.http_client.request(
        reqwest::Method::from_bytes(method.as_str().as_bytes())
            .map_err(|e| ProxyError::Internal(format!("Invalid method: {}", e)))?,
        &backend_url,
    );

    // Forward relevant headers
    for header_name in FORWARD_HEADERS {
        if let Some(value) = req.headers().get(header_name) {
            if let Ok(s) = value.to_str() {
                backend_req = backend_req.header(header_name.as_str(), s);
            }
        }
    }

    // Forward Mcp-Session-Id if present
    if let Some(value) = req.headers().get(MCP_SESSION_ID) {
        if let Ok(s) = value.to_str() {
            backend_req = backend_req.header(MCP_SESSION_ID, s);
        }
    }

    // Inject tenant ID
    backend_req = backend_req.header(X_TENANT_ID, &tenant_id);

    // Forward body
    let body_bytes = axum::body::to_bytes(req.into_body(), 10 * 1024 * 1024) // 10MB limit
        .await
        .map_err(|e| ProxyError::Internal(format!("Failed to read body: {}", e)))?;

    if !body_bytes.is_empty() {
        backend_req = backend_req.body(body_bytes);
    }

    // Send request to backend
    let backend_resp = backend_req
        .send()
        .await
        .map_err(|e| ProxyError::BackendError(format!("Failed to reach backend: {}", e)))?;

    // Build response back to client
    let status = axum::http::StatusCode::from_u16(backend_resp.status().as_u16())
        .unwrap_or(axum::http::StatusCode::BAD_GATEWAY);

    let mut response_headers = HeaderMap::new();

    // Forward response headers
    if let Some(ct) = backend_resp.headers().get(reqwest::header::CONTENT_TYPE) {
        if let Ok(v) = HeaderValue::from_bytes(ct.as_bytes()) {
            response_headers.insert(header::CONTENT_TYPE, v);
        }
    }

    // Forward Mcp-Session-Id from backend
    if let Some(session_id) = backend_resp.headers().get(MCP_SESSION_ID) {
        if let Ok(v) = HeaderValue::from_bytes(session_id.as_bytes()) {
            response_headers.insert(
                header::HeaderName::from_static("mcp-session-id"),
                v,
            );
        }
    }

    // Check if the response is SSE (text/event-stream)
    let is_sse = backend_resp
        .headers()
        .get(reqwest::header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .map(|v| v.contains("text/event-stream"))
        .unwrap_or(false);

    if is_sse {
        // Stream SSE response
        let stream = backend_resp.bytes_stream();
        let body = Body::from_stream(stream);

        let mut response = Response::builder()
            .status(status)
            .body(body)
            .map_err(|e| ProxyError::Internal(format!("Failed to build response: {}", e)))?;

        *response.headers_mut() = response_headers;
        // Ensure content-type is set for SSE
        response.headers_mut().insert(
            header::CONTENT_TYPE,
            HeaderValue::from_static("text/event-stream"),
        );

        Ok(response)
    } else {
        // Non-streaming response — read full body and forward
        let body_bytes = backend_resp
            .bytes()
            .await
            .map_err(|e| ProxyError::BackendError(format!("Failed to read backend response: {}", e)))?;

        let mut response = (status, body_bytes).into_response();

        // Merge our tracked headers into the response
        for (name, value) in response_headers {
            if let Some(name) = name {
                response.headers_mut().insert(name, value);
            }
        }

        Ok(response)
    }
}
