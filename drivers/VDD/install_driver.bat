@echo off
echo Installing Virtual Display Driver (IddCx)...
cd /d "%~dp0"
pnputil /add-driver MttVDD.inf /install
echo.
if %errorlevel% equ 0 (
    echo [SUCCESS] Virtual Display Driver installed successfully!
) else (
    echo [INFO] Please ensure this script was run with Administrator privileges.
)
pause
