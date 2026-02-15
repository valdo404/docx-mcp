use clap::Parser;

/// Configuration for the docx-storage-cloudflare server.
#[derive(Parser, Debug, Clone)]
#[command(name = "docx-storage-cloudflare")]
#[command(about = "Cloudflare R2 gRPC storage server for docx-mcp")]
pub struct Config {
    /// TCP host to bind to
    #[arg(long, default_value = "0.0.0.0", env = "GRPC_HOST")]
    pub host: String,

    /// TCP port to bind to
    #[arg(long, default_value = "50051", env = "GRPC_PORT")]
    pub port: u16,

    /// Cloudflare account ID
    #[arg(long, env = "CLOUDFLARE_ACCOUNT_ID")]
    pub cloudflare_account_id: String,

    /// R2 bucket name for session/checkpoint/index storage
    #[arg(long, env = "R2_BUCKET_NAME")]
    pub r2_bucket_name: String,

    /// R2 access key ID (for S3-compatible API)
    #[arg(long, env = "R2_ACCESS_KEY_ID")]
    pub r2_access_key_id: String,

    /// R2 secret access key (for S3-compatible API)
    #[arg(long, env = "R2_SECRET_ACCESS_KEY")]
    pub r2_secret_access_key: String,
}

impl Config {
    /// Get the R2 endpoint URL for S3-compatible API.
    pub fn r2_endpoint(&self) -> String {
        format!(
            "https://{}.r2.cloudflarestorage.com",
            self.cloudflare_account_id
        )
    }
}
