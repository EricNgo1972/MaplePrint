#!/usr/bin/env pwsh
<#
    Builds MaplePrint as a single self-contained Windows x64 .exe and drops it
    into ./dist as MaplePrint-{version}.exe. No .NET runtime is required on the
    target machine — the counter PC downloads one file and double-clicks it.

    Usage:
        pwsh ./publish.ps1
        # or, from cmd:
        publish.cmd
#>

param(
    [string]$Configuration = 'Release',
    [string]$Runtime       = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'MaplePrint/MaplePrint.csproj'
$dist = Join-Path $root 'dist'

# Read <Version> from the .csproj so the artifact filename is self-describing.
$xml     = [xml](Get-Content $proj)
$version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.0.0' }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

Write-Host "Publishing MaplePrint v$version ($Runtime, $Configuration) -> $dist" -ForegroundColor Cyan

# Why these flags:
#   PublishSingleFile=true              collapse DLLs into the .exe
#   SelfContained=true                  bundle the .NET runtime
#   EnableCompressionInSingleFile=true  ~30% smaller download
#   DebugType=embedded                  readable stack traces, no loose .pdb
# Trimming is OFF on purpose — WinForms and SignalR's JSON protocol both lean on
# reflection that trimming silently breaks. A crash on the counter at open is
# not worth a few megabytes.

& dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=embedded `
    -p:Version=$version `
    -o $dist `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Drop everything except the .exe — the only file anyone downloads.
Get-ChildItem $dist -File | Where-Object { $_.Extension -ne '.exe' } | Remove-Item -Force
Get-ChildItem $dist -Directory | Remove-Item -Recurse -Force

$built = Join-Path $dist 'MaplePrint.exe'
$named = Join-Path $dist "MaplePrint-$version.exe"
if (Test-Path $built) { Move-Item $built $named -Force }

$size = [math]::Round((Get-Item $named).Length / 1MB, 1)
Write-Host "Done: $named ($size MB)" -ForegroundColor Green
