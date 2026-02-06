# =============================================================================
# docx-mcp-storage Dockerfile
# Multi-stage build for the gRPC storage server
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

# Build the storage server
RUN cargo build --release --package docx-mcp-storage

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
COPY --from=builder /build/target/release/docx-mcp-storage /app/docx-mcp-storage

# Create data directory
RUN mkdir -p /app/data

# Environment defaults
ENV RUST_LOG=info
ENV GRPC_HOST=0.0.0.0
ENV GRPC_PORT=50051
ENV STORAGE_BACKEND=local
ENV LOCAL_STORAGE_DIR=/app/data

# Expose gRPC port
EXPOSE 50051

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD ["timeout", "5", "grpc_health_probe", "-addr=localhost:50051"] || exit 1

# Run the server
ENTRYPOINT ["/app/docx-mcp-storage"]
CMD ["--transport", "tcp"]
