FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build

# NativeAOT requires clang as the platform linker
RUN apt-get update && \
    apt-get install -y --no-install-recommends clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY . .

# Build both MCP server and CLI as NativeAOT binaries
RUN dotnet publish src/DocxMcp/DocxMcp.csproj \
    --configuration Release \
    -o /app

RUN dotnet publish src/DocxMcp.Cli/DocxMcp.Cli.csproj \
    --configuration Release \
    -o /app/cli

# Runtime: minimal image with only the binaries
# The runtime-deps image already provides an 'app' user/group
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-preview AS runtime

WORKDIR /app
COPY --from=build /app/docx-mcp .
COPY --from=build /app/cli/docx-cli .

# Sessions persistence directory (WAL, baselines, checkpoints)
RUN mkdir -p /home/app/.docx-mcp/sessions && \
    chown -R app:app /home/app/.docx-mcp
VOLUME /home/app/.docx-mcp/sessions

USER app

ENV DOCX_SESSIONS_DIR=/home/app/.docx-mcp/sessions

ENTRYPOINT ["./docx-mcp"]
