# =============================================================================
# docx-mcp-proxy Dockerfile
# Multi-stage build for the SSE/HTTP proxy with PAT authentication
# =============================================================================

# Stage 1: Build
FROM rust:1.85-slim-bookworm AS builder

WORKDIR /build

# Install build dependencies
RUN apt-get update && apt-get install -y \
    pkg-config \
    protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*

# Copy workspace files
COPY Cargo.toml Cargo.lock ./
COPY proto/ ./proto/
COPY crates/ ./crates/

# Build the proxy server
RUN cargo build --release --package docx-mcp-proxy

# Stage 2: Runtime
FROM debian:bookworm-slim AS runtime

# Install runtime dependencies
RUN apt-get update && apt-get install -y \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Create non-root user
RUN useradd -m -u 1000 docx
USER docx

WORKDIR /app

# Copy the binary from builder
COPY --from=builder /build/target/release/docx-mcp-proxy /app/docx-mcp-proxy

# Copy the MCP binary (built separately or mounted)
# Note: In production, docx-mcp binary should be copied here
# COPY --from=mcp-builder /app/docx-mcp /app/docx-mcp

# Environment defaults
ENV RUST_LOG=info
ENV PROXY_HOST=0.0.0.0
ENV PROXY_PORT=8080

# Required environment variables (must be set at runtime):
# CLOUDFLARE_ACCOUNT_ID
# CLOUDFLARE_API_TOKEN
# D1_DATABASE_ID
# STORAGE_GRPC_URL (e.g., http://storage:50051)

# Expose HTTP port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD ["curl", "-f", "http://localhost:8080/health"] || exit 1

# Run the proxy
ENTRYPOINT ["/app/docx-mcp-proxy"]
