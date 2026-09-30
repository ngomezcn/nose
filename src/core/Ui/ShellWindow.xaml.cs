using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AICopilotCore.Connector;
using AICopilotCore.Domain;
using AICopilotCore.Domain.Agents;
using static AICopilotCore.Ui.NativeMethods;

namespace AICopilotCore.Ui;

// La ventana del core, montada como un editor de codigo: barra de titulo y
// de herramientas arriba, panel izquierdo (aviones / despegue / acciones),
// telemetria a la derecha, log abajo y, en el centro, el hueco donde
// encaja X-Plane.
//
// El hueco es literalmente eso: un Border vacio. Quien pone ahi el
// simulador es XPlaneDocker, que traduce el rectangulo en pantalla de ese
// Border a un SetWindowPos sobre la ventana de X-Plane -- no hay ningun
// reparentado ni nada dibujado dentro (el porque, en XPlaneDocker.cs).
//
// Sustituye a las dos ventanas flotantes anteriores (MainWindow overlay de
// telemetria + LogWindow), que se apoyaban en Topmost para quedarse encima
// del simulador. Aqui ya no hace falta: el simulador esta dentro del
// layout, asi que ninguna ventana tiene que taparlo.
public partial class ShellWindow : Window, INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    private const int MaxLogLines = 300;

    private readonly ConnectorClient _client;
    private readonly Datarefs _d;
    private readonly ControlTuning _tuning;
    private readonly FlightDirector _director;
    private readonly GraphicsSettings _graphics = new();
    private readonly FlightDataLogs _dataLogs = new();
    private readonly BlackBoxView _blackBoxView = new();
    private readonly DispatcherTimer _refreshTimer;
    // Indice del logger enlazado a la lista CSV / path / botones; -1 = sin
    // rebind aun. Al cambiar FocusedXplmIndex se notifica DataLogSamples.
    private int _boundDataLogIndex = -1;

    private XPlaneDocker? _docker;

    // Evita reentrada al forzar IsChecked=false en los hermanos de la barra
    // de actividad (Checked se dispara tambien al desmarcar).
    private bool _switchingLeftView;
    private bool _switchingViewportTab;
    private bool _blackBoxViewportActive;

    // Chrome segun foco GLOBAL vs avion: SyncFocusChrome solo reaplica cuando
    // cambia. El panel de telemetria se deja siempre montado (vacio en GLOBAL)
    // para no mover el hueco de X-Plane al cambiar de foco.
    private bool? _focusChromeIsGlobal;

    public ObservableCollection<string> Logs { get; } = new();

    // Aviones de la partida: el local (indice 0) y las IAs que reporte el
    // connector (Op.Planes). La coleccion la rellena RefreshAircraftList.
    public ObservableCollection<AircraftListEntry> AircraftEntries { get; } = new();

    // Blancos posibles de una interceptacion: cualquier avion de la partida
    // salvo el interceptor (el que esta en foco), local incluido. Objetos estables: RefreshIntercept-
    // Targets los actualiza en sitio en vez de recrearlos, porque recrearlos
    // le quitaria la seleccion al usuario diez veces por segundo.
    public ObservableCollection<InterceptTargetEntry> InterceptTargets { get; } = new();

    public string BuildText { get; } = $"build {BuildInfo.BuildNumber}";

    // Caja negra del avion EN FOCO. Con foco GLOBAL Empezar/Detener/Borrar
    // actuan sobre todo el roster; la lista CSV / graficos muestran LOCAL
    // como vista representativa (cada avion escribe su propio fichero).
    private DataLogger FocusedDataLog =>
        _dataLogs.For(_director.IsGlobalFocus ? 0 : _director.FocusedXplmIndex);
    public ObservableCollection<string> DataLogSamples => FocusedDataLog.Samples;

    // true = Empezar se pulso en GLOBAL: se mantiene grabando el roster
    // completo (incluye IAs que aparezcan despues) hasta Detener en GLOBAL.
    private bool _globalDataLogRecording;

    // IAs con Watch extra por estar grabando (refcount aparte del foco).
    private readonly HashSet<int> _dataLogWatchIndices = new();

    private string _dataLogFilePathText = "";
    public string DataLogFilePathText {
        get => _dataLogFilePathText;
        private set => Set(ref _dataLogFilePathText, value);
    }

    public ShellWindow(ConnectorClient client, Datarefs datarefs, ControlTuning tuning,
                       FlightDirector director) {
        // Asignar deps antes de DataContext: DataLogSamples / FocusedDataLog
        // leen FocusedXplmIndex del director al enlazar la lista CSV.
        _client = client;
        _d = datarefs;
        _tuning = tuning;
        _director = director;

        InitializeComponent();
        DataContext = this;
        HookFloatingChrome();

        // Los campos de texto de la pestana de Config no llevan un valor por
        // defecto propio: se leen del propio ControlTuning para que haya una
        // sola fuente de verdad (ver SyncTuningTextsFromModel).
        SyncTuningTextsFromModel();
        SyncGraphicsUiFromModel();

        // La telemetria llega a ritmo de frame del simulador (60+ Hz), pero
        // no tiene sentido redibujar texto tan rapido: ni se lee ni se nota.
        // Un timer de UI a 10 Hz que va a buscar el estado actual desacopla
        // el refresco de la pantalla del lazo de control, y de paso evita
        // inundar el Dispatcher con un Invoke por frame.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _refreshTimer.Tick += (_, _) => Refresh();
        _refreshTimer.Start();

        // Foco por defecto = GLOBAL: SyncFocusChrome deja solo Graficos y
        // oculta telemetria / pestanas de avion. RefreshAircraftList llama
        // UpdateFocusText → SyncFocusChrome.
        RefreshAircraftList();
        if (AircraftEntries.Count > 0 && AircraftEntries[0].IsGlobal)
            SelectedAircraft = AircraftEntries[0];
        SyncFocusChrome(force: true);
        RefreshInterceptTargets();

        // La vista de graficos vive en el host del viewport (hermano del
        // Border que usa el docker). Se crea aqui porque no es una Page XAML.
        BlackBoxHost.Child = _blackBoxView;
        RebindFocusedDataLogUi(force: true);
    }

    // El docker necesita el HWND del shell para el orden Z, y no existe hasta
    // que la ventana tiene origen nativo.
    protected override void OnSourceInitialized(EventArgs e) {
        base.OnSourceInitialized(e);

        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(OnWindowMessage);

        _docker = new XPlaneDocker(this, Viewport);
        _docker.Log += line => Append(line);
        _docker.DockedChanged += docked => {
            _dockedNow = docked;
            RefreshDockTexts();
        };
        _docker.Start();
        RefreshDockTexts();
    }

    // Sin esto, una ventana con WindowChrome maximizada se pasa del monitor
    // por el grosor del borde de redimension (ResizeBorderThickness) en los
    // cuatro lados: se pierden el borde derecho y los ultimos pixeles de la
    // barra de estado, y ademas tapa la barra de tareas. WM_GETMINMAXINFO es
    // donde Windows pregunta "cuanto ocuparias maximizada", asi que se le
    // contesta con el area de trabajo del monitor en el que este la ventana.
    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (msg != NativeMethods.WM_GETMINMAXINFO) return IntPtr.Zero;

        IntPtr monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
        // MaxPosition va en coordenadas relativas al monitor, no a la pantalla
        // virtual: en un monitor secundario, restarle el origen es lo que evita
        // que la ventana maximizada se vaya al monitor de al lado.
        mmi.MaxPosition = new NativeMethods.POINT {
            X = info.Work.Left - info.Monitor.Left,
            Y = info.Work.Top - info.Monitor.Top,
        };
        mmi.MaxSize = new NativeMethods.POINT { X = info.Work.Width, Y = info.Work.Height };
        mmi.MaxTrackSize = mmi.MaxSize;
        Marshal.StructureToPtr(mmi, lParam, false);

        handled = true;
        return IntPtr.Zero;
    }

    // Se puede llamar desde cualquier hilo: la secuencia corre en el hilo de
    // lectura del pipe, no en el de la UI. Ademas de pintar el log inferior,
    // deja la misma marca en la caja negra del avion indicado (columna Nota)
    // SOLO si ese logger esta grabando. markBlackBox=true (acciones de vuelo)
    // tambien pinta la linea vertical en los graficos. blackBoxIndex: 0 =
    // ownship; 1..19 = IA. En el log de pantalla las lineas de una IA llevan
    // el prefijo [etiqueta]; en el CSV van tal cual (marcas Inicio:/Fin:/Fase:).
    //
    // BeginInvoke (no Invoke): Start/Abort se llaman con el candado de la
    // secuencia cogido. Un Invoke sincrono aqui puede engancharse con el
    // hilo del pipe (que espera el mismo candado) y dejar la Driver API
    // colgada al pedir un despegue.
    public void Append(string line, bool markBlackBox = false, int blackBoxIndex = 0) {
        Dispatcher.BeginInvoke(() => {
            Logs.Add($"{DateTime.Now:HH:mm:ss}  {ScreenPrefix(blackBoxIndex, line)}{line}");
            while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            _dataLogs.For(blackBoxIndex).RecordEvent(line, chartMarker: markBlackBox);
        });
    }

    // El connector avisa de que X-Plane movio el origen de su marco local
    // (ya compensado). Queda en el log y en cada caja negra que graba.
    public void NoteOriginShift(string detail) {
        string line = $"AVISO: origen local desplazado por X-Plane y compensado ({detail})";
        Dispatcher.BeginInvoke(() => {
            Logs.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            foreach (var (_, logger) in _dataLogs.Recording)
                logger.RecordEvent(line, chartMarker: true);
        });
    }

    // Prefijo de avion solo para el log de pantalla (nunca para el CSV).
    private string ScreenPrefix(int xplmIndex, string line) {
        if (xplmIndex < 1) return "";
        string label = _director.World.TryGet(xplmIndex)?.Label ?? AircraftWorld.DefaultLabel(xplmIndex);
        if (line.StartsWith(label, StringComparison.Ordinal) ||
            line.StartsWith($"IA {xplmIndex}", StringComparison.Ordinal))
            return "";
        return $"[{label}] ";
    }

    // --- Telemetria generica: objetivo a la izquierda, real a la derecha ---
    private string _modeText = "Manual";
    public string ModeText { get => _modeText; set => Set(ref _modeText, value); }

    private string _sequenceText = "En espera";
    public string SequenceText { get => _sequenceText; set => Set(ref _sequenceText, value); }

    private string _checklistText = "";
    public string ChecklistText { get => _checklistText; set => Set(ref _checklistText, value); }

    private string _flightText = "";
    public string FlightText { get => _flightText; set => Set(ref _flightText, value); }

    private string _attitudeText = "";
    public string AttitudeText { get => _attitudeText; set => Set(ref _attitudeText, value); }

    private string _controlsText = "";
    public string ControlsText { get => _controlsText; set => Set(ref _controlsText, value); }

    private string _connectionStatus = "Esperando al plugin de X-Plane...";
    public string ConnectionStatus { get => _connectionStatus; set => Set(ref _connectionStatus, value); }

    private Brush _connectionDotColor = Brushes.Gray;
    public Brush ConnectionDotColor { get => _connectionDotColor; set => Set(ref _connectionDotColor, value); }

    // Estilo / tipo de vuelo del proximo "Iniciar" de Ruta.
    // Con ruta en marcha (o pendiente) los toggles reflejan el del avion en
    // foco y al pulsar aplican en vivo; sin ruta, solo preferencia.
    private TakeoffStyleId _styleId = TakeoffStyleId.Relaxed;
    private bool _switchingStyle;
    public string StyleSummary => TakeoffStyles.Get(_styleId).Summary;
    public string StyleSpec => TakeoffStyles.Get(_styleId).Spec;

    private CruiseModeId _flightModeId = CruiseModeId.Straight;
    private bool _switchingFlightMode;
    public string FlightModeSummary => CruiseModes.Get(_flightModeId).Summary;

    private bool _routeActionsEnabled;
    public bool RouteActionsEnabled {
        get => _routeActionsEnabled;
        set => Set(ref _routeActionsEnabled, value);
    }

    private string _routeActionsHint =
        "Con la ruta en marcha: virajes ~90° y cambios de altitud objetivo. No abortan el crucero.";
    public string RouteActionsHint {
        get => _routeActionsHint;
        set => Set(ref _routeActionsHint, value);
    }

    // --- Estado del hueco central ------------------------------------------
    private bool _dockedNow;

    private string _viewportTabText = "X-Plane 12 - buscando ventana";
    public string ViewportTabText { get => _viewportTabText; set => Set(ref _viewportTabText, value); }

    private Brush _dockDotColor = Brushes.Gray;
    public Brush DockDotColor { get => _dockDotColor; set => Set(ref _dockDotColor, value); }

    private string _dockStatusText = "X-Plane: sin ventana";
    public string DockStatusText { get => _dockStatusText; set => Set(ref _dockStatusText, value); }

    private Visibility _viewportHintVisibility = Visibility.Visible;
    public Visibility ViewportHintVisibility { get => _viewportHintVisibility; set => Set(ref _viewportHintVisibility, value); }

    private string _viewportHintTitle = "Esperando a X-Plane";
    public string ViewportHintTitle { get => _viewportHintTitle; set => Set(ref _viewportHintTitle, value); }

    private string _viewportHintDetail = "";
    public string ViewportHintDetail { get => _viewportHintDetail; set => Set(ref _viewportHintDetail, value); }

    // --- Listado de aviones (Despegue / FOCO) --------------------------------

    private string _focusText = "LOCAL · Local";
    public string FocusText {
        get => _focusText;
        set => Set(ref _focusText, value);
    }

    // Desplegable EN FOCO inline (sin Popup HWND: ver comentario en el XAML).
    private bool _focusPickerOpen;
    public bool FocusPickerOpen {
        get => _focusPickerOpen;
        set {
            if (_focusPickerOpen == value) return;
            Set(ref _focusPickerOpen, value);
            OnPropertyChanged(nameof(FocusPickerListVisibility));
        }
    }
    public Visibility FocusPickerListVisibility =>
        FocusPickerOpen ? Visibility.Visible : Visibility.Collapsed;

    private AircraftListEntry? _selectedAircraft;
    public AircraftListEntry? SelectedAircraft {
        get => _selectedAircraft;
        set => Set(ref _selectedAircraft, value);
    }

    private string _aircraftEmptyHint =
        "De momento solo esta tu avion. Cuando haya otras IAs en la partida, apareceran aqui.";
    public string AircraftEmptyHint {
        get => _aircraftEmptyHint;
        set => Set(ref _aircraftEmptyHint, value);
    }

    private Visibility _aircraftEmptyVisibility = Visibility.Visible;
    public Visibility AircraftEmptyVisibility {
        get => _aircraftEmptyVisibility;
        set => Set(ref _aircraftEmptyVisibility, value);
    }

    // --- Interceptar (panel izquierdo) --------------------------------------

    private InterceptTargetEntry? _selectedInterceptTarget;
    public InterceptTargetEntry? SelectedInterceptTarget {
        get => _selectedInterceptTarget;
        set => Set(ref _selectedInterceptTarget, value);
    }

    private string _interceptEmptyHint =
        "No hay otro avion al que interceptar. Anade aviones de IA en X-Plane " +
        "(Flight Configuration > AI Aircraft) y apareceran aqui.";
    public string InterceptEmptyHint {
        get => _interceptEmptyHint;
        set => Set(ref _interceptEmptyHint, value);
    }

    private Visibility _interceptEmptyVisibility = Visibility.Visible;
    public Visibility InterceptEmptyVisibility {
        get => _interceptEmptyVisibility;
        set => Set(ref _interceptEmptyVisibility, value);
    }

    // Puesto respecto al blanco en polares: azimut (0 = delante de su morro,
    // 90 = derecha, 180 = cola, 270 = izquierda), distancia horizontal y
    // altura. Sin posiciones predefinidas. Son deslizadores ligados a
    // ControlTuning.Station: se aplican en tiempo real, tambien con una
    // interceptacion en marcha, y la bolita 3D del juego sigue el cambio.
    public string InterceptStationSpec => _tuning.Station.Summary();

    // El puesto nunca puede quedar DENTRO de la zona de seguridad (cilindro
    // SafeH x SafeV alrededor del blanco): si el regulador que se mueve lo
    // llevaria dentro, se queda en el borde. La distancia baja hasta SafeH y
    // la altura hasta +-SafeV (conserva el signo que llevaba).
    private void UpdateStation(float? az = null, float? dist = null, float? height = null) {
        StationSpec cur = _tuning.Station;
        float d = dist ?? cur.DistanceM, h = height ?? cur.HeightM;
        float safeH = _tuning.SafeHorizontalM, safeV = _tuning.SafeVerticalM;
        if (dist is not null && MathF.Abs(h) < safeV) d = MathF.Max(d, safeH);
        if (height is not null && d < safeH) {
            float sign = h != 0f ? MathF.Sign(h) : (cur.HeightM < 0f ? -1f : 1f);
            h = sign * MathF.Max(MathF.Abs(h), safeV);
        }
        _tuning.Station = new StationSpec(az ?? cur.AzimuthDeg, d, h);
        NotifyStationChanged();
        // Si se corrigio el valor, el slider debe volver al corregido.
        Dispatcher.BeginInvoke(new Action(NotifyStationChanged));
    }

    private void NotifyStationChanged() {
        OnPropertyChanged(nameof(StationAzimuthDeg));
        OnPropertyChanged(nameof(StationDistanceM));
        OnPropertyChanged(nameof(StationHeightM));
        OnPropertyChanged(nameof(InterceptStationSpec));
    }

    public double StationAzimuthDeg {
        get => _tuning.Station.AzimuthDeg;
        set => UpdateStation(az: (float)value);
    }
    public double StationDistanceM {
        get => _tuning.Station.DistanceM;
        set => UpdateStation(dist: (float)value);
    }
    public double StationHeightM {
        get => _tuning.Station.HeightM;
        set => UpdateStation(height: (float)value);
    }

    public double SafeHorizontalM {
        get => _tuning.SafeHorizontalM;
        set { _tuning.SafeHorizontalM = (float)value; OnPropertyChanged(nameof(SafeHorizontalM));
              OnPropertyChanged(nameof(InterceptStationSpec)); }
    }
    public double SafeVerticalM {
        get => _tuning.SafeVerticalM;
        set { _tuning.SafeVerticalM = (float)value; OnPropertyChanged(nameof(SafeVerticalM));
              OnPropertyChanged(nameof(InterceptStationSpec)); }
    }

    // --- Bolita 3D del puesto (overlay del connector) -----------------------
    // Se recalcula en cada refresco de UI (10 Hz) con el blanco seleccionado y
    // el puesto actual; el connector extrapola con la velocidad del blanco.
    private bool _markerShown;
    private bool _showStationMarker = true;
    public bool ShowStationMarker {
        get => _showStationMarker;
        set => Set(ref _showStationMarker, value);
    }
    private StationSpec? _lastMarkerStation;

    private void UpdateStationMarker() {
        if (!_client.IsConnected) { _markerShown = false; return; }
        // Sin interceptor (foco Global) o con el propio interceptor como blanco
        // el puesto no significa nada: saldria pegado a "mi avion".
        bool want = ShowStationMarker && !_director.IsGlobalFocus &&
                    SelectedInterceptTarget is InterceptTargetEntry target &&
                    target.XplmIndex != _director.FocusedXplmIndex &&
                    _director.World.TryCapture(target.XplmIndex, out _);
        if (!want) {
            if (_markerShown) {
                _client.SetStationMarker(false, 0, 0, 0, 0, 0, 0, 1f, 0, 0, 0);
                _markerShown = false;
            }
            return;
        }
        var entry = SelectedInterceptTarget!;
        _director.World.TryCapture(entry.XplmIndex, out TargetSnapshot snap);
        (float aft, float right, float up) = _tuning.ResolveStation();
        InterceptGeometry g = InterceptGeometry.Solve(0, 0, 0, snap, aft, right, up, 0.0);

        // El puesto gira con el morro del blanco: en un viraje se mueve en arco,
        // no con la velocidad del blanco. Se suma la velocidad de esa rotacion
        // (d offset / d psi * yawRate) para que el connector extrapole bien.
        float yawRate = EstimateTargetYawRate(entry.XplmIndex, snap.HeadingDeg);
        double psi = snap.HeadingDeg * Math.PI / 180.0;
        double fwdE = Math.Sin(psi), fwdN = Math.Cos(psi);
        double rgtE = Math.Cos(psi), rgtN = -Math.Sin(psi);
        double dOffE = yawRate * (-aft * rgtE - right * fwdE);
        double dOffN = yawRate * (-aft * rgtN - right * fwdN);
        float mvx = (float)(snap.Vx + dOffE);
        float mvy = (float)snap.Vy;
        float mvz = (float)(snap.Vz - dOffN);
        // Roja si el puesto pedido cae dentro de la zona de seguridad (p. ej. al
        // subir esos reguladores con el puesto ya colocado); cian si no.
        StationSpec st = _tuning.Station;
        bool inside = st.DistanceM < _tuning.SafeHorizontalM && MathF.Abs(st.HeightM) < _tuning.SafeVerticalM;
        _client.SetStationMarker(true, g.Px, g.Py, g.Pz, mvx, mvy, mvz,
                                 StationMarkerRadiusM,
                                 inside ? 1.0f : 0.15f, inside ? 0.12f : 0.9f, inside ? 0.12f : 1.0f);
        _markerShown = true;
    }

    // Velocidad de guinada del blanco (rad/s, + = a derechas) a partir de los
    // rumbos de refrescos consecutivos, filtrada para no amplificar el ruido.
    private int _yawIdx = -1;
    private float _yawPrevDeg;
    private long _yawPrevTicks;
    private float _yawRate;

    private float EstimateTargetYawRate(int idx, float headingDeg) {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (idx != _yawIdx) {
            _yawIdx = idx; _yawPrevDeg = headingDeg; _yawPrevTicks = now; _yawRate = 0f;
            return 0f;
        }
        float dt = (float)((now - _yawPrevTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
        if (dt < 0.02f) return _yawRate;
        if (dt > 1.0f) { _yawPrevDeg = headingDeg; _yawPrevTicks = now; _yawRate = 0f; return 0f; }
        float dPsi = headingDeg - _yawPrevDeg;
        while (dPsi > 180f) dPsi -= 360f;
        while (dPsi < -180f) dPsi += 360f;
        float raw = dPsi * MathF.PI / 180f / dt;
        _yawRate += (raw - _yawRate) * 0.5f;
        _yawPrevDeg = headingDeg; _yawPrevTicks = now;
        return _yawRate;
    }

    private const float StationMarkerRadiusM = 6f;

    private string _interceptStatusText = "Sin interceptacion activa.";
    public string InterceptStatusText {
        get => _interceptStatusText;
        set => Set(ref _interceptStatusText, value);
    }

    private string _interceptSequenceText = "En espera";
    public string InterceptSequenceText {
        get => _interceptSequenceText;
        set => Set(ref _interceptSequenceText, value);
    }

    private string _interceptChecklistText = "";
    public string InterceptChecklistText {
        get => _interceptChecklistText;
        set => Set(ref _interceptChecklistText, value);
    }

    // --- Log de datos (panel izquierdo) -------------------------------------

    private string _dataLogStatusText = "LOCAL · 0 muestras · detenido";
    public string DataLogStatusText { get => _dataLogStatusText; set => Set(ref _dataLogStatusText, value); }

    // --- Config (panel izquierdo): "limites humanos" del control ----------
    //
    // Cada campo de texto se aplica en caliente sobre _tuning en cuanto el
    // valor tecleado parsea como float valido (UpdateSourceTrigger=
    // PropertyChanged en el XAML, asi que no hace falta perder el foco). Si
    // el texto no parsea (usuario a mitad de escribir, o basura), NO se
    // toca _tuning -- se queda con el ultimo valor bueno hasta que el texto
    // vuelva a ser valido. ControlTuning ademas clampa en su propio setter,
    // asi que un valor fuera de rango razonable se recorta ahi, no aqui.
    //
    // SetTuningText hace de "Set<T>" pero con el efecto secundario de
    // aplicar sobre el modelo; se reusa para los ~20 campos de abajo en vez
    // de repetir el mismo cuerpo de 4 lineas veinte veces.
    private void SetTuningText(ref string field, string value, Action<float> apply,
                               [CallerMemberName] string? name = null) {
        field = value;
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) apply(f);
        OnPropertyChanged(name);
    }

    private static string FmtTuning(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    // Mandos / rampas (AircraftControls) -- ver ControlTuning.cs.
    private string _throttleSlewRateText = "";
    public string ThrottleSlewRateText {
        get => _throttleSlewRateText;
        set => SetTuningText(ref _throttleSlewRateText, value, f => _tuning.ThrottleSlewRate = f);
    }

    private string _pitchSlewRateText = "";
    public string PitchSlewRateText {
        get => _pitchSlewRateText;
        set => SetTuningText(ref _pitchSlewRateText, value, f => _tuning.PitchSlewRate = f);
    }

    private string _rollSlewRateText = "";
    public string RollSlewRateText {
        get => _rollSlewRateText;
        set => SetTuningText(ref _rollSlewRateText, value, f => _tuning.RollSlewRate = f);
    }

    private string _yawSlewRateText = "";
    public string YawSlewRateText {
        get => _yawSlewRateText;
        set => SetTuningText(ref _yawSlewRateText, value, f => _tuning.YawSlewRate = f);
    }

    // Agresividad de acciones/maniobras (ManeuverSequence).
    private string _maneuverPitchRampText = "";
    public string ManeuverPitchRampText {
        get => _maneuverPitchRampText;
        set => SetTuningText(ref _maneuverPitchRampText, value, f => _tuning.ManeuverPitchRampDegPerSec = f);
    }

    private string _maneuverBankRampText = "";
    public string ManeuverBankRampText {
        get => _maneuverBankRampText;
        set => SetTuningText(ref _maneuverBankRampText, value, f => _tuning.ManeuverBankRampDegPerSec = f);
    }

    private string _maneuverStickRampText = "";
    public string ManeuverStickRampText {
        get => _maneuverStickRampText;
        set => SetTuningText(ref _maneuverStickRampText, value, f => _tuning.ManeuverStickRampPerSecond = f);
    }

    // Limites de G (ManeuverSequence). Son dos niveles: el blando es el que
    // usa la proteccion continua para ir quitando mando antes de llegar, y el
    // duro es el backstop estructural, que no deberia saltar nunca en vuelo
    // normal. Ver ControlTuning.cs.
    private string _gSoftHighText = "";
    public string GSoftHighText {
        get => _gSoftHighText;
        set => SetTuningText(ref _gSoftHighText, value, f => _tuning.GSoftHigh = f);
    }

    private string _gSoftLowText = "";
    public string GSoftLowText {
        get => _gSoftLowText;
        set => SetTuningText(ref _gSoftLowText, value, f => _tuning.GSoftLow = f);
    }

    private string _gHardHighText = "";
    public string GHardHighText {
        get => _gHardHighText;
        set => SetTuningText(ref _gHardHighText, value, f => _tuning.GHardHigh = f);
    }

    private string _gHardLowText = "";
    public string GHardLowText {
        get => _gHardLowText;
        set => SetTuningText(ref _gHardLowText, value, f => _tuning.GHardLow = f);
    }

    private string _terrainFloorAglText = "";
    public string TerrainFloorAglText {
        get => _terrainFloorAglText;
        set => SetTuningText(ref _terrainFloorAglText, value, f => _tuning.TerrainFloorAglFt = f);
    }

    private string _levelFlightMaxPitchAdjustText = "";
    public string LevelFlightMaxPitchAdjustText {
        get => _levelFlightMaxPitchAdjustText;
        set => SetTuningText(ref _levelFlightMaxPitchAdjustText, value, f => _tuning.LevelFlightMaxPitchAdjustDeg = f);
    }

    // Recuperacion de nivelado tras una maniobra (ManeuverSequence).
    private string _recoverBankCaptureText = "";
    public string RecoverBankCaptureText {
        get => _recoverBankCaptureText;
        set => SetTuningText(ref _recoverBankCaptureText, value, f => _tuning.RecoverBankCaptureDeg = f);
    }

    private string _recoverPitchCaptureText = "";
    public string RecoverPitchCaptureText {
        get => _recoverPitchCaptureText;
        set => SetTuningText(ref _recoverPitchCaptureText, value, f => _tuning.RecoverPitchCaptureDeg = f);
    }

    private string _recoverMinSecondsText = "";
    public string RecoverMinSecondsText {
        get => _recoverMinSecondsText;
        set => SetTuningText(ref _recoverMinSecondsText, value, f => _tuning.RecoverMinSeconds = f);
    }

    // Envolvente de velocidad: Acelerar/Frenar (ManeuverSequence).
    private string _speedStepText = "";
    public string SpeedStepText {
        get => _speedStepText;
        set => SetTuningText(ref _speedStepText, value, f => _tuning.SpeedStepKt = f);
    }

    private string _minTargetIasText = "";
    public string MinTargetIasText {
        get => _minTargetIasText;
        set => SetTuningText(ref _minTargetIasText, value, f => _tuning.MinTargetIasKt = f);
    }

    private string _maxTargetIasText = "";
    public string MaxTargetIasText {
        get => _maxTargetIasText;
        set => SetTuningText(ref _maxTargetIasText, value, f => _tuning.MaxTargetIasKt = f);
    }

    // Relee todos los campos de texto desde ControlTuning (fuente de verdad
    // unica de los defaults): se usa al construir el shell y tras pulsar
    // "Restablecer valores por defecto". Pasar por el setter publico de cada
    // propiedad (en vez de tocar el campo _xxxText a mano) mantiene el
    // parseo en un solo sitio y dispara el PropertyChanged que refresca el
    // TextBox en pantalla.
    private void SyncTuningTextsFromModel() {
        ThrottleSlewRateText = FmtTuning(_tuning.ThrottleSlewRate);
        PitchSlewRateText = FmtTuning(_tuning.PitchSlewRate);
        RollSlewRateText = FmtTuning(_tuning.RollSlewRate);
        YawSlewRateText = FmtTuning(_tuning.YawSlewRate);

        ManeuverPitchRampText = FmtTuning(_tuning.ManeuverPitchRampDegPerSec);
        ManeuverBankRampText = FmtTuning(_tuning.ManeuverBankRampDegPerSec);
        ManeuverStickRampText = FmtTuning(_tuning.ManeuverStickRampPerSecond);

        GSoftHighText = FmtTuning(_tuning.GSoftHigh);
        GSoftLowText = FmtTuning(_tuning.GSoftLow);
        GHardHighText = FmtTuning(_tuning.GHardHigh);
        GHardLowText = FmtTuning(_tuning.GHardLow);
        TerrainFloorAglText = FmtTuning(_tuning.TerrainFloorAglFt);
        LevelFlightMaxPitchAdjustText = FmtTuning(_tuning.LevelFlightMaxPitchAdjustDeg);

        RecoverBankCaptureText = FmtTuning(_tuning.RecoverBankCaptureDeg);
        RecoverPitchCaptureText = FmtTuning(_tuning.RecoverPitchCaptureDeg);
        RecoverMinSecondsText = FmtTuning(_tuning.RecoverMinSeconds);

        NotifyStationChanged();
        OnPropertyChanged(nameof(SafeHorizontalM));
        OnPropertyChanged(nameof(SafeVerticalM));

        SpeedStepText = FmtTuning(_tuning.SpeedStepKt);
        MinTargetIasText = FmtTuning(_tuning.MinTargetIasKt);
        MaxTargetIasText = FmtTuning(_tuning.MaxTargetIasKt);
    }

    private void OnResetTuningClick(object sender, RoutedEventArgs e) {
        _tuning.ResetToDefaults();
        SyncTuningTextsFromModel();
        Append("Config: valores de control restablecidos a los de fabrica.");
    }

    private void RefreshDockTexts() {
        bool attached = _docker?.Attached ?? true;

        if (!attached) {
            ViewportTabText = "X-Plane 12 - suelto";
            DockDotColor = Brushes.Orange;
            DockStatusText = "X-Plane: suelto";
            ViewportHintTitle = "X-Plane esta suelto";
            ViewportHintDetail = "Su ventana ha vuelto a la posicion y el marco que tenia. " +
                                 "Activa \"Anclar X-Plane\" en la barra de herramientas para " +
                                 "volver a encajarla en este hueco.";
            ViewportHintVisibility = Visibility.Visible;
        } else if (_dockedNow) {
            ViewportTabText = "X-Plane 12 - anclado";
            DockDotColor = Brushes.LimeGreen;
            DockStatusText = "X-Plane: anclado";
            ViewportHintVisibility = Visibility.Collapsed;
        } else {
            ViewportTabText = "X-Plane 12 - buscando ventana";
            DockDotColor = Brushes.Gray;
            DockStatusText = "X-Plane: sin ventana";
            ViewportHintTitle = "Esperando a X-Plane";
            ViewportHintDetail = "Arranca X-Plane en modo ventana (no pantalla completa): " +
                                 "en cuanto aparezca su ventana, se colocara aqui y seguira " +
                                 "a este hueco cada vez que muevas el shell o los separadores.";
            ViewportHintVisibility = Visibility.Visible;
        }
    }

    // --- Barra de titulo ----------------------------------------------------

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e) {
        if (e.ClickCount == 2) {
            ToggleMaximize();
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e) {
        _refreshTimer.Stop();
        // Deja la ultima rafaga de muestras escrita en disco antes de salir.
        _dataLogs.CloseAll();
        // Devuelve a X-Plane su marco y su geometria antes de irse: cerrar el
        // shell no puede dejar al usuario con una ventana sin barra de titulo
        // encajada en un hueco que ya no existe.
        _docker?.Dispose();
        base.OnClosed(e);
    }

    // --- Barra de herramientas ----------------------------------------------

    private void OnStartClick(object sender, RoutedEventArgs e) {
        if (!_director.StartRoute(TakeoffStyles.Get(_styleId), CruiseModes.Get(_flightModeId),
                                  out string error, out string startedAs))
            Append($"Ruta ({_director.FocusedLabel}): {error}.");
        else
            Append($"Ruta iniciada en {_director.FocusedLabel}: {startedAs}.");
    }

    private void OnStyleClick(object sender, RoutedEventArgs e) {
        if (!IsInitialized || sender is not System.Windows.Controls.Primitives.ToggleButton tab) return;
        if (tab.IsChecked != true) tab.IsChecked = true;
    }

    private void OnStyleChanged(object sender, RoutedEventArgs e) {
        if (!IsInitialized || _switchingStyle) return;
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { IsChecked: true } tab) return;

        TakeoffStyleId id = tab == StyleCombat ? TakeoffStyleId.Combat
                         : tab == StyleEmergency ? TakeoffStyleId.Emergency
                         : TakeoffStyleId.Relaxed;

        ApplyStyleUi(id);
    }

    private void OnFlightModeClick(object sender, RoutedEventArgs e) {
        if (!IsInitialized || sender is not System.Windows.Controls.Primitives.ToggleButton tab) return;
        if (tab.IsChecked != true) tab.IsChecked = true;
    }

    private void OnFlightModeChanged(object sender, RoutedEventArgs e) {
        if (!IsInitialized || _switchingFlightMode) return;
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { IsChecked: true } tab) return;

        CruiseModeId id = tab == FlightWanderer ? CruiseModeId.Wanderer
                        : tab == FlightNormal ? CruiseModeId.Normal
                        : CruiseModeId.Straight;

        ApplyFlightModeUi(id);

        // Si el avion en foco ya tiene ruta (o pendiente), aplica ya; si no,
        // queda como preferencia del proximo Iniciar.
        if (_director.SetCruiseMode(CruiseModes.Get(id), out string appliedAs) &&
            appliedAs.Length > 0)
            Append($"Tipo de vuelo ({_director.FocusedLabel}): {appliedAs}.");
    }

    private void ApplyFlightModeUi(CruiseModeId id) {
        _switchingFlightMode = true;
        try {
            FlightStraight.IsChecked = id == CruiseModeId.Straight;
            FlightWanderer.IsChecked = id == CruiseModeId.Wanderer;
            FlightNormal.IsChecked = id == CruiseModeId.Normal;
            _flightModeId = id;
            OnPropertyChanged(nameof(FlightModeSummary));
        } finally {
            _switchingFlightMode = false;
        }
    }

    private void ApplyStyleUi(TakeoffStyleId id) {
        _switchingStyle = true;
        try {
            StyleRelaxed.IsChecked = id == TakeoffStyleId.Relaxed;
            StyleCombat.IsChecked = id == TakeoffStyleId.Combat;
            StyleEmergency.IsChecked = id == TakeoffStyleId.Emergency;
            _styleId = id;
            OnPropertyChanged(nameof(StyleSummary));
            OnPropertyChanged(nameof(StyleSpec));
        } finally {
            _switchingStyle = false;
        }
    }

    // Enlaza los toggles del panel RUTA al AgentView del foco (modo real /
    // pendiente). Sin ruta activa no pisa la preferencia del usuario.
    private void SyncRoutePanelFromFocus(AgentView? view) {
        if (view is null) {
            RouteActionsEnabled = false;
            RouteActionsHint =
                "Foco GLOBAL: elige un avion para ver o cambiar su tipo de vuelo y acciones de ruta.";
            return;
        }

        if (view.InterceptRunning || view.InterceptPending) {
            RouteActionsEnabled = false;
            RouteActionsHint =
                $"En interceptacion ({view.ModeText}): las acciones de ruta no aplican. " +
                "El tipo de vuelo de arriba es preferencia para el proximo Iniciar.";
            return;
        }

        if (view.CruiseRunning || view.RoutePending) {
            if (view.CruiseModeId != _flightModeId)
                ApplyFlightModeUi(view.CruiseModeId);
            RouteActionsEnabled = view.CruiseRunning;
            RouteActionsHint = view.CruiseRunning
                ? "Ruta en marcha: virajes ~90° y ±1000 ft de altitud objetivo. Cambiar el tipo de vuelo aplica ya."
                : "Despegue en curso → luego ruta. Cambiar el tipo de vuelo actualiza el pendiente; las acciones de ruta se habilitan al nivelar.";
            return;
        }

        RouteActionsEnabled = false;
        RouteActionsHint =
            "Sin ruta activa: pulsa Iniciar para aplicar el tipo de vuelo. Virajes y altitud solo con la ruta en marcha.";

        if (view.TakeoffRunning)
            ApplyStyleUi(view.TakeoffStyleId);
    }

    private void OnAbortClick(object sender, RoutedEventArgs e) {
        // Aborta el avion en foco (todas sus secuencias); con GLOBAL, todos.
        _director.AbortFocused();
        Append($"Abortar / manual: {_director.FocusedLabel}.");
    }

    // Botones del panel RUTA: virajes / ±1000 ft sin abortar el crucero.
    // Tag = ManeuverKind de viraje, o "AltUp1000" / "AltDown1000".
    private void OnRouteActionClick(object sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: string tag }) return;

        if (tag is "AltUp1000" or "AltDown1000") {
            float delta = tag == "AltUp1000"
                ? CruisePilot.AltitudeNudgeFt
                : -CruisePilot.AltitudeNudgeFt;
            if (!_director.NudgeRouteAltitude(delta, out string altError)) {
                Append($"Ruta ALT ({_director.FocusedLabel}): {altError}.");
                return;
            }
            string dir = delta >= 0f ? $"+{delta:0}" : $"{delta:0}";
            Append($"Ruta ALT {dir} ft en {_director.FocusedLabel}.");
            return;
        }

        if (!Enum.TryParse(tag, out ManeuverKind kind)) return;
        if (!_director.RequestRouteTurn(kind, out string turnError)) {
            Append($"Ruta viraje ({_director.FocusedLabel}): {turnError}.");
            return;
        }
        Append($"Ruta: {ManeuverCatalog.Get(kind).Label} (~{CruisePilot.ManualTurnDeltaDeg:0}°) en {_director.FocusedLabel}.");
    }

    // Un solo handler para los botones de inicio de simulacion: el Tag lleva
    // el nombre del SimStart (ver XAML).
    private void OnSimStartClick(object sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: string tag } ||
            !Enum.TryParse(tag, out SimStart start)) return;
        SimStartPlan plan = SimScenarios.Get(start);
        if (!_director.IsGlobalFocus) {
            Append($"{plan.Name}: solo disponible con foco GLOBAL.");
            return;
        }
        if (!_director.StartSimulation(start, out string error)) {
            Append($"{plan.Name}: {error}.");
            return;
        }
        Append($"{plan.Name}: {plan.Label}.");
    }

    // Un solo handler para los ~25 botones del catalogo de acciones: cada
    // boton lleva en su Tag el nombre del ManeuverKind (ver XAML), asi que no
    // hace falta un metodo por maniobra.
    private void OnManeuverClick(object sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: string tag }) return;
        if (!Enum.TryParse(tag, out ManeuverKind kind)) return;

        ManeuverDefinition def = ManeuverCatalog.Get(kind);
        if (!_director.StartManeuver(kind, force: false, out string error, out string adaptation)) {
            Append($"'{def.Label}' ({_director.FocusedLabel}): no es posible ahora mismo: {error}.");
            return;
        }
        if (adaptation.Length > 0)
            Append($"'{def.Label}' ({_director.FocusedLabel}) se ejecuta adaptada: {adaptation}.");
        else
            Append($"'{def.Label}' en {_director.FocusedLabel}.");
    }

    private void OnFocusAircraftClick(object sender, RoutedEventArgs e) {
        if (SelectedAircraft is not AircraftListEntry ac) {
            Append("Foco: elige GLOBAL o un avion de la lista.");
            return;
        }
        ApplyFocus(ac);
    }

    private void OnFocusPickerItemClick(object sender, RoutedEventArgs e) {
        if (sender is not FrameworkElement { Tag: AircraftListEntry ac }) return;
        SelectedAircraft = ac;
        // Un clic en el desplegable solo cambia a quien van las ordenes: si
        // estabas en vista aerea GLOBAL, la camara se queda (como el pick
        // sobre un avion en overview). GLOBAL si reaplica la vista aerea.
        // Para enganchar chase / soltar camara: doble clic en la lista o
        // "Poner en foco".
        ApplyFocus(ac, keepCamera: !ac.IsGlobal);
        FocusPickerOpen = false;
    }

    // Indice de la IA cuya telemetria se pide a ritmo de frame por estar en
    // foco (-1 = ninguna). Watch/Unwatch son con refcount: no pisan a la
    // interceptacion de otro avion que observe la misma IA.
    private int _watchedFocusIdx = -1;

    private void UpdateFocusWatch(int newIdx) {
        int wanted = newIdx >= 1 ? newIdx : -1;
        if (wanted == _watchedFocusIdx) return;
        if (_watchedFocusIdx >= 1) _director.World.Unwatch(_watchedFocusIdx);
        _watchedFocusIdx = wanted;
        if (wanted >= 1) _director.World.Watch(wanted);
    }

    // Cambiar el foco SOLO cambia a quien van las ordenes y que refleja la UI:
    // no aborta ni toca a ningun avion (cada uno sigue con su ruta / maniobra /
    // intercept). keepCamera=true: solo foco UI (la camara overview ya la
    // mueve el connector con pick/paneo).
    private void ApplyFocus(AircraftListEntry ac, bool keepCamera = false) {
        _director.SetFocus(ac.XplmIndex, ac.DisplayName);
        UpdateFocusWatch(ac.XplmIndex);
        _lastInterceptRefusal = "";

        if (ac.IsGlobal) {
            if (!keepCamera)
                ApplyGlobalFocusPresentation();
            UpdateFocusText();
            RefreshAircraftList();
            Append(keepCamera
                ? "Foco: GLOBAL · vista libre."
                : "Foco: GLOBAL · vista aerea. Clic sobre un avion lo selecciona (sin mover la camara); arrastre izquierdo panea; rueda hace zoom; arrastre derecho orbita; doble clic sobre un avion entra en su vista (chase / local); doble clic en vacio reencuadra.");
            return;
        }

        if (!keepCamera && _client.IsConnected) {
            // Camara automatica al cambiar de foco desde la UI: chase en IA,
            // soltar en local (el ownship usa la vista nativa de X-Plane).
            if (ac.XplmIndex >= 1)
                _client.FollowCamera(ac.XplmIndex);
            else
                _client.ReleaseCamera();
        }

        AgentView? view = _director.FocusedView;
        SyncRoutePanelFromFocus(view);

        UpdateFocusText();
        RefreshAircraftList();
        string role = ac.XplmIndex == 0 ? "LOCAL" : "IA";
        Append(keepCamera
            ? $"Foco: {role} · {ac.DisplayName} (seguimiento aereo)."
            : $"Foco: {role} · {ac.DisplayName}.");
    }

    // Evento OverviewFocus del connector: el usuario eligio un avion (o libre)
    // en la vista aerea. Actualiza el foco UI sin tocar la camara.
    public void ApplyOverviewFocusFromConnector(int xplmIndex) {
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.BeginInvoke(() => ApplyOverviewFocusFromConnector(xplmIndex));
            return;
        }

        if (xplmIndex < 0) {
            AircraftListEntry? global = null;
            foreach (AircraftListEntry e in AircraftEntries) {
                if (e.IsGlobal) { global = e; break; }
            }
            if (global is null) {
                global = new AircraftListEntry {
                    IsGlobal = true,
                    Name = "Global",
                    XplmIndex = FlightDirector.GlobalFocusIndex,
                    RoleLabel = "ZONA",
                };
            }
            SelectedAircraft = global;
            ApplyFocus(global, keepCamera: true);
            return;
        }

        AircraftListEntry? ac = null;
        foreach (AircraftListEntry e in AircraftEntries) {
            if (!e.IsGlobal && e.XplmIndex == xplmIndex) { ac = e; break; }
        }
        if (ac is null) {
            Append($"Vista aerea: avion idx {xplmIndex} no esta en la lista.");
            return;
        }
        SelectedAircraft = ac;
        ApplyFocus(ac, keepCamera: true);
    }

    // Activa marcadores + lineas + triangulo y pide la vista aerea al connector.
    // (Los nombres overlay son por avion en Aviones; no se fuerzan aqui.)
    // Los toggles de Graficos se sincronizan para que la UI refleje lo dibujado.
    private void ApplyGlobalFocusPresentation() {
        bool changed = false;
        if (!_graphics.ShowMarkers) { _graphics.ShowMarkers = true; changed = true; }
        if (!_graphics.ShowLines) { _graphics.ShowLines = true; changed = true; }
        if (!_graphics.ShowTriangle) { _graphics.ShowTriangle = true; changed = true; }
        if (changed) {
            SyncGraphicsUiFromModel();
            ApplyGraphicsConfig();
        } else if (_client.IsConnected) {
            // Reaplica por si el plugin se recargo sin la config actual.
            ApplyGraphicsConfig();
        }

        if (_client.IsConnected)
            _client.StartOverviewCamera();
        else
            Append("Camara aerea: sin conexion con el plugin.");
    }

    // Tras PlaceScenario / ReleaseEverything la camara se suelta: si el foco
    // sigue en GLOBAL, vuelve a pedir la vista aerea.
    // Se llama desde el hilo del pipe (ScenarioReady): toca UI, asi que salta
    // al hilo de UI.
    public void ResumeGlobalCameraIfFocused() {
        if (!Dispatcher.CheckAccess()) {
            Dispatcher.BeginInvoke(ResumeGlobalCameraIfFocused);
            return;
        }
        if (_director.IsGlobalFocus)
            ApplyGlobalFocusPresentation();
    }

    private void OnFollowCameraClick(object sender, RoutedEventArgs e) {
        if (SelectedAircraft is not AircraftListEntry ac) {
            Append("Camara: elige primero un avion de la lista.");
            return;
        }
        if (ac.IsGlobal || ac.XplmIndex < 1) {
            Append("Camara: solo se puede seguir a un avion IA (no GLOBAL ni el local).");
            return;
        }
        if (!_client.IsConnected) {
            Append("Camara: sin conexion con el plugin.");
            return;
        }
        _client.FollowCamera(ac.XplmIndex);
        Append($"Camara: siguiendo IA · {ac.DisplayName} (idx {ac.XplmIndex}).");
    }

    private void OnReleaseCameraClick(object sender, RoutedEventArgs e) {
        if (!_client.IsConnected) {
            Append("Camara: sin conexion con el plugin.");
            return;
        }
        _client.ReleaseCamera();
        Append("Camara: control soltado.");
    }

    // --- Interceptar --------------------------------------------------------
    //
    // Ultimo motivo de rechazo al pedir una interceptacion (InterceptRegistry:
    // mutua, ciclo, a si mismo...). Se muestra en el panel Interceptar.
    private string _lastInterceptRefusal = "";

    private void OnInterceptStartClick(object sender, RoutedEventArgs e) {
        if (_director.IsGlobalFocus) {
            Append("Interceptar: elige primero el avion interceptor (foco).");
            return;
        }
        if (SelectedInterceptTarget is not InterceptTargetEntry target) {
            Append("Interceptar: elige primero a que avion de la lista.");
            return;
        }
        if (!_client.IsConnected) {
            Append("Interceptar: no hay conexion con el plugin.");
            return;
        }

        if (!_director.StartIntercept(target.XplmIndex, target.Name, out string refusal)) {
            _lastInterceptRefusal = refusal;
            Append($"Interceptar {target.Name} ({_director.FocusedLabel}): no se puede ahora mismo -- {refusal}");
        } else {
            _lastInterceptRefusal = "";
        }
    }

    private void OnInterceptAbortClick(object sender, RoutedEventArgs e) {
        AircraftAgent? a = _director.Focused;
        if (a is null || (!a.Intercept.IsRunning && !a.IsInterceptPending)) {
            Append("Interceptar: el avion en foco no tiene ninguna interceptacion en marcha.");
            return;
        }
        _lastInterceptRefusal = "";
        _director.AbortInterceptMission();
    }

    // Todos los handlers de ToggleButton empiezan igual, y no es defensa
    // gratuita: los interruptores nacen con IsChecked="True" en el XAML, asi
    // que sus handlers se disparan DURANTE el parseo -- antes de que existan
    // los campos de los elementos declarados mas abajo en el arbol. El estado
    // inicial ya lo fija el propio XAML (paneles abiertos, anclaje puesto),
    // asi que aqui no hay nada que aplicar hasta que la ventana este montada.
    private void OnAttachToggled(object sender, RoutedEventArgs e) {
        if (!IsInitialized) return;
        _docker?.SetAttached(AttachToggle.IsChecked == true);
        RefreshDockTexts();
    }

    private void OnFrameToggled(object sender, RoutedEventArgs e) {
        if (!IsInitialized) return;
        _docker?.SetHideFrame(FrameToggle.IsChecked == true);
    }

    private void OnRedockClick(object sender, RoutedEventArgs e) => _docker?.DockNow();

    // Aerofrenos automaticos (pestana Config). Apagarlos es lo que permite
    // medir la resistencia del avion limpio para calibrar el modelo: con los
    // aerofrenos entrando y saliendo, una deceleracion no mide lo que se quiere
    // medir. El guard de IsInitialized es el mismo de los demas ToggleButton:
    // nacen con IsChecked="True" en el XAML y su handler se dispara durante el
    // parseo, antes de que existan los campos.
    private void OnAutoSpeedbrakeToggled(object sender, RoutedEventArgs e) {
        if (!IsInitialized) return;
        bool on = AutoSpeedbrakeToggle.IsChecked == true;
        _tuning.AutoSpeedbrake = on;
        Append(on ? "Aerofrenos automaticos: ON."
                  : "Aerofrenos automaticos: OFF (modo calibracion).");
    }

    // Recuadro del mouse yoke (ver el comentario en Datarefs.cs): el estado
    // se guarda aqui, no solo en el propio IsChecked del boton, porque
    // ApplyMouseYokeBoxState se llama tambien tras un reconecte o una
    // recarga de avion -- momentos en los que hay que volver a escribir el
    // dataref pero no tiene sentido tocar la UI.
    //
    // Empieza en true (oculto) para que la IA no se encuentre el recuadro
    // ni el control por raton activos por sorpresa; el XAML deja el boton
    // ya marcado a juego (IsChecked="True"), pero ese Checked inicial se
    // dispara durante InitializeComponent -- antes de que este campo o
    // _client existan -- y OnMouseYokeBoxToggled lo ignora por el guard de
    // IsInitialized, asi que el valor real de arranque es este default.
    private bool _mouseYokeBoxHidden = true;
    public bool MouseYokeBoxHidden => _mouseYokeBoxHidden;

    private void OnMouseYokeBoxToggled(object sender, RoutedEventArgs e) {
        if (!IsInitialized) return;
        _mouseYokeBoxHidden = MouseYokeBoxToggle.IsChecked == true;
        ApplyMouseYokeBoxState();
    }

    // sim/joystick/eq_pfc_yoke vive en el sim, no en nuestra conexion: no
    // hace falta reescribirlo en cada reconecte del pipe. Pero X-Plane SI lo
    // resetea a 0 cuando el avion del usuario se recarga (por eso lo hacen
    // tambien los plugins de terceros que tocan este dataref), asi que esto
    // se llama ademas desde App.xaml.cs cuando llega ese evento.
    public void ApplyMouseYokeBoxState() {
        if (_client.IsConnected) _client.Set(_d.MouseYokeBoxHidden, _mouseYokeBoxHidden ? 1 : 0);
    }

    // Empuja GraphicsSettings al connector. Se llama al tocar la UI y tras
    // SessionReady (el plugin no recuerda la config entre conexiones).
    public void ApplyGraphicsConfig() {
        if (!_client.IsConnected) return;
        _client.SetGraphicsConfig(
            _graphics.Flags,
            (byte)_graphics.Font,
            _graphics.ColorR, _graphics.ColorG, _graphics.ColorB,
            _graphics.Scale,
            _director.World.SnapshotOverlayLabels());
    }

    private bool _syncingGraphicsUi;
    private int _editingAircraftNameIndex = int.MinValue;

    private void SyncGraphicsUiFromModel() {
        _syncingGraphicsUi = true;
        try {
            GfxLinesToggle.IsChecked = _graphics.ShowLines;
            GfxTriangleToggle.IsChecked = _graphics.ShowTriangle;
            GfxMarkersToggle.IsChecked = _graphics.ShowMarkers;
            GfxPathToggle.IsChecked = _graphics.ShowPath;
            GfxFontCombo.SelectedIndex = _graphics.Font;
            GfxColorR.Text = ((int)Math.Round(_graphics.ColorR * 255f)).ToString(CultureInfo.InvariantCulture);
            GfxColorG.Text = ((int)Math.Round(_graphics.ColorG * 255f)).ToString(CultureInfo.InvariantCulture);
            GfxColorB.Text = ((int)Math.Round(_graphics.ColorB * 255f)).ToString(CultureInfo.InvariantCulture);
            GfxScaleText.Text = _graphics.Scale.ToString("0.##", CultureInfo.InvariantCulture);
        } finally {
            _syncingGraphicsUi = false;
        }
    }

    private void ReadGraphicsUiIntoModel() {
        _graphics.ShowLines = GfxLinesToggle.IsChecked == true;
        _graphics.ShowTriangle = GfxTriangleToggle.IsChecked == true;
        _graphics.ShowMarkers = GfxMarkersToggle.IsChecked == true;
        _graphics.ShowPath = GfxPathToggle.IsChecked == true;
        _graphics.Font = GfxFontCombo.SelectedIndex <= 0 ? 0 : 1;

        if (byte.TryParse(GfxColorR.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r))
            _graphics.ColorR = r / 255f;
        if (byte.TryParse(GfxColorG.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g))
            _graphics.ColorG = g / 255f;
        if (byte.TryParse(GfxColorB.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
            _graphics.ColorB = b / 255f;

        if (float.TryParse(GfxScaleText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale))
            _graphics.Scale = scale;
    }

    private void OnGraphicsChanged(object sender, RoutedEventArgs e) {
        if (!IsInitialized || _syncingGraphicsUi) return;
        ReadGraphicsUiIntoModel();
        ApplyGraphicsConfig();
    }

    private void OnGraphicsFontChanged(object sender, SelectionChangedEventArgs e) {
        OnGraphicsChanged(sender, e);
    }

    private void OnGraphicsTextKeyDown(object sender, KeyEventArgs e) {
        if (e.Key != Key.Enter) return;
        if (sender is TextBox tb) {
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        e.Handled = true;
    }

    private void OnResetGraphicsClick(object sender, RoutedEventArgs e) {
        _graphics.ResetToDefaults();
        SyncGraphicsUiFromModel();
        ApplyGraphicsConfig();
        Append("Graficos: valores restablecidos.");
    }

    private void OnAircraftNameGotFocus(object sender, KeyboardFocusChangedEventArgs e) {
        if (sender is TextBox { DataContext: AircraftListEntry ac } && !ac.IsGlobal)
            _editingAircraftNameIndex = ac.XplmIndex;
    }

    private void OnAircraftNameLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
        _editingAircraftNameIndex = int.MinValue;
    }

    private void OnAircraftNameKeyDown(object sender, KeyEventArgs e) {
        if (e.Key != Key.Enter) return;
        if (sender is TextBox tb) {
            CommitAircraftName(tb);
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        e.Handled = true;
    }

    private void OnAircraftNameLostFocus(object sender, RoutedEventArgs e) {
        if (sender is TextBox tb) CommitAircraftName(tb);
    }

    private void CommitAircraftName(TextBox tb) {
        if (tb.DataContext is not AircraftListEntry ac || ac.IsGlobal) return;
        // Fuerza el binding LostFocus por si Enter llego antes.
        tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        string name = (ac.Name ?? string.Empty).Trim();
        if (name.Length > 32) name = name[..32];
        if (!string.Equals(ac.Name, name, StringComparison.Ordinal))
            ac.Name = name;
        _director.World.SetLabel(ac.XplmIndex, name);
        if (_director.FocusedXplmIndex == ac.XplmIndex)
            _director.SetFocus(ac.XplmIndex, ac.DisplayName);
        ApplyGraphicsConfig();
        UpdateFocusText();
    }

    private void OnClearLogClick(object sender, RoutedEventArgs e) => Logs.Clear();

    private void OnStartDataLogClick(object sender, RoutedEventArgs e) {
        if (_director.IsGlobalFocus) {
            _globalDataLogRecording = true;
            EnsureGlobalRosterRecording();
            int n = _dataLogs.RecordingCount;
            Append(n <= 1
                ? "Caja negra GLOBAL: grabando el roster (1 nave)."
                : $"Caja negra GLOBAL: grabando el roster ({n} naves).");
        } else {
            FocusedDataLog.StartRecording();
            SyncDataLogWatches();
        }
        UpdateDataLogRecordingUi();
        RefreshFocusedBlackBoxCharts();
    }

    private void OnClearDataLogClick(object sender, RoutedEventArgs e) {
        if (_director.IsGlobalFocus) {
            _globalDataLogRecording = false;
            int[] roster = RosterXplmIndices().ToArray();
            _dataLogs.ClearMany(roster);
            SyncDataLogWatches();
            Append(roster.Length <= 1
                ? "Caja negra GLOBAL: borrado el log del roster."
                : $"Caja negra GLOBAL: borrados los logs de {roster.Length} naves.");
        } else {
            FocusedDataLog.Clear();
            SyncDataLogWatches();
        }
        UpdateDataLogRecordingUi();
        RefreshFocusedBlackBoxCharts();
    }

    private void OnStopDataLogClick(object sender, RoutedEventArgs e) {
        if (_director.IsGlobalFocus) {
            _globalDataLogRecording = false;
            int n = _dataLogs.RecordingCount;
            _dataLogs.StopAllRecording();
            SyncDataLogWatches();
            Append(n <= 1
                ? "Caja negra GLOBAL: grabacion detenida."
                : $"Caja negra GLOBAL: detenidas {n} grabaciones.");
        } else {
            FocusedDataLog.StopRecording();
            SyncDataLogWatches();
        }
        UpdateDataLogRecordingUi();
        RefreshFocusedBlackBoxCharts();
    }

    // Indices XPLM del roster actual (Op.Planes). Sin listado aun: solo LOCAL.
    private IEnumerable<int> RosterXplmIndices() {
        SimPlane[] planes = _client.Planes;
        if (planes.Length == 0) {
            yield return 0;
            yield break;
        }
        foreach (SimPlane p in planes)
            yield return p.Index;
    }

    // Con modo GLOBAL activo: arranca (o mantiene) grabacion de todo el roster.
    private void EnsureGlobalRosterRecording() {
        _dataLogs.StartMany(RosterXplmIndices());
        SyncDataLogWatches();
    }

    // Watch a ritmo de frame para cada IA que este grabando (refcount aparte
    // del foco). Al dejar de grabar se suelta.
    private void SyncDataLogWatches() {
        var wanted = new HashSet<int>();
        foreach ((int idx, _) in _dataLogs.Recording) {
            if (idx >= 1) wanted.Add(idx);
        }
        foreach (int idx in _dataLogWatchIndices.ToArray()) {
            if (wanted.Contains(idx)) continue;
            _dataLogWatchIndices.Remove(idx);
            _director.World.Unwatch(idx);
        }
        foreach (int idx in wanted) {
            if (!_dataLogWatchIndices.Add(idx)) continue;
            _director.World.Watch(idx);
        }
    }

    private void UpdateDataLogRecordingUi() {
        DataLogger log = FocusedDataLog;
        bool global = _director.IsGlobalFocus;
        if (global) {
            // Empezar queda activo hasta que el modo GLOBAL este grabando el
            // roster entero (permite "ampliar" si solo habia una nave a mano).
            DataLogStartBtn.IsEnabled = !_globalDataLogRecording;
            DataLogStopBtn.IsEnabled = _dataLogs.AnyRecording;
        } else {
            bool rec = log.IsRecording;
            DataLogStartBtn.IsEnabled = !rec;
            DataLogStopBtn.IsEnabled = rec;
        }
        DataLogStatusText = FormatDataLogStatus(log, connected: _client.IsConnected);
        DataLogFilePathText = global ? FormatGlobalDataLogPaths() : log.LogFilePath;
    }

    private string FormatGlobalDataLogPaths() {
        var names = new List<string>();
        foreach (int idx in RosterXplmIndices())
            names.Add(idx == 0 ? "DataLog.csv" : $"DataLog.plane{idx}.csv");
        return names.Count == 0 ? "(sin naves)" : string.Join(" + ", names);
    }

    private static string DataLogRoleLabel(int xplmIndex) =>
        xplmIndex < 0 ? "GLOBAL"
        : xplmIndex == 0 ? "LOCAL"
        : $"IA {xplmIndex}";

    private string FormatDataLogStatus(DataLogger log, bool connected) {
        bool simPaused = connected && _dataLogs.SimFrozen;
        if (_director.IsGlobalFocus) {
            int n = Math.Max(1, _client.Planes.Length);
            int rec = _dataLogs.RecordingCount;
            string state = !connected ? "sin conexion"
                         : rec > 0 && simPaused ? $"grabando {rec}/{n} · en pausa"
                         : rec > 0 ? $"grabando {rec}/{n}"
                         : "detenido";
            return $"GLOBAL · {n} nave(s) · {state}";
        }
        string role = DataLogRoleLabel(_director.FocusedXplmIndex);
        string planeState = !connected ? "sin conexion"
                          : log.IsRecording && simPaused ? "grabando · en pausa"
                          : log.IsRecording ? "grabando"
                          : "detenido";
        return $"{role} · {log.Count} muestras · {planeState}";
    }

    private void RebindFocusedDataLogUi(bool force = false) {
        int idx = _director.FocusedXplmIndex;
        if (!force && idx == _boundDataLogIndex) return;
        _boundDataLogIndex = idx;
        OnPropertyChanged(nameof(DataLogSamples));
        UpdateDataLogRecordingUi();
        RefreshFocusedBlackBoxCharts();
    }

    private void RefreshFocusedBlackBoxCharts() {
        if (!_blackBoxViewportActive) return;
        DataLogger log = FocusedDataLog;
        _blackBoxView.Refresh(log.TypedSamples, log.Markers, DataLogStatusText);
    }

    private void OnShowBlackBoxChartsClick(object sender, RoutedEventArgs e) =>
        ApplyViewportTab(ViewportTab.BlackBox);

    // --- Pestanas del viewport central (X-Plane / Caja negra) ----------------

    private enum ViewportTab { XPlane, BlackBox }

    private void OnViewportTabClick(object sender, RoutedEventArgs e) {
        if (!IsInitialized || sender is not System.Windows.Controls.Primitives.ToggleButton tab) return;
        if (tab.IsChecked != true) tab.IsChecked = true;
    }

    private void OnViewportTabChanged(object sender, RoutedEventArgs e) {
        if (!IsInitialized || _switchingViewportTab) return;
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { IsChecked: true } tab) return;

        ViewportTab view = tab == ViewportBlackBoxTab ? ViewportTab.BlackBox : ViewportTab.XPlane;
        SelectViewportTab(view);
    }

    private void ApplyViewportTab(ViewportTab view) {
        _blackBoxViewportActive = view == ViewportTab.BlackBox;
        BlackBoxHost.Visibility = _blackBoxViewportActive ? Visibility.Visible : Visibility.Collapsed;
        Viewport.Visibility = _blackBoxViewportActive ? Visibility.Collapsed : Visibility.Visible;

        // Sin Covering el docker saca X-Plane del hueco; si no, taparia los
        // graficos (el sim es ventana top-level encima del shell).
        _docker?.SetCovering(!_blackBoxViewportActive);

        if (_blackBoxViewportActive) {
            RefreshFocusedBlackBoxCharts();
            if (ViewportBlackBoxTab.IsChecked != true) {
                _switchingViewportTab = true;
                try {
                    ViewportXPlaneTab.IsChecked = false;
                    ViewportBlackBoxTab.IsChecked = true;
                } finally {
                    _switchingViewportTab = false;
                }
            }
        }
    }

    // --- Selector de vista del panel izquierdo ------------------------------
    //
    // Los iconos de la barra de actividad NO pliegan paneles: cambian que
    // contenido se ve en LeftCol. Aviones es la seleccion global de foco;
    // Interceptar tiene su propia lista de blancos. La telemetria derecha
    // y el log inferior se quedan siempre a la vista; el usuario los
    // redimensiona con los splitters.

    private enum LeftPanelView { Aircraft, Takeoff, Actions, Intercept, Graphics, DataLog, Config, XPlane }

    // Click ademas de Checked: si el usuario pulsa el tab ya activo, WPF
    // intentaria desmarcarlo (ToggleButton); lo volvemos a marcar para que
    // siempre haya exactamente una vista seleccionada.
    private void OnLeftViewClick(object sender, RoutedEventArgs e) {
        if (!IsInitialized || sender is not System.Windows.Controls.Primitives.ToggleButton tab) return;
        if (tab.IsChecked != true) tab.IsChecked = true;
    }

    private void OnLeftViewChanged(object sender, RoutedEventArgs e) {
        if (!IsInitialized || _switchingLeftView) return;
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { IsChecked: true } tab) return;

        LeftPanelView view = tab == TakeoffTab ? LeftPanelView.Takeoff
                          : tab == ActionsTab ? LeftPanelView.Actions
                          : tab == InterceptTab ? LeftPanelView.Intercept
                          : tab == GraphicsTab ? LeftPanelView.Graphics
                          : tab == DataLogTab ? LeftPanelView.DataLog
                          : tab == ConfigTab ? LeftPanelView.Config
                          : tab == XPlaneTab ? LeftPanelView.XPlane
                          : LeftPanelView.Aircraft;

        SelectLeftView(view);
    }

    private void ApplyLeftView(LeftPanelView view) {
        FocusPickerOpen = false;
        AircraftView.Visibility = view == LeftPanelView.Aircraft ? Visibility.Visible : Visibility.Collapsed;
        TakeoffView.Visibility = view == LeftPanelView.Takeoff ? Visibility.Visible : Visibility.Collapsed;
        ActionsView.Visibility = view == LeftPanelView.Actions ? Visibility.Visible : Visibility.Collapsed;
        InterceptView.Visibility = view == LeftPanelView.Intercept ? Visibility.Visible : Visibility.Collapsed;
        GraphicsView.Visibility = view == LeftPanelView.Graphics ? Visibility.Visible : Visibility.Collapsed;
        DataLogView.Visibility = view == LeftPanelView.DataLog ? Visibility.Visible : Visibility.Collapsed;
        ConfigView.Visibility = view == LeftPanelView.Config ? Visibility.Visible : Visibility.Collapsed;
        XPlaneView.Visibility = view == LeftPanelView.XPlane ? Visibility.Visible : Visibility.Collapsed;

        LeftPanelTitle.Text = view switch {
            LeftPanelView.Aircraft => "AVIONES",
            LeftPanelView.Takeoff => "RUTA",
            LeftPanelView.Actions => "ACCIONES",
            LeftPanelView.Intercept => "INTERCEPTAR",
            LeftPanelView.Graphics => "GRAFICOS",
            LeftPanelView.DataLog => "CAJA NEGRA",
            LeftPanelView.Config => "CONFIG",
            LeftPanelView.XPlane => "X-PLANE",
            _ => "AVIONES",
        };

        if (view == LeftPanelView.Aircraft) RefreshAircraftList();
        if (view == LeftPanelView.Intercept) RefreshInterceptTargets();
    }

    // Por ahora, si el connector aun no ha mandado el listado, solo pintamos
    // GLOBAL + el avion local. En cuanto llega Op.Planes se anaden las IAs
    // (indice >= 1) con el nombre del .acf — un Cirrus SR22 cargado como IA
    // sale aqui, no se queda fuera. La fila GLOBAL (XplmIndex -1) va siempre
    // la primera: opciones de zona / vista aerea.
    private void RefreshAircraftList() {
        SimPlane[] planes = _client.Planes;
        bool haveRoster = planes.Length > 0;
        int planeCount = haveRoster ? planes.Length : 1;
        int wanted = 1 + planeCount; // GLOBAL + naves
        int keep = SelectedAircraft?.XplmIndex ?? -999;

        while (AircraftEntries.Count > wanted)
            AircraftEntries.RemoveAt(AircraftEntries.Count - 1);

        // Fila 0: GLOBAL
        if (AircraftEntries.Count == 0 || !AircraftEntries[0].IsGlobal) {
            var global = new AircraftListEntry {
                IsGlobal = true,
                IsLocal = false,
                XplmIndex = FlightDirector.GlobalFocusIndex,
            };
            if (AircraftEntries.Count == 0) AircraftEntries.Add(global);
            else AircraftEntries.Insert(0, global);
        }
        {
            AircraftListEntry g = AircraftEntries[0];
            g.RoleLabel = "ZONA";
            g.Name = "Global";
            g.IsFocused = _director.IsGlobalFocus;
            g.Detail = GlobalFocusDetail();
            g.StatusBrush = _client.IsConnected ? Brushes.DeepSkyBlue : Brushes.Gray;
        }

        for (int i = 0; i < planeCount; i++) {
            int listIdx = i + 1;
            int xplmIndex = haveRoster ? planes[i].Index : 0;
            bool local = xplmIndex == 0;
            if (listIdx >= AircraftEntries.Count || AircraftEntries[listIdx].XplmIndex != xplmIndex
                || AircraftEntries[listIdx].IsGlobal) {
                var created = new AircraftListEntry { IsLocal = local, XplmIndex = xplmIndex };
                if (listIdx >= AircraftEntries.Count) AircraftEntries.Add(created);
                else AircraftEntries[listIdx] = created;
            }

            AircraftListEntry entry = AircraftEntries[listIdx];
            entry.RoleLabel = local ? "LOCAL" : "IA";
            entry.DefaultName = haveRoster ? PlaneDisplayName(planes[i]) : "Tu avion";
            // No pisar el Name mientras el usuario escribe; al crear la fila
            // parte del overlay guardado (vacio = casilla vacia).
            if (_editingAircraftNameIndex != xplmIndex)
                entry.Name = _director.World.GetOverlayLabel(xplmIndex);
            entry.IsFocused = entry.XplmIndex == _director.FocusedXplmIndex;
            FillAircraftEntry(entry, _director.World.Get(xplmIndex));
        }

        // Solo GLOBAL + local cuenta como "sin otras IAs".
        bool onlyLocal = planeCount <= 1;
        AircraftEmptyVisibility = onlyLocal ? Visibility.Visible : Visibility.Collapsed;

        // Restaura la seleccion si el roster recreo filas (mismo criterio que
        // InterceptTargets): sin esto el ListBox pierde el item al 10 Hz.
        if (keep != -999) {
            AircraftListEntry? match = null;
            foreach (AircraftListEntry e in AircraftEntries) {
                if (e.XplmIndex == keep) { match = e; break; }
            }
            if (!ReferenceEquals(SelectedAircraft, match))
                SelectedAircraft = match;
        }

        UpdateFocusText();
    }

    private string GlobalFocusDetail() {
        int n = Math.Max(1, _client.Planes.Length);
        string gfx = (_graphics.ShowMarkers ? "rombos" : "sin rombos")
                   + " · "
                   + (_graphics.ShowLines ? "lineas" : "sin lineas")
                   + " · "
                   + (_graphics.ShowTriangle ? "triangulo" : "sin triangulo");
        return _client.IsConnected
            ? $"{n} nave(s) · vista aerea\nOverlays: {gfx}"
            : "Sin conexion con el plugin";
    }

    private void UpdateFocusText() {
        if (_director.IsGlobalFocus)
            FocusText = "GLOBAL · zona";
        else {
            string role = _director.FocusedXplmIndex == 0 ? "LOCAL" : "IA";
            FocusText = $"{role} · {_director.FocusedLabel}";
        }
        SyncFocusChrome();
        RebindFocusedDataLogUi();
    }

    // GLOBAL: Graficos + Caja negra (grabacion de todo el roster). Un avion:
    // restaura el resto de secciones de la barra de actividad. El panel
    // derecho de telemetria no se colapsa (queda vacio via FillGlobalTelemetry)
    // para no resizear el docker de XP.
    private void SyncFocusChrome(bool force = false) {
        bool global = _director.IsGlobalFocus;
        if (!force && _focusChromeIsGlobal == global) return;
        _focusChromeIsGlobal = global;

        Visibility planeTabs = global ? Visibility.Collapsed : Visibility.Visible;
        AircraftTab.Visibility = planeTabs;
        TakeoffTab.Visibility = planeTabs;
        ActionsTab.Visibility = planeTabs;
        InterceptTab.Visibility = planeTabs;
        ConfigTab.Visibility = planeTabs;
        XPlaneTab.Visibility = planeTabs;
        GraphicsTab.Visibility = Visibility.Visible;
        DataLogTab.Visibility = Visibility.Visible;
        ViewportBlackBoxTab.Visibility = Visibility.Visible;
        SimStartSection.Visibility = global ? Visibility.Visible : Visibility.Collapsed;

        if (global) {
            // Al entrar en GLOBAL: Graficos por defecto, salvo que ya estuvieras
            // en Caja negra (se mantiene para seguir mirando la grabacion).
            if (DataLogView.Visibility != Visibility.Visible)
                SelectLeftView(LeftPanelView.Graphics);
        }
    }

    private void SelectLeftView(LeftPanelView view) {
        _switchingLeftView = true;
        try {
            AircraftTab.IsChecked = view == LeftPanelView.Aircraft;
            TakeoffTab.IsChecked = view == LeftPanelView.Takeoff;
            ActionsTab.IsChecked = view == LeftPanelView.Actions;
            InterceptTab.IsChecked = view == LeftPanelView.Intercept;
            GraphicsTab.IsChecked = view == LeftPanelView.Graphics;
            DataLogTab.IsChecked = view == LeftPanelView.DataLog;
            ConfigTab.IsChecked = view == LeftPanelView.Config;
            XPlaneTab.IsChecked = view == LeftPanelView.XPlane;
            ApplyLeftView(view);
        } finally {
            _switchingLeftView = false;
        }
    }

    private void SelectViewportTab(ViewportTab view) {
        _switchingViewportTab = true;
        try {
            ViewportXPlaneTab.IsChecked = view == ViewportTab.XPlane;
            ViewportBlackBoxTab.IsChecked = view == ViewportTab.BlackBox;
            ApplyViewportTab(view);
        } finally {
            _switchingViewportTab = false;
        }
    }

    // Fila del listado de cualquier avion: lo que su cuerpo sabe de si mismo
    // (NaN -> "—") y lo que hace segun su propio AgentView.
    private void FillAircraftEntry(AircraftListEntry entry, AircraftAgent agent) {
        if (!_client.IsConnected) {
            entry.Detail = "Sin conexion con el plugin";
            entry.StatusBrush = Brushes.Gray;
            return;
        }
        FlightState st = agent.Body.State;
        AgentView view = agent.View();
        string acf = string.IsNullOrEmpty(entry.DefaultName) ? "" : $"{entry.DefaultName}\n";
        entry.Detail =
            acf +
            $"IAS {Fmt(st.IasKt, "0")} kt · ALT {Fmt(st.AltFt, "0")} ft\n" +
            $"Fase: {view.SequenceText}\nModo: {view.ModeText}";
        entry.StatusBrush = view.IsBusy ? Brushes.LimeGreen : Brushes.Orange;
    }

    // El .acf a veces se llama "SR22" y la carpeta "Cirrus SR22". La carpeta
    // gana cuando es mas descriptiva; si no, el nombre del fichero.
    private static string PlaneDisplayName(SimPlane plane) {
        string file = Path.GetFileNameWithoutExtension(plane.FileName);
        if (string.IsNullOrWhiteSpace(file))
            file = Path.GetFileNameWithoutExtension(plane.Path);
        file = (file ?? "").Replace('_', ' ').Trim();

        string folder = "";
        string? dir = Path.GetDirectoryName(plane.Path);
        if (!string.IsNullOrEmpty(dir))
            folder = Path.GetFileName(dir).Replace('_', ' ').Trim();

        bool folderUseful = folder.Length > 0
            && !folder.Equals("Aircraft", StringComparison.OrdinalIgnoreCase)
            && !folder.Equals("Extra Aircraft", StringComparison.OrdinalIgnoreCase)
            && !folder.Equals("Laminar Research", StringComparison.OrdinalIgnoreCase);

        if (folderUseful && folder.Length > file.Length) return folder;
        return string.IsNullOrWhiteSpace(file) ? $"IA {plane.Index}" : file;
    }

    // Lista de blancos posibles: todos los aviones de la partida menos el
    // interceptor (el avion en foco), local incluido. La coleccion se
    // reconstruye unicamente cuando cambia el reparto de indices; el resto de
    // refrescos actualiza los campos EN SITIO, porque recrear las filas diez
    // veces por segundo le quitaria la seleccion al usuario mientras la hace.
    private void RefreshInterceptTargets() {
        SimPlane[] planes = _client.Planes;
        int interceptor = _director.FocusedXplmIndex;

        var roster = new List<(int Index, string Name)>();
        if (planes.Length == 0) {
            roster.Add((0, "Local"));
        } else {
            foreach (SimPlane p in planes)
                roster.Add((p.Index, p.Index == 0 ? "Local" : PlaneDisplayName(p)));
        }
        roster.RemoveAll(r => r.Index == interceptor);

        bool sameRoster = InterceptTargets.Count == roster.Count;
        if (sameRoster) {
            for (int i = 0; i < roster.Count; i++) {
                if (InterceptTargets[i].XplmIndex != roster[i].Index) { sameRoster = false; break; }
            }
        }
        if (!sameRoster) {
            int keep = SelectedInterceptTarget?.XplmIndex ?? -1;
            InterceptTargets.Clear();
            foreach ((int index, _) in roster)
                InterceptTargets.Add(new InterceptTargetEntry { XplmIndex = index });
            SelectedInterceptTarget =
                InterceptTargets.FirstOrDefault(t => t.XplmIndex == keep)
                ?? InterceptTargets.FirstOrDefault();
        }

        InterceptEmptyVisibility = InterceptTargets.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Posicion del interceptor (avion en foco) para distancia y "en tierra".
        AircraftAgent? me = _director.Focused;
        Kinematics ownK = default;
        bool haveOwn = _client.IsConnected && me is not null && me.Body.TryGetKinematics(out ownK);
        float aglM = me?.Body.AglMeters ?? float.NaN;
        bool terrainKnown = !float.IsNaN(aglM);
        double terrainY = haveOwn && terrainKnown ? ownK.Y - aglM : 0.0;

        for (int i = 0; i < roster.Count; i++) {
            InterceptTargetEntry entry = InterceptTargets[i];
            entry.Name = roster[i].Name;

            if (!haveOwn || !_director.World.TryCapture(entry.XplmIndex, out TargetSnapshot snap)) {
                entry.StateLabel = "SIN DATOS";
                entry.Detail = _client.IsConnected ? "Esperando posicion" : "Sin conexion con el plugin";
                entry.StatusBrush = Brushes.Gray;
                continue;
            }

            double dx = snap.X - ownK.X, dz = snap.Z - ownK.Z;
            double flatM = Math.Sqrt(dx * dx + dz * dz);
            TargetAirState air = snap.Classify(terrainY, terrainKnown && flatM < 40000.0);

            entry.StateLabel = air switch {
                TargetAirState.OnGround => "EN TIERRA",
                TargetAirState.Airborne => "EN EL AIRE",
                _ => "?",
            };
            entry.StatusBrush = air switch {
                TargetAirState.OnGround => Brushes.Orange,
                TargetAirState.Airborne => Brushes.LimeGreen,
                _ => Brushes.Gray,
            };
            entry.Detail =
                $"GS {snap.GroundSpeedKt:0} kt · MSL {snap.ElevationM * MetersToFeet:0} ft\n" +
                $"A {flatM * TargetSnapshot.MetersToNm:0.0} NM · rumbo {(int)Math.Round(snap.HeadingDeg):000}";
        }
    }

    // Lo que se lee DESPUES de pulsar Interceptar, del avion en foco (su
    // propio AgentView / InterceptSequence): fase, cuanto falta y mandos.
    // Se pinta en mono para que las cifras no bailen de un refresco a otro.
    private void RefreshInterceptStatus() {
        AircraftAgent? agent = _director.Focused;
        if (agent is null) {
            InterceptSequenceText = "En espera";
            InterceptChecklistText = "";
            InterceptStatusText = "Elige un avion (foco) para que intercepte.";
            return;
        }

        AgentView view = agent.View();
        InterceptSequence icpt = agent.Intercept;
        bool pending = view.InterceptPending;
        bool running = view.InterceptRunning;
        bool includeOwnTakeoff = pending || icpt.DidOwnTakeoff;

        if (pending) {
            InterceptSequenceText =
                $"Despegue combate · {TakeoffSequence.PhaseName(view.TakeoffPhase)} → {view.InterceptTargetLabel}";
            InterceptChecklistText = InterceptSequence.BuildChecklistLine(
                InterceptPhase.OwnTakeoff, includeOwnTakeoff: true, ownTakeoffActive: true);
            InterceptStatusText =
                $"{view.Label} intercepta a {view.InterceptTargetLabel}.\n" +
                $"Preludio: despegue combate hasta +{TakeoffStyles.CombatIntercept.TurnHeightFt:0} ft AGL.\n" +
                $"Luego giro hacia {view.InterceptTargetLabel} e interceptacion.\n" +
                $"Fase despegue: {TakeoffSequence.PhaseName(view.TakeoffPhase)}";
            return;
        }

        if (!running) {
            InterceptSequenceText = "En espera";
            InterceptChecklistText = InterceptSequence.BuildChecklistLine(
                InterceptPhase.Idle, includeOwnTakeoff: false);
            string idle = _client.IsConnected
                ? $"{view.Label} no intercepta a nadie.\nElige un blanco, una posicion y pulsa Interceptar.\n" +
                  "En tierra: despega en combate solo y encadena la persecucion."
                : "Sin conexion con el plugin.";
            InterceptStatusText = _lastInterceptRefusal.Length > 0
                ? idle + "\nRechazada: " + _lastInterceptRefusal
                : idle;
            return;
        }

        InterceptSequenceText = icpt.PhaseText;
        InterceptChecklistText = InterceptSequence.BuildChecklistLine(
            icpt.Phase, includeOwnTakeoff, ownTakeoffActive: false);

        string range = double.IsNaN(icpt.RangeM)
            ? Dash
            : $"{icpt.RangeM:0} m ({icpt.RangeM * TargetSnapshot.MetersToNm:0.00} NM)";
        string closure = double.IsNaN(icpt.ClosureKt) ? Dash : $"{icpt.ClosureKt:+0;-0;0} kt";

        var sb = new System.Text.StringBuilder();
        sb.Append(view.Label).Append(" intercepta a ").AppendLine(icpt.TargetLabel);
        sb.Append("Blanco  ").Append(icpt.TargetLabel).Append(" (")
          .Append(TargetSnapshot.StateName(icpt.TargetState)).AppendLine(")");
        sb.Append("Fase    ").AppendLine(InterceptSequence.PhaseName(icpt.Phase));
        sb.Append("Al pto. ").AppendLine(range);
        if (!double.IsNaN(icpt.SeparationM))
            sb.Append("Avion   ").Append($"{icpt.SeparationM:0}").AppendLine(" m de separacion");
        sb.Append("Cierre  ").AppendLine(closure);
        if (!double.IsNaN(icpt.AlongM))
            sb.Append("Error   ")
              .Append($"long {icpt.AlongM:+0;-0;0} · lat {icpt.CrossM:+0;-0;0} · vert {icpt.VerticalM:+0;-0;0} m")
              .AppendLine();
        if (!double.IsNaN(icpt.TargetSpeedKt))
            sb.Append("Su vel. ").Append($"{icpt.TargetSpeedKt:0}").AppendLine(" kt de suelo");
        if (icpt.IsRepositioning)
            sb.AppendLine("Puesto  en transicion (un poco mas lento que el blanco)");
        sb.Append("Mandos  ")
          .Append($"flaps {icpt.LastFlapCmd * 100f:0}% · aerofreno {icpt.LastSpeedbrakeCmd * 100f:0}%");
        if (icpt.Weaving)
            sb.AppendLine().Append("Serpenteando: el blanco vuela mas despacio de lo que este avion puede.");
        InterceptStatusText = sb.ToString();
    }

    // --- Popups flotantes vs X-Plane ------------------------------------------
    // Los ToolTip WPF abren un HWND owned: Windows sube el shell y el hueco
    // de X-Plane se ve negro. No se mitiga bien con Z-order (ya se intento).
    // Solucion: cancelar el ToolTip nativo y pintar un overlay en el mismo
    // HWND del shell (como el desplegable EN FOCO). ComboBox sigue con Popup
    // + PopupGuard + TOPMOST.

    private static bool _floatingChromeHooked;
    private FrameworkElement? _inlineTipOwner;

    private void HookFloatingChrome() {
        if (!_floatingChromeHooked) {
            _floatingChromeHooked = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement),
                ToolTipService.ToolTipOpeningEvent,
                new ToolTipEventHandler(OnAnyToolTipOpening));
        }
        PreviewMouseDown += OnShellPreviewMouseDownCloseFocusPicker;
        PreviewMouseDown += (_, _) => HideInlineTip();
        GfxFontCombo.DropDownOpened += OnGfxFontComboDropDownOpened;
    }

    private void OnShellPreviewMouseDownCloseFocusPicker(object sender, MouseButtonEventArgs e) {
        if (!FocusPickerOpen) return;
        if (e.OriginalSource is DependencyObject src && IsUnder(src, FocusPickerHost))
            return;
        FocusPickerOpen = false;
    }

    private static bool IsUnder(DependencyObject? node, DependencyObject ancestor) {
        while (node != null) {
            if (ReferenceEquals(node, ancestor)) return true;
            DependencyObject? parent = null;
            if (node is Visual)
                parent = VisualTreeHelper.GetParent(node);
            if (parent is null && node is FrameworkElement fe)
                parent = fe.Parent;
            node = parent;
        }
        return false;
    }

    private static void OnAnyToolTipOpening(object sender, ToolTipEventArgs e) {
        if (sender is not FrameworkElement owner) return;
        ShellWindow? shell = FindShellFor(owner);
        if (shell is null) return;

        // Cancela el Popup HWND nativo antes de que Windows reordene el Z.
        e.Handled = true;

        string? text = ExtractToolTipText(owner.ToolTip);
        if (string.IsNullOrWhiteSpace(text)) return;
        shell.ShowInlineTip(owner, text);
    }

    private static string? ExtractToolTipText(object? tip) {
        return tip switch {
            null => null,
            string s => s,
            System.Windows.Controls.ToolTip t =>
                t.Content as string ?? t.Content?.ToString(),
            _ => tip.ToString(),
        };
    }

    private void ShowInlineTip(FrameworkElement owner, string text) {
        if (_inlineTipOwner is not null && !ReferenceEquals(_inlineTipOwner, owner)) {
            _inlineTipOwner.MouseLeave -= OnInlineTipOwnerLeave;
        }
        _inlineTipOwner = owner;
        owner.MouseLeave -= OnInlineTipOwnerLeave;
        owner.MouseLeave += OnInlineTipOwnerLeave;

        InlineTipText.Text = text;
        InlineTip.Visibility = Visibility.Visible;
        InlineTip.Measure(new Size(InlineTip.MaxWidth, double.PositiveInfinity));
        double tipW = InlineTip.DesiredSize.Width;
        double tipH = InlineTip.DesiredSize.Height;

        Point below = owner.TransformToAncestor(ShellRoot)
            .Transform(new Point(0, owner.ActualHeight + 6));
        double x = below.X;
        double y = below.Y;
        double maxX = Math.Max(8, ShellRoot.ActualWidth - tipW - 8);
        double maxY = Math.Max(8, ShellRoot.ActualHeight - tipH - 8);
        if (y > maxY) {
            // No cabe debajo: encima del control.
            Point above = owner.TransformToAncestor(ShellRoot)
                .Transform(new Point(0, -tipH - 6));
            y = above.Y;
        }
        x = Math.Clamp(x, 8, maxX);
        y = Math.Clamp(y, 8, maxY);
        InlineTip.Margin = new Thickness(x, y, 0, 0);
    }

    private void OnInlineTipOwnerLeave(object sender, MouseEventArgs e) => HideInlineTip();

    private void HideInlineTip() {
        if (_inlineTipOwner is not null) {
            _inlineTipOwner.MouseLeave -= OnInlineTipOwnerLeave;
            _inlineTipOwner = null;
        }
        if (InlineTip.Visibility != Visibility.Collapsed)
            InlineTip.Visibility = Visibility.Collapsed;
    }

    private void OnGfxFontComboDropDownOpened(object? sender, EventArgs e) {
        ElevateComboPopup(GfxFontCombo);
    }

    private void ElevateComboPopup(ComboBox cb) {
        _docker?.BeginPopupGuard();
        cb.ApplyTemplate();
        if (cb.Template.FindName("PART_Popup", cb) is not Popup popup) {
            _docker?.EndPopupGuard();
            return;
        }

        void ElevateChild() {
            if (popup.Child is Visual child)
                ElevateFloating(child, topmost: true);
            _docker?.EnsureStacked();
        }
        ElevateChild();
        cb.Dispatcher.BeginInvoke(ElevateChild, DispatcherPriority.Loaded);

        EventHandler? onClosed = null;
        onClosed = (_, _) => {
            popup.Closed -= onClosed;
            if (popup.Child is Visual child)
                ElevateFloating(child, topmost: false);
            _docker?.EndPopupGuard();
        };
        popup.Closed += onClosed;
    }

    private static ShellWindow? FindShellFor(DependencyObject? d) {
        if (d != null && Window.GetWindow(d) is ShellWindow fromTree) return fromTree;
        if (Application.Current is null) return null;
        foreach (Window w in Application.Current.Windows)
            if (w is ShellWindow s) return s;
        return null;
    }

    private static void ElevateFloating(Visual visual, bool topmost, int retriesLeft = 12) {
        void Apply() {
            if (PresentationSource.FromVisual(visual) is not HwndSource src) {
                if (topmost && retriesLeft > 0) {
                    visual.Dispatcher.BeginInvoke(
                        () => ElevateFloating(visual, topmost, retriesLeft - 1),
                        DispatcherPriority.Loaded);
                }
                return;
            }

            IntPtr hwnd = src.Handle;
            if (topmost) {
                long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
                SetWindowLongPtr(hwnd, GWL_EXSTYLE,
                    new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            }
            SetWindowPos(hwnd,
                         topmost ? HWND_TOPMOST : HWND_NOTOPMOST,
                         0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE |
                         (topmost ? SWP_FRAMECHANGED : 0));
        }

        if (PresentationSource.FromVisual(visual) is null)
            visual.Dispatcher.BeginInvoke(Apply, DispatcherPriority.Loaded);
        else
            Apply();
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Una fila del listado de aviones de la partida. IsGlobal es la fila de
// zona (XplmIndex -1). IsLocal marca el avion que pilota este core; el resto
// son las otras IAs que X-Plane tiene activas. XplmIndex es el indice que
// recibe FlightDirector.SetFocus (-1 = Global, 0 = ownship, 1..19 = IA).
public sealed class AircraftListEntry : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsGlobal { get; init; }
    public bool IsLocal { get; init; }
    public int XplmIndex { get; init; }

    // Texto del desplegable EN FOCO ("GLOBAL · zona", "LOCAL · F-14"...).
    public string FocusLabel =>
        IsGlobal ? "GLOBAL · zona" : $"{RoleLabel} · {DisplayName}";

    // Nombre mostrado en foco / logs: casilla si hay texto, si no el del .acf.
    public string DisplayName =>
        string.IsNullOrWhiteSpace(_name) ? (string.IsNullOrEmpty(_defaultName) ? RoleLabel : _defaultName) : _name;

    private string _defaultName = "";
    public string DefaultName {
        get => _defaultName;
        set {
            if (_defaultName == value) return;
            _defaultName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FocusLabel)));
        }
    }

    private string _name = "";
    public string Name {
        get => _name;
        set {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FocusLabel)));
        }
    }

    private string _roleLabel = "";
    public string RoleLabel {
        get => _roleLabel;
        set {
            if (_roleLabel == value) return;
            _roleLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RoleLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FocusLabel)));
        }
    }

    private string _detail = "";
    public string Detail {
        get => _detail;
        set {
            if (_detail == value) return;
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush {
        get => _statusBrush;
        set {
            if (Equals(_statusBrush, value)) return;
            _statusBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
    }

    private bool _isFocused;
    public bool IsFocused {
        get => _isFocused;
        set {
            if (_isFocused == value) return;
            _isFocused = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFocused)));
        }
    }
}

// Una fila del listado de blancos de la pestana Interceptar. Es su propio
// tipo y no un AircraftListEntry porque lleva lo unico que la interceptacion
// necesita de verdad: el indice XPLM con el que se piden sus datarefs, y si
// esta volando o sigue en tierra.
public sealed class InterceptTargetEntry : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    // Indice de X-Plane: 0 (avion local) o 1..19 (IA); nunca el interceptor.
    public int XplmIndex { get; init; }

    private string _name = "";
    public string Name {
        get => _name;
        set {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    private string _stateLabel = "";
    public string StateLabel {
        get => _stateLabel;
        set {
            if (_stateLabel == value) return;
            _stateLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateLabel)));
        }
    }

    private string _detail = "";
    public string Detail {
        get => _detail;
        set {
            if (_detail == value) return;
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush {
        get => _statusBrush;
        set {
            if (Equals(_statusBrush, value)) return;
            _statusBrush = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        }
    }

    public override string ToString() => Name;
}
