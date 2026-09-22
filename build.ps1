#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Local build helper for the Callspire multi-project solution.

.DESCRIPTION
  Builds one or more platform targets in Release (default) or Debug.
  Artifacts land in publish/<platform>/.

.PARAMETER Target
  One of: all | core | windows | macos | linux
  Default: all

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Publish
  If present, runs dotnet publish after a successful build.

.EXAMPLE
  .\build.ps1                          # Build all targets
  .\build.ps1 -Target windows          # Windows only
  .\build.ps1 -Configuration Debug     # Debug build of all targets
#>

param(
    [ValidateSet("all","core","windows","macos","linux")]
    [string]$Target = "all",
    [ValidateSet("Release","Debug")]
    [string]$Configuration = "Release",
    [switch]$Publish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root = $PSScriptRoot

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$DotNetArguments
    )

    if ($DotNetArguments.Count -eq 0) {
        throw "Invoke-DotNet: no dotnet arguments were provided."
    }

    Write-Host "> dotnet $($DotNetArguments -join ' ')" -ForegroundColor Cyan
    & dotnet @DotNetArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}

function Invoke-ProjectCommand {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("build", "publish")]
        [string]$Command,
        [Parameter(Mandatory = $true)]
        [string]$ProjectRelativePath,
        [string[]]$ExtraArguments = @()
    )

    $projectPath = Join-Path $Root $ProjectRelativePath
    $arguments = @($Command, $projectPath, '-c', $Configuration) + $ExtraArguments
    Invoke-DotNet -DotNetArguments $arguments
}

function Build-Core {
    Write-Host "`n=== Callspire.Core ===" -ForegroundColor Yellow
    Invoke-ProjectCommand -Command build -ProjectRelativePath "Callspire.Core\Callspire.Core.csproj"
}

function Build-Windows {
    Write-Host "`n=== Desktop (Windows) ===" -ForegroundColor Yellow
    $tfm = "net8.0-windows10.0.17763"
    Invoke-ProjectCommand -Command build -ProjectRelativePath "Callspire.Desktop\Callspire.Desktop.csproj" `
        -ExtraArguments @('-f', $tfm)
    if ($Publish) {
        Invoke-ProjectCommand -Command publish -ProjectRelativePath "Callspire.Desktop\Callspire.Desktop.csproj" `
            -ExtraArguments @('-f', $tfm, '-r', 'win-x64', '--no-self-contained', '-o', (Join-Path $Root 'publish\windows'))
        Write-Host "  Artifact: $(Join-Path $Root 'publish\windows')" -ForegroundColor Green
    }
}

function Build-MacOS {
    Write-Host "`n=== Desktop (macOS) ===" -ForegroundColor Yellow
    Invoke-ProjectCommand -Command build -ProjectRelativePath "Callspire.Desktop\Callspire.Desktop.csproj" `
        -ExtraArguments @('-f', 'net8.0')
    if ($Publish) {
        Invoke-ProjectCommand -Command publish -ProjectRelativePath "Callspire.Desktop\Callspire.Desktop.csproj" `
            -ExtraArguments @('-f', 'net8.0', '-r', 'osx-x64', '--no-self-contained', '-o', (Join-Path $Root 'publish\macos'))
        Write-Host "  Artifact: $(Join-Path $Root 'publish\macos')" -ForegroundColor Green
    }
}

function Build-Linux {
    Write-Host "`n=== Desktop (Linux) ===" -ForegroundColor Yellow
    Invoke-ProjectCommand -Command build -ProjectRelativePath "Callspire.Desktop\Callspire.Desktop.csproj" `
        -ExtraArguments @('-f', 'net8.0')
    if ($Publish) {
        Invoke-ProjectCommand -Command publish -ProjectRelativePath "Callspire.Desktop\Callspire.Desktop.csproj" `
            -ExtraArguments @('-f', 'net8.0', '-r', 'linux-x64', '--no-self-contained', '-o', (Join-Path $Root 'publish\linux'))
        Write-Host "  Artifact: $(Join-Path $Root 'publish\linux')" -ForegroundColor Green
    }
}

# ── Dispatch ──────────────────────────────────────────────────────────────────
Write-Host "Callspire build  |  target=$Target  config=$Configuration  publish=$Publish"

switch ($Target) {
    "core"    { Build-Core }
    "windows" { Build-Core; Build-Windows }
    "macos"   { Build-Core; Build-MacOS }
    "linux"   { Build-Core; Build-Linux }
    "all" {
        Build-Core
        Build-Windows
        Build-MacOS
        Build-Linux
    }
}

Write-Host "`nBuild complete." -ForegroundColor Green
