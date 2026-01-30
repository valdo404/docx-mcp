FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
ARG TARGETARCH

WORKDIR /src

# Copy everything and publish (single step: NativeAOT runtime packs
# are only resolved during publish, so a separate restore cannot
# fully cache them)
COPY . .
RUN dotnet publish src/DocxMcp/DocxMcp.csproj \
    --configuration Release \
    -a $TARGETARCH \
    -o /app

# Runtime: minimal image with only the binary
# The runtime-deps image already provides an 'app' user/group
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-preview AS runtime

WORKDIR /app
COPY --from=build /app .

USER app

ENTRYPOINT ["./docx-mcp"]
