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

    #[error("Invalid JSON: {0}")]
    JsonError(#[from] serde_json::Error),

    #[error("Session recovery failed: {0}")]
    SessionRecoveryFailed(String),

    #[error("Internal error: {0}")]
    Internal(String),
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

        (status, axum::Json(body)).into_response()
    }
}

pub type Result<T> = std::result::Result<T, ProxyError>;
