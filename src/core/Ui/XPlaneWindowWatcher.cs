using System.Diagnostics;

namespace AICopilotCore.Ui;

public enum XPlaneWindowState {
    /// No hay proceso de X-Plane, o aun no ha creado su ventana principal.
    Missing,

    /// La ventana existe pero todavia no es visible. Pasa durante el arranque
    /// del simulador, y hay que distinguirlo de "minimizada": confundir los
    /// dos casos hacia que el shell se minimizase al ver la ventana invisible
    /// y acto seguido minimizase a X-Plane de verdad, justo al abrirlo.
    NotReady,

    /// Minimizada de verdad (iconizada) por el usuario.
    Minimized,

    /// Visible y utilizable: es el unico estado en el que se la puede anclar.
    Visible,
}

// Localiza la ventana principal de X-Plane y avisa cuando cambia su estado.
// El shell la necesita para dos cosas: saber a que HWND anclarse y seguirle
// el minimizado, de forma que shell y simulador se comporten como una sola
// ventana.
//
// No hay evento nativo sencillo para "otro proceso minimizo su ventana",
// asi que se hace por poll. El poll es barato porque el HWND se cachea: el
// barrido de procesos, que es lo caro, solo se repite cuando el handle
// cacheado deja de ser una ventana valida.
public sealed class XPlaneWindowWatcher {
    public event Action<IntPtr, XPlaneWindowState>? Changed;

    public IntPtr Handle { get; private set; }

    private CancellationTokenSource? _cts;
    private IntPtr _cachedHwnd;
    private (IntPtr Hwnd, XPlaneWindowState State)? _last;

    public void Start() {
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    public void Stop() => _cts?.Cancel();

    private async Task PollLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            IntPtr hwnd = FindWindow();
            XPlaneWindowState state =
                hwnd == IntPtr.Zero ? XPlaneWindowState.Missing
                : NativeMethods.IsIconic(hwnd) ? XPlaneWindowState.Minimized
                : !NativeMethods.IsWindowVisible(hwnd) ? XPlaneWindowState.NotReady
                : XPlaneWindowState.Visible;

            Handle = hwnd;
            if (_last is null || _last.Value.Hwnd != hwnd || _last.Value.State != state) {
                _last = (hwnd, state);
                Changed?.Invoke(hwnd, state);
            }

            try {
                await Task.Delay(300, ct);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    private IntPtr FindWindow() {
        if (_cachedHwnd != IntPtr.Zero && NativeMethods.IsWindow(_cachedHwnd)) return _cachedHwnd;

        // Busca por nombre de proceso en vez de un titulo/clase de ventana
        // fijos -- "X-Plane.exe" vs "X-Plane 12.exe" varia entre
        // instalaciones/versiones, y el nombre siempre empieza por "X-Plane".
        foreach (var proc in Process.GetProcesses()) {
            using (proc) {
                if (!proc.ProcessName.StartsWith("X-Plane", StringComparison.OrdinalIgnoreCase)) continue;
                IntPtr hwnd = proc.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                _cachedHwnd = hwnd;
                return hwnd;
            }
        }

        _cachedHwnd = IntPtr.Zero;
        return IntPtr.Zero;
    }
}
