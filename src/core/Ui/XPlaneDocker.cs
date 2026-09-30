using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using static AICopilotCore.Ui.NativeMethods;

namespace AICopilotCore.Ui;

// Mete la ventana de X-Plane en el hueco central del shell sin reparentarla.
//
// La alternativa evidente seria SetParent(xplane, shell): recorte perfecto y
// una sola ventana. Se descarto a proposito. Cuando dos procesos quedan en
// relacion padre/hijo, Windows engancha sus colas de entrada
// (AttachThreadInput implicito) y las dos interfaces pasan a bloquearse la
// una a la otra: un tiron repintando el shell congela el simulador, y un
// frame largo del simulador congela el shell. Con un sim a 60 fps eso no es
// un riesgo teorico.
//
// Asi que X-Plane sigue siendo una ventana de nivel superior independiente y
// este docker se limita a tres cosas, todas desde fuera:
//
//   1. Geometria: la coloca y dimensiona sobre el rectangulo en pantalla del
//      viewport del shell.
//   2. Marco: le quita barra de titulo y borde para que no se vea la costura
//      (reversible: se guarda el estilo original).
//   3. Orden Z: mantiene el par apilado, con X-Plane inmediatamente encima
//      del shell. Es la parte con mas trampa, porque activar cualquiera de
//      las dos ventanas la sube: si la que sube es el shell, su fondo tapa al
//      simulador y el hueco se ve negro. Ver EnsureZOrder.
//
// Todas las llamadas del camino normal son asincronas (SWP_ASYNCWINDOWPOS,
// ShowWindowAsync): encolan la peticion en el hilo de X-Plane y vuelven al
// instante, asi que el shell nunca se queda esperando al simulador. Las de
// restaurar, que corren al cerrar, si son sincronas -- ahi interesa que haya
// terminado de verdad antes de salir.
public sealed class XPlaneDocker : IDisposable {
    private readonly Window _shell;
    private readonly FrameworkElement _viewport;
    private readonly XPlaneWindowWatcher _watcher = new();
    private readonly DispatcherTimer _timer;

    public event Action<string>? Log;
    public event Action<bool>? DockedChanged;

    private IntPtr _shellHwnd;
    private IntPtr _hwnd;

    // Estado original de la ventana de X-Plane, para poder devolversela al
    // usuario tal cual estaba. Se captura la primera vez que la tocamos y se
    // olvida si el HWND cambia (el sim se cerro y se volvio a abrir).
    private IntPtr _savedFor;
    private RECT _originalRect;
    private IntPtr _originalStyle;
    private IntPtr _originalExStyle;
    private bool _hasSavedState;

    private bool _attached = true;
    private bool _hideFrame = true;
    private bool _docked;
    // Cuando el viewport muestra otra cosa (caja negra / graficos), el
    // simulador no puede seguir tapando el hueco: se restaura su ventana
    // sin cambiar Attached, y al volver a "cubrir" se redockea solo.
    private bool _covering = true;

    // Ultimo rectangulo pedido a X-Plane, para no repetir una peticion que ya
    // se sabe que no va a cumplir (ver DockNow).
    private RECT? _lastRequest;
    private bool _tooSmallLogged;

    public bool Attached => _attached;

    public XPlaneDocker(Window shell, FrameworkElement viewport) {
        _shell = shell;
        _viewport = viewport;

        // 250 ms es el cinturon de seguridad, no el mecanismo principal: los
        // eventos de Start() cubren todo lo que hace el usuario en el shell, y
        // el timer solo esta para lo que pasa al otro lado (X-Plane se
        // redimensiona solo al cambiar opciones de render, otra ventana se
        // cuela en el orden Z, el sim arranca a mitad de sesion).
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => DockNow();
    }

    public void Start() {
        _shellHwnd = new WindowInteropHelper(_shell).Handle;

        _shell.LocationChanged += (_, _) => DockNow();
        _shell.SizeChanged += (_, _) => DockNow();
        _shell.Activated += (_, _) => DockNow();
        _shell.StateChanged += OnShellStateChanged;
        // Cubre los splitters: mover uno no cambia el tamano de la ventana,
        // solo el del viewport.
        _viewport.SizeChanged += (_, _) => DockNow();

        _watcher.Changed += OnXPlaneWindowChanged;
        _watcher.Start();
        _timer.Start();
    }

    public void SetAttached(bool attached) {
        if (_attached == attached) return;
        _attached = attached;
        if (attached) {
            Log?.Invoke("X-Plane anclado al layout.");
            DockNow();
        } else {
            RestoreXPlaneWindow();
            Log?.Invoke("X-Plane suelto: vuelve a su posicion y marco originales.");
        }
    }

    // Libera el hueco central para otra vista (graficos de la caja negra)
    // sin "soltar" el anclaje: Attached sigue true y al SetCovering(true)
    // vuelve a encajar. Si el usuario ya tenia X-Plane suelto, no se toca.
    public void SetCovering(bool covering) {
        if (_covering == covering) return;
        _covering = covering;
        if (covering) {
            if (_attached) DockNow();
        } else if (_attached) {
            RestoreXPlaneWindow();
            _lastRequest = null;
        }
    }

    public void SetHideFrame(bool hide) {
        if (_hideFrame == hide) return;
        _hideFrame = hide;
        if (!hide) RestoreFrame();
        DockNow();
    }

    // --- Nucleo ------------------------------------------------------------

    public void DockNow() {
        if (!_attached || !_covering) return;
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)) { SetDocked(false); return; }
        if (_shell.WindowState == WindowState.Minimized) return;
        // Iconizada o aun sin mostrarse: no se toca. Moverle la ventana a un
        // X-Plane que todavia esta arrancando no lleva a nada bueno.
        if (IsIconic(_hwnd) || !IsWindowVisible(_hwnd)) { SetDocked(false); return; }
        if (!TryGetViewportRect(out RECT target)) return;

        CaptureOriginalState();
        if (_hideFrame) StripFrame();

        bool knownRect = GetWindowRect(_hwnd, out RECT current);
        bool offTarget = !knownRect || !SameRect(current, target);

        if (offTarget && !SameRect(_lastRequest, target)) {
            SetWindowPos(_hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            _lastRequest = target;
        } else if (offTarget && knownRect) {
            // Ya se pidio exactamente este rectangulo y X-Plane sigue sin
            // cumplirlo: no es una peticion perdida, es que no puede. Insistir
            // cuatro veces por segundo solo provocaria repintados, asi que se
            // deja estar y se avisa. En cuanto el usuario cambia el layout el
            // objetivo cambia y se vuelve a intentar.
            WarnIfViewportTooSmall(current, target);
        } else {
            _tooSmallLogged = false;
        }

        EnsureZOrder();
        // El rectangulo aplicado se registra al engancharse, no en cada
        // pasada: es la unica forma de distinguir luego "el hueco esta negro
        // porque nunca se ancló" de "se ancló bien y lo que se ve negro es lo
        // que pinta el simulador".
        if (SetDocked(true))
            Log?.Invoke($"X-Plane anclado: {target.Width}x{target.Height} en ({target.Left},{target.Top}).");
    }

    private static bool SameRect(RECT? a, RECT b) =>
        a is RECT r &&
        Math.Abs(r.Left - b.Left) <= 1 && Math.Abs(r.Top - b.Top) <= 1 &&
        Math.Abs(r.Width - b.Width) <= 1 && Math.Abs(r.Height - b.Height) <= 1;

    // X-Plane impone un tamano minimo a su propia ventana (1280x720 en una
    // instalacion normal) y lo aplica el mismo al procesar el SetWindowPos:
    // se puede pedir menos, pero lo que queda es mayor que el hueco y se
    // derrama sobre los paneles. No hay forma limpia de saltarselo desde
    // fuera, asi que lo unico honesto es decirselo al usuario en vez de
    // dejarle un layout descuadrado sin explicacion.
    private void WarnIfViewportTooSmall(RECT current, RECT target) {
        if (_tooSmallLogged) return;
        if (current.Width <= target.Width + 2 && current.Height <= target.Height + 2) return;
        _tooSmallLogged = true;
        Log?.Invoke($"El hueco central ({target.Width}x{target.Height}) es menor que el minimo de " +
                    $"ventana de X-Plane ({current.Width}x{current.Height}), asi que el simulador " +
                    "se desborda sobre los paneles. Maximiza la ventana, estrecha los paneles " +
                    "laterales o baja el panel de log.");
    }

    // El viewport es un elemento del shell; PointToScreen devuelve pixeles
    // fisicos de pantalla, que es justo lo que quiere SetWindowPos, y de paso
    // resuelve el escalado de DPI sin tener que consultarlo a mano.
    private bool TryGetViewportRect(out RECT rect) {
        rect = default;
        if (_viewport.ActualWidth < 1 || _viewport.ActualHeight < 1) return false;
        if (PresentationSource.FromVisual(_viewport) is null) return false;

        Point topLeft = _viewport.PointToScreen(new Point(0, 0));
        Point bottomRight = _viewport.PointToScreen(new Point(_viewport.ActualWidth, _viewport.ActualHeight));
        rect = new RECT {
            Left = (int)Math.Round(topLeft.X),
            Top = (int)Math.Round(topLeft.Y),
            Right = (int)Math.Round(bottomRight.X),
            Bottom = (int)Math.Round(bottomRight.Y),
        };
        return rect.Width > 0 && rect.Height > 0;
    }

    // Mantiene el par apilado, con X-Plane inmediatamente por encima del
    // shell, y siempre moviendo el shell hacia abajo -- nunca X-Plane hacia
    // arriba.
    //
    // No es una preferencia, es la unica operacion que Windows garantiza:
    // ningun proceso puede colocar una ventana por encima de la ventana
    // ACTIVA. Medido, con el core parado y otra aplicacion en primer plano:
    // ni "insertar X-Plane justo encima de esa ventana" ni HWND_TOP mueven
    // nada, las dos llamadas devuelven exito y el orden Z se queda igual. O
    // sea que en cuanto el usuario pulsa un panel y el shell pasa a ser la
    // ventana activa, subir X-Plane por encima de el es imposible. Bajar la
    // propia ventana, en cambio, no tiene ninguna restriccion.
    //
    // Que el shell quede debajo no le quita el foco: activa y encima son
    // cosas distintas. El usuario sigue escribiendo en los campos porque los
    // paneles estan fuera del rectangulo que ocupa el simulador.
    //
    // Va sin comprobar si "hace falta": pedir una posicion Z que ya se tiene
    // no repinta nada, y averiguarlo recorriendo la lista Z no es viable --
    // un escritorio normal tiene cientos de ventanas en ella.
    public void EnsureStacked() {
        if (_shellHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        EnsureZOrder();
    }

    public IntPtr XPlaneHwnd => _hwnd;

    // Mientras un tooltip/ComboBox Popup esta abierto, Windows reordena el Z
    // varias veces (crear HWND, owned window, sombra). Un solo EnsureZOrder
    // en Opened no basta: el shell vuelve a subir y el hueco se ve negro.
    // Este guard reapila a ~30 Hz hasta EndPopupGuard.
    private int _popupGuard;
    private DispatcherTimer? _popupGuardTimer;

    public void BeginPopupGuard() {
        if (!_attached || !_covering) return;
        _popupGuard++;
        if (_popupGuard == 1) {
            _popupGuardTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _popupGuardTimer.Tick -= OnPopupGuardTick;
            _popupGuardTimer.Tick += OnPopupGuardTick;
            _popupGuardTimer.Start();
        }
        EnsureZOrder();
    }

    public void EndPopupGuard() {
        if (_popupGuard > 0) _popupGuard--;
        if (_popupGuard == 0) {
            if (_popupGuardTimer is not null) {
                _popupGuardTimer.Stop();
                _popupGuardTimer.Tick -= OnPopupGuardTick;
            }
            EnsureZOrder();
        }
    }

    private void OnPopupGuardTick(object? sender, EventArgs e) => EnsureZOrder();

    private void EnsureZOrder() {
        // Sincrona a proposito: es nuestra ventana, asi que no hay riesgo de
        // quedarse esperando a que el simulador atienda su cola.
        if (_shellHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        SetWindowPos(_shellHwnd, _hwnd, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void CaptureOriginalState() {
        if (_hasSavedState && _savedFor == _hwnd) return;
        GetWindowRect(_hwnd, out _originalRect);
        _originalStyle = GetWindowLongPtr(_hwnd, GWL_STYLE);
        _originalExStyle = GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        _savedFor = _hwnd;
        _hasSavedState = true;
    }

    // Se comprueba y reaplica en cada pasada, no solo al engancharse: X-Plane
    // rehace su propia ventana en algunos cambios (pasar por pantalla completa
    // y volver), y con ello reaparece el marco.
    private void StripFrame() {
        long style = (long)GetWindowLongPtr(_hwnd, GWL_STYLE);
        long exStyle = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        long wantedStyle = style & ~(WS_CAPTION | WS_THICKFRAME);
        long wantedExStyle = exStyle & ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE |
                                         WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
        if (style == wantedStyle && exStyle == wantedExStyle) return;

        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(wantedStyle));
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(wantedExStyle));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE |
                     SWP_FRAMECHANGED | SWP_ASYNCWINDOWPOS);
    }

    // Al restaurar se fuerzan barra de titulo y borde en vez de replicar tal
    // cual el estilo capturado. La diferencia importa cuando al core lo matan
    // en vez de cerrarlo (Stop-Process, un cuelgue): ahi nadie restaura nada,
    // X-Plane se queda sin marco, y en el siguiente arranque lo que se captura
    // como "original" ya viene pelado -- replicarlo dejaria al usuario sin
    // forma de recuperar la barra de titulo desde la aplicacion.
    private void RestoreFrame() {
        if (!_hasSavedState || _savedFor != _hwnd || !IsWindow(_hwnd)) return;
        long restored = (long)_originalStyle | WS_CAPTION | WS_THICKFRAME;
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(restored));
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, _originalExStyle);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void RestoreXPlaneWindow() {
        SetDocked(false);
        if (!_hasSavedState || _savedFor != _hwnd || !IsWindow(_hwnd)) return;
        RestoreFrame();
        SetWindowPos(_hwnd, IntPtr.Zero, _originalRect.Left, _originalRect.Top,
                     _originalRect.Width, _originalRect.Height,
                     SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // Devuelve true solo cuando el estado ha cambiado de verdad, para que
    // quien llama pueda registrar la transicion sin repetirla cada pasada.
    private bool SetDocked(bool docked) {
        if (_docked == docked) return false;
        _docked = docked;
        DockedChanged?.Invoke(docked);
        return true;
    }

    // --- Minimizado conjunto ------------------------------------------------
    //
    // Shell y simulador tienen que comportarse como una sola ventana: si el
    // usuario minimiza uno, el otro le sigue. Que sea en los dos sentidos
    // exige cortar el eco de forma explicita (_syncingState): confiar en "si
    // el estado ya es el que quiero no hago nada" no basta, porque los dos
    // estados pueden diferir de verdad y entonces el rebote no es un no-op
    // sino una orden real -- que es como una ventana de X-Plane aun invisible
    // durante el arranque acababa minimizando el shell, y el shell
    // minimizando a X-Plane.

    private bool _syncingState;

    // A que estado devolver el shell cuando X-Plane se restaura. Hace falta
    // guardarlo porque el shell arranca maximizado y casi siempre se usa asi:
    // restaurarlo "a Normal" a secas lo dejaria en su tamano de diseno, y de
    // paso encogeria el hueco central por debajo del minimo de X-Plane.
    private WindowState _stateBeforeAutoMinimize = WindowState.Maximized;

    private void SetShellState(WindowState state) {
        if (_shell.WindowState == state) return;
        _syncingState = true;
        try {
            _shell.WindowState = state;
        } finally {
            _syncingState = false;
        }
    }

    private void OnShellStateChanged(object? sender, EventArgs e) {
        // Solo se replica lo que hace el usuario con el shell, nunca lo que
        // acabamos de hacerle nosotros para seguir a X-Plane.
        if (_syncingState) return;
        if (!_attached || _hwnd == IntPtr.Zero || !IsWindow(_hwnd)) return;
        // Una ventana que aun no es visible no esta minimizada: no se la toca.
        if (!IsWindowVisible(_hwnd)) return;

        if (_shell.WindowState == WindowState.Minimized) {
            if (!IsIconic(_hwnd)) ShowWindowAsync(_hwnd, SW_MINIMIZE);
        } else {
            if (IsIconic(_hwnd)) ShowWindowAsync(_hwnd, SW_RESTORE);
            DockNow();
        }
    }

    private void OnXPlaneWindowChanged(IntPtr hwnd, XPlaneWindowState state) {
        // El watcher corre en su propio hilo; todo lo que sigue toca la UI.
        _shell.Dispatcher.BeginInvoke(() => {
            if (hwnd != _hwnd) {
                _hwnd = hwnd;
                _hasSavedState = false;
                _lastRequest = null;
                if (hwnd != IntPtr.Zero) Log?.Invoke("Ventana de X-Plane detectada.");
            }

            switch (state) {
                case XPlaneWindowState.Missing:
                    SetDocked(false);
                    Log?.Invoke("Sin ventana de X-Plane (simulador cerrado o arrancando).");
                    break;

                // Existe pero no es visible: el simulador esta arrancando (o
                // cerrandose). Ni se ancla ni se sincroniza el minimizado --
                // hacerlo aqui es justo lo que minimizaba X-Plane al abrirlo.
                case XPlaneWindowState.NotReady:
                    SetDocked(false);
                    break;

                case XPlaneWindowState.Minimized:
                    SetDocked(false);
                    if (_attached && _shell.WindowState != WindowState.Minimized) {
                        _stateBeforeAutoMinimize = _shell.WindowState;
                        SetShellState(WindowState.Minimized);
                    }
                    break;

                case XPlaneWindowState.Visible:
                    if (_attached) {
                        if (_shell.WindowState == WindowState.Minimized)
                            SetShellState(_stateBeforeAutoMinimize);
                        DockNow();
                    }
                    break;
            }
        });
    }

    public void Dispose() {
        _timer.Stop();
        _watcher.Stop();
        // Dejar el marco puesto y la ventana donde estaba: cerrar el shell no
        // puede dejarle al usuario un X-Plane sin barra de titulo y del tamano
        // de un hueco que ya no existe.
        RestoreXPlaneWindow();
    }
}
