<#
Build + deploy global: addon (connector) y UI (core) en un solo paso.

Invoca build_and_deploy_addon.ps1 y build_and_deploy_ui.ps1. El contador
de build solo lo sube el script de la UI. Al acabar siempre lanza la UI.

Uso:
  .\tools\build_and_deploy.ps1
#>

$ErrorActionPreference = "Stop"

Write-Host "=== GLOBAL: addon + UI ==="
Write-Host ""

Write-Host "--- Addon ---"
& (Join-Path $PSScriptRoot "build_and_deploy_addon.ps1") -SkipLaunch
Write-Host ""

Write-Host "--- UI ---"
& (Join-Path $PSScriptRoot "build_and_deploy_ui.ps1")

Write-Host ""
Write-Host "Listo: build global (addon + UI) terminado."
