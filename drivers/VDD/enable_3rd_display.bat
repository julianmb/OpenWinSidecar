@echo off
echo ===================================================
echo   Enabling 3rd Virtual Display for iPad (IddCx)
echo ===================================================
cd /d "%~dp0"
"%~dp0control\Dependencies\devcon.exe" install "%~dp0control\SignedDrivers\x86\VDD\MttVDD.inf" Root\MttVDD
echo.
echo Device node configured.
pause
