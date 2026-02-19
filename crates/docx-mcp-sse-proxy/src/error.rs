//! Error types for the HTTP reverse proxy.

use axum::http::StatusCode;
use axum::response::{IntoResponse, Response};
use serde::Serialize;

/// Application-level errors.
#[derive(Debug, thiserror::Error)]
pub enum ProxyError {
    #[error("Authentication required")]
    Unauthorized,

    #[error("Invalid or expired PAT token")]
    InvalidToken,

    #[error("D1 API error: {0}")]
    D1Error(String),

    #[error("Backend error: {0}")]
    BackendError(String),

    #[error("Backend temporarily unavailable after {1} retries: {0}")]
    BackendUnavailable(String, u32),

    #[error("Invalid JSON: {0}")]
    JsonError(#[from] serde_json::Error),

    #[error("Session recovery failed: {0}")]
    SessionRecoveryFailed(String),

    #[error("Internal error: {0}")]
    Internal(String),
}

// Thread-local context for resource metadata URL (used in WWW-Authenticate header).
// Set by the handler before returning auth errors.
std::thread_local! {
    static RESOURCE_METADATA_URL: std::cell::RefCell<Option<String>> = const { std::cell::RefCell::new(None) };
}

/// Set the resource metadata URL for WWW-Authenticate headers in 401 responses.
pub fn set_resource_metadata_url(url: Option<String>) {
    RESOURCE_METADATA_URL.with(|cell| {
        *cell.borrow_mut() = url;
    });
}

impl IntoResponse for ProxyError {
    fn into_response(self) -> Response {
        #[derive(Serialize)]
        struct ErrorBody {
            error: String,
            code: &'static str,
        }

        let (status, code) = match &self {
            ProxyError::Unauthorized => (StatusCode::UNAUTHORIZED, "UNAUTHORIZED"),
            ProxyError::InvalidToken => (StatusCode::UNAUTHORIZED, "INVALID_TOKEN"),
            ProxyError::D1Error(_) => (StatusCode::BAD_GATEWAY, "D1_ERROR"),
            ProxyError::BackendError(_) => (StatusCode::BAD_GATEWAY, "BACKEND_ERROR"),
            ProxyError::BackendUnavailable(_, _) => {
                (StatusCode::SERVICE_UNAVAILABLE, "BACKEND_UNAVAILABLE")
            }
            ProxyError::SessionRecoveryFailed(_) => {
                (StatusCode::BAD_GATEWAY, "SESSION_RECOVERY_FAILED")
            }
            ProxyError::JsonError(_) => (StatusCode::BAD_REQUEST, "INVALID_JSON"),
            ProxyError::Internal(_) => (StatusCode::INTERNAL_SERVER_ERROR, "INTERNAL_ERROR"),
        };

        let body = ErrorBody {
            error: self.to_string(),
            code,
        };

        let mut response = (status, axum::Json(body)).into_response();

        // Add WWW-Authenticate header on 401 responses
        if status == StatusCode::UNAUTHORIZED {
            RESOURCE_METADATA_URL.with(|cell| {
                if let Some(ref url) = *cell.borrow() {
                    let header_value = format!(
                        "Bearer resource_metadata=\"{}/.well-known/oauth-protected-resource\"",
                        url
                    );
                    if let Ok(val) = axum::http::HeaderValue::from_str(&header_value) {
                        response.headers_mut().insert(
                            axum::http::header::WWW_AUTHENTICATE,
                            val,
                        );
                    }
                }
            });
        }

        response
    }
}

pub type Result<T> = std::result::Result<T, ProxyError>;
