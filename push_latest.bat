@echo off
setlocal

:: Re-run as admin if not already elevated
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

git rev-parse --show-toplevel >nul 2>&1
if errorlevel 1 goto error

git add .
if errorlevel 1 goto error

git commit -m "Overwrite with latest version"
if errorlevel 1 goto error

git push --force
if errorlevel 1 goto error

powershell -Command "Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('Completed successfully.','Git Push Complete')"
exit /b 0

:error
powershell -Command "Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('An error occurred. The script stopped before completing.','Git Push Failed')"
exit /b 1