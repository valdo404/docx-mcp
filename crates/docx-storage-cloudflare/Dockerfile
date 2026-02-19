# =============================================================================
# docx-storage-cloudflare Dockerfile
# Multi-stage build for the gRPC storage server (Cloudflare R2 + KV)
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

# Build the storage server
RUN cargo build --release --package docx-storage-cloudflare

# Stage 2: Runtime
FROM debian:bookworm-slim AS runtime

# Install runtime dependencies (minimal)
RUN apt-get update && apt-get install -y \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd -m -u 1000 docx
USER docx

WORKDIR /app

# Copy the binary from builder
COPY --from=builder /build/target/release/docx-storage-cloudflare /app/docx-storage-cloudflare

# Environment defaults
ENV RUST_LOG=info
ENV GRPC_HOST=0.0.0.0
ENV GRPC_PORT=50051

# Required environment variables (must be set at runtime):
# CLOUDFLARE_ACCOUNT_ID
# CLOUDFLARE_API_TOKEN
# R2_BUCKET_NAME
# KV_NAMESPACE_ID
# R2_ACCESS_KEY_ID
# R2_SECRET_ACCESS_KEY

# Optional:
# WATCH_POLL_INTERVAL (default: 30 seconds)

# Expose gRPC port
EXPOSE 50051

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD ["timeout", "5", "grpc_health_probe", "-addr=localhost:50051"] || exit 1

# Run the server
ENTRYPOINT ["/app/docx-storage-cloudflare"]
CMD ["--transport", "tcp"]
