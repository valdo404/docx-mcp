# run-docker-windows.ps1
# Script to run docx-mcp Docker container on Windows with OneDrive support

param(
    [string]$DocumentsPath = "$env:USERPROFILE\OneDrive\Documents",
    [string]$Image = "valdo404/docx-mcp:latest",
    [switch]$Pull,
    [switch]$Interactive
)

Write-Host "📎 Doccy - Docker Launcher for Windows" -ForegroundColor Cyan
Write-Host ""

# Check if Docker is running
try {
    docker info | Out-Null
} catch {
    Write-Host "Error: Docker is not running. Please start Docker Desktop." -ForegroundColor Red
    exit 1
}

# Pull latest image if requested
if ($Pull) {
    Write-Host "Pulling latest image..." -ForegroundColor Yellow
    docker pull $Image
    Write-Host ""
}

# Validate documents path
if (-not (Test-Path $DocumentsPath)) {
    Write-Host "Error: Documents path not found: $DocumentsPath" -ForegroundColor Red
    Write-Host "Use -DocumentsPath to specify a different path" -ForegroundColor Yellow
    exit 1
}

# Check for OneDrive "Files On-Demand" warning
if ($DocumentsPath -like "*OneDrive*") {
    Write-Host "OneDrive detected. Make sure 'Files On-Demand' is disabled for this folder:" -ForegroundColor Yellow
    Write-Host "  Right-click folder -> 'Always keep on this device'" -ForegroundColor Gray
    Write-Host ""
}

# Convert Windows path to Docker-compatible format
$DockerPath = $DocumentsPath -replace '\\', '/' -replace '^([A-Za-z]):', '/$1'
$DockerPath = $DockerPath.ToLower()

Write-Host "Documents path: $DocumentsPath" -ForegroundColor Gray
Write-Host "Docker mount:   $DockerPath -> /data" -ForegroundColor Gray
Write-Host ""

# Build docker run command
$dockerArgs = @(
    "run"
    "--rm"
    "-v", "${DocumentsPath}:/data"
    "-v", "docx-sessions:/home/app/.docx-mcp/sessions"
)

if ($Interactive) {
    $dockerArgs += "-it"
    Write-Host "Running in interactive mode..." -ForegroundColor Green
} else {
    $dockerArgs += "-i"
    Write-Host "Running in MCP mode (stdin/stdout)..." -ForegroundColor Green
}

$dockerArgs += $Image

Write-Host "Command: docker $($dockerArgs -join ' ')" -ForegroundColor Gray
Write-Host ""

# Run container
& docker @dockerArgs
