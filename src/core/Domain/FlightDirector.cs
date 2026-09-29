namespace AICopilotCore.Domain;

// Fachada unica para arrancar/parar las tres secuencias de vuelo.
//
// TakeoffSequence, ManeuverSequence e InterceptSequence comparten los mismos
// overrides (AircraftControls): si dos corrieran a la vez se pisarian los
// mandos. La UI y la ControlApi del driver LLM pasan por aqui para que la
// exclusion mutua viva en un solo sitio, no duplicada en cada boton/endpoint.
public sealed class FlightDirector
{
    private readonly TakeoffSequence _takeoff;
    private readonly ManeuverSequence _maneuvers;
    private readonly InterceptSequence _intercept;
    private readonly Connector.ConnectorClient _client;

    // Ultimas lineas de ActionLogged de cualquiera de las tres secuencias,
    // para que el driver las lea sin depender del panel de UI.
    private readonly object _logGate = new();
    private readonly Queue<string> _recentLog = new();
    private const int MaxRecentLog = 200;

    public FlightDirector(TakeoffSequence takeoff, ManeuverSequence maneuvers,
                          InterceptSequence intercept,
                          Connector.ConnectorClient client)
    {
        _takeoff = takeoff;
        _maneuvers = maneuvers;
        _intercept = intercept;
        _client = client;

        _takeoff.ActionLogged += Remember;
        _maneuvers.ActionLogged += Remember;
        _intercept.ActionLogged += Remember;
    }

    public TakeoffSequence Takeoff => _takeoff;
    public ManeuverSequence Maneuvers => _maneuvers;
    public InterceptSequence Intercept => _intercept;

    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_logGate) return _recentLog.ToArray();
        }
    }

    public void AbortAll()
    {
        _takeoff.Abort();
        _maneuvers.Abort();
        _intercept.Abort();
    }

    // Arranca un despegue. Corta maniobra/interceptacion si las hubiera.
    // Devuelve false si no hay conexion con el plugin.
    public bool StartTakeoff(TakeoffStyle style, out string error)
    {
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }
        if (_maneuvers.IsRunning) _maneuvers.Abort();
        if (_intercept.IsRunning) _intercept.Abort();
        _takeoff.Start(style);
        error = "";
        return true;
    }

    // Nivelado (crucero): corta despegue/intercept y engancha LevelWings.
    public bool StartCruise(out string error)
    {
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }
        if (_takeoff.IsRunning) _takeoff.Abort();
        if (_intercept.IsRunning) _intercept.Abort();
        _maneuvers.Start(ManeuverKind.LevelWings);
        error = "";
        return true;
    }

    // Arranca una maniobra del catalogo. Si Preview dice Impossible, no arranca.
    // force=true salta el preview (sigue siendo responsabilidad del llamador).
    public bool StartManeuver(ManeuverKind kind, bool force, out string error, out string adaptation)
    {
        adaptation = "";
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }

        var (level, text) = _maneuvers.Preview(kind);
        adaptation = text;
        if (!force && level == AdaptationLevel.Impossible)
        {
            error = text.Length > 0 ? text : "maniobra imposible en el estado actual";
            return false;
        }

        if (_takeoff.IsRunning) _takeoff.Abort();
        if (_intercept.IsRunning) _intercept.Abort();
        _maneuvers.Start(kind);
        error = "";
        return true;
    }

    public bool StartIntercept(int xplmIndex, InterceptStation station, string label,
                               out string error)
    {
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }
        if (_takeoff.IsRunning) _takeoff.Abort();
        if (_maneuvers.IsRunning) _maneuvers.Abort();
        if (!_intercept.Start(xplmIndex, station, label, out string refusal))
        {
            error = refusal;
            return false;
        }
        error = "";
        return true;
    }

    // Aborta secuencias, suelta overrides y pide al connector el escenario
    // fijo (LEBL 24L + IA a ~15 km). Ver SimScenario. La config idle de
    // suelo (gases/flaps/freno) se aplica cuando llega ScenarioReady /
    // AircraftReloaded -- PlaceUser pisa los mandos al cargar el aeropuerto.
    public bool PendingGroundIdle { get; private set; }

    public bool ResetSimulation(out string error)
    {
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }

        AbortAll();
        _client.ReleaseAll();
        PendingGroundIdle = true;
        _client.PlaceScenario(
            SimScenario.UserLat, SimScenario.UserLon, SimScenario.UserElevMsl,
            SimScenario.UserHdgTrue, SimScenario.UserSpeedMps,
            SimScenario.AiLat, SimScenario.AiLon, SimScenario.AiElevMsl,
            SimScenario.AiHdgTrue, SimScenario.AiSpeedMps,
            SimScenario.AiAircraftRelPath);
        Remember($"Reset simulacion: {SimScenario.Label}");
        error = "";
        return true;
    }

    public void ClearPendingGroundIdle() => PendingGroundIdle = false;

    private void Remember(string line)
    {
        lock (_logGate)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > MaxRecentLog) _recentLog.Dequeue();
        }
    }
}
