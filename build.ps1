#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Local build helper for the Callspire multi-project solution.

.DESCRIPTION
  Builds one or more platform targets in Release (default) or Debug.
  Artifacts land in publish/<platform>/.

.PARAMETER Target
  One of: all | core | windows | macos | linux | android
  Default: all

.PARAMETER Configuration
  Release (default) or Debug.

.PARAMETER Publish
  If present, runs dotnet publish after a successful build.

.EXAMPLE
  .\build.ps1                          # Build all targets
  .\build.ps1 -Target windows          # Windows only
  .\build.ps1 -Target android -Publish # Build + publish APK
  .\build.ps1 -Configuration Debug     # Debug build of all targets
#>

param(
    [ValidateSet("all","core","windows","macos","linux","android")]
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

function Get-AndroidSdkDirectory {
    $candidates = @(
        $env:ANDROID_SDK_ROOT,
        $env:ANDROID_HOME,
        (Join-Path $env:LOCALAPPDATA 'Android\Sdk')
    ) | Where-Object { $_ -and (Test-Path $_) }

    $list = @($candidates)
    if ($list.Length -gt 0) { return $list[0] }
    return $null
}

function Get-JavaSdkDirectory {
    $candidates = @(
        $env:JAVA_HOME,
        'C:\Program Files\Android\Android Studio\jbr',
        'C:\Program Files\Microsoft\jdk-17',
        'C:\Program Files\Eclipse Adoptium\jdk-17'
    ) | Where-Object { $_ -and (Test-Path (Join-Path $_ 'bin\java.exe')) }

    $list = @($candidates)
    if ($list.Length -gt 0) { return $list[0] }
    return $null
}

function Get-AndroidToolchainProperties {
    $sdk = Get-AndroidSdkDirectory
    $jdk = Get-JavaSdkDirectory

    if (-not $sdk -or -not $jdk) {
        return $null
    }

    return @(
        "-p:AndroidSdkDirectory=$sdk",
        ('-p:JavaSdkDirectory="' + $jdk + '"')
    )
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

function Build-Android {
    param([bool]$Required = $true)

    Write-Host "`n=== Android APK ===" -ForegroundColor Yellow

    $toolchain = Get-AndroidToolchainProperties
    if (-not $toolchain) {
        $sdk = Get-AndroidSdkDirectory
        $jdk = Get-JavaSdkDirectory
        $message = "Android toolchain incomplete (SDK: $(if ($sdk) { $sdk } else { 'not found' }); JDK: $(if ($jdk) { $jdk } else { 'not found' }))."
        if ($Required) {
            throw "$message Install Android SDK + JDK, or set ANDROID_SDK_ROOT / JAVA_HOME. See https://aka.ms/dotnet-android-install-sdk"
        }
        Write-Host "  SKIP: $message" -ForegroundColor DarkYellow
        return
    }

    $workloads = & dotnet workload list 2>&1
    if ($workloads -notmatch "android") {
        Write-Host "  Installing .NET android workload..." -ForegroundColor DarkGray
        Invoke-DotNet -DotNetArguments @('workload', 'install', 'android')
    }

    $androidArgs = @('-f', 'net8.0-android34.0') + $toolchain
    Invoke-ProjectCommand -Command build -ProjectRelativePath "Callspire.Android\Callspire.Android.csproj" `
        -ExtraArguments $androidArgs
    if ($Publish) {
        Invoke-ProjectCommand -Command publish -ProjectRelativePath "Callspire.Android\Callspire.Android.csproj" `
            -ExtraArguments ($androidArgs + @('-o', (Join-Path $Root 'publish\android')))
        Write-Host "  Artifact: $(Join-Path $Root 'publish\android')" -ForegroundColor Green
    }
}

# ── Dispatch ──────────────────────────────────────────────────────────────────
Write-Host "Callspire build  |  target=$Target  config=$Configuration  publish=$Publish"

switch ($Target) {
    "core"    { Build-Core }
    "windows" { Build-Core; Build-Windows }
    "macos"   { Build-Core; Build-MacOS }
    "linux"   { Build-Core; Build-Linux }
    "android" { Build-Core; Build-Android -Required $true }
    "all" {
        Build-Core
        Build-Windows
        Build-MacOS
        Build-Linux
        Build-Android -Required $false
    }
}

Write-Host "`nBuild complete." -ForegroundColor Green
