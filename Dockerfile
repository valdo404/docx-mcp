# =============================================================================
# docx-mcp Full Stack Dockerfile
# Builds MCP server, CLI, and local storage server
# =============================================================================

# Stage 1: Build Rust storage server
FROM rust:1.85-slim-bookworm AS rust-builder

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

# Build the storage server
RUN cargo build --release --package docx-mcp-storage

# Stage 2: Build .NET MCP server and CLI
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS dotnet-builder

# NativeAOT requires clang as the platform linker
RUN apt-get update && \
    apt-get install -y --no-install-recommends clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy .NET source
COPY DocxMcp.sln ./
COPY proto/ ./proto/
COPY src/ ./src/
COPY tests/ ./tests/

# Build MCP server and CLI as NativeAOT binaries
RUN dotnet publish src/DocxMcp/DocxMcp.csproj \
    --configuration Release \
    -o /app

RUN dotnet publish src/DocxMcp.Cli/DocxMcp.Cli.csproj \
    --configuration Release \
    -o /app/cli

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-preview AS runtime

# Install curl for health checks
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy binaries from builders
COPY --from=rust-builder /rust/target/release/docx-mcp-storage ./
COPY --from=dotnet-builder /app/docx-mcp ./
COPY --from=dotnet-builder /app/cli/docx-cli ./

# Create directories
RUN mkdir -p /home/app/.docx-mcp/sessions && \
    mkdir -p /app/data && \
    chown -R app:app /home/app/.docx-mcp /app/data

# Volumes for data persistence
VOLUME /home/app/.docx-mcp/sessions
VOLUME /app/data

USER app

# Environment variables
ENV DOCX_SESSIONS_DIR=/home/app/.docx-mcp/sessions
# Socket path is dynamically generated with PID for uniqueness
ENV LOCAL_STORAGE_DIR=/app/data
ENV RUST_LOG=info

# Default entrypoint is the MCP server
ENTRYPOINT ["./docx-mcp"]

# =============================================================================
# Alternative entrypoints:
# - Storage server: docker run --entrypoint ./docx-mcp-storage ...
# - CLI: docker run --entrypoint ./docx-cli ...
# =============================================================================
