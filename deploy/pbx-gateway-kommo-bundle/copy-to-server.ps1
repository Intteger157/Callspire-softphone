#Requires -Version 5.1
<#
.SYNOPSIS
  Копирует kommo bundle на сервер через scp.
.EXAMPLE
  .\copy-to-server.ps1 -Server user@pbx.example.com -GatewayPath /opt/mikopbx-cdr-proxy
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $Server,
    [Parameter(Mandatory = $true)]
    [string] $GatewayPath
)

$BundleDir = $PSScriptRoot
Write-Host "Copying from $BundleDir to ${Server}:${GatewayPath} ..."

scp "$BundleDir\*.py" "${Server}:${GatewayPath}/"
scp "$BundleDir\templates\admin_kommo.html" "${Server}:${GatewayPath}/templates/"

Write-Host "Done. Apply APP_PY_PATCH.md to app.py on server if needed, then restart gateway."
