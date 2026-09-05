@echo off
:: OpenWinSidecar Service - Run as Administrator
:: DXGI Desktop Duplication requires admin for GPU-direct capture

net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting Administrator privileges...
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo ============================================
echo   OpenWinSidecar DXGI Turbo Service
echo   Running as Administrator
echo ============================================
echo.

cd /d "%~dp0"
dotnet run --project "src\OpenWinSidecar.Service\OpenWinSidecar.Service.csproj"

pause
