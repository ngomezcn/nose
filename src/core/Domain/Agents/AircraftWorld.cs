using AICopilotCore.Connector;

namespace AICopilotCore.Domain.Agents;

// Todos los aviones de la partida (XPLM 0..19). Cada uno es un AircraftAgent
// independiente que se crea bajo demanda (Get). El foco de la UI NO existe
// aqui: solo es "a quien van las ordenes" y vive en FlightDirector.
//
// Tambien es la ITargetSource de las intercepciones (resuelve cualquier
// indice: un agente creado responde con la cinematica de su cuerpo; los demas,
// observando Other*/local_*), y guarda el InterceptRegistry compartido (quien
// intercepta a quien).
//
// Hilos: Tick corre en el hilo del pipe; Get/Start* en el de UI. Los agentes
// viven en un array de referencias publicadas con Volatile (lectura sin lock).
public sealed class AircraftWorld : ITargetSource
{
    public const int MaxPlanes = 1 + Datarefs.OtherPlaneSlots;

    private readonly ConnectorClient _client;
    private readonly Datarefs _d;
    private readonly ControlTuning _tuning;
    private readonly IAircraftBody _localBody;

    private readonly object _gate = new();
    private readonly AircraftAgent?[] _agents = new AircraftAgent?[MaxPlanes];
    private readonly string?[] _labels = new string?[MaxPlanes];
    private readonly bool[] _tickFaultLogged = new bool[MaxPlanes];

    public AircraftWorld(ConnectorClient client, Datarefs datarefs, AircraftControls controls,
                         ControlTuning tuning)
    {
        _client = client;
        _d = datarefs;
        _tuning = tuning;
        _localBody = new LocalAircraftBody(datarefs, controls);
        Registry = new InterceptRegistry();
    }

    public InterceptRegistry Registry { get; }

    // (indice del avion, linea) de cualquier agente. Ver AircraftAgent.ActionLogged.
    public event Action<int, string>? ActionLogged;

    // --- Agentes ------------------------------------------------------------

    public bool IsValidIndex(int xplmIndex) => xplmIndex >= 0 && xplmIndex < MaxPlanes;

    // Creacion perezosa. 0 = ownship (LocalAircraftBody + F14Profile);
    // 1..19 = KinematicAiBody + A330Profile (piloto comercial: alabeo suave).
    public AircraftAgent Get(int xplmIndex)
    {
        if (!IsValidIndex(xplmIndex))
            throw new ArgumentOutOfRangeException(nameof(xplmIndex), "0..19");

        AircraftAgent? a = Volatile.Read(ref _agents[xplmIndex]);
        if (a is not null) return a;

        lock (_gate)
        {
            a = _agents[xplmIndex];
            if (a is not null) return a;

            IFlightProfile profile = xplmIndex == 0
                ? F14Profile.Instance
                : A330Profile.Instance;
            IAircraftBody body = xplmIndex == 0
                ? _localBody
                : new KinematicAiBody(_client, _d, xplmIndex, profile);
            string overlay = _labels[xplmIndex] ?? "";
            string label = overlay.Length > 0 ? overlay : DefaultLabel(xplmIndex);
            a = new AircraftAgent(xplmIndex, label, body, profile, _tuning, this, Registry,
                                  () => _client.IsConnected);
            a.ActionLogged += (idx, line) => ActionLogged?.Invoke(idx, line);
            Volatile.Write(ref _agents[xplmIndex], a);
            return a;
        }
    }

    public AircraftAgent? TryGet(int xplmIndex) =>
        IsValidIndex(xplmIndex) ? Volatile.Read(ref _agents[xplmIndex]) : null;

    // Agentes ya creados (0..19 en orden).
    public IEnumerable<AircraftAgent> Active
    {
        get
        {
            for (int i = 0; i < _agents.Length; i++)
            {
                AircraftAgent? a = Volatile.Read(ref _agents[i]);
                if (a is not null) yield return a;
            }
        }
    }

    // Nombre overlay (casilla en Aviones). Vacio = sin etiqueta en pantalla.
    // El Label del agente (logs / UI) cae al default si se limpia.
    public void SetLabel(int xplmIndex, string label)
    {
        if (!IsValidIndex(xplmIndex)) return;
        string t = (label ?? string.Empty).Trim();
        if (t.Length > 32) t = t[..32];
        lock (_gate)
        {
            _labels[xplmIndex] = t;
            AircraftAgent? a = _agents[xplmIndex];
            if (a is not null) a.Label = t.Length > 0 ? t : DefaultLabel(xplmIndex);
        }
    }

    // Nombre overlay crudo ("" si nunca se puso / se limpio). No usa DefaultLabel.
    public string GetOverlayLabel(int xplmIndex)
    {
        if (!IsValidIndex(xplmIndex)) return "";
        lock (_gate) return _labels[xplmIndex] ?? "";
    }

    // 20 nombres para Op.GraphicsConfig (indices XPLM 0..19).
    public string[] SnapshotOverlayLabels()
    {
        var outLabels = new string[MaxPlanes];
        lock (_gate)
        {
            for (int i = 0; i < MaxPlanes; i++)
                outLabels[i] = _labels[i] ?? "";
        }
        return outLabels;
    }

    public static string DefaultLabel(int xplmIndex) => xplmIndex == 0 ? "Local" : $"IA {xplmIndex}";

    // --- Bucle --------------------------------------------------------------

    // Un frame de sim (hilo del pipe): tickea todos los agentes creados. Un fallo
    // en uno no debe parar a los demas (ni al ownship): se avisa una vez y se sigue.
    public void Tick(float dt)
    {
        for (int i = 0; i < _agents.Length; i++)
        {
            AircraftAgent? a = Volatile.Read(ref _agents[i]);
            if (a is null) continue;
            try
            {
                a.Tick(dt);
            }
            catch (Exception ex)
            {
                if (_tickFaultLogged[i]) continue;
                _tickFaultLogged[i] = true;
                ActionLogged?.Invoke(i, $"{a.Label}: error interno en el lazo de control ({ex.GetType().Name}: {ex.Message}).");
            }
        }
    }

    // --- Ordenes globales ---------------------------------------------------

    public void AbortAll()
    {
        foreach (AircraftAgent a in Active) a.AbortAll();
    }

    public void AbortInterceptMissions()
    {
        foreach (AircraftAgent a in Active) a.AbortInterceptMission();
    }

    // Pipe caido: el SafetyGuard del connector ya solto los holds; cada agente
    // olvida su estado.
    public void OnConnectionLost()
    {
        foreach (AircraftAgent a in Active) a.ForgetState();
    }

    // El plugin solto AiControl y holds por su cuenta (OverridesReleased,
    // AircraftReloaded, sesion nueva): los agentes olvidan lo que creian tener.
    public void OnConnectorReleased()
    {
        foreach (AircraftAgent a in Active) a.ForgetState();
    }

    // --- Inicios de simulacion ----------------------------------------------

    private SimStartPlan? _pendingStart;

    // Solo el ownship en tierra necesita el idle de suelo tras recargarse.
    public bool PendingGroundIdle { get; private set; }

    public void ClearPendingGroundIdle() => PendingGroundIdle = false;

    // Inicio de simulacion: no cambia el foco (eso es de quien llama). Para todo,
    // suelta el connector y recoloca ownship + A330 (idx 1) segun el plan.
    public bool StartSimulation(SimStartPlan plan, out string error)
    {
        if (!_client.IsConnected)
        {
            error = "no hay conexion con el plugin";
            return false;
        }

        AbortAll();
        _client.ReleaseAll();
        PendingGroundIdle = plan.UserOnGround;
        _pendingStart = plan;
        _client.PlaceScenario(
            plan.UserLat, plan.UserLon, plan.UserElevMsl, plan.UserHdgTrue, plan.UserSpeedMps,
            plan.AiLat, plan.AiLon, plan.AiElevMsl, plan.AiHdgTrue, plan.AiSpeedMps,
            plan.AiAircraftRelPath, plan.AiOnGround);
        error = "";
        return true;
    }

    // Tras ScenarioReady: da las ordenes iniciales del plan (A330 en ruta vuelo
    // normal; caza interceptando). Devuelve lineas para el log. El foco no se toca.
    public IReadOnlyList<string> CompletePendingStart()
    {
        var lines = new List<string>();
        SimStartPlan? plan = _pendingStart;
        _pendingStart = null;
        if (plan is null || !_client.IsConnected) return lines;

        const string aiLabel = "Airbus A330";
        SetLabel(1, aiLabel);

        if (plan.AiRoute)
        {
            if (Get(1).StartRoute(TakeoffStyles.Relaxed, CruiseModes.Normal,
                                  out string err, out string startedAs))
                lines.Add($"{aiLabel}: {startedAs}");
            else
                lines.Add($"{aiLabel}: ruta rechazada ({err}).");
        }

        else if (plan.AiOnGround)
        {
            // Sin orden de vuelo: la tomamos igualmente para que se quede
            // exactamente donde el connector la puso (parada, freno puesto).
            Get(1).Body.ApplyGroundIdle();
            lines.Add($"{aiLabel}: parado en pista.");
        }

        if (plan.UserIntercept)
        {
            if (Get(0).StartIntercept(1, plan.UserStation, aiLabel, out string err,
                                      alreadyFlying: !plan.UserOnGround))
                lines.Add($"Interceptor: interceptando a {aiLabel} ({InterceptCatalog.Get(plan.UserStation).Label.ToLowerInvariant()}).");
            else
                lines.Add($"Interceptor: interceptacion rechazada ({err}).");
        }
        return lines;
    }

    // --- ITargetSource ------------------------------------------------------

    public bool TryCapture(int xplmIndex, out TargetSnapshot snapshot)
    {
        snapshot = default;
        if (!IsValidIndex(xplmIndex)) return false;

        AircraftAgent? a = Volatile.Read(ref _agents[xplmIndex]);
        if (a is not null)
        {
            if (!a.Body.TryGetKinematics(out Kinematics k)) return false;
            snapshot = TargetSnapshot.FromKinematics(xplmIndex, k);
            return snapshot.Valid;
        }

        // Sin agente: se observa (local_* para 0, planeN_* para 1..19).
        snapshot = TargetSnapshot.Capture(_d, xplmIndex);
        return snapshot.Valid;
    }

    public void Watch(int xplmIndex) { if (xplmIndex >= 1) _d.WatchPlane(xplmIndex - 1); }
    public void Unwatch(int xplmIndex) { if (xplmIndex >= 1) _d.UnwatchPlane(xplmIndex - 1); }
}
