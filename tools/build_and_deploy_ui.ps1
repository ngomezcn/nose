<#
Compila y despliega solo la UI / core (C# -> AICopilotCore.exe).

  0. Cierra AICopilotCore.exe si esta abierto (el .exe queda bloqueado
     si no, y dotnet publish falla).
  1. Sube en 1 tools\BUILD_NUMBER.txt y regenera
     src\core\BuildNumber.generated.cs (se ve en la cabecera de la UI).
  2. Publica el core con dotnet publish (Release, win-x64).
  3. Copia el .exe a la instalacion de X-Plane.
  4. Lanza siempre AICopilotCore.exe al acabar.

Uso:
  .\tools\build_and_deploy_ui.ps1
  .\tools\build_and_deploy_ui.ps1 -SkipLaunch
#>
param(
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$BuildNumberFile = Join-Path $PSScriptRoot "BUILD_NUMBER.txt"
$GeneratedCoreCs = Join-Path $RepoRoot "src\core\BuildNumber.generated.cs"
$DestDir = "E:\X-Plane 12\Resources\plugins\AICopilot\win_x64"
$CoreProject = Join-Path $RepoRoot "src\core\AICopilotCore.csproj"
$CorePublishOut = Join-Path $RepoRoot "src\core\bin\Release\net10.0-windows\win-x64\publish\AICopilotCore.exe"
$CoreDest = Join-Path $DestDir "AICopilotCore.exe"

# --- 0. Cerrar el core si esta abierto --------------------------------------
# Cierre suave primero (Window.Closing -> DataLogger.Close) para flush del
# CSV; si no, Stop-Process -Force puede dejar la caja negra a medias.
# Ademas se respalda DataLog.csv -> .previous antes de matar el proceso,
# por si el cierre suave no llega a dispararse.
function Backup-DataLog([string]$dir) {
    $csv = Join-Path $dir "DataLog.csv"
    $prev = Join-Path $dir "DataLog.previous.csv"
    if (-not (Test-Path $csv)) { return }
    $len = (Get-Item $csv).Length
    if ($len -lt 200) { return }
    $prevLen = 0
    if (Test-Path $prev) { $prevLen = (Get-Item $prev).Length }
    if ($len -ge $prevLen) {
        Copy-Item -Path $csv -Destination $prev -Force
        Write-Host "Caja negra respaldada -> DataLog.previous.csv ($len bytes)"
    }
}

$coreWasRunning = Get-Process -Name "AICopilotCore" -ErrorAction SilentlyContinue
if ($coreWasRunning) {
    Write-Host "Cerrando AICopilotCore.exe (instancia abierta)..."
    Backup-DataLog $DestDir
    foreach ($p in $coreWasRunning) {
        try { $p.CloseMainWindow() | Out-Null } catch { }
    }
    Start-Sleep -Milliseconds 800
    Get-Process -Name "AICopilotCore" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

# --- 1. Contador de build (solo UI) ----------------------------------------
$buildNumber = 0
if (Test-Path $BuildNumberFile) {
    $raw = (Get-Content $BuildNumberFile -Raw).Trim()
    if ($raw -match '^\d+$') { $buildNumber = [int]$raw }
}
$buildNumber += 1
Set-Content -Path $BuildNumberFile -Value $buildNumber -NoNewline
Set-Content -Path $GeneratedCoreCs -Value @"
// Generado automaticamente por tools\build_and_deploy_ui.ps1 en cada build de la UI.
// No lo edites a mano: se sobreescribe en el siguiente build. El contador
// vive en tools\BUILD_NUMBER.txt. Se ve en la cabecera del core.
namespace AICopilotCore;
internal static class BuildInfo {
    public const string BuildNumber = "$buildNumber";
}
"@
Write-Host "Build number (UI) -> $buildNumber"

# --- 2. Publicar el core ---------------------------------------------------
Write-Host "Publicando core..."
& dotnet publish $CoreProject -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish del core fallo (exit $LASTEXITCODE)" }
if (-not (Test-Path $CorePublishOut)) {
    throw "Publico pero no encuentro el .exe en $CorePublishOut"
}
Write-Host "Core publicado."

# --- 3. Desplegar ----------------------------------------------------------
New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
Copy-Item -Path $CorePublishOut -Destination $CoreDest -Force
Write-Host "Core desplegado a: $CoreDest"

# --- 4. Lanzar siempre la UI -----------------------------------------------
if (-not $SkipLaunch) {
    Write-Host "Lanzando AICopilotCore.exe..."
    Start-Process -FilePath $CoreDest -WorkingDirectory $DestDir
}

Write-Host ""
Write-Host "Listo: UI build $buildNumber compilada y desplegada."
