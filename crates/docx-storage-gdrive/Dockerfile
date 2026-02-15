# =============================================================================
# docx-storage-gdrive Dockerfile
# Multi-stage build for the Google Drive sync/watch gRPC server
# =============================================================================

# Stage 1: Build
FROM rust:1.93-slim-bookworm AS builder

WORKDIR /build

# Install build dependencies
RUN apt-get update && apt-get install -y \
    pkg-config \
    protobuf-compiler \
    libssl-dev \
    && rm -rf /var/lib/apt/lists/*

# Copy workspace files
COPY Cargo.toml Cargo.lock ./
COPY proto/ ./proto/
COPY crates/ ./crates/

# Build the gdrive server
RUN cargo build --release --package docx-storage-gdrive

# Stage 2: Runtime
FROM debian:bookworm-slim AS runtime

# Install runtime dependencies (ca-certificates for Google API TLS)
RUN apt-get update && apt-get install -y \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd -m -u 1000 docx
USER docx

WORKDIR /app

# Copy the binary from builder
COPY --from=builder /build/target/release/docx-storage-gdrive /app/docx-storage-gdrive

# Environment defaults
ENV RUST_LOG=info
ENV GRPC_HOST=0.0.0.0
ENV GRPC_PORT=50052

# Required environment variables (must be set at runtime):
# GOOGLE_CREDENTIALS_JSON  (service account key JSON string or file path)

# Optional:
# WATCH_POLL_INTERVAL (default: 60 seconds)

# Expose gRPC port
EXPOSE 50052

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD ["timeout", "5", "sh", "-c", "echo > /dev/tcp/localhost/50052"] || exit 1

# Run the server
ENTRYPOINT ["/app/docx-storage-gdrive"]
