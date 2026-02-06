use std::sync::Arc;

use docx_storage_core::{SourceDescriptor, SourceType, SyncBackend};
use tokio_stream::StreamExt;
use tonic::{Request, Response, Status, Streaming};
use tracing::{debug, instrument};

use crate::service::proto;
use proto::source_sync_service_server::SourceSyncService;
use proto::*;

/// Implementation of the SourceSyncService gRPC service.
pub struct SourceSyncServiceImpl {
    sync_backend: Arc<dyn SyncBackend>,
}

impl SourceSyncServiceImpl {
    pub fn new(sync_backend: Arc<dyn SyncBackend>) -> Self {
        Self { sync_backend }
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
    fn convert_source_descriptor(proto: Option<&proto::SourceDescriptor>) -> Option<SourceDescriptor> {
        proto.map(|s| SourceDescriptor {
            source_type: Self::convert_source_type(s.r#type),
            uri: s.uri.clone(),
            metadata: s.metadata.clone(),
        })
    }

    /// Convert core SourceType to proto SourceType.
    fn to_proto_source_type(source_type: SourceType) -> i32 {
        match source_type {
            SourceType::LocalFile => 1,
            SourceType::SharePoint => 2,
            SourceType::OneDrive => 3,
            SourceType::S3 => 4,
            SourceType::R2 => 5,
        }
    }

    /// Convert core SourceDescriptor to proto SourceDescriptor.
    fn to_proto_source_descriptor(source: &SourceDescriptor) -> proto::SourceDescriptor {
        proto::SourceDescriptor {
            r#type: Self::to_proto_source_type(source.source_type),
            uri: source.uri.clone(),
            metadata: source.metadata.clone(),
        }
    }

    /// Convert core SyncStatus to proto SyncStatus.
    fn to_proto_sync_status(status: &docx_storage_core::SyncStatus) -> proto::SyncStatus {
        proto::SyncStatus {
            session_id: status.session_id.clone(),
            source: Some(Self::to_proto_source_descriptor(&status.source)),
            auto_sync_enabled: status.auto_sync_enabled,
            last_synced_at_unix: status.last_synced_at.unwrap_or(0),
            has_pending_changes: status.has_pending_changes,
            last_error: status.last_error.clone().unwrap_or_default(),
        }
    }
}

#[tonic::async_trait]
impl SourceSyncService for SourceSyncServiceImpl {
    #[instrument(skip(self, request), level = "debug")]
    async fn register_source(
        &self,
        request: Request<RegisterSourceRequest>,
    ) -> Result<Response<RegisterSourceResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let source = Self::convert_source_descriptor(req.source.as_ref())
            .ok_or_else(|| Status::invalid_argument("source is required"))?;

        match self
            .sync_backend
            .register_source(tenant_id, &req.session_id, source, req.auto_sync)
            .await
        {
            Ok(()) => {
                debug!(
                    "Registered source for tenant {} session {}",
                    tenant_id, req.session_id
                );
                Ok(Response::new(RegisterSourceResponse {
                    success: true,
                    error: String::new(),
                }))
            }
            Err(e) => Ok(Response::new(RegisterSourceResponse {
                success: false,
                error: e.to_string(),
            })),
        }
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn unregister_source(
        &self,
        request: Request<UnregisterSourceRequest>,
    ) -> Result<Response<UnregisterSourceResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        self.sync_backend
            .unregister_source(tenant_id, &req.session_id)
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        debug!(
            "Unregistered source for tenant {} session {}",
            tenant_id, req.session_id
        );
        Ok(Response::new(UnregisterSourceResponse { success: true }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn update_source(
        &self,
        request: Request<UpdateSourceRequest>,
    ) -> Result<Response<UpdateSourceResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        // Convert optional source
        let source = Self::convert_source_descriptor(req.source.as_ref());

        // Convert optional auto_sync (only if update_auto_sync is true)
        let auto_sync = if req.update_auto_sync {
            Some(req.auto_sync)
        } else {
            None
        };

        match self
            .sync_backend
            .update_source(tenant_id, &req.session_id, source, auto_sync)
            .await
        {
            Ok(()) => {
                debug!(
                    "Updated source for tenant {} session {}",
                    tenant_id, req.session_id
                );
                Ok(Response::new(UpdateSourceResponse {
                    success: true,
                    error: String::new(),
                }))
            }
            Err(e) => Ok(Response::new(UpdateSourceResponse {
                success: false,
                error: e.to_string(),
            })),
        }
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn sync_to_source(
        &self,
        request: Request<Streaming<SyncToSourceChunk>>,
    ) -> Result<Response<SyncToSourceResponse>, Status> {
        let mut stream = request.into_inner();

        let mut tenant_id: Option<String> = None;
        let mut session_id: Option<String> = None;
        let mut data = Vec::new();

        while let Some(chunk) = stream.next().await {
            let chunk = chunk?;

            // Extract metadata from first chunk
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
            "Syncing {} bytes to source for tenant {} session {}",
            data.len(),
            tenant_id,
            session_id
        );

        match self
            .sync_backend
            .sync_to_source(&tenant_id, &session_id, &data)
            .await
        {
            Ok(synced_at) => Ok(Response::new(SyncToSourceResponse {
                success: true,
                error: String::new(),
                synced_at_unix: synced_at,
            })),
            Err(e) => Ok(Response::new(SyncToSourceResponse {
                success: false,
                error: e.to_string(),
                synced_at_unix: 0,
            })),
        }
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn get_sync_status(
        &self,
        request: Request<GetSyncStatusRequest>,
    ) -> Result<Response<GetSyncStatusResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let status = self
            .sync_backend
            .get_sync_status(tenant_id, &req.session_id)
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        Ok(Response::new(GetSyncStatusResponse {
            registered: status.is_some(),
            status: status.map(|s| Self::to_proto_sync_status(&s)),
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn list_sources(
        &self,
        request: Request<ListSourcesRequest>,
    ) -> Result<Response<ListSourcesResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let sources = self
            .sync_backend
            .list_sources(tenant_id)
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        let proto_sources: Vec<proto::SyncStatus> = sources
            .iter()
            .map(Self::to_proto_sync_status)
            .collect();

        Ok(Response::new(ListSourcesResponse {
            sources: proto_sources,
        }))
    }
}
