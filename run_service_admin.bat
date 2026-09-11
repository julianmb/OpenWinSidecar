@echo off
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [OpenWinSidecar] Requesting Administrator Privileges...
    powershell -Command "Start-Process '%~0' -Verb RunAs"
    exit /b
)

title OpenWinSidecar Turbo Streaming Service (Administrator)
cd /d "%~dp0"

echo [OpenWinSidecar] Stopping stale service instances...
taskkill /F /IM OpenWinSidecar.Service.exe >nul 2>&1
taskkill /F /IM OpenWinSidecar.Service.exe >nul 2>&1
taskkill /F /IM ffmpeg.exe >nul 2>&1

echo [OpenWinSidecar] Launching OpenWinSidecar.Service...
dotnet run --project src/OpenWinSidecar.Service/OpenWinSidecar.Service.csproj
pause
