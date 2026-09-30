namespace AICopilotCore.Domain;

// Fachada unica para arrancar/parar las secuencias de vuelo.
//
// TakeoffSequence, ManeuverSequence e InterceptSequence comparten los mismos
// overrides (AircraftControls): si dos corrieran a la vez se pisarian los
// mandos. La UI pasa por aqui para que la exclusion mutua viva en un solo
// sitio, no duplicada en cada boton.
//
// CruisePilot orquesta LevelWings/virajes sobre ManeuverSequence: no escribe
// mandos por su cuenta, pero hay que Stop()earlo al pedir otra secuencia.
//
// AiStraightHold es distinto: controla una IA por Holds cinematicos y puede
// correr EN PARALELO con las secuencias del ownship.
//
// FocusedXplmIndex selecciona a quien van StartRoute/StartTakeoff/StartCruise/
// StartManeuver:
//   -1     = Global (opciones de zona / camara aerea; sin ordenes de vuelo)
//    0     = avion local
//    1..19 = IA (solo crucero cinematico).
//
// Interceptacion desde tierra: StartIntercept encola el blanco, arranca
// despegue combate (handoff a minima altura) y Tick() encadena la
// persecucion cuando el despegue llega a Done.
//
// Ruta desde tierra: StartRoute encola el CruiseMode y, al Done del
// despegue, Tick() arranca CruisePilot.
public sealed class FlightDirector
{
    private readonly TakeoffSequence _takeoff;
    private readonly ManeuverSequence _maneuvers;
    private readonly InterceptSequence _intercept;
    private readonly CruisePilot _cruise;
    private readonly AiStraightHold _aiHold;
    private readonly Connector.ConnectorClient _client;

    private readonly object _logGate = new();
    private readonly Queue<string> _recentLog = new();
    private const int MaxRecentLog = 200;

    private readonly object _pendingGate = new();
    private PendingIntercept? _pending;
    private CruiseMode? _pendingCruise;

    private sealed class PendingIntercept
    {
        public required int Index { get; init; }
        public required InterceptStation Station { get; init; }
        public required string Label { get; init; }
    }

    public FlightDirector(TakeoffSequence takeoff, ManeuverSequence maneuvers,
                          InterceptSequence intercept, CruisePilot cruise,
                          AiStraightHold aiHold, Connector.ConnectorClient client)
    {
        _takeoff = takeoff;
        _maneuvers = maneuvers;
        _intercept = intercept;
        _cruise = cruise;
        _aiHold = aiHold;
        _client = client;

        _takeoff.ActionLogged += Remember;
        _maneuvers.ActionLogged += Remember;
        _intercept.ActionLogged += Remember;
        _cruise.ActionLogged += Remember;
        _aiHold.ActionLogged += Remember;
    }

    public TakeoffSequence Takeoff => _takeoff;
    public ManeuverSequence Maneuvers => _maneuvers;
    public InterceptSequence Intercept => _intercept;
    public CruisePilot Cruise => _cruise;
    public AiStraightHold AiHold => _aiHold;

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

    public bool IsInterceptPending
    {
        get { lock (_pendingGate) return _pending is not null; }
    }

    public bool IsRouteCruisePending
    {
        get { lock (_pendingGate) return _pendingCruise is not null; }
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
        ClearAllPending();
        _cruise.Abort();
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

    public void AbortInterceptMission()
    {
        bool hadPending = ClearInterceptPending();
        if (_intercept.IsRunning) _intercept.Abort();
        else if (hadPending && _takeoff.IsRunning) _takeoff.Abort();
    }

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

    // Ruta: en tierra despega con el estilo; en el aire (o tras Done)
    // aplica el modo de vuelo. startedAs describe que se engancho.
    public bool StartRoute(TakeoffStyle style, CruiseMode cruiseMode,
                           out string error, out string startedAs)
    {
        startedAs = "";
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
            if (cruiseMode.Id != CruiseModeId.Straight)
                Remember($"Ruta IA: modo '{cruiseMode.Name}' no aplica a holds " +
                         "cinematicos; se usa recto estabilizado.");
            if (!StartAiStraightHoldFocused(out error))
                return false;
            startedAs = "crucero IA (recto)";
            return true;
        }

        ClearAllPending();
        _cruise.Stop();
        if (_maneuvers.IsRunning) _maneuvers.Abort();
        if (_intercept.IsRunning) _intercept.Abort();

        if (_intercept.NeedsOwnTakeoff())
        {
            if (_takeoff.IsRunning) _takeoff.Abort();
            lock (_pendingGate) _pendingCruise = cruiseMode;
            _takeoff.Start(style);
            startedAs = $"despegue [{style.Name}] → luego {cruiseMode.Name}";
            error = "";
            return true;
        }

        if (_takeoff.IsRunning) _takeoff.Abort();
        _cruise.Start(cruiseMode);
        startedAs = $"vuelo [{cruiseMode.Name}]";
        error = "";
        return true;
    }

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
        ClearAllPending();
        _cruise.Stop();
        if (_maneuvers.IsRunning) _maneuvers.Abort();
        if (_intercept.IsRunning) _intercept.Abort();
        _takeoff.Start(style);
        error = "";
        return true;
    }

    // Nivelado directo (sin detectar tierra): ownship → CruisePilot recto;
    // IA → hold cinematico. No arranca despegue aunque el avion este en suelo.
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

        ClearAllPending();
        _cruise.Stop();
        if (_takeoff.IsRunning) _takeoff.Abort();
        if (_intercept.IsRunning) _intercept.Abort();
        _cruise.Start(CruiseModes.Straight);
        error = "";
        return true;
    }

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

        ClearAllPending();
        _cruise.Stop();
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
        ClearPendingCruise();
        _cruise.Stop();
        if (_maneuvers.IsRunning) _maneuvers.Abort();

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

        ClearInterceptPending();
        if (_takeoff.IsRunning) _takeoff.Abort();
        if (!_intercept.Start(xplmIndex, station, label, out string refusal))
        {
            error = refusal;
            return false;
        }
        error = "";
        return true;
    }

    public void Tick()
    {
        PendingIntercept? pendingIx;
        CruiseMode? pendingCruise;
        lock (_pendingGate)
        {
            pendingIx = _pending;
            pendingCruise = _pendingCruise;
        }

        if (pendingIx is not null)
        {
            if (_takeoff.Phase == TakeoffPhase.Done)
            {
                ClearInterceptPending();
                if (!_intercept.Start(pendingIx.Index, pendingIx.Station, pendingIx.Label,
                                      out string refusal, afterOwnTakeoff: true))
                {
                    Remember($"Interceptar {pendingIx.Label}: no se pudo encadenar tras " +
                             $"despegue — {refusal}");
                }
                return;
            }

            if (!_takeoff.IsRunning && _takeoff.Phase == TakeoffPhase.Idle)
            {
                ClearInterceptPending();
                Remember($"Interceptar {pendingIx.Label}: cancelada (despegue interrumpido).");
            }
            return;
        }

        if (pendingCruise is null) return;

        if (_takeoff.Phase == TakeoffPhase.Done)
        {
            ClearPendingCruise();
            _cruise.Start(pendingCruise);
            return;
        }

        if (!_takeoff.IsRunning && _takeoff.Phase == TakeoffPhase.Idle)
        {
            ClearPendingCruise();
            Remember($"Ruta: vuelo [{pendingCruise.Name}] cancelado (despegue interrumpido).");
        }
    }

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

    private bool ClearInterceptPending()
    {
        lock (_pendingGate)
        {
            bool had = _pending is not null;
            _pending = null;
            return had;
        }
    }

    private void ClearPendingCruise()
    {
        lock (_pendingGate) _pendingCruise = null;
    }

    private void ClearAllPending()
    {
        lock (_pendingGate)
        {
            _pending = null;
            _pendingCruise = null;
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
