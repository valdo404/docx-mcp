use std::pin::Pin;
use std::sync::Arc;

use docx_storage_core::{BrowsableBackend, SourceDescriptor, SourceType, SyncBackend};
use tokio_stream::{Stream, StreamExt};
use tonic::{Request, Response, Status, Streaming};
use tracing::{debug, instrument};

use crate::service::proto;
use proto::source_sync_service_server::SourceSyncService;
use proto::*;

type DownloadStream = Pin<Box<dyn Stream<Item = Result<DataChunk, Status>> + Send>>;

/// Implementation of the SourceSyncService gRPC service.
pub struct SourceSyncServiceImpl {
    sync_backend: Arc<dyn SyncBackend>,
    browse_backend: Arc<dyn BrowsableBackend>,
}

impl SourceSyncServiceImpl {
    pub fn new(sync_backend: Arc<dyn SyncBackend>, browse_backend: Arc<dyn BrowsableBackend>) -> Self {
        Self { sync_backend, browse_backend }
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
            6 => SourceType::GoogleDrive,
            _ => SourceType::LocalFile, // Default
        }
    }

    /// Convert proto SourceDescriptor to core SourceDescriptor.
    fn convert_source_descriptor(proto: Option<&proto::SourceDescriptor>) -> Option<SourceDescriptor> {
        proto.map(|s| SourceDescriptor {
            source_type: Self::convert_source_type(s.r#type),
            connection_id: if s.connection_id.is_empty() { None } else { Some(s.connection_id.clone()) },
            path: s.path.clone(),
            file_id: if s.file_id.is_empty() { None } else { Some(s.file_id.clone()) },
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
            SourceType::GoogleDrive => 6,
        }
    }

    /// Convert core SourceDescriptor to proto SourceDescriptor.
    fn to_proto_source_descriptor(source: &SourceDescriptor) -> proto::SourceDescriptor {
        proto::SourceDescriptor {
            r#type: Self::to_proto_source_type(source.source_type),
            connection_id: source.connection_id.clone().unwrap_or_default(),
            path: source.path.clone(),
            file_id: source.file_id.clone().unwrap_or_default(),
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
    type DownloadFromSourceStream = DownloadStream;

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

    #[instrument(skip(self, request), level = "debug")]
    async fn list_connections(
        &self,
        request: Request<ListConnectionsRequest>,
    ) -> Result<Response<ListConnectionsResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let connections = self
            .browse_backend
            .list_connections(tenant_id)
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        let proto_connections: Vec<proto::ConnectionInfo> = connections
            .into_iter()
            .map(|c| proto::ConnectionInfo {
                connection_id: c.connection_id,
                r#type: Self::to_proto_source_type(c.source_type),
                display_name: c.display_name,
                provider_account_id: c.provider_account_id.unwrap_or_default(),
            })
            .collect();

        Ok(Response::new(ListConnectionsResponse {
            connections: proto_connections,
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn list_connection_files(
        &self,
        request: Request<ListConnectionFilesRequest>,
    ) -> Result<Response<ListConnectionFilesResponse>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let page_size = if req.page_size > 0 { req.page_size as u32 } else { 50 };
        let page_token = if req.page_token.is_empty() { None } else { Some(req.page_token.as_str()) };

        let result = self
            .browse_backend
            .list_files(
                tenant_id,
                &req.connection_id,
                &req.path,
                page_token,
                page_size,
            )
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        let proto_files: Vec<proto::FileEntry> = result
            .files
            .into_iter()
            .map(|f| proto::FileEntry {
                name: f.name,
                path: f.path,
                file_id: f.file_id.unwrap_or_default(),
                is_folder: f.is_folder,
                size_bytes: f.size_bytes as i64,
                modified_at_unix: f.modified_at,
                mime_type: f.mime_type.unwrap_or_default(),
            })
            .collect();

        Ok(Response::new(ListConnectionFilesResponse {
            files: proto_files,
            next_page_token: result.next_page_token.unwrap_or_default(),
        }))
    }

    #[instrument(skip(self, request), level = "debug")]
    async fn download_from_source(
        &self,
        request: Request<DownloadFromSourceRequest>,
    ) -> Result<Response<Self::DownloadFromSourceStream>, Status> {
        let req = request.into_inner();
        let tenant_id = Self::get_tenant_id(req.context.as_ref())?;

        let file_id = if req.file_id.is_empty() { None } else { Some(req.file_id.as_str()) };

        let data = self
            .browse_backend
            .download_file(tenant_id, &req.connection_id, &req.path, file_id)
            .await
            .map_err(|e| Status::internal(e.to_string()))?;

        // Stream the data in chunks
        let chunk_size = 256 * 1024; // 256KB
        let total_size = data.len() as u64;
        let stream = async_stream::stream! {
            let mut offset = 0;
            while offset < data.len() {
                let end = std::cmp::min(offset + chunk_size, data.len());
                let is_last = end == data.len();
                yield Ok(DataChunk {
                    data: data[offset..end].to_vec(),
                    is_last,
                    found: true,
                    total_size: if offset == 0 { total_size } else { 0 },
                });
                offset = end;
            }
            if data.is_empty() {
                yield Ok(DataChunk {
                    data: vec![],
                    is_last: true,
                    found: true,
                    total_size: 0,
                });
            }
        };

        Ok(Response::new(Box::pin(stream)))
    }
}
