use std::pin::Pin;
use std::sync::Arc;

use docx_storage_core::{SourceDescriptor, SourceType, WatchBackend};
use tokio::sync::mpsc;
use tokio_stream::{wrappers::ReceiverStream, Stream};
use tonic::{Request, Response, Status};
use tracing::{debug, instrument, warn};

use crate::service::proto;
use proto::external_watch_service_server::ExternalWatchService;
use proto::*;

/// Implementation of the ExternalWatchService gRPC service.
pub struct ExternalWatchServiceImpl {
    watch_backend: Arc<dyn WatchBackend>,
}

impl ExternalWatchServiceImpl {
    pub fn new(watch_backend: Arc<dyn WatchBackend>) -> Self {
        Self { watch_backend }
    }

    /// Extract tenant_id from request context.
    fn get_tenant_id(context: Option<&TenantContext>) -> Result<&str, Status> {
        context
            .map(|c| c.tenant_id.as_str())
            .ok_or_else(|| Status::invalid_argument("tenant context is required"))
    }

    /// Convert proto SourceType to core SourceType.
    fn convert_source_type(proto_type: i32) -> SourceType {
        match proto_type {
            1 => SourceType::LocalFile,
            2 => SourceType::SharePoint,
            3 => SourceType::OneDrive,
            4 => SourceType::S3,
            5 => SourceType::R2,
            _ => SourceType::LocalFile, // Default
        }
    }

    /// Convert proto SourceDescriptor to core SourceDescriptor.
    fn convert_source_descriptor(
        proto: Option<&proto::SourceDescriptor>,
    ) -> Option<SourceDescriptor> {
        proto.map(|s| SourceDescriptor {
            source_type: Self::convert_source_type(s.r#type),
            uri: s.uri.clone(),
            metadata: s.metadata.clone(),
        })
    }

    /// Convert core SourceMetadata to proto SourceMetadata.
    fn to_proto_source_metadata(
        metadata: &docx_storage_core::SourceMetadata,
    ) -> proto::SourceMetadata {
        proto::SourceMetadata {
            size_bytes: metadata.size_bytes as i64,
            modified_at_unix: metadata.modified_at,
            etag: metadata.etag.clone().unwrap_or_default(),
            version_id: metadata.version_id.clone().unwrap_or_default(),
            content_hash: metadata.content_hash.clone().unwrap_or_default(),
        }
    }

    /// Convert core ExternalChangeType to proto ExternalChangeType.
    fn to_proto_change_type(
        change_type: docx_storage_core::ExternalChangeType,
    ) -> i32 {
        match change_type {
            docx_storage_core::ExternalChangeType::Modified => 1,
            docx_storage_core::ExternalChangeType::Deleted => 2,
            docx_storage_core::ExternalChangeType::Renamed => 3,
            docx_storage_core::ExternalChangeType::PermissionChanged => 4,
        }
    }
}

type WatchChangesStream = Pin<Box<dyn Stream<Item = Result<ExternalChangeEvent, Status>> + Send>>;

#[tonic::async_trait]
impl ExternalWatchService for ExternalWatchServiceImpl {
    type WatchChangesStream = WatchChangesStream;

    #[instrument(skip(self, request), level = "debug")]
    async fn start_watch(
        &self,
        request: Request<StartWatchRequest>,
    ) -> Result<Response<StartWatchResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let source = Self::convert_source_descriptor(req.source.as_ref())
            .ok_or_else(|| Status::invalid_argument("source is required"))?;

        match self
            .watch_backend
            .start_watch(tenant_id, &req.session_id, &source, req.poll_interval_seconds as u32)
            .await
        {
            Ok(watch_id) => {
                debug!(
                    "Started watching for tenant {} session {}: {}",
                    tenant_id, req.session_id, watch_id
                );
                Ok(Response::new(StartWatchResponse {
                    success: true,
                    watch_id,
                    error: String::new(),
                }))
            }
            Err(e) => Ok(Response::new(StartWatchResponse {
                success: false,
                watch_id: String::new(),
                error: e.to_string(),
            })),
        }
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn stop_watch(
        &self,
        request: Request<StopWatchRequest>,
    ) -> Result<Response<StopWatchResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        self.watch_backend
            .stop_watch(tenant_id, &req.session_id)
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        debug!(
            "Stopped watching for tenant {} session {}",
            tenant_id, req.session_id
        );
        Ok(Response::new(StopWatchResponse { success: true }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn check_for_changes(
        &self,
        request: Request<CheckForChangesRequest>,
    ) -> Result<Response<CheckForChangesResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let change = self
            .watch_backend
            .check_for_changes(tenant_id, &req.session_id)
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        let (current_metadata, known_metadata) = if change.is_some() {
            (
                self.watch_backend
                    .get_source_metadata(tenant_id, &req.session_id)
                    .await
                    .ok()
                    .flatten()
                    .map(|m| Self::to_proto_source_metadata(&m)),
                self.watch_backend
                    .get_known_metadata(tenant_id, &req.session_id)
                    .await
                    .ok()
                    .flatten()
                    .map(|m| Self::to_proto_source_metadata(&m)),
            )
        } else {
            (None, None)
        };

        Ok(Response::new(CheckForChangesResponse {
            has_changes: change.is_some(),
            current_metadata,
            known_metadata,
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn watch_changes(
        &self,
        request: Request<WatchChangesRequest>,
    ) -> Result<Response<Self::WatchChangesStream>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?.to_string();
        let session_ids = req.session_ids;

        let (tx, rx) = mpsc::channel(100);
        let watch_backend = self.watch_backend.clone();

        // Spawn a task that polls for changes
        tokio::spawn(async move {
            loop {
                // Check each session for changes
                for session_id in &session_ids {
                    match watch_backend.check_for_changes(&tenant_id, session_id).await {
                        Ok(Some(change)) => {
                            let proto_event = ExternalChangeEvent {
                                session_id: change.session_id.clone(),
                                change_type: Self::to_proto_change_type(change.change_type),
                                old_metadata: change
                                    .old_metadata
                                    .as_ref()
                                    .map(Self::to_proto_source_metadata),
                                new_metadata: change
                                    .new_metadata
                                    .as_ref()
                                    .map(Self::to_proto_source_metadata),
                                detected_at_unix: change.detected_at,
                                new_uri: change.new_uri.clone().unwrap_or_default(),
                            };

                            if tx.send(Ok(proto_event)).await.is_err() {
                                // Client disconnected
                                return;
                            }
                        }
                        Ok(None) => {}
                        Err(e) => {
                            warn!(
                                "Error checking for changes for session {}: {}",
                                session_id, e
                            );
                        }
                    }
                }

                // Sleep before next poll cycle
                tokio::time::sleep(tokio::time::Duration::from_secs(1)).await;
            }
        });

        Ok(Response::new(Box::pin(ReceiverStream::new(rx))))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn get_source_metadata(
        &self,
        request: Request<GetSourceMetadataRequest>,
    ) -> Result<Response<GetSourceMetadataResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        match self
            .watch_backend
            .get_source_metadata(tenant_id, &req.session_id)
            .await
        {
            Ok(Some(metadata)) => Ok(Response::new(GetSourceMetadataResponse {
                success: true,
                metadata: Some(Self::to_proto_source_metadata(&metadata)),
                error: String::new(),
            })),
            Ok(None) => Ok(Response::new(GetSourceMetadataResponse {
                success: false,
                metadata: None,
                error: "Source not found".to_string(),
            })),
            Err(e) => Ok(Response::new(GetSourceMetadataResponse {
                success: false,
                metadata: None,
                error: e.to_string(),
            })),
        }
    }
}
