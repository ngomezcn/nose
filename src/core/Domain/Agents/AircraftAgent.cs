namespace AICopilotCore.Domain.Agents;

// Un avion con su propio "hilo logico": cuerpo (sensores+actuadores), perfil,
// secuencias (despegue, maniobras, crucero, intercept), ordenes pendientes y
// registro de acciones. Cualquier indice XPLM 0..19 admite EXACTAMENTE las
// mismas ordenes: no hay ramas por tipo de avion; lo que el cuerpo no tiene
// lo declara BodyCaps y la maniobra se adapta.
//
// Concurrencia: Tick corre en el hilo del pipe (un frame de sim), Start*/Abort*
// en el hilo de UI. Cada secuencia lleva su propio candado; las ordenes
// pendientes, el suyo.
//
// Es la logica que antes vivia en FlightDirector (StartRoute/StartTakeoff/...)
// sin foco y sin ramas de IA. La exclusion mutua entre secuencias es POR AVION.
public sealed class AircraftAgent
{
    private readonly Func<bool> _isConnected;
    private readonly InterceptRegistry _registry;

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

    public AircraftAgent(int xplmIndex, string label, IAircraftBody body, IFlightProfile profile,
                         ControlTuning tuning, ITargetSource targets, InterceptRegistry registry,
                         Func<bool> isConnected)
    {
        XplmIndex = xplmIndex;
        Label = label;
        Body = body;
        Profile = profile;
        _registry = registry;
        _isConnected = isConnected;

        Takeoff = new TakeoffSequence(body);
        Maneuvers = new ManeuverSequence(body, tuning);
        Cruise = new CruisePilot(Maneuvers, body);
        Intercept = new InterceptSequence(body, targets, tuning, registry);

        Takeoff.ActionLogged += Remember;
        Maneuvers.ActionLogged += Remember;
        Cruise.ActionLogged += Remember;
        Intercept.ActionLogged += Remember;
        if (body is KinematicAiBody ai) ai.ActionLogged += Remember;
    }

    public int XplmIndex { get; }
    public string Label { get; set; }
    public IAircraftBody Body { get; }
    public IFlightProfile Profile { get; }

    public TakeoffSequence Takeoff { get; }
    public ManeuverSequence Maneuvers { get; }
    public CruisePilot Cruise { get; }
    public InterceptSequence Intercept { get; }

    // (indice del avion, linea). Se lanza en el hilo que la genere (pipe o UI).
    public event Action<int, string>? ActionLogged;

    public IReadOnlyList<string> RecentLog
    {
        get { lock (_logGate) return _recentLog.ToArray(); }
    }

    // --- Estado consultable -------------------------------------------------

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

    // A quien intercepta este avion (activo o pendiente tras despegue), -1 = a nadie.
    public int InterceptTargetIndex
    {
        get
        {
            if (Intercept.IsRunning) return Intercept.TargetIndex;
            return PendingInterceptIndex;
        }
    }

    public bool IsBusy =>
        Takeoff.IsRunning || Maneuvers.IsRunning || Cruise.IsRunning || Intercept.IsRunning ||
        IsInterceptPending || IsRouteCruisePending;

    public AgentView View()
    {
        bool takeoff = Takeoff.IsRunning;
        bool cruise = Cruise.IsRunning;
        bool maneuver = Maneuvers.IsRunning;
        bool intercept = Intercept.IsRunning;
        bool routePending = IsRouteCruisePending;
        bool interceptPending = IsInterceptPending;
        string pendingLabel = PendingInterceptLabel;
        int targetIdx = InterceptTargetIndex;
        string targetLabel = intercept ? Intercept.TargetLabel : pendingLabel;

        string sequence;
        if (takeoff || routePending)
            sequence = routePending
                ? $"{Takeoff.StyleName} · {TakeoffSequence.PhaseName(Takeoff.Phase)} → {Cruise.Mode.Name}"
                : $"{Takeoff.StyleName} · {TakeoffSequence.PhaseName(Takeoff.Phase)}";
        else if (cruise)
            sequence = Cruise.PhaseText;
        else
            sequence = "En espera";

        AgentMode mode;
        string modeText;
        if (intercept)
        {
            mode = AgentMode.Intercept;
            modeText = $"Interceptar {targetLabel}";
        }
        else if (interceptPending)
        {
            mode = AgentMode.Takeoff;
            modeText = $"Despegue → interceptar {pendingLabel}";
        }
        else if (takeoff || routePending)
        {
            mode = AgentMode.Takeoff;
            modeText = routePending ? $"Despegue → {Cruise.Mode.Name}" : "Despegue";
        }
        else if (cruise)
        {
            mode = AgentMode.Route;
            modeText = $"Ruta · {Cruise.Mode.Name}";
        }
        else if (maneuver)
        {
            mode = AgentMode.Maneuver;
            modeText = Maneuvers.ActiveLabel;
        }
        else
        {
            mode = AgentMode.Idle;
            modeText = "Manual";
        }

        return new AgentView(
            XplmIndex, Label, Profile.Name, mode, modeText, sequence,
            TakeoffSequence.BuildChecklistLine(Takeoff.Phase),
            takeoff, Takeoff.Phase, Takeoff.StyleName,
            cruise, Cruise.Mode.Name, Cruise.PhaseText, routePending,
            maneuver, Maneuvers.ActiveLabel, Maneuvers.PhaseText,
            Maneuvers.AdaptationText, Maneuvers.ProtectionText,
            intercept, interceptPending, targetIdx, targetLabel,
            Intercept.PhaseText,
            intercept ? Intercept.Station : (_pending?.Station ?? Intercept.Station));
    }

    // --- Ordenes ------------------------------------------------------------

    // Ruta: en tierra despega con el estilo; en el aire aplica el modo de vuelo.
    // startedAs describe que se engancho.
    public bool StartRoute(TakeoffStyle style, CruiseMode cruiseMode,
                           out string error, out string startedAs)
    {
        startedAs = "";
        if (!CheckConnected(out error)) return false;

        ClearAllPending();
        Cruise.Stop();
        if (Maneuvers.IsRunning) Maneuvers.Abort();
        if (Intercept.IsRunning) Intercept.Abort();

        Body.Sense();
        if (Intercept.NeedsOwnTakeoff())
        {
            if (Takeoff.IsRunning) Takeoff.Abort();
            lock (_pendingGate) _pendingCruise = cruiseMode;
            Takeoff.Start(style);
            startedAs = $"despegue [{style.Name}] → luego {cruiseMode.Name}";
            error = "";
            return true;
        }

        if (Takeoff.IsRunning) Takeoff.Abort();
        Cruise.Start(cruiseMode);
        startedAs = $"vuelo [{cruiseMode.Name}]";
        error = "";
        return true;
    }

    public bool StartTakeoff(TakeoffStyle style, out string error)
    {
        if (!CheckConnected(out error)) return false;
        ClearAllPending();
        Cruise.Stop();
        if (Maneuvers.IsRunning) Maneuvers.Abort();
        if (Intercept.IsRunning) Intercept.Abort();
        Takeoff.Start(style);
        error = "";
        return true;
    }

    // Nivelado directo (sin detectar tierra): CruisePilot recto.
    public bool StartCruise(out string error)
    {
        if (!CheckConnected(out error)) return false;
        ClearAllPending();
        Cruise.Stop();
        if (Takeoff.IsRunning) Takeoff.Abort();
        if (Intercept.IsRunning) Intercept.Abort();
        Cruise.Start(CruiseModes.Straight);
        error = "";
        return true;
    }

    public bool StartManeuver(ManeuverKind kind, bool force, out string error, out string adaptation)
    {
        adaptation = "";
        if (!CheckConnected(out error)) return false;

        var (level, text) = Maneuvers.Preview(kind);
        adaptation = text;
        if (!force && level == AdaptationLevel.Impossible)
        {
            error = text.Length > 0 ? text : "maniobra imposible en el estado actual";
            return false;
        }

        ClearAllPending();
        Cruise.Stop();
        if (Takeoff.IsRunning) Takeoff.Abort();
        if (Intercept.IsRunning) Intercept.Abort();
        Maneuvers.Start(kind);
        error = "";
        return true;
    }

    // Este avion intercepta a targetIdx. Un solo objetivo activo: si ya
    // intercepta a otro se sustituye. El registro compartido rechaza los cruces
    // (A->B con B->A) con un mensaje claro, sin tocar lo que el avion ya hacia.
    // alreadyFlying: el llamador sabe que el avion esta en el aire (inicio de
    // simulacion en vuelo) aunque la telemetria aun diga IAS 0, p. ej. en pausa.
    public bool StartIntercept(int targetIdx, InterceptStation station, string label,
                               out string error, bool alreadyFlying = false)
    {
        if (!CheckConnected(out error)) return false;

        Body.Sense();
        bool needsTakeoff = !alreadyFlying && Intercept.NeedsOwnTakeoff();

        if (needsTakeoff && !_registry.TryRegister(XplmIndex, targetIdx, out error))
            return false;   // la intencion pendiente tambien cuenta para el registro

        ClearPendingCruise();
        Cruise.Stop();
        if (Maneuvers.IsRunning) Maneuvers.Abort();

        if (needsTakeoff)
        {
            if (Intercept.IsRunning) Intercept.Abort();   // desregistra
            if (Takeoff.IsRunning) Takeoff.Abort();
            if (!_registry.TryRegister(XplmIndex, targetIdx, out error))
                return false;

            lock (_pendingGate)
            {
                _pending = new PendingIntercept
                {
                    Index = targetIdx,
                    Station = station,
                    Label = label,
                };
            }
            Takeoff.Start(TakeoffStyles.CombatIntercept, handoffAtTurnAltitude: true);
            Remember($"Interceptar {label}: despegue combate primero " +
                     $"(handoff a +{TakeoffStyles.CombatIntercept.TurnHeightFt:0} ft), " +
                     "luego giro hacia el blanco.");
            error = "";
            return true;
        }

        ClearInterceptPending(keepRegistration: false);
        if (Takeoff.IsRunning) Takeoff.Abort();
        if (!Intercept.Start(targetIdx, station, label, out string refusal,
                             afterOwnTakeoff: alreadyFlying))
        {
            error = refusal;
            return false;
        }
        error = "";
        return true;
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

        if (Intercept.IsRunning)
        {
            if (!Intercept.ChangeStation(station, out string refusal))
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

    // Todo parado. Un avion sin mandos propios (cinematico) se devuelve a la IA
    // nativa; el ownship queda en manual.
    public void AbortAll()
    {
        ClearAllPending();
        Cruise.Abort();
        Takeoff.Abort();
        Maneuvers.Abort();
        Intercept.Abort();
        if (!Body.IsLocal) Body.ReleaseAllOverrides();
    }

    public void AbortInterceptMission()
    {
        bool hadPending = ClearInterceptPending(keepRegistration: false);
        if (Intercept.IsRunning) Intercept.Abort();
        else if (hadPending && Takeoff.IsRunning) Takeoff.Abort();
        if (!Body.IsLocal && !IsBusy) Body.ReleaseAllOverrides();
    }

    // --- Bucle --------------------------------------------------------------

    // Un frame de sim (hilo del pipe): sentir -> pendientes -> secuencias -> avanzar cuerpo.
    public void Tick(float dt)
    {
        Body.Sense();
        ProcessPending();
        Takeoff.Update(dt);
        Maneuvers.Update(dt);
        Cruise.Update(dt);
        Intercept.Update(dt);
        Body.Step(dt);
        bool hasKin = Body.TryGetKinematics(out Kinematics k);
        Cadence.Observe(dt, hasKin, k);
    }

    // Ritmo de Tick y saltos de posicion, para la caja negra (ver TickCadence).
    public TickCadence Cadence { get; } = new();

    private void ProcessPending()
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
            if (Takeoff.Phase == TakeoffPhase.Done)
            {
                ClearInterceptPending(keepRegistration: true);
                if (!Intercept.Start(pendingIx.Index, pendingIx.Station, pendingIx.Label,
                                     out string refusal, afterOwnTakeoff: true))
                {
                    _registry.Unregister(XplmIndex);
                    Remember($"Interceptar {pendingIx.Label}: no se pudo encadenar tras " +
                             $"despegue — {refusal}");
                }
                return;
            }

            if (!Takeoff.IsRunning && Takeoff.Phase == TakeoffPhase.Idle)
            {
                ClearInterceptPending(keepRegistration: false);
                Remember($"Interceptar {pendingIx.Label}: cancelada (despegue interrumpido).");
            }
            return;
        }

        if (pendingCruise is null) return;

        if (Takeoff.Phase == TakeoffPhase.Done)
        {
            ClearPendingCruise();
            Cruise.Start(pendingCruise);
            return;
        }

        if (!Takeoff.IsRunning && Takeoff.Phase == TakeoffPhase.Idle)
        {
            ClearPendingCruise();
            Remember($"Ruta: vuelo [{pendingCruise.Name}] cancelado (despegue interrumpido).");
        }
    }

    // --- Conexion / connector ----------------------------------------------

    // Pipe caido o el connector solto todo (ReleaseEverything: AiControl y
    // holds): el estado interno ya no es verdad. Se olvida sin escribir nada.
    public void ForgetState()
    {
        ClearAllPending();
        Cruise.Stop();
        Takeoff.OnConnectionLost();
        Maneuvers.OnConnectionLost();
        Intercept.OnConnectionLost();
        Body.OnConnectionLost();
        _registry.Unregister(XplmIndex);
    }

    // --- Utilidades ---------------------------------------------------------

    private bool CheckConnected(out string error)
    {
        if (!_isConnected())
        {
            error = "no hay conexion con el plugin";
            return false;
        }
        error = "";
        return true;
    }

    private bool ClearInterceptPending(bool keepRegistration)
    {
        bool had;
        lock (_pendingGate)
        {
            had = _pending is not null;
            _pending = null;
        }
        if (had && !keepRegistration && !Intercept.IsRunning)
            _registry.Unregister(XplmIndex);
        return had;
    }

    private void ClearPendingCruise()
    {
        lock (_pendingGate) _pendingCruise = null;
    }

    private void ClearAllPending()
    {
        ClearPendingCruise();
        ClearInterceptPending(keepRegistration: false);
    }

    private void Remember(string line)
    {
        lock (_logGate)
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > MaxRecentLog) _recentLog.Dequeue();
        }
        ActionLogged?.Invoke(XplmIndex, line);
    }
}
