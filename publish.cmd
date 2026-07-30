@echo off
rem One-click wrapper around publish.ps1 — works from a regular cmd.exe and
rem from File Explorer (right-click -> Run). Bypasses execution policy so
rem operators don't need to fiddle with PowerShell config.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
echo.
pause
