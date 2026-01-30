FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build

# NativeAOT requires clang as the platform linker
RUN apt-get update && \
    apt-get install -y --no-install-recommends clang zlib1g-dev && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src

COPY . .
RUN dotnet publish src/DocxMcp/DocxMcp.csproj \
    --configuration Release \
    -o /app

# Runtime: minimal image with only the binary
# The runtime-deps image already provides an 'app' user/group
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-preview AS runtime

WORKDIR /app
COPY --from=build /app .

USER app

ENTRYPOINT ["./docx-mcp"]
