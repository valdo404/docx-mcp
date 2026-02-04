@echo off
REM run-docker-windows.bat
REM Simple launcher for docx-mcp on Windows with OneDrive

setlocal

REM Configuration - modify these paths as needed
set DOCUMENTS_PATH=%USERPROFILE%\OneDrive\Documents
set IMAGE=valdo404/docx-mcp:latest

echo.
echo  📎 Doccy - Docker Launcher
echo.

REM Check if Docker is available
docker info >nul 2>&1
if errorlevel 1 (
    echo ERROR: Docker is not running. Please start Docker Desktop.
    pause
    exit /b 1
)

REM Check if path exists
if not exist "%DOCUMENTS_PATH%" (
    echo ERROR: Documents path not found: %DOCUMENTS_PATH%
    echo Edit this script to set the correct DOCUMENTS_PATH
    pause
    exit /b 1
)

echo Documents: %DOCUMENTS_PATH%
echo.

REM Pull if first argument is "pull"
if "%1"=="pull" (
    echo Pulling latest image...
    docker pull %IMAGE%
    echo.
)

REM Run container
echo Starting container...
docker run -i --rm ^
    -v "%DOCUMENTS_PATH%:/data" ^
    -v "docx-sessions:/home/app/.docx-mcp/sessions" ^
    %IMAGE%
