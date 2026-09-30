using System.Threading;
using System.Windows;
using AICopilotCore.Connector;
using AICopilotCore.Domain;
using AICopilotCore.Domain.Agents;

namespace AICopilotCore.Ui;

// Punto de entrada del core. Aqui se monta la cadena entera:
//
//   ConnectorClient  -- el pipe con el plugin
//     -> Datarefs        que datarefs nos importan (se definen y suscriben)
//     -> AircraftControls como se toca el avion local
//     -> AircraftWorld    todos los aviones (0..19), cada uno con su agente
//     -> FlightDirector   el foco: a quien van las ordenes de la UI
//     -> ShellWindow      lo que ve el usuario
//
// El detalle de timing que importa: AircraftWorld.Tick() se llama desde
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
    private AircraftWorld? _world;
    private FlightDirector? _director;
    private bool _autoStartDone;

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

        // Todos los aviones (0 = ownship, 1..19 = IAs, creados bajo demanda)
        // viven en el mundo; cada uno lleva sus propias secuencias y sigue
        // a lo suyo aunque cambie el foco. El director solo lleva el foco.
        _world = new AircraftWorld(_client, _datarefs, _controls, _tuning);
        _director = new FlightDirector(_world);
        _world.Get(0);   // el avion local existe desde el arranque

        // Una sola ventana: el shell lleva dentro los paneles que antes eran
        // dos overlays flotantes (telemetria y log) y, en el hueco central,
        // la ventana de X-Plane.
        _shell = new ShellWindow(_client, _datarefs, _tuning, _director);

        // Acciones de cada avion a su propia caja negra (idx = avion del agente).
        _world.ActionLogged += (idx, line) =>
            _shell.Append(line, markBlackBox: true, blackBoxIndex: idx);
        _client.ConnectorEvent += (kind, text) => {
            if (kind == EventKind.OverviewFocus) {
                if (int.TryParse(text, out int idx))
                    _shell.ApplyOverviewFocusFromConnector(idx);
                return;
            }

            if (kind == EventKind.OriginShift) {
                // X-Plane movio el origen local y el connector ya lo compenso:
                // dejar constancia en el log y en TODAS las cajas negras
                // activas (hipotesis del teletransporte de 80 km).
                _shell.NoteOriginShift(text);
                return;
            }

            _shell.Append(kind == EventKind.Info ? text : $"[{kind}] {text}");
            // X-Plane resetea sim/joystick/eq_pfc_yoke a 0 al recargar el
            // avion del usuario, asi que el toggle de "Ocultar recuadro
            // yoke" (ShellWindow) se tiene que reaplicar aqui.
            if (kind == EventKind.AircraftReloaded) _shell.ApplyMouseYokeBoxState();

            // El plugin solto AiControl y holds por su cuenta: los agentes
            // olvidan lo que creian tener (antes de reaplicar idle, abajo).
            if (kind == EventKind.OverridesReleased || kind == EventKind.AircraftReloaded)
                _world.OnConnectorReleased();

            // Tras PlaceScenario el aeropuerto/avion se recargan y pisan
            // mandos: si el ownship arranca en tierra, reaplicamos idle en
            // cada AircraftReloaded pendiente y cerramos el pendiente al
            // llegar ScenarioReady. Ahi mismo se dan las ordenes iniciales
            // del inicio elegido (ruta del A330, interceptacion del caza),
            // sin tocar el foco.
            bool scenarioReady = kind == EventKind.ScenarioReady;
            if (_world.PendingGroundIdle &&
                (kind == EventKind.AircraftReloaded || scenarioReady))
            {
                _world.Get(0).Body.ApplyGroundIdle();
                if (scenarioReady)
                {
                    _world.ClearPendingGroundIdle();
                    _shell.Append("Idle de suelo: gases abajo, flaps abajo, freno puesto, tren abajo.");
                }
            }
            if (scenarioReady)
            {
                foreach (string line in _world.CompletePendingStart())
                    _shell.Append(line);
                _shell.ResumeGlobalCameraIfFocused();
            }
        };

        // El connector arranca cada sesion sin holds ni ids, asi que las
        // banderas de "ya tengo el override puesto" que llevan los cuerpos
        // dejan de ser verdad al reconectar.
        _client.SessionReady += () => {
            _world.OnConnectorReleased();
            _shell.ApplyMouseYokeBoxState();
            _shell.ApplyGraphicsConfig();
            // Al lanzar el core (build nuevo) la primera conexion arranca
            // siempre en "Inicio en pista: dos aviones". Las reconexiones
            // posteriores (recarga del addon) no recolocan nada.
            if (!_autoStartDone)
            {
                _autoStartDone = true;
                SimStart first = SimStart.RunwayPair;
                string name = SimScenarios.Get(first).Name;
                if (_director.StartSimulation(first, out string startErr))
                    _shell.Append($"{name} (auto al lanzar).");
                else
                    _shell.Append($"{name} (auto al lanzar): {startErr}.");
            }
        };
        _client.Disconnected += () => _world.OnConnectionLost();

        // El lazo de control: un tick por frame de simulador, con el dt que
        // reporta el propio X-Plane. Cada avion del mundo corre sus secuencias
        // en este mismo hilo (el del pipe), sincronizado con el sim.
        _client.TelemetryReceived += frame => _world.Tick(frame.Dt);

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
        if (_world is not null)
            foreach (AircraftAgent a in _world.Active)
            {
                if (a.IsBusy) a.AbortAll();
                else if (!a.Body.IsLocal) a.Body.ReleaseAllOverrides();
            }
        _client?.Dispose();
        base.OnExit(e);
    }
}
