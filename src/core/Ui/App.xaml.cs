using System.Threading;
using System.Windows;
using AICopilotCore.Connector;
using AICopilotCore.Domain;

namespace AICopilotCore.Ui;

// Punto de entrada del core. Aqui se monta la cadena entera:
//
//   ConnectorClient  -- el pipe con el plugin
//     -> Datarefs        que datarefs nos importan (se definen y suscriben)
//     -> AircraftControls como se toca el avion
//     -> TakeoffSequence  la logica de despegue
//     -> FlightDirector   exclusion mutua entre secuencias
//     -> ShellWindow      lo que ve el usuario
//
// El detalle de timing que importa: TakeoffSequence.Update() se llama desde
// TelemetryReceived, o sea desde el hilo de lectura del pipe y al ritmo de
// los frames del simulador, NO desde un DispatcherTimer de la UI. Asi los
// PID corren sincronizados con el sim, y una UI atascada (redibujando, o el
// usuario arrastrando una ventana) no arrastra al lazo de control con ella.
public partial class App : Application
{
    // El core lo abre el usuario a mano: el plugin NO lo lanza. Aun asi hace
    // falta protegerse de una segunda instancia (doble clic de mas, o el
    // build_and_deploy reabriendolo cuando ya estaba), porque el pipe del
    // connector admite una sola conexion: dos cores se pelearian por ella y
    // el segundo se quedaria colgado esperando sin decir por que.
    private static Mutex? _singleInstanceMutex;

    private ConnectorClient? _client;
    private Datarefs? _datarefs;
    private ControlTuning? _tuning;
    private AircraftControls? _controls;
    private TakeoffSequence? _sequence;
    private ManeuverSequence? _maneuvers;
    private InterceptSequence? _intercept;
    private AiStraightHold? _aiHold;
    private FlightDirector? _director;

    private ShellWindow? _shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, "Local\\AICopilotCore_SingleInstance",
                                         out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }
        base.OnStartup(e);

        _client = new ConnectorClient();
        // Define() y Subscribe() se pueden llamar sin conexion: el cliente
        // se los apunta y los replica en cuanto el pipe aparece, y otra vez
        // en cada reconexion.
        _datarefs = new Datarefs(_client);
        // Los "limites humanos" del control (rampas de mando, agresividad de
        // maniobras, backstop de G...) viven en un solo sitio. La pestana de
        // Config del shell los edita en caliente sobre esta misma instancia.
        // El despegue no los usa: su perfil lo elige un TakeoffStyle.
        _tuning = new ControlTuning();
        _controls = new AircraftControls(_client, _datarefs, _tuning);
        _sequence = new TakeoffSequence(_controls, _datarefs);
        _maneuvers = new ManeuverSequence(_controls, _datarefs, _tuning);
        // La tercera secuencia: ir a por otro avion de la partida y quedarse
        // en formacion con el. Comparte overrides con las otras dos, asi que
        // el director corta la que este en marcha antes de arrancar cualquiera.
        _intercept = new InterceptSequence(_controls, _datarefs, _tuning);
        // Hold cinematico de una IA (recto y nivelado). Corre en paralelo
        // con las secuencias del ownship: no usa AircraftControls.
        _aiHold = new AiStraightHold(_client, _datarefs);
        _director = new FlightDirector(_sequence, _maneuvers, _intercept, _aiHold, _client);

        // Una sola ventana: el shell lleva dentro los paneles que antes eran
        // dos overlays flotantes (telemetria y log) y, en el hueco central,
        // la ventana de X-Plane.
        _shell = new ShellWindow(_client, _datarefs, _controls, _sequence, _maneuvers,
                                 _intercept, _tuning, _director);

        _sequence.ActionLogged += line => _shell.Append(line, markBlackBox: true);
        _maneuvers.ActionLogged += line => _shell.Append(line, markBlackBox: true);
        _intercept.ActionLogged += line => _shell.Append(line, markBlackBox: true);
        // Marcas del hold van a la caja negra de esa IA (no al ownship).
        _aiHold.ActionLogged += line => {
            int idx = _aiHold.XplmIndex;
            _shell.Append(line, markBlackBox: true, blackBoxIndex: idx >= 1 ? idx : 0);
        };
        _client.ConnectorEvent += (kind, text) => {
            _shell.Append(kind == EventKind.Info ? text : $"[{kind}] {text}");
            // X-Plane resetea sim/joystick/eq_pfc_yoke a 0 al recargar el
            // avion del usuario, asi que el toggle de "Ocultar recuadro
            // yoke" (ShellWindow) se tiene que reaplicar aqui.
            if (kind == EventKind.AircraftReloaded) _shell.ApplyMouseYokeBoxState();

            // Tras PlaceScenario el aeropuerto/avion se recargan y pisan
            // mandos: reaplicamos idle en cada AircraftReloaded pendiente y
            // cerramos el pendiente al llegar ScenarioReady. Luego, si el
            // reset pidio crucero de IA, arrancamos el A330 en recto/nivelado.
            if (_director.PendingGroundIdle &&
                (kind == EventKind.AircraftReloaded || kind == EventKind.ScenarioReady))
            {
                _controls.ApplyGroundIdle();
                if (kind == EventKind.ScenarioReady)
                {
                    _director.ClearPendingGroundIdle();
                    _shell.Append("Idle de suelo: gases abajo, flaps abajo, freno puesto, tren abajo.");
                    if (_director.PendingAiCruise && _director.TryStartPendingAiCruise())
                        _shell.Append("Airbus: vuelo recto y nivelado");
                    _shell.ResumeGlobalCameraIfFocused();
                }
            }
            else if (kind == EventKind.ScenarioReady)
            {
                _shell.ResumeGlobalCameraIfFocused();
            }
        };

        // El connector arranca cada sesion sin holds ni ids, asi que las
        // banderas de "ya tengo el override puesto" que lleva
        // AircraftControls dejan de ser verdad al reconectar.
        _client.SessionReady += () => {
            _controls.ForgetOverrideState();
            _shell.ApplyMouseYokeBoxState();
            _shell.ApplyGraphicsConfig();
            // Tras recargar el addon (o reconectar el core) partimos siempre
            // del mismo escenario de prueba: LEBL 24L + IA a ~15 km.
            if (_director.ResetSimulation(out string resetErr))
                _shell.Append($"Reset simulacion (auto): {SimScenario.Label}.");
            else
                _shell.Append($"Reset simulacion (auto): {resetErr}.");
        };
        _client.Disconnected += () => {
            _sequence.OnConnectionLost();
            _maneuvers.OnConnectionLost();
            _intercept.OnConnectionLost();
            _aiHold.OnConnectionLost();
        };

        // El lazo de control: un tick por frame de simulador, con el dt que
        // reporta el propio X-Plane. TakeoffSequence y ManeuverSequence son
        // mutuamente excluyentes -- el director aborta la otra antes de
        // arrancar cualquiera de las dos -- asi que llamarlas siempre a
        // ambas es seguro: la que esta en Idle no hace nada. AiStraightHold
        // puede correr a la vez (IA distinta, Holds cinematicos).
        _client.TelemetryReceived += frame => {
            _sequence.Update(frame.Dt);
            _maneuvers.Update(frame.Dt);
            _intercept.Update(frame.Dt);
            _aiHold.Update(frame.Dt);
            _director.Tick();
        };

        MainWindow = _shell;
        // Cerrar el shell cierra el core; el XPlaneDocker que vive dentro se
        // encarga de devolverle a X-Plane su marco y su posicion al salir.
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _shell.Show();

        _client.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Soltar antes de cerrar el pipe. El SafetyGuard del connector lo
        // haria igualmente al ver el pipe roto, pero pedirlo explicitamente
        // es instantaneo y no depende de que el otro lado reaccione.
        if (_sequence?.IsRunning == true || _maneuvers?.IsRunning == true ||
            _intercept?.IsRunning == true) _controls?.ReleaseAllOverrides();
        if (_aiHold?.IsRunning == true) _aiHold.Abort();
        _client?.Dispose();
        base.OnExit(e);
    }
}
