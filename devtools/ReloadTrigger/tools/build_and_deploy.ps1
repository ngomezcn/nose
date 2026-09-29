<#
Compila ReloadTrigger (MSVC via CMake) y lo despliega en la instalacion
de X-Plane, sin cerrar X-Plane (mismo truco de renombrar-y-copiar que usa
AICopilot\tools\build_and_deploy.ps1).

Esto es infraestructura del pipeline, no parte del desarrollo diario de
AICopilot: normalmente se lanza una sola vez (o cuando cambies
reload_trigger.cpp). Una vez desplegado y con un reload de plugins dado
a mano una primera vez para que quede cargado, AICopilot\tools\build_and_deploy.ps1
ya puede disparar el reload automaticamente por UDP.

Uso:
  .\devtools\ReloadTrigger\tools\build_and_deploy.ps1
#>

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$BuildDir = Join-Path $RepoRoot "build"
$CmakeXplOut = Join-Path $BuildDir "install\ReloadTrigger\win_x64\ReloadTrigger.xpl"
$DestDir = "E:\X-Plane 12\Resources\plugins\ReloadTrigger\win_x64"
$Dest = Join-Path $DestDir "ReloadTrigger.xpl"

function Find-CMake {
    $inPath = Get-Command cmake.exe -ErrorAction SilentlyContinue
    if ($inPath) { return $inPath.Source }

    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $installPath = & $vswhere -latest -products * -property installationPath
        if ($installPath) {
            $candidate = Join-Path $installPath "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }
    throw "No se encuentra cmake.exe (ni en PATH ni junto a Visual Studio). Instala 'Desktop development with C++' con el componente CMake."
}
$Cmake = Find-CMake
Write-Host "cmake: $Cmake"

if (-not (Test-Path (Join-Path $BuildDir "ReloadTrigger.sln"))) {
    Write-Host "Configurando proyecto (primera vez / build dir limpio)..."
    & $Cmake -S $RepoRoot -B $BuildDir -G "Visual Studio 17 2022" -A x64
    if ($LASTEXITCODE -ne 0) { throw "cmake configure fallo (exit $LASTEXITCODE)" }
}

Write-Host "Compilando (Release)..."
& $Cmake --build $BuildDir --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake build fallo (exit $LASTEXITCODE)" }

if (-not (Test-Path $CmakeXplOut)) {
    throw "Compilo pero no encuentro el .xpl en $CmakeXplOut"
}

if (Test-Path $Dest) {
    $backup = Join-Path $DestDir ("ReloadTrigger.xpl.old_{0}" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
    Rename-Item -Path $Dest -NewName (Split-Path $backup -Leaf)
    Write-Host "Fichero instalado (aun en memoria) apartado como: $backup"
} else {
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
}

Copy-Item -Path $CmakeXplOut -Destination $Dest -Force
Write-Host "ReloadTrigger desplegado a: $Dest"

Get-ChildItem -Path $DestDir -Filter "ReloadTrigger.xpl.old_*" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Listo. Recarga los plugins una vez a mano dentro de X-Plane (no hay 'Plugin Admin': cada"
Write-Host "plugin gestiona su propio menu/reload via el SDK) para que ReloadTrigger quede cargado."
Write-Host "A partir de ahi, AICopilot\tools\build_and_deploy.ps1 ya puede disparar el reload el solo."
