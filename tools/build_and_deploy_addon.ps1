<#
Compila y despliega solo el addon / connector (C++ -> AICopilot.xpl).

  1. Configura (si hace falta) y compila el connector en Release con
     CMake + MSVC.
  2. Copia el .xpl a la instalacion de X-Plane sin cerrarla (rename + copy).
  3. Dispara reload de plugins por UDP (ReloadTrigger) y confirma el
     dialogo de X-Plane con Enter.
  4. Lanza siempre AICopilotCore.exe al acabar (el .exe ya desplegado;
     este script no recompila la UI ni sube el contador de build).

Uso:
  .\tools\build_and_deploy_addon.ps1
  .\tools\build_and_deploy_addon.ps1 -SkipLaunch
#>
param(
    [switch]$SkipLaunch
)

$ErrorActionPreference = "Stop"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$BuildDir = Join-Path $RepoRoot "build"
$XplOut = Join-Path $RepoRoot "dist\win_x64\AICopilot.xpl"
$CmakeXplOut = Join-Path $BuildDir "install\AICopilot\win_x64\AICopilot.xpl"
$DestDir = "E:\X-Plane 12\Resources\plugins\AICopilot\win_x64"
$Dest = Join-Path $DestDir "AICopilot.xpl"
$CoreDest = Join-Path $DestDir "AICopilotCore.exe"

# --- 1. Localizar CMake ----------------------------------------------------
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

# --- 2. Configurar (si hace falta) y compilar en Release -------------------
if (-not (Test-Path (Join-Path $BuildDir "AICopilot.sln"))) {
    Write-Host "Configurando proyecto (primera vez / build dir limpio)..."
    & $Cmake -S $RepoRoot -B $BuildDir -G "Visual Studio 17 2022" -A x64
    if ($LASTEXITCODE -ne 0) { throw "cmake configure fallo (exit $LASTEXITCODE)" }
}

Write-Host "Compilando connector (Release)..."
& $Cmake --build $BuildDir --config Release
if ($LASTEXITCODE -ne 0) { throw "cmake build fallo (exit $LASTEXITCODE)" }

if (-not (Test-Path $CmakeXplOut)) {
    throw "Compilo pero no encuentro el .xpl en $CmakeXplOut"
}

New-Item -ItemType Directory -Force -Path (Split-Path $XplOut) | Out-Null
Copy-Item -Path $CmakeXplOut -Destination $XplOut -Force
Write-Host "Build copiado a: $XplOut"

# --- 3. Desplegar en X-Plane sin cerrarlo ----------------------------------
if (Test-Path $Dest) {
    $backup = Join-Path $DestDir ("AICopilot.xpl.old_{0}" -f (Get-Date -Format "yyyyMMdd_HHmmss"))
    Rename-Item -Path $Dest -NewName (Split-Path $backup -Leaf)
    Write-Host "Fichero instalado (aun en memoria) apartado como: $backup"
} else {
    New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
}

Copy-Item -Path $XplOut -Destination $Dest -Force
Write-Host "Nuevo build desplegado a: $Dest"

Get-ChildItem -Path $DestDir -Filter "AICopilot.xpl.old_*" -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

# --- 4. Reload de plugins por UDP + confirmar dialogo ---------------------
$reloadSignalSent = $false
try {
    $udpClient = New-Object System.Net.Sockets.UdpClient
    $bytes = [System.Text.Encoding]::ASCII.GetBytes("RELOAD")
    $udpClient.Send($bytes, $bytes.Length, "127.0.0.1", 34570) | Out-Null
    $udpClient.Close()
    $reloadSignalSent = $true
    Write-Host "Senal de reload enviada a ReloadTrigger (127.0.0.1:34570)."
} catch {
    Write-Host "No se pudo enviar la senal de reload (ReloadTrigger no desplegado/cargado?): $_"
}

if ($reloadSignalSent) {
    try {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Win32Focus {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
"@ -ErrorAction SilentlyContinue

        Start-Sleep -Milliseconds 3000
        $xplaneProc = Get-Process -Name "X-Plane*" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($xplaneProc -and $xplaneProc.MainWindowHandle -ne [IntPtr]::Zero) {
            $hwnd = $xplaneProc.MainWindowHandle
            $focused = $false
            for ($i = 0; $i -lt 6 -and -not $focused; $i++) {
                $fgWnd = [Win32Focus]::GetForegroundWindow()
                $dummyPid1 = [uint32]0
                $dummyPid2 = [uint32]0
                $fgThread = [Win32Focus]::GetWindowThreadProcessId($fgWnd, [ref]$dummyPid1)
                $targetThread = [Win32Focus]::GetWindowThreadProcessId($hwnd, [ref]$dummyPid2)
                $curThread = [Win32Focus]::GetCurrentThreadId()

                [Win32Focus]::AttachThreadInput($curThread, $targetThread, $true) | Out-Null
                if ($fgThread -ne $targetThread) {
                    [Win32Focus]::AttachThreadInput($fgThread, $targetThread, $true) | Out-Null
                }
                if ([Win32Focus]::IsIconic($hwnd)) { [Win32Focus]::ShowWindow($hwnd, 9) | Out-Null }
                [Win32Focus]::BringWindowToTop($hwnd) | Out-Null
                [Win32Focus]::SetForegroundWindow($hwnd) | Out-Null
                if ($fgThread -ne $targetThread) {
                    [Win32Focus]::AttachThreadInput($fgThread, $targetThread, $false) | Out-Null
                }
                [Win32Focus]::AttachThreadInput($curThread, $targetThread, $false) | Out-Null

                Start-Sleep -Milliseconds 250
                $focused = ([Win32Focus]::GetForegroundWindow() -eq $hwnd)
                if (-not $focused) { Start-Sleep -Milliseconds 300 }
            }

            if ($focused) {
                Write-Host "X-Plane puesto en primer plano."
            } else {
                Write-Host "No se confirmo el cambio de foco a X-Plane; se manda el Enter igualmente (mejor esfuerzo)."
            }

            Start-Sleep -Milliseconds 200
            $VK_RETURN = 0x0D
            $KEYEVENTF_KEYUP = 0x0002
            [Win32Focus]::keybd_event($VK_RETURN, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
            [Win32Focus]::keybd_event($VK_RETURN, 0, $KEYEVENTF_KEYUP, [UIntPtr]::Zero)
            Write-Host "Enter enviado a X-Plane via keybd_event (confirma la pantalla de reload)."
        } else {
            Write-Host "X-Plane no parece estar corriendo (o sin ventana); nada que confirmar."
        }
    } catch {
        Write-Host "No se pudo enviar Enter a X-Plane: $_"
    }
}

# --- 5. Lanzar siempre la UI -----------------------------------------------
if (-not $SkipLaunch) {
    if (-not (Test-Path $CoreDest)) {
        Write-Host "AVISO: no hay AICopilotCore.exe en $CoreDest; no se puede lanzar la UI. Ejecuta build_and_deploy_ui.ps1 antes."
    } else {
        $running = Get-Process -Name "AICopilotCore" -ErrorAction SilentlyContinue
        if ($running) {
            Write-Host "Reiniciando AICopilotCore.exe para reconectar tras el reload..."
            # Respaldo de caja negra + cierre suave (igual que build_and_deploy_ui).
            $csv = Join-Path $DestDir "DataLog.csv"
            $prev = Join-Path $DestDir "DataLog.previous.csv"
            if (Test-Path $csv) {
                $len = (Get-Item $csv).Length
                $prevLen = 0
                if (Test-Path $prev) { $prevLen = (Get-Item $prev).Length }
                if ($len -ge 200 -and $len -ge $prevLen) {
                    Copy-Item -Path $csv -Destination $prev -Force
                    Write-Host "Caja negra respaldada -> DataLog.previous.csv ($len bytes)"
                }
            }
            foreach ($p in $running) {
                try { $p.CloseMainWindow() | Out-Null } catch { }
            }
            Start-Sleep -Milliseconds 800
            Get-Process -Name "AICopilotCore" -ErrorAction SilentlyContinue |
                Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 300
        } else {
            Write-Host "Lanzando AICopilotCore.exe..."
        }
        Start-Process -FilePath $CoreDest -WorkingDirectory $DestDir
    }
}

Write-Host ""
Write-Host "Listo: addon compilado y desplegado sin cerrar X-Plane."
Write-Host "Si ReloadTrigger esta cargado, el reload ya se ha disparado."
