@echo off
setlocal

:: Re-run as admin if not already elevated
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

:: Ensure we're inside a git repo
git rev-parse --show-toplevel >nul 2>&1
if errorlevel 1 goto error

:: Fetch latest changes first
git fetch
if errorlevel 1 goto error

:: Pull (you can change strategy if needed)
git pull
if errorlevel 1 goto error

powershell -Command "Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('Repository updated successfully.','Git Pull Complete')"
exit /b 0

:error
powershell -Command "Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('An error occurred during git pull.','Git Pull Failed')"
exit /b 1