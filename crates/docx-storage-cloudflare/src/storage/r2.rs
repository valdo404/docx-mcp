use std::time::Duration;

use async_trait::async_trait;
use aws_sdk_s3::primitives::ByteStream;
use aws_sdk_s3::Client as S3Client;
use docx_storage_core::{
    CheckpointInfo, SessionIndex, SessionInfo, StorageBackend, StorageError, WalEntry,
};
use tracing::{debug, instrument, warn};

/// Maximum retries for transient errors (429 / 5xx).
const MAX_RETRIES: u32 = 5;
/// Base delay for exponential backoff.
const BASE_DELAY_MS: u64 = 200;
/// Maximum retries for CAS (compare-and-swap) loops.
const CAS_MAX_RETRIES: u32 = 10;

/// R2 storage backend using Cloudflare R2 (S3-compatible) with ETag-based optimistic locking.
///
/// Storage layout in R2:
/// ```
/// {bucket}/
///   {tenant_id}/
///     index.json                     # Session index (was in KV, now in R2)
///     sessions/
///       {session_id}.docx            # Session document
///       {session_id}.wal             # WAL file (JSONL format)
///       {session_id}.ckpt.{pos}.docx # Checkpoint files
/// ```
#[derive(Clone)]
pub struct R2Storage {
    s3_client: S3Client,
    bucket_name: String,
}

impl R2Storage {
    /// Create a new R2Storage backend.
    pub fn new(s3_client: S3Client, bucket_name: String) -> Self {
        Self {
            s3_client,
            bucket_name,
        }
    }

    /// Get the S3 key for a session document.
    fn session_key(&self, tenant_id: &str, session_id: &str) -> String {
        format!("{}/sessions/{}.docx", tenant_id, session_id)
    }

    /// Get the S3 key for a session WAL file.
    fn wal_key(&self, tenant_id: &str, session_id: &str) -> String {
        format!("{}/sessions/{}.wal", tenant_id, session_id)
    }

    /// Get the S3 key for a checkpoint.
    fn checkpoint_key(&self, tenant_id: &str, session_id: &str, position: u64) -> String {
        format!("{}/sessions/{}.ckpt.{}.docx", tenant_id, session_id, position)
    }

    /// Get the R2 key for a tenant's index.
    fn index_key(&self, tenant_id: &str) -> String {
        format!("{}/index.json", tenant_id)
    }

    // =========================================================================
    // Retry helper
    // =========================================================================

    /// Sleep with exponential backoff + jitter.
    async fn backoff_sleep(attempt: u32) {
        let base = Duration::from_millis(BASE_DELAY_MS * 2u64.pow(attempt));
        let jitter = Duration::from_millis(rand_jitter());
        tokio::time::sleep(base + jitter).await;
    }

    /// Check if an S3 error is retryable (429 or 5xx).
    fn is_retryable_s3_error(err: &aws_sdk_s3::error::SdkError<impl std::fmt::Debug>) -> bool {
        use aws_sdk_s3::error::SdkError;
        match err {
            SdkError::ServiceError(e) => {
                let raw = e.raw();
                let status = raw.status().as_u16();
                status == 429 || (500..=504).contains(&status)
            }
            SdkError::ResponseError(e) => {
                let status = e.raw().status().as_u16();
                status == 429 || (500..=504).contains(&status)
            }
            SdkError::TimeoutError(_) | SdkError::DispatchFailure(_) => true,
            _ => false,
        }
    }

    /// Check if an S3 error is a 412 Precondition Failed.
    fn is_precondition_failed(err: &aws_sdk_s3::error::SdkError<impl std::fmt::Debug>) -> bool {
        use aws_sdk_s3::error::SdkError;
        match err {
            SdkError::ServiceError(e) => e.raw().status().as_u16() == 412,
            SdkError::ResponseError(e) => e.raw().status().as_u16() == 412,
            _ => false,
        }
    }

    // =========================================================================
    // R2 primitives with retry
    // =========================================================================

    /// Get an object from R2, with retry on transient errors.
    async fn get_object(&self, key: &str) -> Result<Option<Vec<u8>>, StorageError> {
        for attempt in 0..=MAX_RETRIES {
            let result = self
                .s3_client
                .get_object()
                .bucket(&self.bucket_name)
                .key(key)
                .send()
                .await;

            match result {
                Ok(output) => {
                    let bytes = output
                        .body
                        .collect()
                        .await
                        .map_err(|e| {
                            StorageError::Io(format!("Failed to read R2 object body: {}", e))
                        })?
                        .into_bytes();
                    return Ok(Some(bytes.to_vec()));
                }
                Err(e) => {
                    if Self::is_retryable_s3_error(&e) && attempt < MAX_RETRIES {
                        warn!(attempt, key, "R2 get_object retryable error, retrying");
                        Self::backoff_sleep(attempt).await;
                        continue;
                    }
                    let service_error = e.into_service_error();
                    if service_error.is_no_such_key() {
                        return Ok(None);
                    }
                    return Err(StorageError::Io(format!(
                        "R2 get_object error: {}",
                        service_error
                    )));
                }
            }
        }
        unreachable!()
    }

    /// Get an object from R2 along with its ETag, with retry on transient errors.
    /// Returns `None` if the object does not exist.
    async fn get_object_with_etag(
        &self,
        key: &str,
    ) -> Result<Option<(Vec<u8>, String)>, StorageError> {
        for attempt in 0..=MAX_RETRIES {
            let result = self
                .s3_client
                .get_object()
                .bucket(&self.bucket_name)
                .key(key)
                .send()
                .await;

            match result {
                Ok(output) => {
                    let etag = output
                        .e_tag()
                        .unwrap_or("")
                        .to_string();
                    let bytes = output
                        .body
                        .collect()
                        .await
                        .map_err(|e| {
                            StorageError::Io(format!("Failed to read R2 object body: {}", e))
                        })?
                        .into_bytes();
                    return Ok(Some((bytes.to_vec(), etag)));
                }
                Err(e) => {
                    if Self::is_retryable_s3_error(&e) && attempt < MAX_RETRIES {
                        warn!(attempt, key, "R2 get_object_with_etag retryable error, retrying");
                        Self::backoff_sleep(attempt).await;
                        continue;
                    }
                    let service_error = e.into_service_error();
                    if service_error.is_no_such_key() {
                        return Ok(None);
                    }
                    return Err(StorageError::Io(format!(
                        "R2 get_object_with_etag error: {}",
                        service_error
                    )));
                }
            }
        }
        unreachable!()
    }

    /// Put an object to R2, with retry on transient errors.
    async fn put_object(&self, key: &str, data: &[u8]) -> Result<(), StorageError> {
        for attempt in 0..=MAX_RETRIES {
            let result = self
                .s3_client
                .put_object()
                .bucket(&self.bucket_name)
                .key(key)
                .body(ByteStream::from(data.to_vec()))
                .send()
                .await;

            match result {
                Ok(_) => return Ok(()),
                Err(e) => {
                    if Self::is_retryable_s3_error(&e) && attempt < MAX_RETRIES {
                        warn!(attempt, key, "R2 put_object retryable error, retrying");
                        Self::backoff_sleep(attempt).await;
                        continue;
                    }
                    return Err(StorageError::Io(format!("R2 put_object error: {}", e)));
                }
            }
        }
        unreachable!()
    }

    /// Conditionally put an object using ETag.
    ///
    /// - If `expected_etag` is `Some(etag)`: uses `If-Match` (update existing).
    /// - If `expected_etag` is `None`: uses `If-None-Match: *` (create new, fail if exists).
    ///
    /// Returns the new ETag on success, or `StorageError::Lock` on 412.
    /// Retries on transient 429/5xx errors.
    async fn put_object_conditional(
        &self,
        key: &str,
        data: &[u8],
        expected_etag: Option<&str>,
    ) -> Result<String, StorageError> {
        for attempt in 0..=MAX_RETRIES {
            let mut req = self
                .s3_client
                .put_object()
                .bucket(&self.bucket_name)
                .key(key)
                .body(ByteStream::from(data.to_vec()));

            if let Some(etag) = expected_etag {
                req = req.if_match(etag);
            } else {
                req = req.if_none_match("*");
            }

            let result = req.send().await;

            match result {
                Ok(output) => {
                    let new_etag = output
                        .e_tag()
                        .unwrap_or("")
                        .to_string();
                    return Ok(new_etag);
                }
                Err(e) => {
                    if Self::is_precondition_failed(&e) {
                        return Err(StorageError::Lock(
                            "ETag mismatch: object was modified concurrently".to_string(),
                        ));
                    }
                    if Self::is_retryable_s3_error(&e) && attempt < MAX_RETRIES {
                        warn!(
                            attempt,
                            key, "R2 put_object_conditional retryable error, retrying"
                        );
                        Self::backoff_sleep(attempt).await;
                        continue;
                    }
                    return Err(StorageError::Io(format!(
                        "R2 put_object_conditional error: {}",
                        e
                    )));
                }
            }
        }
        unreachable!()
    }

    /// Delete an object from R2, with retry on transient errors.
    async fn delete_object(&self, key: &str) -> Result<(), StorageError> {
        for attempt in 0..=MAX_RETRIES {
            let result = self
                .s3_client
                .delete_object()
                .bucket(&self.bucket_name)
                .key(key)
                .send()
                .await;

            match result {
                Ok(_) => return Ok(()),
                Err(e) => {
                    if Self::is_retryable_s3_error(&e) && attempt < MAX_RETRIES {
                        warn!(attempt, key, "R2 delete_object retryable error, retrying");
                        Self::backoff_sleep(attempt).await;
                        continue;
                    }
                    return Err(StorageError::Io(format!("R2 delete_object error: {}", e)));
                }
            }
        }
        unreachable!()
    }

    /// List objects with a prefix, with retry on transient errors.
    async fn list_objects(&self, prefix: &str) -> Result<Vec<String>, StorageError> {
        let mut keys = Vec::new();
        let mut continuation_token: Option<String> = None;

        loop {
            let mut request = self
                .s3_client
                .list_objects_v2()
                .bucket(&self.bucket_name)
                .prefix(prefix);

            if let Some(token) = continuation_token.take() {
                request = request.continuation_token(token);
            }

            let output = {
                let mut last_err = None;
                let mut result = None;
                for attempt in 0..=MAX_RETRIES {
                    match request.clone().send().await {
                        Ok(o) => {
                            result = Some(o);
                            break;
                        }
                        Err(e) => {
                            if Self::is_retryable_s3_error(&e) && attempt < MAX_RETRIES {
                                warn!(
                                    attempt,
                                    prefix, "R2 list_objects retryable error, retrying"
                                );
                                Self::backoff_sleep(attempt).await;
                                last_err = Some(e);
                                continue;
                            }
                            return Err(StorageError::Io(format!(
                                "R2 list_objects error: {}",
                                e
                            )));
                        }
                    }
                }
                result.ok_or_else(|| {
                    StorageError::Io(format!(
                        "R2 list_objects exhausted retries: {:?}",
                        last_err
                    ))
                })?
            };

            if let Some(contents) = output.contents {
                for obj in contents {
                    if let Some(key) = obj.key {
                        keys.push(key);
                    }
                }
            }

            if output.is_truncated.unwrap_or(false) {
                continuation_token = output.next_continuation_token;
            } else {
                break;
            }
        }

        Ok(keys)
    }

    // =========================================================================
    // CAS (Compare-And-Swap) operations
    // =========================================================================

    /// Atomically read-modify-write the session index using ETag-based CAS.
    ///
    /// 1. GET index with ETag
    /// 2. Apply `mutator` to the deserialized index
    /// 3. PUT with If-Match (or If-None-Match: * for new)
    /// 4. On 412, retry from step 1 (up to `CAS_MAX_RETRIES`)
    pub async fn cas_index<F>(
        &self,
        tenant_id: &str,
        mut mutator: F,
    ) -> Result<SessionIndex, StorageError>
    where
        F: FnMut(&mut SessionIndex),
    {
        let key = self.index_key(tenant_id);

        for attempt in 0..CAS_MAX_RETRIES {
            // Step 1: Read current index + ETag
            let (mut index, etag) = match self.get_object_with_etag(&key).await? {
                Some((data, etag)) => {
                    let index: SessionIndex = serde_json::from_slice(&data).map_err(|e| {
                        StorageError::Serialization(format!("Failed to parse index: {}", e))
                    })?;
                    (index, Some(etag))
                }
                None => (SessionIndex::default(), None),
            };

            // Step 2: Apply mutation
            mutator(&mut index);

            // Step 3: Serialize and conditional write
            let json = serde_json::to_vec(&index).map_err(|e| {
                StorageError::Serialization(format!("Failed to serialize index: {}", e))
            })?;

            match self
                .put_object_conditional(&key, &json, etag.as_deref())
                .await
            {
                Ok(_) => {
                    debug!(
                        attempt,
                        tenant_id,
                        sessions = index.sessions.len(),
                        "CAS index succeeded"
                    );
                    return Ok(index);
                }
                Err(StorageError::Lock(_)) => {
                    // Step 4: ETag mismatch — retry with jitter
                    warn!(
                        attempt,
                        tenant_id, "CAS index conflict (412), retrying"
                    );
                    Self::backoff_sleep(attempt).await;
                    continue;
                }
                Err(e) => return Err(e),
            }
        }

        Err(StorageError::Lock(format!(
            "CAS index exhausted {} retries for tenant {}",
            CAS_MAX_RETRIES, tenant_id
        )))
    }

    /// Atomically append WAL entries using ETag-based CAS.
    async fn cas_append_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        entries: &[WalEntry],
    ) -> Result<u64, StorageError> {
        if entries.is_empty() {
            return Ok(0);
        }

        let key = self.wal_key(tenant_id, session_id);

        for attempt in 0..CAS_MAX_RETRIES {
            // Read current WAL + ETag
            let (mut wal_data, etag) = match self.get_object_with_etag(&key).await? {
                Some((data, etag)) if data.len() >= 8 => {
                    let data_len = i64::from_le_bytes(data[..8].try_into().unwrap()) as usize;
                    let used_len = 8 + data_len;
                    let mut truncated = data;
                    truncated.truncate(used_len.min(truncated.len()));
                    (truncated, Some(etag))
                }
                _ => {
                    // New file - start with 8-byte header (data_len = 0)
                    (vec![0u8; 8], None)
                }
            };

            // Append new entries as JSONL
            let mut last_position = 0u64;
            for entry in entries {
                wal_data.extend_from_slice(&entry.patch_json);
                if !entry.patch_json.ends_with(b"\n") {
                    wal_data.push(b'\n');
                }
                last_position = entry.position;
            }

            // Update header with data length
            let data_len = (wal_data.len() - 8) as i64;
            wal_data[..8].copy_from_slice(&data_len.to_le_bytes());

            // Conditional write
            match self
                .put_object_conditional(&key, &wal_data, etag.as_deref())
                .await
            {
                Ok(_) => {
                    debug!(
                        "Appended {} WAL entries, last position: {}",
                        entries.len(),
                        last_position
                    );
                    return Ok(last_position);
                }
                Err(StorageError::Lock(_)) => {
                    warn!(
                        attempt,
                        session_id, "WAL append conflict (412), retrying"
                    );
                    Self::backoff_sleep(attempt).await;
                    continue;
                }
                Err(e) => return Err(e),
            }
        }

        Err(StorageError::Lock(format!(
            "WAL append exhausted {} retries for session {}",
            CAS_MAX_RETRIES, session_id
        )))
    }

    /// Atomically truncate WAL using ETag-based CAS.
    async fn cas_truncate_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        keep_count: u64,
        entries: Vec<WalEntry>,
    ) -> Result<u64, StorageError> {
        let (to_keep, to_remove): (Vec<_>, Vec<_>) =
            entries.into_iter().partition(|e| e.position <= keep_count);

        let removed_count = to_remove.len() as u64;
        if removed_count == 0 {
            return Ok(0);
        }

        let key = self.wal_key(tenant_id, session_id);

        for attempt in 0..CAS_MAX_RETRIES {
            // Get current ETag
            let etag = match self.get_object_with_etag(&key).await? {
                Some((_, etag)) => Some(etag),
                None => return Ok(0),
            };

            // Build new WAL with only kept entries
            let mut wal_data = vec![0u8; 8]; // Header placeholder
            for entry in &to_keep {
                wal_data.extend_from_slice(&entry.patch_json);
                if !entry.patch_json.ends_with(b"\n") {
                    wal_data.push(b'\n');
                }
            }

            // Update header
            let data_len = (wal_data.len() - 8) as i64;
            wal_data[..8].copy_from_slice(&data_len.to_le_bytes());

            match self
                .put_object_conditional(&key, &wal_data, etag.as_deref())
                .await
            {
                Ok(_) => {
                    debug!(
                        "Truncated WAL, removed {} entries, kept {}",
                        removed_count,
                        to_keep.len()
                    );
                    return Ok(removed_count);
                }
                Err(StorageError::Lock(_)) => {
                    warn!(
                        attempt,
                        session_id, "WAL truncate conflict (412), retrying"
                    );
                    Self::backoff_sleep(attempt).await;
                    continue;
                }
                Err(e) => return Err(e),
            }
        }

        Err(StorageError::Lock(format!(
            "WAL truncate exhausted {} retries for session {}",
            CAS_MAX_RETRIES, session_id
        )))
    }
}

/// Simple jitter: random-ish value 0..50ms using timestamp nanos.
fn rand_jitter() -> u64 {
    use std::time::SystemTime;
    SystemTime::now()
        .duration_since(SystemTime::UNIX_EPOCH)
        .map(|d| d.subsec_nanos() as u64 % 50)
        .unwrap_or(0)
}

#[async_trait]
impl StorageBackend for R2Storage {
    fn backend_name(&self) -> &'static str {
        "r2"
    }

    // =========================================================================
    // Session Operations
    // =========================================================================

    #[instrument(skip(self), level = "debug")]
    async fn load_session(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Option<Vec<u8>>, StorageError> {
        let key = self.session_key(tenant_id, session_id);
        let result = self.get_object(&key).await?;
        if result.is_some() {
            debug!("Loaded session {} from R2", session_id);
        }
        Ok(result)
    }

    #[instrument(skip(self, data), level = "debug", fields(data_len = data.len()))]
    async fn save_session(
        &self,
        tenant_id: &str,
        session_id: &str,
        data: &[u8],
    ) -> Result<(), StorageError> {
        let key = self.session_key(tenant_id, session_id);
        self.put_object(&key, data).await?;
        debug!("Saved session {} to R2 ({} bytes)", session_id, data.len());
        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn delete_session(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError> {
        let session_key = self.session_key(tenant_id, session_id);
        let wal_key = self.wal_key(tenant_id, session_id);

        // Check if session exists
        let existed = self.get_object(&session_key).await?.is_some();

        // Delete session file
        if let Err(e) = self.delete_object(&session_key).await {
            warn!("Failed to delete session file: {}", e);
        }

        // Delete WAL
        if let Err(e) = self.delete_object(&wal_key).await {
            warn!("Failed to delete WAL file: {}", e);
        }

        // Delete all checkpoints
        let checkpoints = self.list_checkpoints(tenant_id, session_id).await?;
        for ckpt in checkpoints {
            let ckpt_key = self.checkpoint_key(tenant_id, session_id, ckpt.position);
            if let Err(e) = self.delete_object(&ckpt_key).await {
                warn!("Failed to delete checkpoint: {}", e);
            }
        }

        debug!("Deleted session {} (existed: {})", session_id, existed);
        Ok(existed)
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_sessions(&self, tenant_id: &str) -> Result<Vec<SessionInfo>, StorageError> {
        let prefix = format!("{}/sessions/", tenant_id);
        let keys = self.list_objects(&prefix).await?;

        let mut sessions = Vec::new();
        for key in keys {
            // Only include .docx files that aren't checkpoints
            if key.ends_with(".docx") && !key.contains(".ckpt.") {
                let session_id = key
                    .strip_prefix(&prefix)
                    .and_then(|s| s.strip_suffix(".docx"))
                    .unwrap_or_default()
                    .to_string();

                if !session_id.is_empty() {
                    // Get object metadata for size/timestamps
                    let head = self
                        .s3_client
                        .head_object()
                        .bucket(&self.bucket_name)
                        .key(&key)
                        .send()
                        .await;

                    let (size_bytes, modified_at) = match head {
                        Ok(output) => {
                            let size = output.content_length.unwrap_or(0) as u64;
                            let modified = output
                                .last_modified
                                .and_then(|dt| {
                                    chrono::DateTime::from_timestamp(dt.secs(), dt.subsec_nanos())
                                })
                                .unwrap_or_else(chrono::Utc::now);
                            (size, modified)
                        }
                        Err(_) => (0, chrono::Utc::now()),
                    };

                    sessions.push(SessionInfo {
                        session_id,
                        source_path: None,
                        created_at: modified_at, // R2 doesn't store creation time
                        modified_at,
                        size_bytes,
                    });
                }
            }
        }

        debug!(
            "Listed {} sessions for tenant {}",
            sessions.len(),
            tenant_id
        );
        Ok(sessions)
    }

    #[instrument(skip(self), level = "debug")]
    async fn session_exists(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<bool, StorageError> {
        let key = self.session_key(tenant_id, session_id);
        let result = self
            .s3_client
            .head_object()
            .bucket(&self.bucket_name)
            .key(&key)
            .send()
            .await;

        match result {
            Ok(_) => Ok(true),
            Err(e) => {
                let service_error = e.into_service_error();
                if service_error.is_not_found() {
                    Ok(false)
                } else {
                    Err(StorageError::Io(format!(
                        "R2 head_object error: {}",
                        service_error
                    )))
                }
            }
        }
    }

    // =========================================================================
    // Index Operations (stored in R2 with ETag-based CAS)
    // =========================================================================

    #[instrument(skip(self), level = "debug")]
    async fn load_index(&self, tenant_id: &str) -> Result<Option<SessionIndex>, StorageError> {
        let key = self.index_key(tenant_id);
        match self.get_object(&key).await? {
            Some(data) => {
                let index: SessionIndex = serde_json::from_slice(&data).map_err(|e| {
                    StorageError::Serialization(format!("Failed to parse index: {}", e))
                })?;
                debug!(
                    "Loaded index with {} sessions from R2",
                    index.sessions.len()
                );
                Ok(Some(index))
            }
            None => Ok(None),
        }
    }

    #[instrument(skip(self, index), level = "debug", fields(sessions = index.sessions.len()))]
    async fn save_index(
        &self,
        tenant_id: &str,
        index: &SessionIndex,
    ) -> Result<(), StorageError> {
        let key = self.index_key(tenant_id);
        let json = serde_json::to_vec(index).map_err(|e| {
            StorageError::Serialization(format!("Failed to serialize index: {}", e))
        })?;
        self.put_object(&key, &json).await?;
        debug!("Saved index with {} sessions to R2", index.sessions.len());
        Ok(())
    }

    // =========================================================================
    // WAL Operations (ETag-based CAS for atomic append/truncate)
    // =========================================================================

    #[instrument(skip(self, entries), level = "debug", fields(entries_count = entries.len()))]
    async fn append_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        entries: &[WalEntry],
    ) -> Result<u64, StorageError> {
        self.cas_append_wal(tenant_id, session_id, entries).await
    }

    #[instrument(skip(self), level = "debug")]
    async fn read_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        from_position: u64,
        limit: Option<u64>,
    ) -> Result<(Vec<WalEntry>, bool), StorageError> {
        let key = self.wal_key(tenant_id, session_id);

        let raw_data = match self.get_object(&key).await? {
            Some(data) => data,
            None => return Ok((vec![], false)),
        };

        if raw_data.len() < 8 {
            return Ok((vec![], false));
        }

        // Parse header
        let data_len = i64::from_le_bytes(raw_data[..8].try_into().unwrap()) as usize;
        if data_len == 0 {
            return Ok((vec![], false));
        }

        // Extract JSONL portion
        let end = (8 + data_len).min(raw_data.len());
        let jsonl_data = &raw_data[8..end];

        let content = std::str::from_utf8(jsonl_data).map_err(|e| {
            StorageError::Io(format!("WAL is not valid UTF-8: {}", e))
        })?;

        // Parse JSONL - each line is a .NET WalEntry JSON
        let mut entries = Vec::new();
        let limit = limit.unwrap_or(u64::MAX);
        let mut position = 1u64;

        for line in content.lines() {
            let line = line.trim();
            if line.is_empty() {
                continue;
            }

            if position >= from_position {
                let value: serde_json::Value = serde_json::from_str(line).map_err(|e| {
                    StorageError::Serialization(format!(
                        "Failed to parse WAL entry at position {}: {}",
                        position, e
                    ))
                })?;

                let timestamp = value
                    .get("timestamp")
                    .and_then(|v| v.as_str())
                    .and_then(|s| chrono::DateTime::parse_from_rfc3339(s).ok())
                    .map(|dt| dt.with_timezone(&chrono::Utc))
                    .unwrap_or_else(chrono::Utc::now);

                entries.push(WalEntry {
                    position,
                    operation: String::new(),
                    path: String::new(),
                    patch_json: line.as_bytes().to_vec(),
                    timestamp,
                });

                if entries.len() as u64 >= limit {
                    return Ok((entries, true));
                }
            }

            position += 1;
        }

        debug!(
            "Read {} WAL entries from position {}",
            entries.len(),
            from_position
        );
        Ok((entries, false))
    }

    #[instrument(skip(self), level = "debug")]
    async fn truncate_wal(
        &self,
        tenant_id: &str,
        session_id: &str,
        keep_count: u64,
    ) -> Result<u64, StorageError> {
        let (entries, _) = self.read_wal(tenant_id, session_id, 0, None).await?;
        self.cas_truncate_wal(tenant_id, session_id, keep_count, entries)
            .await
    }

    // =========================================================================
    // Checkpoint Operations
    // =========================================================================

    #[instrument(skip(self, data), level = "debug", fields(data_len = data.len()))]
    async fn save_checkpoint(
        &self,
        tenant_id: &str,
        session_id: &str,
        position: u64,
        data: &[u8],
    ) -> Result<(), StorageError> {
        let key = self.checkpoint_key(tenant_id, session_id, position);
        self.put_object(&key, data).await?;
        debug!(
            "Saved checkpoint at position {} ({} bytes)",
            position,
            data.len()
        );
        Ok(())
    }

    #[instrument(skip(self), level = "debug")]
    async fn load_checkpoint(
        &self,
        tenant_id: &str,
        session_id: &str,
        position: u64,
    ) -> Result<Option<(Vec<u8>, u64)>, StorageError> {
        if position == 0 {
            // Load latest checkpoint
            let checkpoints = self.list_checkpoints(tenant_id, session_id).await?;
            if let Some(latest) = checkpoints.last() {
                let key = self.checkpoint_key(tenant_id, session_id, latest.position);
                if let Some(data) = self.get_object(&key).await? {
                    debug!(
                        "Loaded latest checkpoint at position {} ({} bytes)",
                        latest.position,
                        data.len()
                    );
                    return Ok(Some((data, latest.position)));
                }
            }
            return Ok(None);
        }

        let key = self.checkpoint_key(tenant_id, session_id, position);
        match self.get_object(&key).await? {
            Some(data) => {
                debug!(
                    "Loaded checkpoint at position {} ({} bytes)",
                    position,
                    data.len()
                );
                Ok(Some((data, position)))
            }
            None => Ok(None),
        }
    }

    #[instrument(skip(self), level = "debug")]
    async fn list_checkpoints(
        &self,
        tenant_id: &str,
        session_id: &str,
    ) -> Result<Vec<CheckpointInfo>, StorageError> {
        let prefix = format!("{}/sessions/{}.ckpt.", tenant_id, session_id);
        let keys = self.list_objects(&prefix).await?;

        let mut checkpoints = Vec::new();
        for key in keys {
            if key.ends_with(".docx") {
                // Extract position from key: {tenant}/sessions/{session}.ckpt.{position}.docx
                let position_str = key
                    .strip_prefix(&prefix)
                    .and_then(|s| s.strip_suffix(".docx"))
                    .unwrap_or("0");

                if let Ok(position) = position_str.parse::<u64>() {
                    // Get object metadata
                    let head = self
                        .s3_client
                        .head_object()
                        .bucket(&self.bucket_name)
                        .key(&key)
                        .send()
                        .await;

                    let (size_bytes, created_at) = match head {
                        Ok(output) => {
                            let size = output.content_length.unwrap_or(0) as u64;
                            let created = output
                                .last_modified
                                .and_then(|dt| {
                                    chrono::DateTime::from_timestamp(dt.secs(), dt.subsec_nanos())
                                })
                                .unwrap_or_else(chrono::Utc::now);
                            (size, created)
                        }
                        Err(_) => (0, chrono::Utc::now()),
                    };

                    checkpoints.push(CheckpointInfo {
                        position,
                        created_at,
                        size_bytes,
                    });
                }
            }
        }

        // Sort by position
        checkpoints.sort_by_key(|c| c.position);

        debug!(
            "Listed {} checkpoints for session {}",
            checkpoints.len(),
            session_id
        );
        Ok(checkpoints)
    }
}
