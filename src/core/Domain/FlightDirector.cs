namespace AICopilotCore.Domain;

// Fachada unica para arrancar/parar las tres secuencias de vuelo.
//
// TakeoffSequence, ManeuverSequence e InterceptSequence comparten los mismos
// overrides (AircraftControls): si dos corrieran a la vez se pisarian los
// mandos. La UI pasa por aqui para que la exclusion mutua viva en un solo
// sitio, no duplicada en cada boton.
//
// Interceptacion desde tierra: StartIntercept encola el blanco, arranca
// despegue combate (handoff a minima altura) y Tick() encadena la
// persecucion cuando el despegue llega a Done.
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

    private readonly object _pendingGate = new();
    private PendingIntercept? _pending;

    private sealed class PendingIntercept
    {
        public required int Index { get; init; }
        public required InterceptStation Station { get; init; }
        public required string Label { get; init; }
    }

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

    // Despegue combate en marcha como preludio de una interceptacion.
    public bool IsInterceptPending
    {
        get { lock (_pendingGate) return _pending is not null; }
    }

    public string PendingInterceptLabel
    {
        get { lock (_pendingGate) return _pending?.Label ?? ""; }
    }

    public int PendingInterceptIndex
    {
        get { lock (_pendingGate) return _pending?.Index ?? -1; }
    }

    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_logGate) return _recentLog.ToArray();
        }
    }

    public void AbortAll()
    {
        ClearPending();
        _takeoff.Abort();
        _maneuvers.Abort();
        _intercept.Abort();
    }

    // Cancela interceptacion en curso o el despegue combate pendiente.
    public void AbortInterceptMission()
    {
        bool hadPending = ClearPending();
        if (_intercept.IsRunning) _intercept.Abort();
        else if (hadPending && _takeoff.IsRunning) _takeoff.Abort();
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
        ClearPending();
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
        ClearPending();
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

        ClearPending();
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
        if (_maneuvers.IsRunning) _maneuvers.Abort();

        // En tierra (o demasiado lento): despegue combate hasta altura minima
        // y Tick() encadena la persecucion. En el aire: intercept directo.
        if (_intercept.NeedsOwnTakeoff())
        {
            if (_intercept.IsRunning) _intercept.Abort();
            if (_takeoff.IsRunning) _takeoff.Abort();

            lock (_pendingGate)
            {
                _pending = new PendingIntercept
                {
                    Index = xplmIndex,
                    Station = station,
                    Label = label,
                };
            }
            _takeoff.Start(TakeoffStyles.CombatIntercept, handoffAtTurnAltitude: true);
            Remember($"Interceptar {label}: despegue combate primero " +
                     $"(handoff a +{TakeoffStyles.CombatIntercept.TurnHeightFt:0} ft), " +
                     "luego giro hacia el blanco.");
            error = "";
            return true;
        }

        ClearPending();
        if (_takeoff.IsRunning) _takeoff.Abort();
        if (!_intercept.Start(xplmIndex, station, label, out string refusal))
        {
            error = refusal;
            return false;
        }
        error = "";
        return true;
    }

    // Llamar tras Update de las secuencias. Si el despegue combate del
    // preludio acaba (Done), arranca la interceptacion hacia el blanco.
    public void Tick()
    {
        PendingIntercept? pending;
        lock (_pendingGate) pending = _pending;
        if (pending is null) return;

        if (_takeoff.Phase == TakeoffPhase.Done)
        {
            ClearPending();
            if (!_intercept.Start(pending.Index, pending.Station, pending.Label,
                                  out string refusal, afterOwnTakeoff: true))
            {
                Remember($"Interceptar {pending.Label}: no se pudo encadenar tras " +
                         $"despegue — {refusal}");
            }
            return;
        }

        // Despegue abortado / perdido sin llegar a Done: la mision muere.
        if (!_takeoff.IsRunning && _takeoff.Phase == TakeoffPhase.Idle)
        {
            ClearPending();
            Remember($"Interceptar {pending.Label}: cancelada (despegue interrumpido).");
        }
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

    private bool ClearPending()
    {
        lock (_pendingGate)
        {
            bool had = _pending is not null;
            _pending = null;
            return had;
        }
    }

    private void Remember(string line)
    {
        lock (_logGate)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > MaxRecentLog) _recentLog.Dequeue();
        }
    }
}
