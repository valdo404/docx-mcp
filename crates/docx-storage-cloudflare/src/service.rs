use std::pin::Pin;
use std::sync::Arc;

use tokio::sync::mpsc;
use tokio_stream::{wrappers::ReceiverStream, Stream, StreamExt};
use tonic::{Request, Response, Status, Streaming};
use tracing::{debug, instrument};

use crate::error::StorageResultExt;
use crate::storage::{R2Storage, StorageBackend};

// Include the generated protobuf code
pub mod proto {
    tonic::include_proto!("docx.storage");
}

use proto::storage_service_server::StorageService;
use proto::*;

/// Default chunk size for streaming: 256KB
const DEFAULT_CHUNK_SIZE: usize = 256 * 1024;

/// Implementation of the StorageService gRPC service.
pub struct StorageServiceImpl {
    storage: Arc<R2Storage>,
    version: String,
    chunk_size: usize,
}

impl StorageServiceImpl {
    pub fn new(storage: Arc<R2Storage>) -> Self {
        Self {
            storage,
            version: env!("CARGO_PKG_VERSION").to_string(),
            chunk_size: DEFAULT_CHUNK_SIZE,
        }
    }

    /// Extract tenant_id from request context.
    fn get_tenant_id(context: Option<&TenantContext>) -> Result<&str, Status> {
        context
            .map(|c| c.tenant_id.as_str())
            .ok_or_else(|| Status::invalid_argument("tenant context is required"))
    }
}

type StreamResult<T> = Pin<Box<dyn Stream<Item = Result<T, Status>> + Send>>;

#[tonic::async_trait]
impl StorageService for StorageServiceImpl {
    type LoadSessionStream = StreamResult<DataChunk>;
    type LoadCheckpointStream = StreamResult<LoadCheckpointChunk>;

    // =========================================================================
    // Session Operations (Streaming)
    // =========================================================================

    #[instrument(skip(self, request), level = "debug")]
    async fn load_session(
        &self,
        request: Request<LoadSessionRequest>,
    ) -> Result<Response<Self::LoadSessionStream>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?.to_string();
        let session_id = req.session_id.clone();

        let result = self
            .storage
            .load_session(&tenant_id, &session_id)
            .await
            .map_storage_err()?;

        let (tx, rx) = mpsc::channel(4);
        let chunk_size = self.chunk_size;

        tokio::spawn(async move {
            match result {
                Some(data) => {
                    let total_size = data.len() as u64;
                    let chunks: Vec<Vec<u8>> = data.chunks(chunk_size).map(|c| c.to_vec()).collect();
                    let total_chunks = chunks.len();

                    for (i, chunk) in chunks.into_iter().enumerate() {
                        let is_first = i == 0;
                        let is_last = i == total_chunks - 1;

                        let msg = DataChunk {
                            data: chunk,
                            is_last,
                            found: is_first,
                            total_size: if is_first { total_size } else { 0 },
                        };

                        if tx.send(Ok(msg)).await.is_err() {
                            break;
                        }
                    }
                }
                None => {
                    let _ = tx
                        .send(Ok(DataChunk {
                            data: vec![],
                            is_last: true,
                            found: false,
                            total_size: 0,
                        }))
                        .await;
                }
            }
        });

        Ok(Response::new(Box::pin(ReceiverStream::new(rx))))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn save_session(
        &self,
        request: Request<Streaming<SaveSessionChunk>>,
    ) -> Result<Response<SaveSessionResponse>, Status> {
        let mut stream = request.into_inner();

        let mut tenant_id: Option<String> = None;
        let mut session_id: Option<String> = None;
        let mut data = Vec::new();

        while let Some(chunk) = stream.next().await {
            let chunk = chunk?;

            if tenant_id.is_none() {
                tenant_id = chunk.context.map(|c| c.tenant_id);
                session_id = Some(chunk.session_id);
            }

            data.extend(chunk.data);

            if chunk.is_last {
                break;
            }
        }

        let tenant_id = tenant_id
            .ok_or_else(|| Status::invalid_argument("tenant context is required in first chunk"))?;
        let session_id = session_id
            .filter(|s| !s.is_empty())
            .ok_or_else(|| Status::invalid_argument("session_id is required in first chunk"))?;

        debug!(
            "Saving session {} for tenant {} ({} bytes)",
            session_id,
            tenant_id,
            data.len()
        );

        self.storage
            .save_session(&tenant_id, &session_id, &data)
            .await
            .map_storage_err()?;

        Ok(Response::new(SaveSessionResponse { success: true }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn list_sessions(
        &self,
        request: Request<ListSessionsRequest>,
    ) -> Result<Response<ListSessionsResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let sessions = self
            .storage
            .list_sessions(tenant_id)
            .await
            .map_storage_err()?;

        let sessions = sessions
            .into_iter()
            .map(|s| SessionInfo {
                session_id: s.session_id,
                source_path: s.source_path.unwrap_or_default(),
                created_at_unix: s.created_at.timestamp(),
                modified_at_unix: s.modified_at.timestamp(),
                size_bytes: s.size_bytes as i64,
            })
            .collect();

        Ok(Response::new(ListSessionsResponse { sessions }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn delete_session(
        &self,
        request: Request<DeleteSessionRequest>,
    ) -> Result<Response<DeleteSessionResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let existed = self
            .storage
            .delete_session(tenant_id, &req.session_id)
            .await
            .map_storage_err()?;

        Ok(Response::new(DeleteSessionResponse {
            success: true,
            existed,
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn session_exists(
        &self,
        request: Request<SessionExistsRequest>,
    ) -> Result<Response<SessionExistsResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let exists = self
            .storage
            .session_exists(tenant_id, &req.session_id)
            .await
            .map_storage_err()?;

        // Read pending_external_change from the index
        let pending_external_change = if exists {
            self.storage
                .load_index(tenant_id)
                .await
                .map_storage_err()?
                .and_then(|idx| idx.get(&req.session_id).map(|e| e.pending_external_change))
                .unwrap_or(false)
        } else {
            false
        };

        Ok(Response::new(SessionExistsResponse { exists, pending_external_change }))
    }

    // =========================================================================
    // Index Operations (Atomic - ETag-based CAS, no external lock)
    // =========================================================================

    #[instrument(skip(self, request), level = "debug")]
    async fn load_index(
        &self,
        request: Request<LoadIndexRequest>,
    ) -> Result<Response<LoadIndexResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let result = self
            .storage
            .load_index(tenant_id)
            .await
            .map_storage_err()?;

        let (index_json, found) = match result {
            Some(index) => {
                let json = serde_json::to_vec(&index)
                    .map_err(|e| Status::internal(format!("Failed to serialize index: {}", e)))?;
                (json, true)
            }
            None => (vec![], false),
        };

        Ok(Response::new(LoadIndexResponse { index_json, found }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn add_session_to_index(
        &self,
        request: Request<AddSessionToIndexRequest>,
    ) -> Result<Response<AddSessionToIndexResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?.to_string();
        let session_id = req.session_id;
        let entry = req
            .entry
            .ok_or_else(|| Status::invalid_argument("entry is required"))?;

        // Capture values for the closure
        let sid = session_id.clone();
        let mut already_exists = false;

        self.storage
            .cas_index(&tenant_id, |index| {
                if index.contains(&sid) {
                    already_exists = true;
                } else {
                    already_exists = false;
                    index.upsert(crate::storage::SessionIndexEntry {
                        id: sid.clone(),
                        source_path: if entry.source_path.is_empty() {
                            None
                        } else {
                            Some(entry.source_path.clone())
                        },
                        auto_sync: true,
                        created_at: chrono::DateTime::from_timestamp(entry.created_at_unix, 0)
                            .unwrap_or_else(chrono::Utc::now),
                        last_modified_at: chrono::DateTime::from_timestamp(
                            entry.modified_at_unix,
                            0,
                        )
                        .unwrap_or_else(chrono::Utc::now),
                        docx_file: Some(format!("{}.docx", sid)),
                        wal_count: entry.wal_position,
                        cursor_position: entry.wal_position,
                        checkpoint_positions: entry.checkpoint_positions.clone(),
                        pending_external_change: entry.pending_external_change,
                    });
                }
            })
            .await
            .map_storage_err()?;

        Ok(Response::new(AddSessionToIndexResponse {
            success: true,
            already_exists,
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn update_session_in_index(
        &self,
        request: Request<UpdateSessionInIndexRequest>,
    ) -> Result<Response<UpdateSessionInIndexResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?.to_string();
        let session_id = req.session_id;

        let sid = session_id.clone();
        let mut not_found = false;

        // Clone req fields for the closure
        let modified_at_unix = req.modified_at_unix;
        let wal_position = req.wal_position;
        let cursor_position = req.cursor_position;
        let pending_external_change = req.pending_external_change;
        let source_path = req.source_path.clone();
        let add_checkpoint_positions = req.add_checkpoint_positions.clone();
        let remove_checkpoint_positions = req.remove_checkpoint_positions.clone();

        self.storage
            .cas_index(&tenant_id, |index| {
                if !index.contains(&sid) {
                    not_found = true;
                    return;
                }
                not_found = false;
                let entry = index.get_mut(&sid).unwrap();

                if let Some(modified_at) = modified_at_unix {
                    entry.last_modified_at = chrono::DateTime::from_timestamp(modified_at, 0)
                        .unwrap_or_else(chrono::Utc::now);
                }
                if let Some(wal_pos) = wal_position {
                    entry.wal_count = wal_pos;
                    if cursor_position.is_none() {
                        entry.cursor_position = wal_pos;
                    }
                }
                if let Some(cursor_pos) = cursor_position {
                    entry.cursor_position = cursor_pos;
                }
                if let Some(pending) = pending_external_change {
                    entry.pending_external_change = pending;
                }
                if let Some(ref sp) = source_path {
                    entry.source_path = if sp.is_empty() { None } else { Some(sp.clone()) };
                }

                for pos in &add_checkpoint_positions {
                    if !entry.checkpoint_positions.contains(pos) {
                        entry.checkpoint_positions.push(*pos);
                    }
                }

                entry
                    .checkpoint_positions
                    .retain(|p| !remove_checkpoint_positions.contains(p));

                entry.checkpoint_positions.sort();
            })
            .await
            .map_storage_err()?;

        Ok(Response::new(UpdateSessionInIndexResponse {
            success: !not_found,
            not_found,
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn remove_session_from_index(
        &self,
        request: Request<RemoveSessionFromIndexRequest>,
    ) -> Result<Response<RemoveSessionFromIndexResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?.to_string();
        let session_id = req.session_id;

        let sid = session_id.clone();
        let mut existed = false;

        self.storage
            .cas_index(&tenant_id, |index| {
                existed = index.remove(&sid).is_some();
            })
            .await
            .map_storage_err()?;

        Ok(Response::new(RemoveSessionFromIndexResponse {
            success: true,
            existed,
        }))
    }

    // =========================================================================
    // WAL Operations
    // =========================================================================

    #[instrument(skip(self, request), level = "debug", fields(entries_count = request.get_ref().entries.len()))]
    async fn append_wal(
        &self,
        request: Request<AppendWalRequest>,
    ) -> Result<Response<AppendWalResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let entries: Vec<crate::storage::WalEntry> = req
            .entries
            .into_iter()
            .map(|e| crate::storage::WalEntry {
                position: e.position,
                operation: e.operation,
                path: e.path,
                patch_json: e.patch_json,
                timestamp: chrono::DateTime::from_timestamp(e.timestamp_unix, 0)
                    .unwrap_or_else(chrono::Utc::now),
            })
            .collect();

        let new_position = self
            .storage
            .append_wal(tenant_id, &req.session_id, &entries)
            .await
            .map_storage_err()?;

        Ok(Response::new(AppendWalResponse {
            success: true,
            new_position,
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn read_wal(
        &self,
        request: Request<ReadWalRequest>,
    ) -> Result<Response<ReadWalResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let limit = if req.limit > 0 { Some(req.limit) } else { None };

        let (entries, has_more) = self
            .storage
            .read_wal(tenant_id, &req.session_id, req.from_position, limit)
            .await
            .map_storage_err()?;

        let entries = entries
            .into_iter()
            .map(|e| WalEntry {
                position: e.position,
                operation: e.operation,
                path: e.path,
                patch_json: e.patch_json,
                timestamp_unix: e.timestamp.timestamp(),
            })
            .collect();

        Ok(Response::new(ReadWalResponse { entries, has_more }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn truncate_wal(
        &self,
        request: Request<TruncateWalRequest>,
    ) -> Result<Response<TruncateWalResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let entries_removed = self
            .storage
            .truncate_wal(tenant_id, &req.session_id, req.keep_from_position)
            .await
            .map_storage_err()?;

        Ok(Response::new(TruncateWalResponse {
            success: true,
            entries_removed,
        }))
    }

    // =========================================================================
    // Checkpoint Operations (Streaming)
    // =========================================================================

    #[instrument(skip(self, request), level = "debug")]
    async fn save_checkpoint(
        &self,
        request: Request<Streaming<SaveCheckpointChunk>>,
    ) -> Result<Response<SaveCheckpointResponse>, Status> {
        let mut stream = request.into_inner();

        let mut tenant_id: Option<String> = None;
        let mut session_id: Option<String> = None;
        let mut position: u64 = 0;
        let mut data = Vec::new();

        while let Some(chunk) = stream.next().await {
            let chunk = chunk?;

            if tenant_id.is_none() {
                tenant_id = chunk.context.map(|c| c.tenant_id);
                session_id = Some(chunk.session_id);
                position = chunk.position;
            }

            data.extend(chunk.data);

            if chunk.is_last {
                break;
            }
        }

        let tenant_id = tenant_id
            .ok_or_else(|| Status::invalid_argument("tenant context is required in first chunk"))?;
        let session_id = session_id
            .filter(|s| !s.is_empty())
            .ok_or_else(|| Status::invalid_argument("session_id is required in first chunk"))?;

        debug!(
            "Saving checkpoint at position {} for session {} tenant {} ({} bytes)",
            position,
            session_id,
            tenant_id,
            data.len()
        );

        self.storage
            .save_checkpoint(&tenant_id, &session_id, position, &data)
            .await
            .map_storage_err()?;

        Ok(Response::new(SaveCheckpointResponse { success: true }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn load_checkpoint(
        &self,
        request: Request<LoadCheckpointRequest>,
    ) -> Result<Response<Self::LoadCheckpointStream>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?.to_string();
        let session_id = req.session_id.clone();
        let position = req.position;

        let result = self
            .storage
            .load_checkpoint(&tenant_id, &session_id, position)
            .await
            .map_storage_err()?;

        let (tx, rx) = mpsc::channel(4);
        let chunk_size = self.chunk_size;

        tokio::spawn(async move {
            match result {
                Some((data, actual_position)) => {
                    let total_size = data.len() as u64;
                    let chunks: Vec<Vec<u8>> = data.chunks(chunk_size).map(|c| c.to_vec()).collect();
                    let total_chunks = chunks.len();

                    for (i, chunk) in chunks.into_iter().enumerate() {
                        let is_first = i == 0;
                        let is_last = i == total_chunks - 1;

                        let msg = LoadCheckpointChunk {
                            data: chunk,
                            is_last,
                            found: is_first,
                            position: if is_first { actual_position } else { 0 },
                            total_size: if is_first { total_size } else { 0 },
                        };

                        if tx.send(Ok(msg)).await.is_err() {
                            break;
                        }
                    }
                }
                None => {
                    let _ = tx
                        .send(Ok(LoadCheckpointChunk {
                            data: vec![],
                            is_last: true,
                            found: false,
                            position: 0,
                            total_size: 0,
                        }))
                        .await;
                }
            }
        });

        Ok(Response::new(Box::pin(ReceiverStream::new(rx))))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn list_checkpoints(
        &self,
        request: Request<ListCheckpointsRequest>,
    ) -> Result<Response<ListCheckpointsResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let checkpoints = self
            .storage
            .list_checkpoints(tenant_id, &req.session_id)
            .await
            .map_storage_err()?;

        let checkpoints = checkpoints
            .into_iter()
            .map(|c| CheckpointInfo {
                position: c.position,
                created_at_unix: c.created_at.timestamp(),
                size_bytes: c.size_bytes as i64,
            })
            .collect();

        Ok(Response::new(ListCheckpointsResponse { checkpoints }))
    }

    // =========================================================================
    // Health Check
    // =========================================================================

    #[instrument(skip(self), level = "debug")]
    async fn health_check(
        &self,
        _request: Request<HealthCheckRequest>,
    ) -> Result<Response<HealthCheckResponse>, Status> {
        debug!("Health check requested");
        Ok(Response::new(HealthCheckResponse {
            healthy: true,
            backend: self.storage.backend_name().to_string(),
            version: self.version.clone(),
        }))
    }
}
