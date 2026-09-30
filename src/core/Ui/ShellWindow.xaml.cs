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
    private readonly AircraftControls _controls;
    private readonly TakeoffSequence _sequence;
    private readonly ManeuverSequence _maneuvers;
    private readonly InterceptSequence _intercept;
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

    public ObservableCollection<string> Logs { get; } = new();

    // Aviones de la partida: el local (indice 0) y las IAs que reporte el
    // connector (Op.Planes). La coleccion la rellena RefreshAircraftList.
    public ObservableCollection<AircraftListEntry> AircraftEntries { get; } = new();

    // Blancos posibles de una interceptacion: solo las IAs (el avion local
    // no se puede interceptar a si mismo). Objetos estables: RefreshIntercept-
    // Targets los actualiza en sitio en vez de recrearlos, porque recrearlos
    // le quitaria la seleccion al usuario diez veces por segundo.
    public ObservableCollection<InterceptTargetEntry> InterceptTargets { get; } = new();

    public string BuildText { get; } = $"build {BuildInfo.BuildNumber}";

    // Caja negra del avion EN FOCO. Con foco GLOBAL no tiene sentido grabar:
    // For() clamppea indices invalidos, pero la UI deshabilita Empezar.
    private DataLogger FocusedDataLog =>
        _dataLogs.For(_director.IsGlobalFocus ? 0 : _director.FocusedXplmIndex);
    public ObservableCollection<string> DataLogSamples => FocusedDataLog.Samples;

    private string _dataLogFilePathText = "";
    public string DataLogFilePathText {
        get => _dataLogFilePathText;
        private set => Set(ref _dataLogFilePathText, value);
    }

    public ShellWindow(ConnectorClient client, Datarefs datarefs, AircraftControls controls,
                       TakeoffSequence sequence, ManeuverSequence maneuvers,
                       InterceptSequence intercept, ControlTuning tuning,
                       FlightDirector director) {
        // Asignar deps antes de DataContext: DataLogSamples / FocusedDataLog
        // leen FocusedXplmIndex del director al enlazar la lista CSV.
        _client = client;
        _d = datarefs;
        _controls = controls;
        _sequence = sequence;
        _maneuvers = maneuvers;
        _intercept = intercept;
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

        // Arranca en Aviones (seleccion global de foco); el XAML ya deja
        // AircraftTab marcado y las otras vistas collapsadas, pero
        // ApplyLeftView deja el titulo y las visibilidades consistentes.
        ApplyLeftView(LeftPanelView.Aircraft);
        RefreshAircraftList();
        // Foco por defecto = GLOBAL: selecciona la fila de zona.
        if (AircraftEntries.Count > 0 && AircraftEntries[0].IsGlobal)
            SelectedAircraft = AircraftEntries[0];
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
    // ownship (takeoff/maneuver/intercept); 1..19 = IA (AiStraightHold).
    //
    // BeginInvoke (no Invoke): Start/Abort se llaman con el candado de la
    // secuencia cogido. Un Invoke sincrono aqui puede engancharse con el
    // hilo del pipe (que espera el mismo candado) y dejar la Driver API
    // colgada al pedir un despegue.
    public void Append(string line, bool markBlackBox = false, int blackBoxIndex = 0) {
        Dispatcher.BeginInvoke(() => {
            Logs.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            _dataLogs.For(blackBoxIndex).RecordEvent(line, chartMarker: markBlackBox);
        });
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
    private TakeoffStyleId _styleId = TakeoffStyleId.Relaxed;
    private bool _switchingStyle;
    public string StyleSummary => TakeoffStyles.Get(_styleId).Summary;
    public string StyleSpec => TakeoffStyles.Get(_styleId).Spec;

    private CruiseModeId _flightModeId = CruiseModeId.Straight;
    private bool _switchingFlightMode;
    public string FlightModeSummary => CruiseModes.Get(_flightModeId).Summary;

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
        "No hay ninguna IA en la partida todavia. Anade aviones de IA en X-Plane " +
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

    // Posicion que usara la PROXIMA interceptacion. Cambiarla con una en
    // marcha no la mueve: hay que volver a pulsar Interceptar, igual que el
    // estilo de despegue no se aplica a un despegue ya empezado.
    private InterceptStation _stationId = InterceptStation.TailHigh;
    private bool _switchingStation;
    public string InterceptStationSummary => InterceptCatalog.Get(_stationId).Description;
    public string InterceptStationSpec =>
        InterceptCatalog.Get(_stationId).Summary(_tuning.InterceptDistanceM,
                                                 _tuning.InterceptLateralM,
                                                 _tuning.InterceptVerticalM);

    // Las tres separaciones viven en ControlTuning (fuente de verdad unica,
    // con su clamp en el setter) y se editan aqui con el mismo mecanismo que
    // los campos de Config: se aplican en caliente en cuanto el texto parsea.
    private string _interceptDistanceText = "";
    public string InterceptDistanceText {
        get => _interceptDistanceText;
        set => SetTuningText(ref _interceptDistanceText, value, f => {
            _tuning.InterceptDistanceM = f;
            OnPropertyChanged(nameof(InterceptStationSpec));
        });
    }

    private string _interceptLateralText = "";
    public string InterceptLateralText {
        get => _interceptLateralText;
        set => SetTuningText(ref _interceptLateralText, value, f => {
            _tuning.InterceptLateralM = f;
            OnPropertyChanged(nameof(InterceptStationSpec));
        });
    }

    private string _interceptVerticalText = "";
    public string InterceptVerticalText {
        get => _interceptVerticalText;
        set => SetTuningText(ref _interceptVerticalText, value, f => {
            _tuning.InterceptVerticalM = f;
            OnPropertyChanged(nameof(InterceptStationSpec));
        });
    }

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

        InterceptDistanceText = FmtTuning(_tuning.InterceptDistanceM);
        InterceptLateralText = FmtTuning(_tuning.InterceptLateralM);
        InterceptVerticalText = FmtTuning(_tuning.InterceptVerticalM);

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

    private void OnAbortClick(object sender, RoutedEventArgs e) {
        // Con foco en IA suelta solo su hold; con foco local, despegue/maniobra/intercept.
        _director.AbortFocused();
        Append($"Abortar / manual: {_director.FocusedLabel}.");
    }

    private void OnResetSimClick(object sender, RoutedEventArgs e) {
        if (!_director.ResetSimulation(out string error)) {
            Append($"Reset simulacion: {error}.");
            return;
        }
        Append($"Reset simulacion: {SimScenario.Label}.");
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
        ApplyFocus(ac);
        FocusPickerOpen = false;
    }

    private void ApplyFocus(AircraftListEntry ac) {
        _director.SetFocus(ac.XplmIndex, ac.Name);

        if (ac.IsGlobal) {
            // Vista de zona: enmarcar todas las naves.
            // Ritmo de telemetria Other*: conserva AiHold/Intercept si siguen.
            if (_director.AiHold.IsRunning)
                _d.FocusOtherPlane(_director.AiHold.XplmIndex - 1);
            else if (!_intercept.IsRunning)
                _d.FocusOtherPlane(-1);

            ApplyGlobalFocusPresentation();
            UpdateFocusText();
            RefreshAircraftList();
            Append("Foco: GLOBAL · vista aerea de la zona.");
            return;
        }

        // Ritmo de telemetria Other*: la IA en foco a ~60 Hz para el panel
        // derecho; al volver al local, conserva el slot de AiHold/Intercept
        // si sigue activo.
        if (ac.XplmIndex >= 1)
            _d.FocusOtherPlane(ac.XplmIndex - 1);
        else if (_director.AiHold.IsRunning)
            _d.FocusOtherPlane(_director.AiHold.XplmIndex - 1);
        else if (!_intercept.IsRunning)
            _d.FocusOtherPlane(-1);

        // Camara automatica al cambiar de foco: chase en IA, soltar en local
        // (el ownship usa la vista nativa de X-Plane).
        if (_client.IsConnected) {
            if (ac.XplmIndex >= 1)
                _client.FollowCamera(ac.XplmIndex);
            else
                _client.ReleaseCamera();
        }

        UpdateFocusText();
        RefreshAircraftList();
        string role = ac.XplmIndex == 0 ? "LOCAL" : "IA";
        Append($"Foco: {role} · {ac.Name}.");
    }

    // Activa marcadores + lineas y pide la vista aerea al connector.
    // (Las etiquetas de texto son por avion; no se fuerzan aqui.)
    // Los toggles de Graficos se sincronizan para que la UI refleje lo dibujado.
    private void ApplyGlobalFocusPresentation() {
        bool changed = false;
        if (!_graphics.ShowMarkers) { _graphics.ShowMarkers = true; changed = true; }
        if (!_graphics.ShowLines) { _graphics.ShowLines = true; changed = true; }
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
    public void ResumeGlobalCameraIfFocused() {
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
        Append($"Camara: siguiendo IA · {ac.Name} (idx {ac.XplmIndex}).");
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
    // Mismo patron que el selector de estilo de despegue: Click ademas de
    // Checked para que pulsar el que ya esta activo no lo desmarque y deje la
    // seleccion vacia.
    private void OnInterceptStationClick(object sender, RoutedEventArgs e) {
        if (!IsInitialized || sender is not System.Windows.Controls.Primitives.ToggleButton tab) return;
        if (tab.IsChecked != true) tab.IsChecked = true;
    }

    private void OnInterceptStationChanged(object sender, RoutedEventArgs e) {
        if (!IsInitialized || _switchingStation) return;
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { IsChecked: true, Tag: string tag }) return;
        if (!Enum.TryParse(tag, out InterceptStation id)) return;

        _switchingStation = true;
        try {
            StationTailHigh.IsChecked = id == InterceptStation.TailHigh;
            StationParallelLeft.IsChecked = id == InterceptStation.ParallelLeft;
            StationParallelRight.IsChecked = id == InterceptStation.ParallelRight;
            StationAbove.IsChecked = id == InterceptStation.Above;
            StationBelow.IsChecked = id == InterceptStation.Below;
            _stationId = id;
            OnPropertyChanged(nameof(InterceptStationSummary));
            OnPropertyChanged(nameof(InterceptStationSpec));

            // Con la interceptacion en marcha (o pendiente de despegue) el
            // cambio de boton mueve el puesto de formacion; no hace falta
            // abortar y volver a pedir.
            if (_intercept.IsRunning || _director.IsInterceptPending) {
                if (_director.ChangeInterceptStation(id, out string err))
                    Append($"Interceptar: puesto → {InterceptCatalog.Get(id).Label}.");
                else if (!string.IsNullOrEmpty(err))
                    Append($"Interceptar: no se pudo cambiar de puesto — {err}.");
            }
        } finally {
            _switchingStation = false;
        }
    }

    private void OnInterceptStartClick(object sender, RoutedEventArgs e) {
        if (SelectedInterceptTarget is not InterceptTargetEntry target) {
            Append("Interceptar: elige primero a que avion de la lista.");
            return;
        }
        if (!_client.IsConnected) {
            Append("Interceptar: no hay conexion con el plugin.");
            return;
        }

        if (!_director.StartIntercept(target.XplmIndex, _stationId, target.Name, out string refusal))
            Append($"Interceptar {target.Name}: no se puede ahora mismo -- {refusal}.");
    }

    private void OnInterceptAbortClick(object sender, RoutedEventArgs e) {
        if (!_intercept.IsRunning && !_director.IsInterceptPending) {
            Append("Interceptar: no hay ninguna interceptacion en marcha.");
            return;
        }
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
            _graphics.LabelText);
    }

    private bool _syncingGraphicsUi;

    private void SyncGraphicsUiFromModel() {
        _syncingGraphicsUi = true;
        try {
            GfxOwnLabelToggle.IsChecked = _graphics.ShowOwnLabel;
            GfxOtherLabelsToggle.IsChecked = _graphics.ShowOtherLabels;
            GfxLinesToggle.IsChecked = _graphics.ShowLines;
            GfxMarkersToggle.IsChecked = _graphics.ShowMarkers;
            GfxPathToggle.IsChecked = _graphics.ShowPath;
            GfxLabelTextBox.Text = _graphics.LabelText;
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
        _graphics.ShowOwnLabel = GfxOwnLabelToggle.IsChecked == true;
        _graphics.ShowOtherLabels = GfxOtherLabelsToggle.IsChecked == true;
        _graphics.ShowLines = GfxLinesToggle.IsChecked == true;
        _graphics.ShowMarkers = GfxMarkersToggle.IsChecked == true;
        _graphics.ShowPath = GfxPathToggle.IsChecked == true;
        _graphics.LabelText = GfxLabelTextBox.Text ?? string.Empty;
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

    private void OnClearLogClick(object sender, RoutedEventArgs e) => Logs.Clear();

    private void OnStartDataLogClick(object sender, RoutedEventArgs e) {
        if (_director.IsGlobalFocus) {
            Append("Caja negra: elige un avion (no GLOBAL).");
            return;
        }
        FocusedDataLog.StartRecording();
        UpdateDataLogRecordingUi();
        RefreshFocusedBlackBoxCharts();
    }

    private void OnClearDataLogClick(object sender, RoutedEventArgs e) {
        if (_director.IsGlobalFocus) {
            Append("Caja negra: elige un avion (no GLOBAL).");
            return;
        }
        FocusedDataLog.Clear();
        UpdateDataLogRecordingUi();
        RefreshFocusedBlackBoxCharts();
    }

    private void OnStopDataLogClick(object sender, RoutedEventArgs e) {
        FocusedDataLog.StopRecording();
        UpdateDataLogRecordingUi();
        RefreshFocusedBlackBoxCharts();
    }

    private void UpdateDataLogRecordingUi() {
        DataLogger log = FocusedDataLog;
        bool global = _director.IsGlobalFocus;
        bool rec = log.IsRecording;
        DataLogStartBtn.IsEnabled = !global && !rec;
        DataLogStopBtn.IsEnabled = !global && rec;
        DataLogStatusText = FormatDataLogStatus(log, connected: _client.IsConnected);
        DataLogFilePathText = global ? "(elige un avion para grabar)" : log.LogFilePath;
    }

    private static string DataLogRoleLabel(int xplmIndex) =>
        xplmIndex < 0 ? "GLOBAL"
        : xplmIndex == 0 ? "LOCAL"
        : $"IA {xplmIndex}";

    private string FormatDataLogStatus(DataLogger log, bool connected) {
        if (_director.IsGlobalFocus)
            return "GLOBAL · elige un avion para grabar caja negra";
        string role = DataLogRoleLabel(_director.FocusedXplmIndex);
        string state = !connected ? "sin conexion"
                     : log.IsRecording ? "grabando"
                     : "detenido";
        return $"{role} · {log.Count} muestras · {state}";
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
        _switchingViewportTab = true;
        try {
            ViewportXPlaneTab.IsChecked = view == ViewportTab.XPlane;
            ViewportBlackBoxTab.IsChecked = view == ViewportTab.BlackBox;
            ApplyViewportTab(view);
        } finally {
            _switchingViewportTab = false;
        }
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
            entry.Name = haveRoster ? PlaneDisplayName(planes[i]) : "Tu avion";
            entry.IsFocused = entry.XplmIndex == _director.FocusedXplmIndex;
            if (local) {
                entry.Detail = LocalAircraftDetail();
                entry.StatusBrush = LocalAircraftStatus();
            } else {
                FillOtherAircraft(entry, entry.XplmIndex);
            }
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
                   + (_graphics.ShowLines ? "lineas" : "sin lineas");
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
        RebindFocusedDataLogUi();
    }

    private string LocalAircraftDetail() {
        string phase = TakeoffSequence.PhaseName(_sequence.Phase);
        string action = _maneuvers.IsRunning ? _maneuvers.PhaseText : "en espera";
        return _client.IsConnected
            ? $"IAS {_d.IasKt.Float:0} kt · ALT {_d.AltFt.Float:0} ft\n" +
              $"Fase: {phase}\nAccion: {action}"
            : "Sin conexion con el plugin";
    }

    private Brush LocalAircraftStatus() =>
        !_client.IsConnected ? Brushes.Gray
        : _maneuvers.IsRunning || _sequence.IsRunning ? Brushes.LimeGreen
        : Brushes.Orange;

    // XPLM index 1 -> OtherElevMeters[0] (plane1). No hay IAS de las IAs
    // en los datarefs de multiplayer: la velocidad que se puede leer es
    // la del vector horizontal, y la cota es elevacion MSL, no altitud
    // indicada.
    private void FillOtherAircraft(AircraftListEntry entry, int xplmIndex) {
        int slot = xplmIndex - 1;
        if (!_client.IsConnected) {
            entry.Detail = "Sin conexion con el plugin";
            entry.StatusBrush = Brushes.Gray;
            return;
        }
        if (slot < 0 || slot >= Datarefs.OtherPlaneSlots) {
            entry.Detail = "Sin posicion (fuera de los 19 slots de IA)";
            entry.StatusBrush = Brushes.Orange;
            return;
        }

        DataHandle el = _d.OtherElevMeters[slot];
        DataHandle hdg = _d.OtherHeadingDeg[slot];
        DataHandle vx = _d.OtherVelX[slot];
        DataHandle vz = _d.OtherVelZ[slot];
        if (!el.HasValue || !hdg.HasValue || !vx.HasValue || !vz.HasValue) {
            entry.Detail = "Esperando posicion";
            entry.StatusBrush = Brushes.Orange;
            return;
        }

        double gsKt = Math.Sqrt(vx.Value * vx.Value + vz.Value * vz.Value) * MpsToKnots;
        double mslFt = el.Value * MetersToFeet;
        double hdgDeg = hdg.Value % 360.0;
        if (hdgDeg < 0) hdgDeg += 360.0;
        entry.Detail = $"GS {gsKt:0} kt · MSL {mslFt:0} ft\nHDG {(int)Math.Round(hdgDeg):000}";
        entry.StatusBrush = Brushes.LimeGreen;
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

    // Lista de blancos posibles: solo las IAs (indice >= 1). La coleccion se
    // reconstruye unicamente cuando cambia el reparto de indices; el resto de
    // refrescos actualiza los campos EN SITIO, porque recrear las filas diez
    // veces por segundo le quitaria la seleccion al usuario mientras la hace.
    private void RefreshInterceptTargets() {
        SimPlane[] planes = _client.Planes;

        int wanted = 0;
        foreach (SimPlane p in planes) if (p.Index >= 1) wanted++;

        bool sameRoster = InterceptTargets.Count == wanted;
        if (sameRoster) {
            int i = 0;
            foreach (SimPlane p in planes) {
                if (p.Index < 1) continue;
                if (InterceptTargets[i].XplmIndex != p.Index) { sameRoster = false; break; }
                i++;
            }
        }
        if (!sameRoster) {
            int keep = SelectedInterceptTarget?.XplmIndex ?? -1;
            InterceptTargets.Clear();
            foreach (SimPlane p in planes) {
                if (p.Index < 1) continue;
                InterceptTargets.Add(new InterceptTargetEntry { XplmIndex = p.Index });
            }
            SelectedInterceptTarget =
                InterceptTargets.FirstOrDefault(t => t.XplmIndex == keep)
                ?? InterceptTargets.FirstOrDefault();
        }

        InterceptEmptyVisibility = InterceptTargets.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        bool haveOwn = _client.IsConnected && _d.LocalX.HasValue && _d.LocalY.HasValue &&
                       _d.LocalZ.HasValue;
        double ownX = _d.LocalX.Value, ownY = _d.LocalY.Value, ownZ = _d.LocalZ.Value;
        double terrainY = ownY - _d.AglMeters.Value;

        int idx = 0;
        foreach (SimPlane p in planes) {
            if (p.Index < 1) continue;
            InterceptTargetEntry entry = InterceptTargets[idx++];
            entry.Name = PlaneDisplayName(p);

            TargetSnapshot snap = TargetSnapshot.Capture(_d, p.Index);
            if (!haveOwn || !snap.Valid) {
                entry.StateLabel = "SIN DATOS";
                entry.Detail = _client.IsConnected ? "Esperando posicion" : "Sin conexion con el plugin";
                entry.StatusBrush = Brushes.Gray;
                continue;
            }

            double dx = snap.X - ownX, dz = snap.Z - ownZ;
            double flatM = Math.Sqrt(dx * dx + dz * dz);
            TargetAirState air = snap.Classify(terrainY, flatM < 40000.0);

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

    // Lo que se lee DESPUES de pulsar Interceptar: en que fase va, cuanto
    // falta y que esta haciendo con los mandos lentos. Se pinta en mono para
    // que las cifras no bailen de un refresco a otro.
    private void RefreshInterceptStatus() {
        bool pending = _director.IsInterceptPending;
        bool running = _intercept.IsRunning;
        bool includeOwnTakeoff = pending || _intercept.DidOwnTakeoff;

        if (pending) {
            InterceptSequenceText =
                $"Despegue combate · {TakeoffSequence.PhaseName(_sequence.Phase)} → {_director.PendingInterceptLabel}";
            InterceptChecklistText = InterceptSequence.BuildChecklistLine(
                InterceptPhase.OwnTakeoff, includeOwnTakeoff: true, ownTakeoffActive: true);
            InterceptStatusText =
                $"Preludio: despegue combate hasta +{TakeoffStyles.CombatIntercept.TurnHeightFt:0} ft AGL.\n" +
                $"Luego giro hacia {_director.PendingInterceptLabel} e interceptacion.\n" +
                $"Fase despegue: {TakeoffSequence.PhaseName(_sequence.Phase)}";
            return;
        }

        if (!running) {
            InterceptSequenceText = "En espera";
            InterceptChecklistText = InterceptSequence.BuildChecklistLine(
                InterceptPhase.Idle, includeOwnTakeoff: false);
            InterceptStatusText = _client.IsConnected
                ? "Sin interceptacion activa.\nElige un avion, una posicion y pulsa Interceptar.\n" +
                  "En tierra: despega en combate solo y encadena la persecucion."
                : "Sin conexion con el plugin.";
            return;
        }

        InterceptSequenceText = _intercept.PhaseText;
        InterceptChecklistText = InterceptSequence.BuildChecklistLine(
            _intercept.Phase, includeOwnTakeoff, ownTakeoffActive: false);

        string range = double.IsNaN(_intercept.RangeM)
            ? "--"
            : $"{_intercept.RangeM:0} m ({_intercept.RangeM * TargetSnapshot.MetersToNm:0.00} NM)";
        string closure = double.IsNaN(_intercept.ClosureKt) ? "--" : $"{_intercept.ClosureKt:+0;-0;0} kt";

        var sb = new System.Text.StringBuilder();
        sb.Append("Blanco  ").Append(_intercept.TargetLabel).Append(" (")
          .Append(TargetSnapshot.StateName(_intercept.TargetState)).AppendLine(")");
        sb.Append("Fase    ").AppendLine(InterceptSequence.PhaseName(_intercept.Phase));
        sb.Append("Al pto. ").AppendLine(range);
        if (!double.IsNaN(_intercept.SeparationM))
            sb.Append("Avion   ").Append($"{_intercept.SeparationM:0}").AppendLine(" m de separacion");
        sb.Append("Cierre  ").AppendLine(closure);
        if (!double.IsNaN(_intercept.AlongM))
            sb.Append("Error   ")
              .Append($"long {_intercept.AlongM:+0;-0;0} · lat {_intercept.CrossM:+0;-0;0} · vert {_intercept.VerticalM:+0;-0;0} m")
              .AppendLine();
        if (!double.IsNaN(_intercept.TargetSpeedKt))
            sb.Append("Su vel. ").Append($"{_intercept.TargetSpeedKt:0}").AppendLine(" kt de suelo");
        if (_intercept.IsRepositioning)
            sb.AppendLine("Puesto  en transicion (un poco mas lento que el blanco)");
        sb.Append("Mandos  ")
          .Append($"flaps {_intercept.LastFlapCmd * 100f:0}% · aerofreno {_intercept.LastSpeedbrakeCmd * 100f:0}%");
        if (_intercept.Weaving)
            sb.AppendLine().Append("Serpenteando: el blanco vuela mas despacio de lo que este avion puede.");
        InterceptStatusText = sb.ToString();
    }

    // --- Refresco -------------------------------------------------------------

    private void Refresh() {
        bool connected = _client.IsConnected;
        ConnectionStatus = connected
            ? $"Conectado a {_client.PluginVersion}"
            : "Sin conexion con el plugin";
        ConnectionDotColor = connected ? Brushes.LimeGreen : Brushes.Gray;

        bool maneuverActive = _maneuvers.IsRunning;
        bool takeoffActive = _sequence.IsRunning;
        bool cruiseActive = _director.Cruise.IsRunning;
        bool routeCruisePending = _director.IsRouteCruisePending;
        bool interceptActive = _intercept.IsRunning;
        bool interceptPending = _director.IsInterceptPending;

        if (takeoffActive || routeCruisePending)
            SequenceText = routeCruisePending
                ? $"{_sequence.StyleName} · {TakeoffSequence.PhaseName(_sequence.Phase)} → {_director.Cruise.Mode.Name}"
                : $"{_sequence.StyleName} · {TakeoffSequence.PhaseName(_sequence.Phase)}";
        else if (cruiseActive)
            SequenceText = _director.Cruise.PhaseText;
        else
            SequenceText = "En espera";
        ChecklistText = TakeoffSequence.BuildChecklistLine(_sequence.Phase);

        // Una sola fuente de objetivos ownship: la maniobra si hay una, si no
        // el despegue/intercept. En manual todo objetivo es NaN ("--").
        float pitchObj = interceptActive ? _intercept.LastPitchTarget
                      : maneuverActive ? _maneuvers.LastPitchTarget
                      : takeoffActive ? _sequence.LastPitchTarget : float.NaN;
        float bankObj = interceptActive ? _intercept.LastBankTarget
                     : maneuverActive ? _maneuvers.LastBankTarget
                     : takeoffActive ? _sequence.LastBankTarget : float.NaN;
        float iasObj = interceptActive ? _intercept.LastIasTarget
                    : maneuverActive ? _maneuvers.LastIasTarget
                    : takeoffActive ? _sequence.LastIasTarget : float.NaN;
        float vsObj = interceptActive ? _intercept.LastVsTarget
                   : maneuverActive ? _maneuvers.LastVsTarget
                   : takeoffActive ? _sequence.LastVsTarget : float.NaN;
        float altObj = takeoffActive ? _sequence.LastAltTarget : float.NaN;
        float hdgObj = takeoffActive ? _sequence.HeadingTargetDeg
                     : interceptActive ? _intercept.LastDesiredTrack : float.NaN;
        float thrCmd = interceptActive ? _intercept.LastThrottleCmd
                    : maneuverActive ? _maneuvers.LastThrottleCmd
                    : takeoffActive ? _sequence.LastThrottleCmd : float.NaN;
        float flapObj = interceptActive ? _intercept.LastFlapCmd * 100f
                     : takeoffActive ? _sequence.LastFlapCmd * 100f : float.NaN;

        float iasKt = _d.IasKt.Float;
        float altFt = _d.AltFt.Float;
        float aglFt = _d.AglMeters.Float * MetersToFeet;
        float vsFpm = _d.VsFpm.Float;
        float pitchReal = _d.PitchDeg.Float;
        float bankReal = _d.BankDeg.Float;
        float headingReal = _d.HeadingDeg.Float;
        float gNormal = _d.GNormal.Float;
        float throttleReal = _d.EngineThrottleUse.Float * 100f;
        float flapsReal = _d.FlapHandle.Float * 100f;

        int focus = _director.FocusedXplmIndex;
        if (focus < 0)
            FillGlobalTelemetry(connected);
        else if (focus >= 1)
            FillFocusedOtherTelemetry(focus);
        else
            FillOwnshipTelemetry(
                maneuverActive, takeoffActive, interceptActive, interceptPending,
                pitchObj, bankObj, iasObj, vsObj, altObj, hdgObj, thrCmd, flapObj,
                iasKt, altFt, aglFt, vsFpm, pitchReal, bankReal, headingReal,
                gNormal, throttleReal, flapsReal);

        // Caja negra por avion: alimenta TODOS los loggers con IsRecording.
        // Ownship usa telemetria completa; IAs solo Other* (GS≈IAS, ALT MSL,
        // actitud; G/mandos/AoA/AGL NaN — telemetria IA reducida).
        if (connected) {
            foreach ((int xplmIndex, DataLogger log) in _dataLogs.Recording) {
                if (xplmIndex == 0)
                    RecordOwnshipSample(log, takeoffActive, maneuverActive,
                                        interceptActive, interceptPending,
                                        pitchObj, bankObj, hdgObj, thrCmd, flapObj,
                                        iasKt, altFt, aglFt, vsFpm,
                                        pitchReal, bankReal, headingReal, gNormal,
                                        throttleReal, flapsReal);
                else
                    RecordOtherPlaneSample(log, xplmIndex);
            }
        }

        DataLogger focused = FocusedDataLog;
        DataLogStatusText = FormatDataLogStatus(focused, connected);
        RefreshFocusedBlackBoxCharts();

        RefreshManeuverButtons();
        // Listado + IsFocused siempre: la franja EN FOCO es global y el roster
        // puede cambiar con el A330 del reset aunque no estes en Aviones.
        RefreshAircraftList();
        if (InterceptView.Visibility == Visibility.Visible) {
            RefreshInterceptTargets();
            RefreshInterceptStatus();
        }
    }

    // Panel derecho con foco GLOBAL: resumen de zona, sin objetivos de vuelo.
    private void FillGlobalTelemetry(bool connected)
    {
        int n = Math.Max(1, _client.Planes.Length);
        ModeText = "Vista global";
        FlightText =
            $"Naves en escena: {n}\n" +
            (connected
                ? $"Local IAS {_d.IasKt.Float:0} kt · ALT {_d.AltFt.Float:0} ft"
                : "Sin conexion");
        AttitudeText =
            "Camara aerea sobre la zona.\n" +
            "Rombos y lineas: pestana Graficos\n" +
            "(se activan al poner foco GLOBAL).";
        ControlsText =
            "Opciones de zona:\n" +
            "· Reset simulacion (LEBL)\n" +
            "· Graficos / etiquetas / rombos\n" +
            "Elige un avion para ordenes de vuelo.";
    }

    // Panel derecho con foco en ownship (indice 0): telemetria completa +
    // objetivos de takeoff/maniobra/intercept.
    private void FillOwnshipTelemetry(
        bool maneuverActive, bool takeoffActive,
        bool interceptActive, bool interceptPending,
        float pitchObj, float bankObj, float iasObj, float vsObj,
        float altObj, float hdgObj, float thrCmd, float flapObj,
        float iasKt, float altFt, float aglFt, float vsFpm,
        float pitchReal, float bankReal, float headingReal,
        float gNormal, float throttleReal, float flapsReal)
    {
        ModeText = interceptActive ? _intercept.PhaseText
                 : interceptPending ? "Interceptar · Despegue combate"
                 : takeoffActive ? (_director.IsRouteCruisePending
                     ? $"Ruta · {_sequence.StyleName} → {_director.Cruise.Mode.Name}"
                     : $"Ruta · {_sequence.StyleName}")
                 : _director.Cruise.IsRunning ? $"Ruta · {_director.Cruise.PhaseText}"
                 : maneuverActive ? _maneuvers.PhaseText
                 : "Manual";

        float thrShown = float.IsNaN(thrCmd) ? float.NaN : thrCmd * 100f;
        FlightText =
            Head("obj") + "\n" +
            Row("IAS", iasObj, iasKt, "0", "kt") + "\n" +
            Row("V/S", vsObj, vsFpm, "0", "fpm") + "\n" +
            Row("ALT", altObj, altFt, "0", "ft") + "\n" +
            Row("AGL", float.NaN, aglFt, "0", "ft");
        AttitudeText =
            Head("obj") + "\n" +
            Row("Pitch", pitchObj, pitchReal, "0.0", "deg") + "\n" +
            Row("Bank", bankObj, bankReal, "0.0", "deg") + "\n" +
            Row("Rumbo", hdgObj, headingReal, "0", "deg") + "\n" +
            Row("G", maneuverActive ? _maneuvers.LastGCommand : float.NaN, gNormal, "0.0", "g") + "\n" +
            Row("AoA", float.NaN, _d.AoaDeg.Float, "0.0", "deg");
        string gearObj = takeoffActive ? (_sequence.GearDownCommanded ? "abajo" : "arriba") : "--";
        string gearReal = _d.GearHandleDown.Bool ? "abajo" : "arriba";
        ControlsText =
            Head("mando") + "\n" +
            Row("Gas", thrShown, throttleReal, "0", "%") + "\n" +
            Row("Flaps", flapObj, flapsReal, "0", "%") + "\n" +
            RowText("Tren", gearObj, gearReal, "") + "\n" +
            Row("Freno", float.NaN, _d.ParkBrakeReadback.Float * 100f, "0", "%") + "\n" +
            Row("QNH", float.NaN, _d.BarometerPilot.Float, "0.00", "inHg");
    }

    // Panel derecho con foco en IA (indice >= 1): telemetria Other* reducida.
    // Objetivos solo si AiHold corre sobre esa misma nave (pitch 2 / bank 0;
    // rumbo del hold no se expone → "--").
    private void FillFocusedOtherTelemetry(int xplmIndex)
    {
        int slot = xplmIndex - 1;
        bool holdOnFocus = AiHoldRunningOn(xplmIndex);
        ModeText = holdOnFocus
            ? "IA · Crucero cinematico"
            : "IA · sin control AICopilot";

        float pitchObj = holdOnFocus ? 2f : float.NaN;
        float bankObj = holdOnFocus ? 0f : float.NaN;

        float iasKt = float.NaN, altFt = float.NaN, vsFpm = float.NaN;
        float pitchReal = float.NaN, bankReal = float.NaN, headingReal = float.NaN;

        if (slot >= 0 && slot < Datarefs.OtherPlaneSlots) {
            DataHandle el = _d.OtherElevMeters[slot];
            DataHandle hdg = _d.OtherHeadingDeg[slot];
            DataHandle pitch = _d.OtherPitchDeg[slot];
            DataHandle bank = _d.OtherBankDeg[slot];
            DataHandle vx = _d.OtherVelX[slot];
            DataHandle vz = _d.OtherVelZ[slot];
            DataHandle vy = _d.OtherVelY[slot];

            if (vx.HasValue && vz.HasValue)
                iasKt = (float)(Math.Sqrt(vx.Value * vx.Value + vz.Value * vz.Value) * MpsToKnots);
            if (el.HasValue)
                altFt = (float)(el.Value * MetersToFeet);
            if (vy.HasValue)
                vsFpm = (float)(vy.Value * MetersToFeet * 60.0);
            if (pitch.HasValue) pitchReal = (float)pitch.Value;
            if (bank.HasValue) bankReal = (float)bank.Value;
            if (hdg.HasValue) headingReal = (float)hdg.Value;
        }

        FlightText =
            Head("obj") + "\n" +
            Row("IAS", float.NaN, iasKt, "0", "kt") + "\n" +
            Row("V/S", float.NaN, vsFpm, "0", "fpm") + "\n" +
            Row("ALT", float.NaN, altFt, "0", "ft") + "\n" +
            Row("AGL", float.NaN, float.NaN, "0", "ft");
        AttitudeText =
            Head("obj") + "\n" +
            Row("Pitch", pitchObj, pitchReal, "0.0", "deg") + "\n" +
            Row("Bank", bankObj, bankReal, "0.0", "deg") + "\n" +
            Row("Rumbo", float.NaN, headingReal, "0", "deg") + "\n" +
            Row("G", float.NaN, float.NaN, "0.0", "g") + "\n" +
            Row("AoA", float.NaN, float.NaN, "0.0", "deg");
        ControlsText =
            Head("mando") + "\n" +
            Row("Gas", float.NaN, float.NaN, "0", "%") + "\n" +
            Row("Flaps", float.NaN, float.NaN, "0", "%") + "\n" +
            RowText("Tren", "--", "--", "") + "\n" +
            Row("Freno", float.NaN, float.NaN, "0", "%") + "\n" +
            Row("QNH", float.NaN, float.NaN, "0.00", "inHg");
    }

    private void RecordOwnshipSample(
        DataLogger log,
        bool takeoffActive, bool maneuverActive,
        bool interceptActive, bool interceptPending,
        float pitchObj, float bankObj, float hdgObj, float thrCmd, float flapObj,
        float iasKt, float altFt, float aglFt, float vsFpm,
        float pitchReal, float bankReal, float headingReal, float gNormal,
        float throttleReal, float flapsReal)
    {
        string phase = takeoffActive ? TakeoffSequence.PhaseName(_sequence.Phase) : "-";
        string action = interceptActive ? _intercept.PhaseText
                      : interceptPending ? "Interceptar · Despegue combate"
                      : takeoffActive ? "Ruta · Despegue"
                      : _director.Cruise.IsRunning ? $"Ruta · {_director.Cruise.Mode.Name}"
                      : maneuverActive ? _maneuvers.PhaseText
                      : "-";
        log.Record(
            phase: phase,
            action: action,
            iasKt: iasKt, altFt: altFt, aglFt: aglFt, vsFpm: vsFpm,
            pitchTargetDeg: pitchObj, pitchRealDeg: pitchReal,
            bankTargetDeg: bankObj, bankRealDeg: bankReal,
            headingTargetDeg: hdgObj, headingRealDeg: headingReal,
            gNormal: gNormal,
            throttleTarget01: float.IsNaN(thrCmd) ? float.NaN : thrCmd,
            throttleReal01: throttleReal / 100f,
            flapsTarget01: float.IsNaN(flapObj) ? float.NaN : flapObj / 100f,
            flapsReal01: flapsReal / 100f,
            pitchCmd: _controls.PitchInputCmd, rollCmd: _controls.RollInputCmd,
            yawCmd: _controls.YawInputCmd,
            gCommand: maneuverActive ? _maneuvers.LastGCommand : float.NaN,
            gPredicted: maneuverActive ? _maneuvers.PredictedG : float.NaN,
            aoaDeg: _d.AoaDeg.Float,
            speedbrake01: maneuverActive ? _maneuvers.LastSpeedbrakeCmd
                        : interceptActive ? _intercept.LastSpeedbrakeCmd
                        : _d.SpeedbrakeHandle.Float,
            weightLb: _d.TotalWeightKg.Float * 2.20462f, mach: _d.Mach.Float,
            pitchRateDps: _d.PitchRateDegPerSec.Float,
            rollRateDps: _d.RollRateDegPerSec.Float,
            adaptation: maneuverActive ? _maneuvers.AdaptationText
                      : interceptActive ? _intercept.TelemetryText : "",
            protection: maneuverActive ? _maneuvers.ProtectionText : "");
    }

    // Telemetria IA reducida: multiplayer no expone IAS/G/mandos/AoA.
    // IAS_kt = GS horizontal (aprox); ALT = elevacion MSL; AGL/G/mandos NaN.
    private void RecordOtherPlaneSample(DataLogger log, int xplmIndex)
    {
        int slot = xplmIndex - 1;
        if (slot < 0 || slot >= Datarefs.OtherPlaneSlots) return;

        DataHandle el = _d.OtherElevMeters[slot];
        DataHandle hdg = _d.OtherHeadingDeg[slot];
        DataHandle pitch = _d.OtherPitchDeg[slot];
        DataHandle bank = _d.OtherBankDeg[slot];
        DataHandle vx = _d.OtherVelX[slot];
        DataHandle vz = _d.OtherVelZ[slot];
        DataHandle vy = _d.OtherVelY[slot];
        if (!el.HasValue || !hdg.HasValue || !pitch.HasValue || !bank.HasValue ||
            !vx.HasValue || !vz.HasValue)
            return;

        float gsKt = (float)(Math.Sqrt(vx.Value * vx.Value + vz.Value * vz.Value) * MpsToKnots);
        float altFt = (float)(el.Value * MetersToFeet);
        float vsFpm = vy.HasValue
            ? (float)(vy.Value * MetersToFeet * 60.0)
            : float.NaN;
        float headingReal = (float)hdg.Value;
        float pitchReal = (float)pitch.Value;
        float bankReal = (float)bank.Value;

        string action = AiHoldRunningOn(xplmIndex) ? "Recto/nivelado" : "-";
        log.Record(
            phase: "-",
            action: action,
            iasKt: gsKt, altFt: altFt, aglFt: float.NaN, vsFpm: vsFpm,
            pitchTargetDeg: float.NaN, pitchRealDeg: pitchReal,
            bankTargetDeg: float.NaN, bankRealDeg: bankReal,
            headingTargetDeg: float.NaN, headingRealDeg: headingReal,
            gNormal: float.NaN,
            throttleTarget01: float.NaN, throttleReal01: float.NaN,
            flapsTarget01: float.NaN, flapsReal01: float.NaN,
            pitchCmd: float.NaN, rollCmd: float.NaN, yawCmd: float.NaN,
            gCommand: float.NaN, gPredicted: float.NaN, aoaDeg: float.NaN,
            speedbrake01: float.NaN,
            weightLb: float.NaN, mach: float.NaN,
            pitchRateDps: float.NaN, rollRateDps: float.NaN,
            adaptation: "", protection: "");
    }

    // Texto de accion en CSV IA: FlightDirector ya expone el hold.
    private bool AiHoldRunningOn(int xplmIndex) =>
        _director.AiHold.IsRunning && _director.AiHold.XplmIndex == xplmIndex;

    // Marca cada boton de accion con lo que va a pasar si se pulsa: normal si
    // la maniobra sale tal cual, en cursiva y con el motivo en el tooltip si va
    // a salir adaptada, y deshabilitado solo cuando no existe ninguna version
    // segura de ella aqui y ahora.
    //
    // Antes esto solo apagaba el boton cuando la IAS/AGL se salian de un rango
    // fijo -- y ni siquiera ponia el motivo que su propio comentario prometia.
    // Apagar el boton era la forma barata de evitar que la maniobra saliera
    // mal; ahora la maniobra no sale mal, sale adaptada, asi que lo unico que
    // hace falta es contarlo.
    private void RefreshManeuverButtons() {
        foreach (Button btn in ActionsStack.Children.OfType<Button>()) {
            if (btn.Tag is not string tag || !Enum.TryParse(tag, out ManeuverKind kind)) continue;

            var (level, text) = _maneuvers.Preview(kind);
            btn.IsEnabled = level != AdaptationLevel.Impossible;
            btn.FontStyle = level == AdaptationLevel.Adapted ? FontStyles.Italic : FontStyles.Normal;

            // El tooltip se reasigna solo si cambio: a 10 Hz, reescribirlo
            // siempre cerraria y reabriria el popup mientras se lee.
            string tip = level switch {
                AdaptationLevel.Impossible => text.Length > 0 ? $"No es posible: {text}." : "",
                AdaptationLevel.Adapted => text.Length > 0 ? $"Se ejecutara adaptada: {text}." : "",
                _ => "",
            };
            string? current = btn.ToolTip as string;
            if (tip.Length == 0) { if (current is not null) btn.ToolTip = null; }
            else if (current != tip) btn.ToolTip = tip;
        }
    }

    private static string Head(string left) => $"{"",-6}{left,7}  {"real",7}";

    private static string Row(string name, float obj, float real, string fmt, string unit)
    {
        string o = float.IsNaN(obj) ? "--" : obj.ToString(fmt);
        string r = float.IsNaN(real) ? "--" : real.ToString(fmt);
        return $"{name,-6}{o,7}  {r,7}  {unit}";
    }

    private static string RowText(string name, string obj, string real, string unit) =>
        $"{name,-6}{obj,7}  {real,7}  {unit}";

    private const float MetersToFeet = 3.28084f;
    private const double MpsToKnots = 1.943844;

    // --- Popups flotantes vs X-Plane ------------------------------------------
    // Tooltips y ComboBox abren un HWND aparte. Al crearse, Windows suele
    // subir el shell (owner) por encima de X-Plane y el hueco se ve negro.
    // Mitigacion: PopupGuard reapila el Z mientras el popup vive, y el HWND
    // del tip se marca NOACTIVATE + TOPMOST para verse sin activar el shell.

    private static bool _floatingChromeHooked;

    private void HookFloatingChrome() {
        if (!_floatingChromeHooked) {
            _floatingChromeHooked = true;
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip),
                System.Windows.Controls.ToolTip.OpenedEvent,
                new RoutedEventHandler(OnAnyToolTipOpened));
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ToolTip),
                System.Windows.Controls.ToolTip.ClosedEvent,
                new RoutedEventHandler(OnAnyToolTipClosed));
        }
        PreviewMouseDown += OnShellPreviewMouseDownCloseFocusPicker;
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

    private static void OnAnyToolTipOpened(object sender, RoutedEventArgs e) {
        if (sender is not System.Windows.Controls.ToolTip tip) return;
        ShellWindow? shell = FindShellFor(tip.PlacementTarget as DependencyObject);
        if (shell is null) return;
        shell._docker?.BeginPopupGuard();
        ElevateFloating(tip, topmost: true);
        // El HWND del tip a veces aparece un tick despues de Opened.
        shell.Dispatcher.BeginInvoke(() => {
            ElevateFloating(tip, topmost: true);
            shell._docker?.EnsureStacked();
        }, DispatcherPriority.Loaded);
        shell.Dispatcher.BeginInvoke(() => shell._docker?.EnsureStacked(),
                                     DispatcherPriority.Input);
    }

    private static void OnAnyToolTipClosed(object sender, RoutedEventArgs e) {
        if (sender is not System.Windows.Controls.ToolTip tip) return;
        ElevateFloating(tip, topmost: false);
        FindShellFor(tip.PlacementTarget as DependencyObject)?._docker?.EndPopupGuard();
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
        IsGlobal ? "GLOBAL · zona" : $"{RoleLabel} · {Name}";

    private string _name = "";
    public string Name {
        get => _name;
        set {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
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

    // Indice de X-Plane: 1..19 (el 0 es el avion del usuario y no es un blanco).
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
