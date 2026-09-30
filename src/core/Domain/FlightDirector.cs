namespace AICopilotCore.Domain;

// Fachada unica para arrancar/parar las secuencias de vuelo.
//
// TakeoffSequence, ManeuverSequence e InterceptSequence comparten los mismos
// overrides (AircraftControls): si dos corrieran a la vez se pisarian los
// mandos. La UI pasa por aqui para que la exclusion mutua viva en un solo
// sitio, no duplicada en cada boton.
//
// AiStraightHold es distinto: controla una IA por Holds cinematicos y puede
// correr EN PARALELO con las secuencias del ownship.
//
// FocusedXplmIndex selecciona a quien van StartTakeoff/StartCruise/
// StartManeuver:
//   -1     = Global (opciones de zona / camara aerea; sin ordenes de vuelo)
//    0     = avion local
//    1..19 = IA (solo crucero / LevelWings).
//
// Interceptacion desde tierra: StartIntercept encola el blanco, arranca
// despegue combate (handoff a minima altura) y Tick() encadena la
// persecucion cuando el despegue llega a Done.
public sealed class FlightDirector
{
    private readonly TakeoffSequence _takeoff;
    private readonly ManeuverSequence _maneuvers;
    private readonly InterceptSequence _intercept;
    private readonly AiStraightHold _aiHold;
    private readonly Connector.ConnectorClient _client;

    // Ultimas lineas de ActionLogged de cualquiera de las secuencias,
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
                          InterceptSequence intercept, AiStraightHold aiHold,
                          Connector.ConnectorClient client)
    {
        _takeoff = takeoff;
        _maneuvers = maneuvers;
        _intercept = intercept;
        _aiHold = aiHold;
        _client = client;

        _takeoff.ActionLogged += Remember;
        _maneuvers.ActionLogged += Remember;
        _intercept.ActionLogged += Remember;
        _aiHold.ActionLogged += Remember;
    }

    public TakeoffSequence Takeoff => _takeoff;
    public ManeuverSequence Maneuvers => _maneuvers;
    public InterceptSequence Intercept => _intercept;
    public AiStraightHold AiHold => _aiHold;

    // -1 = Global (por defecto); 0 = ownship; 1..19 = IA bajo foco de la UI.
    public const int GlobalFocusIndex = -1;

    public int FocusedXplmIndex { get; private set; } = GlobalFocusIndex;
    public string FocusedLabel { get; private set; } = "Global";

    public bool IsGlobalFocus => FocusedXplmIndex == GlobalFocusIndex;

    public void SetFocus(int xplmIndex, string label)
    {
        if (xplmIndex < GlobalFocusIndex) xplmIndex = GlobalFocusIndex;
        if (xplmIndex > Connector.Datarefs.OtherPlaneSlots)
            xplmIndex = Connector.Datarefs.OtherPlaneSlots;
        FocusedXplmIndex = xplmIndex;
        if (xplmIndex == GlobalFocusIndex)
            FocusedLabel = string.IsNullOrWhiteSpace(label) ? "Global" : label;
        else
            FocusedLabel = string.IsNullOrWhiteSpace(label)
                ? (xplmIndex == 0 ? "Local" : $"IA {xplmIndex}")
                : label;
    }

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

    // Solo secuencias del ownship. El hold cinematico de una IA es independiente:
    // abortar un despegue/intercept no debe tumbar el Airbus en crucero.
    public void AbortAll()
    {
        ClearPending();
        _takeoff.Abort();
        _maneuvers.Abort();
        _intercept.Abort();
    }

    public void AbortFocused()
    {
        if (IsGlobalFocus) AbortAll();
        else if (FocusedXplmIndex >= 1) _aiHold.Abort();
        else AbortAll();
    }

    // Cancela interceptacion en curso o el despegue combate pendiente.
    public void AbortInterceptMission()
    {
        bool hadPending = ClearPending();
        if (_intercept.IsRunning) _intercept.Abort();
        else if (hadPending && _takeoff.IsRunning) _takeoff.Abort();
    }

    // Cambia el puesto de formacion con la mision ya pedida (en vuelo o
    // pendiente de despegue combate). La UI lo llama al pulsar otro boton
    // de estacion sin tener que abortar y relanzar.
    public bool ChangeInterceptStation(InterceptStation station, out string error)
    {
        lock (_pendingGate)
        {
            if (_pending is not null)
            {
                _pending = new PendingIntercept
                {
                    Index = _pending.Index,
                    Station = station,
                    Label = _pending.Label,
                };
                Remember($"Interceptar {_pending.Label}: estacion → " +
                         $"{InterceptCatalog.Get(station).Label} (tras el despegue).");
            }
        }

        if (_intercept.IsRunning)
        {
            if (!_intercept.ChangeStation(station, out string refusal))
            {
                error = refusal;
                return false;
            }
            error = "";
            return true;
        }

        if (IsInterceptPending)
        {
            error = "";
            return true;
        }

        error = "no hay interceptacion en marcha";
        return false;
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
        if (IsGlobalFocus)
        {
            error = "foco global: elige un avion primero";
            return false;
        }
        if (FocusedXplmIndex >= 1)
        {
            error = "solo disponible en el avion local por ahora";
            return false;
        }
        ClearPending();
        if (_maneuvers.IsRunning) _maneuvers.Abort();
        if (_intercept.IsRunning) _intercept.Abort();
        _takeoff.Start(style);
        error = "";
        return true;
    }

    // Nivelado (crucero): en ownship corta despegue/intercept y engancha
    // LevelWings; en IA arranca AiStraightHold sin tocar el ownship.
    public bool StartCruise(out string error)
    {
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }
        if (IsGlobalFocus)
        {
            error = "foco global: elige un avion primero";
            return false;
        }
        if (FocusedXplmIndex >= 1)
            return StartAiStraightHoldFocused(out error);

        ClearPending();
        if (_takeoff.IsRunning) _takeoff.Abort();
        if (_intercept.IsRunning) _intercept.Abort();
        _maneuvers.Start(ManeuverKind.LevelWings);
        error = "";
        return true;
    }

    // Arranca una maniobra del catalogo. Si Preview dice Impossible, no arranca.
    // force=true salta el preview (sigue siendo responsabilidad del llamador).
    // Con foco en IA solo LevelWings (→ AiStraightHold); el resto se niega.
    public bool StartManeuver(ManeuverKind kind, bool force, out string error, out string adaptation)
    {
        adaptation = "";
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }

        if (IsGlobalFocus)
        {
            error = "foco global: elige un avion primero";
            return false;
        }

        if (FocusedXplmIndex >= 1)
        {
            if (kind == ManeuverKind.LevelWings)
                return StartAiStraightHoldFocused(out error);
            error = "solo disponible en el avion local por ahora";
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
    // fijo (LEBL 24L + A330 a FL210 delante). Ver SimScenario. La config
    // idle de suelo (gases/flaps/freno) se aplica cuando llega ScenarioReady /
    // AircraftReloaded -- PlaceUser pisa los mandos al cargar el aeropuerto.
    // PendingAiCruise pide el hold cinematico del A330 tras ScenarioReady.
    public bool PendingGroundIdle { get; private set; }
    public bool PendingAiCruise { get; private set; }

    public bool ResetSimulation(out string error)
    {
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }

        AbortAll();
        _aiHold.Abort();
        _client.ReleaseAll();
        PendingGroundIdle = true;
        PendingAiCruise = true;
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
    public void ClearPendingAiCruise() => PendingAiCruise = false;

    // Tras ScenarioReady: hold cinematico del A330 (indice 1) sin dejar el
    // foco de UI en la IA — el usuario suele querer despegar/interceptar
    // con el avion local justo despues del reset.
    public bool TryStartPendingAiCruise()
    {
        if (!PendingAiCruise) return false;
        PendingAiCruise = false;
        if (!_client.IsConnected) return false;

        int prevIdx = FocusedXplmIndex;
        string prevLabel = FocusedLabel;
        SetFocus(1, "Airbus A330");
        bool ok = StartAiStraightHoldFocused(out _);
        SetFocus(prevIdx, prevLabel);
        return ok;
    }

    // Crucero cinematico sobre el avion enfocado (IA). Si ya hay hold en
    // otro slot, lo aborta antes; si es el mismo, no-op.
    private bool StartAiStraightHoldFocused(out string error)
    {
        int idx = FocusedXplmIndex;
        if (idx < 1 || idx > Connector.Datarefs.OtherPlaneSlots)
        {
            error = "hace falta enfocar una IA (1..19)";
            return false;
        }

        if (_aiHold.IsRunning && _aiHold.XplmIndex != idx)
            _aiHold.Abort();
        if (_aiHold.IsRunning && _aiHold.XplmIndex == idx)
        {
            error = "";
            return true;
        }
        return _aiHold.Start(idx, out error);
    }

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
