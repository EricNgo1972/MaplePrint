@echo off
setlocal
rem One-click wrapper around publish.ps1 — works from a regular cmd.exe and
rem from File Explorer (right-click -> Run). Bypasses execution policy so
rem operators don't need to fiddle with PowerShell config.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
rem Captured before anything else runs: explorer.exe reports a non-zero exit
rem code even when it succeeds, so asking it later would lose the real result.
set "RESULT=%ERRORLEVEL%"

if not "%RESULT%"=="0" (
    echo.
    echo Publish failed with exit code %RESULT%. Leaving the output folder alone.
    echo.
    pause
    exit /b %RESULT%
)

rem publish.ps1 empties dist and leaves exactly one .exe behind, always under
rem this name — the version lives in the file's properties, not its filename.
set "ARTIFACT=%~dp0dist\MaplePrint.exe"
if not exist "%ARTIFACT%" set "ARTIFACT="

if defined ARTIFACT (
    rem /select opens the folder with the new file already highlighted, which
    rem saves hunting for it when several versions have been built.
    explorer /select,"%ARTIFACT%"
) else (
    if exist "%~dp0dist" explorer "%~dp0dist"
)

echo.
pause
