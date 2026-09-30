using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AICopilotCore.Connector;
using AICopilotCore.Domain;

namespace AICopilotCore.Ui;

// La ventana del core, montada como un editor de codigo: barra de titulo y
// de herramientas arriba, panel izquierdo (despegue / acciones / aviones),
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
    private readonly DataLogger _dataLog = new();
    private readonly BlackBoxView _blackBoxView = new();
    private readonly DispatcherTimer _refreshTimer;

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

    // Caja negra (pestana izquierda + graficos del viewport): buffer tipado,
    // lineas CSV y fichero en disco — ver DataLogger.cs.
    public ObservableCollection<string> DataLogSamples => _dataLog.Samples;
    public string DataLogFilePathText => DataLogger.LogFilePath;

    public ShellWindow(ConnectorClient client, Datarefs datarefs, AircraftControls controls,
                       TakeoffSequence sequence, ManeuverSequence maneuvers,
                       InterceptSequence intercept, ControlTuning tuning,
                       FlightDirector director) {
        InitializeComponent();
        DataContext = this;

        _client = client;
        _d = datarefs;
        _controls = controls;
        _sequence = sequence;
        _maneuvers = maneuvers;
        _intercept = intercept;
        _tuning = tuning;
        _director = director;
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

        // Arranca en la vista de despegue; el XAML ya deja TakeoffTab marcado
        // y las otras vistas collapsadas, pero ApplyLeftView deja el titulo y
        // las visibilidades consistentes si el orden de Init cambia.
        ApplyLeftView(LeftPanelView.Takeoff);
        RefreshAircraftList();
        RefreshInterceptTargets();

        // La vista de graficos vive en el host del viewport (hermano del
        // Border que usa el docker). Se crea aqui porque no es una Page XAML.
        BlackBoxHost.Child = _blackBoxView;
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
    // deja la misma marca en el DataLog (columna Nota). markBlackBox=true
    // (acciones de vuelo) tambien pinta la linea vertical en los graficos.
    //
    // BeginInvoke (no Invoke): Start/Abort se llaman con el candado de la
    // secuencia cogido. Un Invoke sincrono aqui puede engancharse con el
    // hilo del pipe (que espera el mismo candado) y dejar la Driver API
    // colgada al pedir un despegue.
    public void Append(string line, bool markBlackBox = false) {
        Dispatcher.BeginInvoke(() => {
            Logs.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
            _dataLog.RecordEvent(line, chartMarker: markBlackBox);
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

    // Estilo que usara el proximo "Iniciar despegue". No se aplica a una
    // secuencia que ya esta corriendo.
    private TakeoffStyleId _styleId = TakeoffStyleId.Relaxed;
    private bool _switchingStyle;
    public string StyleSummary => TakeoffStyles.Get(_styleId).Summary;
    public string StyleSpec => TakeoffStyles.Get(_styleId).Spec;

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

    // --- Listado de aviones (panel izquierdo) -------------------------------

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

    private string _dataLogStatusText = "0 muestras · detenido";
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
        _dataLog.Close();
        // Devuelve a X-Plane su marco y su geometria antes de irse: cerrar el
        // shell no puede dejar al usuario con una ventana sin barra de titulo
        // encajada en un hueco que ya no existe.
        _docker?.Dispose();
        base.OnClosed(e);
    }

    // --- Barra de herramientas ----------------------------------------------

    private void OnStartClick(object sender, RoutedEventArgs e) {
        // Exclusion mutua con maniobras/intercept: vive en FlightDirector
        // (misma fachada que usa la ControlApi del driver LLM).
        if (!_director.StartTakeoff(TakeoffStyles.Get(_styleId), out string error))
            Append($"Despegue: {error}.");
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

    private void OnAbortClick(object sender, RoutedEventArgs e) {
        _director.AbortAll();
    }

    // "Volar en crucero" no es un Abortar mas: Abortar suelta los overrides
    // y devuelve el avion a mando manual tal cual estaba; esto en cambio
    // corta cualquier despegue/accion en marcha y ENGANCHA el autopiloto de
    // nivelado (la misma maniobra LevelWings del catalogo), como pedir "pon
    // el avion a volar recto" antes de la siguiente orden -- el reset que
    // hace falta para no tener que abortar del todo entre una accion y otra.
    private void OnCruiseClick(object sender, RoutedEventArgs e) {
        if (!_director.StartCruise(out string error))
            Append($"Crucero: {error}.");
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
            Append($"'{def.Label}' no es posible ahora mismo: {error}.");
            return;
        }
        if (adaptation.Length > 0) Append($"'{def.Label}' se ejecuta adaptada: {adaptation}.");
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
            GfxInterceptPathToggle.IsChecked = _graphics.ShowInterceptPath;
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
        _graphics.ShowInterceptPath = GfxInterceptPathToggle.IsChecked == true;
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

    private void OnClearDataLogClick(object sender, RoutedEventArgs e) {
        _dataLog.Clear();
        UpdateDataLogRecordingUi();
        if (_blackBoxViewportActive)
            _blackBoxView.Refresh(_dataLog.TypedSamples, _dataLog.Markers, DataLogStatusText);
    }

    private void OnStartDataLogClick(object sender, RoutedEventArgs e) {
        _dataLog.StartRecording();
        UpdateDataLogRecordingUi();
        if (_blackBoxViewportActive)
            _blackBoxView.Refresh(_dataLog.TypedSamples, _dataLog.Markers, DataLogStatusText);
    }

    private void OnStopDataLogClick(object sender, RoutedEventArgs e) {
        _dataLog.StopRecording();
        UpdateDataLogRecordingUi();
        if (_blackBoxViewportActive)
            _blackBoxView.Refresh(_dataLog.TypedSamples, _dataLog.Markers, DataLogStatusText);
    }

    private void UpdateDataLogRecordingUi() {
        bool rec = _dataLog.IsRecording;
        DataLogStartBtn.IsEnabled = !rec;
        DataLogStopBtn.IsEnabled = rec;
        DataLogStatusText = rec
            ? $"{_dataLog.Count} muestras · grabando (~10 Hz)"
            : $"{_dataLog.Count} muestras · detenido";
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
            _blackBoxView.Refresh(_dataLog.TypedSamples, _dataLog.Markers, DataLogStatusText);
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
    // Los tres iconos de la barra de actividad NO pliegan paneles: cambian
    // que contenido se ve en LeftCol (despegue / acciones / aviones). La
    // telemetria derecha y el log inferior se quedan siempre a la vista;
    // el usuario los redimensiona con los splitters.

    private enum LeftPanelView { Takeoff, Actions, Aircraft, Intercept, Graphics, DataLog, Config, XPlane }

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

        LeftPanelView view = tab == ActionsTab ? LeftPanelView.Actions
                          : tab == AircraftTab ? LeftPanelView.Aircraft
                          : tab == InterceptTab ? LeftPanelView.Intercept
                          : tab == GraphicsTab ? LeftPanelView.Graphics
                          : tab == DataLogTab ? LeftPanelView.DataLog
                          : tab == ConfigTab ? LeftPanelView.Config
                          : tab == XPlaneTab ? LeftPanelView.XPlane
                          : LeftPanelView.Takeoff;

        _switchingLeftView = true;
        try {
            TakeoffTab.IsChecked = view == LeftPanelView.Takeoff;
            ActionsTab.IsChecked = view == LeftPanelView.Actions;
            AircraftTab.IsChecked = view == LeftPanelView.Aircraft;
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
        TakeoffView.Visibility = view == LeftPanelView.Takeoff ? Visibility.Visible : Visibility.Collapsed;
        ActionsView.Visibility = view == LeftPanelView.Actions ? Visibility.Visible : Visibility.Collapsed;
        AircraftView.Visibility = view == LeftPanelView.Aircraft ? Visibility.Visible : Visibility.Collapsed;
        InterceptView.Visibility = view == LeftPanelView.Intercept ? Visibility.Visible : Visibility.Collapsed;
        GraphicsView.Visibility = view == LeftPanelView.Graphics ? Visibility.Visible : Visibility.Collapsed;
        DataLogView.Visibility = view == LeftPanelView.DataLog ? Visibility.Visible : Visibility.Collapsed;
        ConfigView.Visibility = view == LeftPanelView.Config ? Visibility.Visible : Visibility.Collapsed;
        XPlaneView.Visibility = view == LeftPanelView.XPlane ? Visibility.Visible : Visibility.Collapsed;

        LeftPanelTitle.Text = view switch {
            LeftPanelView.Actions => "ACCIONES",
            LeftPanelView.Aircraft => "AVIONES",
            LeftPanelView.Intercept => "INTERCEPTAR",
            LeftPanelView.Graphics => "GRAFICOS",
            LeftPanelView.DataLog => "CAJA NEGRA",
            LeftPanelView.Config => "CONFIG",
            LeftPanelView.XPlane => "X-PLANE",
            _ => "DESPEGUE",
        };

        if (view == LeftPanelView.Aircraft) RefreshAircraftList();
        if (view == LeftPanelView.Intercept) RefreshInterceptTargets();
    }

    // Por ahora, si el connector aun no ha mandado el listado, solo pintamos
    // el avion local. En cuanto llega Op.Planes se anaden las IAs (indice
    // >= 1) con el nombre del .acf — un Cirrus SR22 cargado como IA sale
    // aqui, no se queda fuera.
    private void RefreshAircraftList() {
        SimPlane[] planes = _client.Planes;
        bool haveRoster = planes.Length > 0;
        int wanted = haveRoster ? planes.Length : 1;

        while (AircraftEntries.Count > wanted)
            AircraftEntries.RemoveAt(AircraftEntries.Count - 1);

        for (int i = 0; i < wanted; i++) {
            bool local = !haveRoster || planes[i].Index == 0;
            if (i >= AircraftEntries.Count || AircraftEntries[i].IsLocal != local) {
                var created = new AircraftListEntry { IsLocal = local };
                if (i >= AircraftEntries.Count) AircraftEntries.Add(created);
                else AircraftEntries[i] = created;
            }

            AircraftListEntry entry = AircraftEntries[i];
            entry.RoleLabel = local ? "LOCAL" : "IA";
            entry.Name = haveRoster ? PlaneDisplayName(planes[i]) : "Tu avion";
            if (local) {
                entry.Detail = LocalAircraftDetail();
                entry.StatusBrush = LocalAircraftStatus();
            } else {
                FillOtherAircraft(entry, planes[i].Index);
            }
        }

        // Solo el local cuenta como "sin otras IAs".
        bool onlyLocal = wanted <= 1;
        AircraftEmptyVisibility = onlyLocal ? Visibility.Visible : Visibility.Collapsed;
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
        sb.Append("Mandos  ")
          .Append($"flaps {_intercept.LastFlapCmd * 100f:0}% · aerofreno {_intercept.LastSpeedbrakeCmd * 100f:0}%");
        if (_intercept.Weaving)
            sb.AppendLine().Append("Serpenteando: el blanco vuela mas despacio de lo que este avion puede.");
        InterceptStatusText = sb.ToString();
    }

    // --- Refresco -------------------------------------------------------------

    private bool _interceptPathSent;

    // Empuja la ruta de interceptacion al connector (Refresh corre a 10 Hz).
    // Solo con el flag activo y la intercept corriendo; al parar, una vez
    // vacia para borrar (el connector ademas la caduca a 1 s).
    private void PushInterceptPath() {
        if (!_client.IsConnected) { _interceptPathSent = false; return; }
        var path = _graphics.ShowInterceptPath && _intercept.IsRunning
            ? _intercept.PlannedPath : null;
        if (path != null && path.Count >= 2) {
            _client.SetInterceptPath(path);
            _interceptPathSent = true;
        } else if (_interceptPathSent) {
            _client.SetInterceptPath(null);
            _interceptPathSent = false;
        }
    }

    private void Refresh() {
        PushInterceptPath();
        bool connected = _client.IsConnected;
        ConnectionStatus = connected
            ? $"Conectado a {_client.PluginVersion}"
            : "Sin conexion con el plugin";
        ConnectionDotColor = connected ? Brushes.LimeGreen : Brushes.Gray;

        bool maneuverActive = _maneuvers.IsRunning;
        bool takeoffActive = _sequence.IsRunning;
        bool interceptActive = _intercept.IsRunning;
        bool interceptPending = _director.IsInterceptPending;

        ModeText = interceptActive ? _intercept.PhaseText
                 : interceptPending ? $"Interceptar · Despegue combate"
                 : maneuverActive ? _maneuvers.PhaseText
                 : takeoffActive ? $"Despegue · {_sequence.StyleName}"
                 : "Manual";
        SequenceText = _sequence.Phase == TakeoffPhase.Idle
            ? "En espera"
            : $"{_sequence.StyleName} · {TakeoffSequence.PhaseName(_sequence.Phase)}";
        ChecklistText = TakeoffSequence.BuildChecklistLine(_sequence.Phase);

        // Una sola fuente de objetivos: la maniobra si hay una, si no el
        // despegue. En manual todo objetivo es NaN y la columna sale "--".
        // El gas de la izquierda es el mando que pedimos, no un objetivo:
        // el objetivo de motor, cuando existe, es la IAS.
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

        // Caja negra: solo si el usuario pulso Empezar y hay conexion.
        // En Idle tambien aporta (trim, oscilaciones, deriva de G...); el CSV
        // rota a 8 MB y el buffer tipado se queda en ~5 min @ 10 Hz.
        if (connected && _dataLog.IsRecording) {
            string phase = takeoffActive ? TakeoffSequence.PhaseName(_sequence.Phase) : "-";
            string action = interceptActive ? _intercept.PhaseText
                          : interceptPending ? "Interceptar · Despegue combate"
                          : maneuverActive ? _maneuvers.PhaseText
                          : takeoffActive ? "Despegue" : "-";
            _dataLog.Record(
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
        if (!connected)
            DataLogStatusText = $"{_dataLog.Count} muestras · sin conexion";
        else if (_dataLog.IsRecording)
            DataLogStatusText = $"{_dataLog.Count} muestras · grabando (~10 Hz)";
        else
            DataLogStatusText = $"{_dataLog.Count} muestras · detenido";

        if (_blackBoxViewportActive)
            _blackBoxView.Refresh(_dataLog.TypedSamples, _dataLog.Markers, DataLogStatusText);

        RefreshManeuverButtons();
        if (AircraftView.Visibility == Visibility.Visible) RefreshAircraftList();
        if (InterceptView.Visibility == Visibility.Visible) {
            RefreshInterceptTargets();
            RefreshInterceptStatus();
        }
    }

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
        string r = real.ToString(fmt);
        return $"{name,-6}{o,7}  {r,7}  {unit}";
    }

    private static string RowText(string name, string obj, string real, string unit) =>
        $"{name,-6}{obj,7}  {real,7}  {unit}";

    private const float MetersToFeet = 3.28084f;
    private const double MpsToKnots = 1.943844;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Una fila del listado de aviones de la partida. IsLocal marca el avion
// que pilota este core; el resto son las otras IAs que X-Plane tiene activas.
public sealed class AircraftListEntry : INotifyPropertyChanged {
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsLocal { get; init; }

    private string _name = "";
    public string Name {
        get => _name;
        set {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    private string _roleLabel = "";
    public string RoleLabel {
        get => _roleLabel;
        set {
            if (_roleLabel == value) return;
            _roleLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RoleLabel)));
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
