// Re-export from docx-storage-core
pub use docx_storage_core::StorageError;

/// Convert StorageError to tonic::Status
pub fn storage_error_to_status(err: StorageError) -> tonic::Status {
    match err {
        StorageError::Io(msg) => tonic::Status::internal(msg),
        StorageError::Serialization(msg) => tonic::Status::internal(msg),
        StorageError::NotFound(msg) => tonic::Status::not_found(msg),
        StorageError::Lock(msg) => tonic::Status::failed_precondition(msg),
        StorageError::InvalidArgument(msg) => tonic::Status::invalid_argument(msg),
        StorageError::Internal(msg) => tonic::Status::internal(msg),
        StorageError::Sync(msg) => tonic::Status::internal(msg),
        StorageError::Watch(msg) => tonic::Status::internal(msg),
    }
}

/// Extension trait for converting StorageError Result to tonic::Status Result
pub trait StorageResultExt<T> {
    fn map_storage_err(self) -> Result<T, tonic::Status>;
}

impl<T> StorageResultExt<T> for Result<T, StorageError> {
    fn map_storage_err(self) -> Result<T, tonic::Status> {
        self.map_err(storage_error_to_status)
    }
}
