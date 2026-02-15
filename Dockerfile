# =============================================================================
# docx-mcp Full Stack Dockerfile
# Builds MCP server and CLI with embedded Rust storage (single binary)
# =============================================================================

# Stage 1: Build Rust staticlib
FROM rust:1.93-slim-bookworm AS rust-builder

WORKDIR /rust

# Install build dependencies
RUN apt-get update && apt-get install -y \
    pkg-config \
    protobuf-compiler \
    && rm -rf /var/lib/apt/lists/*

# Copy Rust workspace files
COPY Cargo.toml Cargo.lock ./
COPY proto/ ./proto/
COPY crates/ ./crates/

# Build the staticlib for embedding
RUN cargo build --release --package docx-storage-local --lib

# Stage 2: Build .NET MCP server and CLI with embedded Rust storage
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS dotnet-builder

# NativeAOT requires clang as the platform linker
RUN apt-get update && \
    apt-get install -y --no-install-recommends clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy Rust staticlib from builder
COPY --from=rust-builder /rust/target/release/libdocx_storage_local.a /rust-lib/

# Copy .NET source
COPY DocxMcp.sln ./
COPY proto/ ./proto/
COPY src/ ./src/
COPY tests/ ./tests/

# Build MCP server with embedded storage
RUN dotnet publish src/DocxMcp/DocxMcp.csproj \
    --configuration Release \
    -p:RustStaticLibPath=/rust-lib/libdocx_storage_local.a \
    -o /app

# Build CLI with embedded storage
RUN dotnet publish src/DocxMcp.Cli/DocxMcp.Cli.csproj \
    --configuration Release \
    -p:RustStaticLibPath=/rust-lib/libdocx_storage_local.a \
    -o /app/cli

# Stage 3: Runtime (single binary, no separate storage process)
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-preview AS runtime

# Install curl for health checks
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy binaries from builder (no docx-storage-local needed!)
COPY --from=dotnet-builder /app/docx-mcp ./
COPY --from=dotnet-builder /app/cli/docx-cli ./

# Create directories
RUN mkdir -p /app/data && \
    chown -R app:app /app/data

# Volumes for data persistence
VOLUME /app/data

USER app

# Environment variables
ENV LOCAL_STORAGE_DIR=/app/data

# Default entrypoint is the MCP server
ENTRYPOINT ["./docx-mcp"]

# =============================================================================
# Alternative entrypoints:
# - CLI: docker run --entrypoint ./docx-cli ...
# - Remote storage mode: docker run -e STORAGE_GRPC_URL=http://host:50051 ...
# =============================================================================
