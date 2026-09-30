using AICopilotCore.Domain.Agents;

namespace AICopilotCore.Domain;

// Foco de la UI: "a quien van las ordenes". Nada mas.
//
// Cada avion (AircraftAgent, en AircraftWorld) lleva su propia logica y sigue
// a lo suyo aunque el foco cambie. Todos admiten las mismas ordenes; aqui solo
// se resuelve el destinatario y se delega:
//   -1     = Global (sin ordenes de vuelo; Abort = todos)
//    0     = avion local
//    1..19 = IA
//
// Las secuencias de cada avion (despegue, maniobras, crucero, intercept) y su
// exclusion mutua viven en AircraftAgent.
public sealed class FlightDirector
{
    private readonly AircraftWorld _world;

    private readonly object _logGate = new();
    private readonly Queue<string> _recentLog = new();
    private const int MaxRecentLog = 200;

    private const string GlobalFocusError = "foco global: elige un avion primero";

    public FlightDirector(AircraftWorld world)
    {
        _world = world;
        _world.ActionLogged += (idx, line) =>
            Remember(idx == 0 ? line : $"[{_world.TryGet(idx)?.Label ?? AircraftWorld.DefaultLabel(idx)}] {line}");
    }

    public AircraftWorld World => _world;

    // --- Foco ---------------------------------------------------------------

    public const int GlobalFocusIndex = -1;

    public int FocusedXplmIndex { get; private set; } = GlobalFocusIndex;
    public string FocusedLabel { get; private set; } = "Global";

    public bool IsGlobalFocus => FocusedXplmIndex == GlobalFocusIndex;

    // Agente en foco (null con foco Global).
    public AircraftAgent? Focused => IsGlobalFocus ? null : _world.Get(FocusedXplmIndex);

    public void SetFocus(int xplmIndex, string label)
    {
        if (xplmIndex < GlobalFocusIndex) xplmIndex = GlobalFocusIndex;
        if (xplmIndex > Connector.Datarefs.OtherPlaneSlots)
            xplmIndex = Connector.Datarefs.OtherPlaneSlots;
        FocusedXplmIndex = xplmIndex;
        if (xplmIndex == GlobalFocusIndex)
            FocusedLabel = string.IsNullOrWhiteSpace(label) ? "Global" : label;
        else
        {
            // Nombre para UI/logs. El overlay en X-Plane lo pone la casilla
            // de Aviones (World.SetLabel); aqui no se fuerza.
            string overlay = _world.GetOverlayLabel(xplmIndex);
            FocusedLabel = !string.IsNullOrWhiteSpace(overlay)
                ? overlay
                : (string.IsNullOrWhiteSpace(label)
                    ? AircraftWorld.DefaultLabel(xplmIndex)
                    : label);
        }
    }

    // --- Estado del avion en foco (vacio con foco Global) ----------------------

    public bool IsInterceptPending => Focused?.IsInterceptPending ?? false;
    public bool IsRouteCruisePending => Focused?.IsRouteCruisePending ?? false;
    public string PendingInterceptLabel => Focused?.PendingInterceptLabel ?? "";
    public int PendingInterceptIndex => Focused?.PendingInterceptIndex ?? -1;

    public AgentView? FocusedView => Focused?.View();

    // Log agregado de todos los aviones (los de IA llevan prefijo [etiqueta]).
    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_logGate) return _recentLog.ToArray();
        }
    }

    // --- Ordenes: van al avion en foco ----------------------------------------

    public void AbortAll() => _world.AbortAll();

    public void AbortFocused()
    {
        AircraftAgent? a = Focused;
        if (a is null) _world.AbortAll();
        else a.AbortAll();
    }

    public void AbortInterceptMission()
    {
        AircraftAgent? a = Focused;
        if (a is null) _world.AbortInterceptMissions();
        else a.AbortInterceptMission();
    }

    public bool ChangeInterceptStation(InterceptStation station, out string error)
    {
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.ChangeInterceptStation(station, out error);
    }

    public bool StartRoute(TakeoffStyle style, CruiseMode cruiseMode,
                           out string error, out string startedAs)
    {
        startedAs = "";
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.StartRoute(style, cruiseMode, out error, out startedAs);
    }

    public bool StartTakeoff(TakeoffStyle style, out string error)
    {
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.StartTakeoff(style, out error);
    }

    public bool StartCruise(out string error)
    {
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.StartCruise(out error);
    }

    public bool StartManeuver(ManeuverKind kind, bool force, out string error, out string adaptation)
    {
        adaptation = "";
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.StartManeuver(kind, force, out error, out adaptation);
    }

    public bool RequestRouteTurn(ManeuverKind kind, out string error)
    {
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.RequestRouteTurn(kind, out error);
    }

    public bool NudgeRouteAltitude(float deltaFt, out string error)
    {
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.NudgeRouteAltitude(deltaFt, out error);
    }

    // Tipo de vuelo del panel izquierdo: aplica al crucero en marcha o al
    // pendiente tras despegue. Sin ruta activa no hace nada (preferencia UI).
    public bool SetCruiseMode(CruiseMode mode, out string appliedAs)
    {
        appliedAs = "";
        AircraftAgent? a = Focused;
        if (a is null) return false;
        return a.SetCruiseMode(mode, out appliedAs);
    }

    // El avion en foco es el interceptor; xplmIndex es el blanco.
    public bool StartIntercept(int xplmIndex, InterceptStation station, string label,
                               out string error)
    {
        AircraftAgent? a = Focused;
        if (a is null)
        {
            error = GlobalFocusError;
            return false;
        }
        return a.StartIntercept(xplmIndex, station, label, out error);
    }

    // --- Inicios de simulacion (solo Global) -----------------------------------

    public bool PendingGroundIdle => _world.PendingGroundIdle;
    public void ClearPendingGroundIdle() => _world.ClearPendingGroundIdle();

    public bool StartSimulation(SimStart start, out string error)
    {
        if (!IsGlobalFocus)
        {
            error = "solo disponible con foco GLOBAL";
            return false;
        }
        SimStartPlan plan = SimScenarios.Get(start);
        if (!_world.StartSimulation(plan, out error)) return false;
        Remember($"Inicio de simulacion: {plan.Name} ({plan.Label})");
        return true;
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
