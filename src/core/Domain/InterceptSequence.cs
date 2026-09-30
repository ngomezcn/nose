using AICopilotCore.Connector;
using AICopilotCore.Domain.Agents;

namespace AICopilotCore.Domain;

public enum InterceptPhase
{
    Idle,
    // Despegue combate propio antes de perseguir (solo UI / orquestacion;
    // InterceptSequence no vuela esta fase: la vuela TakeoffSequence).
    OwnTakeoff,
    // El blanco sigue en tierra: se vuela nivelado y se espera a que despegue.
    WaitingTakeoff,
    // Lejos: rumbo de colision, gas a fondo, sin mirar la separacion fina.
    Pursuit,
    // Cerca: se administra el exceso de velocidad para no pasarse de largo.
    Closing,
    // Encima: formacion, velocidad igualada y correcciones pequenas.
    Station,
    // El blanco ha desaparecido del listado o no manda posicion.
    Lost,
}

// Interceptacion militar de otro avion de la partida: ir a buscarlo lo mas
// rapido posible y quedarse pegado a el en la posicion que se haya pedido
// (por defecto, detras y un poco por encima).
//
// Es la tercera secuencia del core, al lado de TakeoffSequence (guion de
// despegue) y ManeuverSequence (una accion del catalogo). Las tres escriben
// sobre los mismos overrides, asi que son mutuamente excluyentes: quien
// arranca una corta las otras (lo hace el shell).
//
// NOTA (rehecho): el guiado ya no lo decide este fichero sino
// InterceptPlanner.cs, en DOS etapas: Transito al punto de reunion (RP, 3 km
// detras del blanco sobre su cola, apuntando al RP PREDICHO para cortar la
// curva) y Formacion (copiar velocidad y cerrar suave sobre la estacion). Las
// fases de abajo son el vocabulario con el que el lazo cambia limites y
// autothrottle.
//
// -- Las tres fases, y por que son tres ---------------------------------
//
// Un solo lazo "vete a ese punto" no sirve para todo el problema. A 20 km
// lo unico que importa es la geometria de colision y llegar antes de que se
// escape; a 200 m lo unico que importa es no adelantarlo. Son dos
// problemas distintos con dos soluciones distintas, y pegarlas con
// histeresis es mas honesto que una unica ganancia de compromiso que hace
// las dos cosas mal:
//
//   Pursuit  -- se apunta a donde ESTARA el blanco (lead), no a donde esta.
//               Persecucion pura = llegar siempre por detras y por fuera.
//               Gas al maximo que permita la VNE, alabeo hasta el limite de
//               G utilizable, sin flaps.
//   Closing  -- la velocidad de acercamiento se raciona con la distancia
//               que queda (v = sqrt(2*a*d)): es la misma cuenta de un
//               frenado, y es lo que evita el clasico "pasar silbando por
//               delante del blanco" y tener que dar la vuelta.
//   Station  -- ya no se persigue un punto, se COPIA una velocidad: la del
//               blanco, mas una correccion pequena proporcional a lo
//               retrasado o adelantado que se va. Las correcciones laterales
//               salen por rumbo y las verticales por V/S.
//
// -- El problema de los blancos lentos ----------------------------------
//
// Un F-14 no vuela a 110 kt. Si el blanco es una avioneta, "igualar su
// velocidad" no es una orden que se pueda cumplir limpia, y hay tres
// herramientas, en este orden:
//
//   1. Flaps: bajan la velocidad de perdida ~18% y con ella el suelo de
//      velocidad segura. Salen solos cuando la velocidad pedida se acerca a
//      ese suelo, y solo por debajo de su limite de extension.
//   2. Aerofrenos: para QUITAR velocidad deprisa sin bajar el morro, que es
//      justo lo que hace falta en la fase de acercamiento.
//   3. Serpenteo: si ni con flaps se puede volar tan despacio, se vuela al
//      minimo seguro y se alarga el camino zigzagueando alrededor de la
//      linea del blanco. cos(angulo) = velocidad del blanco / la nuestra:
//      con eso, el avance NETO iguala al suyo aunque el avion siga volando
//      40 kt mas rapido. Es lo que hace un interceptor real con un blanco
//      lento, y es la unica solucion que no acaba en perdida.
public sealed class InterceptSequence
{
    // Avion que INTERCEPTA (sensores + actuadores) y fuente de los blancos
    // (cualquier indice XPLM 0..19). Cada instancia lleva su propio estado,
    // plan y memoria del planner: no hay estaticos mutables compartidos.
    private readonly IAircraftBody _body;
    private readonly ITargetSource _targets;
    private readonly InterceptRegistry? _registry;
    private readonly ControlTuning _tuning;
    private readonly InterceptTrace _trace;
    // Indice del blanco que tenemos suscrito a 60 Hz (-1 = ninguno).
    private int _watchedIdx = -1;

    // Mismo candado y misma razon que en las otras dos secuencias: Start()/
    // Abort() los llama la UI y Update() el hilo de lectura del pipe.
    private readonly object _gate = new();

    private readonly Pid _pitchPid = new(PitchKp, PitchKi, PitchKd, -1f, 1f);
    private readonly Pid _bankPid = new(BankKp, BankKi, BankKd, -1f, 1f);
    // Cascada V/S (fpm) -> correccion de morro (deg) sobre el morro que ya
    // sale del feedforward de trayectoria. Limite pequeno a proposito: es un
    // ajuste fino, no el que manda.
    private readonly Pid _vsPid = new(VsKp, VsKi, VsKd, -VsTrimLimitDeg, VsTrimLimitDeg);
    private readonly Pid _speedPid = new(SpeedKp, SpeedKi, SpeedKd, 0f, 1f);

    private readonly SlewLimiter _pitchTargetRamp = new(12f);
    private readonly SlewLimiter _bankTargetRamp = new(45f);
    private readonly SlewLimiter _flapRamp = new(FlapRatePerSec);
    private readonly SlewLimiter _speedbrakeRamp = new(SpeedbrakeRatePerSec);

    // Planificador de aproximacion (InterceptPlanner.cs): decide rumbo,
    // velocidad, fase y ruta; este lazo solo lo VUELA con suavidad. Corre a
    // PlanPeriodSec (cada llamada simula decenas de candidatos por delante) y
    // entre llamadas se reutiliza el ultimo plan.
    private readonly TurnRateEstimator _turnEst = new();
    private bool _insideSafety;
    private readonly SlewLimiter _iasCmdRamp = new(IasCmdRateKtPerSec);
    private PlannerMemory? _plannerMem;
    private InterceptPlan? _plan;
    private float _planTimer = 99f;
    private float _phaseDwell;
    private float _trackFiltDeg = float.NaN;
    // Red de seguridad del marco local: distancia plana al blanco del tick
    // anterior, para detectar saltos imposibles (ver CheckRangeJump).
    private double _prevFlatM = double.NaN;

    private int _targetIndex = -1;
    private string _targetLabel = "";
    // Ultimo puesto resuelto: detecta cambios en caliente (deslizadores).
    private (float Aft, float Right, float Up) _lastStation;
    private bool _haveLastStation;
    private float _stationLogTimer;

    private InterceptPhase _phase = InterceptPhase.Idle;
    private float _statusLogTimer;
    private float _settledElapsed;
    private bool _announced;
    private float _gWarnTimer;
    private float _lowAglWarnTimer;
    // True si esta mision empezo con despegue combate propio (checklist).
    private bool _didOwnTakeoff;

    // Cambio de estacion en formacion: se pierde un poco de velocidad
    // respecto al blanco y se recupera al llegar al nuevo puesto.
    private bool _repositioning;

    // Serpenteo: +1 = desviado a la derecha de la linea del blanco.
    private int _weaveSign = 1;
    private float _weaveDeg;
    // Velocidad de avance pedida sobre la linea del blanco (kt de suelo). La
    // calcula la ley de velocidad y la consume el serpenteo.
    private double _desiredAlongGsKt;

    // Mandos "lentos" (flaps/aerofrenos): se mandan por Set, asi que se
    // guardan aqui para no repetir el mismo valor 60 veces por segundo.
    private float _flapCmd;
    private float _speedbrakeCmd;
    private float _flapSent = float.NaN;
    private float _speedbrakeSent = float.NaN;
    private float _slowWriteTimer;
    private bool _flapsAreOurs;

    // registry == null: sin control de intercepciones cruzadas.
    public InterceptSequence(IAircraftBody body, ITargetSource targets, ControlTuning tuning,
                             InterceptRegistry? registry = null)
    {
        _body = body;
        _targets = targets;
        _tuning = tuning;
        _registry = registry;
        _trace = new InterceptTrace(body.XplmIndex);
    }

    // Indice XPLM del avion que intercepta (0 = ownship).
    public int OwnIndex => _body.XplmIndex;

    public event Action<string>? ActionLogged;

    public bool IsRunning => _phase != InterceptPhase.Idle;
    public InterceptPhase Phase => _phase;
    public int TargetIndex => _targetIndex;
    public bool IsRepositioning => _repositioning;
    public string TargetLabel => _targetLabel;

    // --- Lo ultimo medido/pedido, para la telemetria y el panel -----------
    public float LastPitchTarget { get; private set; } = float.NaN;
    public float LastBankTarget { get; private set; } = float.NaN;
    public float LastIasTarget { get; private set; } = float.NaN;
    public float LastVsTarget { get; private set; } = float.NaN;
    public float LastThrottleCmd { get; private set; }
    public float LastFlapCmd => _flapCmd;
    public float LastSpeedbrakeCmd => _speedbrakeCmd;

    public double RangeM { get; private set; } = double.NaN;
    public double SeparationM { get; private set; } = double.NaN;
    public double ClosureKt { get; private set; } = double.NaN;
    public double AlongM { get; private set; } = double.NaN;
    public double CrossM { get; private set; } = double.NaN;
    public double VerticalM { get; private set; } = double.NaN;
    public double TargetSpeedKt { get; private set; } = double.NaN;
    // Lo que el planner pidio y el lazo persigue, para la caja negra.
    public float LastDesiredTrack { get; private set; } = float.NaN;
    public float LastTrackErr { get; private set; } = float.NaN;
    public string TelemetryText { get; private set; } = "";
    public TargetAirState TargetState { get; private set; } = TargetAirState.Unknown;
    public bool Weaving => _weaveDeg > 0.5f;
    public bool DidOwnTakeoff => _didOwnTakeoff;

    public static string PhaseName(InterceptPhase p) => p switch
    {
        InterceptPhase.OwnTakeoff => "Despegue combate",
        InterceptPhase.WaitingTakeoff => "Esperando telemetria del blanco",
        InterceptPhase.Pursuit => "Persecucion (cierre rapido)",
        InterceptPhase.Closing => "Acercamiento",
        InterceptPhase.Station => "En formacion",
        InterceptPhase.Lost => "Blanco perdido",
        _ => "En espera",
    };

    public string PhaseText
    {
        get
        {
            if (_phase == InterceptPhase.Idle) return "Sin interceptacion activa";
            string label = string.IsNullOrEmpty(_targetLabel) ? $"IA {_targetIndex}" : _targetLabel;
            return $"Interceptar {label} · {PhaseName(_phase)} [{_tuning.Station.Summary()}]";
        }
    }

    // --- Arranque y parada --------------------------------------------------

    // En tierra (o demasiado lento) el director debe encadenar un despegue
    // combate antes de llamar a Start. Publico para que FlightDirector lo
    // consulte sin duplicar el umbral de IAS.
    public bool NeedsOwnTakeoff()
    {
        lock (_gate)
            return _body.State.OnGround || _body.State.IasKt < MinOwnIasKt;
    }

    // Devuelve false y el motivo si la interceptacion no se puede pedir ahora
    // mismo. Se niega en vez de intentarlo a medias: enganchar los overrides
    // de cabeceo y alabeo con el avion rodando por la pista es exactamente el
    // tipo de cosa que deja al usuario sin poder pilotar preguntandose por que.
    // afterOwnTakeoff: el director ya hizo el despegue combate; no re-exige
    // el umbral de IAS (puede estar justo por debajo un instante).
    public bool Start(int xplmIndex, string label, out string refusal,
                      bool afterOwnTakeoff = false)
    {
        lock (_gate)
        {
            if (xplmIndex < 0 || xplmIndex > Datarefs.OtherPlaneSlots)
            {
                refusal = "ese avion no esta en los 20 slots de X-Plane (0..19)";
                return false;
            }
            if (xplmIndex == _body.XplmIndex)
            {
                refusal = "un avion no puede interceptarse a si mismo";
                return false;
            }
            _body.Sense();
            if (_body.State.OnGround)
            {
                refusal = _body.IsLocal
                    ? "tu avion esta en tierra: despega primero"
                    : $"{OwnName} esta en tierra: que despegue primero";
                return false;
            }
            if (!afterOwnTakeoff && _body.State.IasKt < MinOwnIasKt)
            {
                refusal = $"hace falta al menos {MinOwnIasKt:0} kt para maniobrar " +
                          $"(vas a {_body.State.IasKt:0} kt)";
                return false;
            }
            if (_registry is not null &&
                !_registry.TryRegister(_body.XplmIndex, xplmIndex, out refusal))
                return false;
            ReleaseWatch();

            _targetIndex = xplmIndex;
            _targetLabel = label;
            _haveLastStation = false;
            _didOwnTakeoff = afterOwnTakeoff;

            _pitchPid.Reset();
            _bankPid.Reset();
            _vsPid.Reset();
            _speedPid.SeedTrim(_body.ThrottleReadback);
            _pitchTargetRamp.Reset(_body.State.PitchDeg);
            _bankTargetRamp.Reset(_body.State.BankDeg);
            _flapRamp.Reset(_body.FlapRatio);
            _speedbrakeRamp.Reset(_body.SpeedbrakeRatio);
            _flapCmd = _body.FlapRatio;
            _speedbrakeCmd = _body.SpeedbrakeRatio;
            _flapSent = _speedbrakeSent = float.NaN;
            _flapsAreOurs = false;
            _weaveSign = 1;
            _weaveDeg = 0f;
            _turnEst.Reset();
            _plannerMem = null;
            _trace.Begin();
            _plan = null;
            _planTimer = 99f;
            _phaseDwell = 0f;
            _trackFiltDeg = float.NaN;
            _iasCmdRamp.Reset(_body.State.IasKt);
            _announced = false;
            _settledElapsed = 0f;
            _statusLogTimer = StatusLogSeconds;
            _gWarnTimer = _lowAglWarnTimer = 0f;

            // La posicion del blanco entra en un PID: con datos a 10 Hz el
            // avion persigue donde estaba, no donde esta.
            _targets.Watch(xplmIndex);
            _watchedIdx = xplmIndex;

            _body.EnablePitchOverride();
            _body.EnableRollOverride();
            _body.EnableThrottleOverride();

            SetPhase(InterceptPhase.Pursuit);
            FlightState own = _body.State;
            string prelude = afterOwnTakeoff
                ? $"Inicio: interceptar {label} tras despegue combate"
                : $"Inicio: interceptar {label}";
            LogAction(BlackBoxSnap.Join(
                prelude,
                StationSummary(),
                InterceptConfigExtras(),
                BlackBoxSnap.Of(own)));
            _repositioning = false;
            refusal = "";
            return true;
        }
    }

    // El puesto se lee de ControlTuning cada frame, asi que moverlo (azimut /
    // distancia / altura) con la interceptacion en marcha lo aplica en
    // caliente. Aqui solo se detecta el cambio para preparar la transicion:
    // formacion (no un re-transito completo) y rampa de velocidad algo mas
    // viva. Se rearma el planner y se loguea como mucho cada 2 s para que
    // arrastrar un deslizador no inunde el registro.
    private void TrackStationChange((float Aft, float Right, float Up) st, float dt)
    {
        _stationLogTimer += dt;
        if (!_haveLastStation)
        {
            _lastStation = st;
            _haveLastStation = true;
            return;
        }
        float d = MathF.Abs(st.Aft - _lastStation.Aft) + MathF.Abs(st.Right - _lastStation.Right) +
                  MathF.Abs(st.Up - _lastStation.Up);
        if (d < 0.5f) return;
        _lastStation = st;

        bool near = _phase is InterceptPhase.Station or InterceptPhase.Closing ||
                    (!double.IsNaN(RangeM) && RangeM < RepositionArmRangeM);
        if (near)
        {
            _repositioning = true;
            _announced = false;
            _settledElapsed = 0f;
            if (_plannerMem is not null)
                _plannerMem = _plannerMem with { Stage = 1, SideLocked = false };
            if (_phase == InterceptPhase.Station)
                SetPhase(InterceptPhase.Closing, "cambio de puesto");
        }
        _plan = null;
        _planTimer = 99f;
        if (_stationLogTimer >= 2f)
        {
            _stationLogTimer = 0f;
            LogAction(BlackBoxSnap.Join(
                $"Interceptacion: puesto → {_tuning.Station.Summary()}",
                near ? "transicion: cruce rapido por detras del blanco con zona segura"
                     : "en persecucion: el planificador apunta ya al puesto nuevo",
                InterceptLiveExtras()));
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (_phase == InterceptPhase.Idle) return;
            FlightState own = _body.State;
            LogAction(BlackBoxSnap.Join(
                $"Fin: interceptacion abortada ({_targetLabel}) — control manual",
                InterceptLiveExtras(),
                _flapsAreOurs
                    ? $"flaps se quedan al {_flapCmd * 100f:0}% (los puse yo)"
                    : null,
                BlackBoxSnap.Of(own)));
            // El aerofreno si se recoge: es el unico de los dos que no
            // sostiene al avion, y dejarlo fuera solo cuesta velocidad.
            _body.SetSpeedbrake(0f);
            _body.ReleaseAllOverrides();
            ReleaseTarget();
            ResetOutputs();
            _phase = InterceptPhase.Idle;
        }
    }

    // Se pierde el pipe: el SafetyGuard del connector ya habra soltado los
    // overrides por su cuenta, aqui solo hay que olvidarse del estado.
    public void OnConnectionLost()
    {
        lock (_gate)
        {
            if (_phase == InterceptPhase.Idle) return;
            _body.ForgetOverrideState();
            // El blanco vuelve al ritmo de pantalla. Con el pipe caido esto
            // solo apunta el divisor nuevo, que es justo lo que hace falta:
            // el cliente lo replica al reconectar.
            ReleaseTarget();
            ResetOutputs();
            _phase = InterceptPhase.Idle;
        }
    }

    // Suelta la suscripcion a 60 Hz del blanco (refcount en Datarefs).
    private void ReleaseWatch()
    {
        if (_watchedIdx < 0) return;
        _targets.Unwatch(_watchedIdx);
        _watchedIdx = -1;
    }

    // Fin de mision (abortada, pipe caido): suelta watch, registro y traza.
    private void ReleaseTarget()
    {
        ReleaseWatch();
        _registry?.Unregister(_body.XplmIndex);
        _trace.Dispose();
    }

    private string OwnName => _body.XplmIndex == 0 ? "Local (idx 0)" : $"IA {_body.XplmIndex}";

    // Si la distancia plana al blanco cambia en un tick mas de lo que permiten
    // las velocidades de los dos, una de las dos posiciones cambio de marco o
    // se teletransporto; no es fisica. El connector compensa los saltos de
    // origen de X-Plane (connector/OriginWatch.h), asi que esto no deberia
    // saltar nunca: si salta, la hipotesis del origen no lo explica y hacen
    // falta ESTAS posiciones para saber quien se movio.
    private void CheckRangeJump(double flatM, float dt, double ownX, double ownZ,
                                double ownGsMps, in TargetSnapshot t)
    {
        double prev = _prevFlatM;
        _prevFlatM = flatM;
        if (double.IsNaN(prev) || dt <= 0f) return;

        double allowed = 1000.0 + 3.0 * (ownGsMps + t.GroundSpeedMps) * Math.Max(dt, 0.1);
        if (Math.Abs(flatM - prev) <= allowed) return;

        LogAction($"AVISO: salto de rango imposible {prev:0} -> {flatM:0} m en {dt:0.00} s " +
                  $"(permitido {allowed:0}). propio x={ownX:0} z={ownZ:0} v={ownGsMps:0} m/s; " +
                  $"blanco x={t.X:0} z={t.Z:0} v={t.GroundSpeedMps:0} m/s. " +
                  "Sin 'origen local desplazado' antes, no fue un salto de origen de X-Plane.");
    }

    private void ResetOutputs()
    {
        _targetIndex = -1;
        _targetLabel = "";
        _didOwnTakeoff = false;
        _repositioning = false;
        LastPitchTarget = LastBankTarget = LastIasTarget = LastVsTarget = float.NaN;
        RangeM = SeparationM = ClosureKt = AlongM = CrossM = VerticalM = double.NaN;
        TargetSpeedKt = double.NaN;
        LastDesiredTrack = LastTrackErr = float.NaN;
        _prevFlatM = double.NaN;
        TelemetryText = "";
        _plan = null;
        TargetState = TargetAirState.Unknown;
        _weaveDeg = 0f;
        _flapCmd = _speedbrakeCmd = 0f;
        _flapsAreOurs = false;
    }

    // --- El lazo ------------------------------------------------------------

    public void Update(float dt)
    {
        lock (_gate) { UpdateLocked(dt); }
    }

    private void UpdateLocked(float dt)
    {
        if (_phase == InterceptPhase.Idle || dt <= 0f) return;

        _statusLogTimer += dt;
        _gWarnTimer += dt;
        _lowAglWarnTimer += dt;
        _slowWriteTimer += dt;
        _planTimer += dt;
        _phaseDwell += dt;

        _body.Sense();
        FlightState st = _body.State;
        // Un frame sin telemetria util no se "arregla" mandando un mando
        // calculado con NaN: se deja en pie lo del frame anterior, que los
        // holds del connector siguen manteniendo.
        if (!st.IsUsable) return;
        if (!_body.TryGetKinematics(out Kinematics ownK)) return;

        double ownX = ownK.X, ownY = ownK.Y, ownZ = ownK.Z;
        double ownVx = ownK.Vx, ownVz = ownK.Vz;
        double ownVy = ownK.Vy;
        double ownGsMps = Math.Sqrt(ownVx * ownVx + ownVz * ownVz);
        double ownGsKt = ownGsMps * TargetSnapshot.MpsToKnots;
        float ownTrack = ownGsMps < 5.0
            ? st.HeadingDeg
            : Pid.NormalizeAngleDeg360((float)(Math.Atan2(ownVx, -ownVz) * F14Aero.Rad2Deg));

        if (!_targets.TryCapture(_targetIndex, out TargetSnapshot t) || !t.Valid)
        {
            if (_phase != InterceptPhase.Lost)
            {
                SetPhase(InterceptPhase.Lost);
                LogAction($"Interceptacion: {_targetLabel} ha dejado de mandar posicion " +
                          "(se ha ido de la partida?). Manteniendo vuelo nivelado.");
            }
            FlyLevelHold(st, dt);
            return;
        }
        if (_phase == InterceptPhase.Lost)
        {
            LogAction($"Interceptacion: {_targetLabel} vuelve a estar en pantalla.");
            SetPhase(InterceptPhase.Pursuit);
        }

        // Elevacion del terreno estimada con NUESTRA propia posicion: la Y
        // local menos nuestra AGL. Solo vale si el blanco esta cerca -- a
        // 100 km puede haber una sierra de por medio -- asi que de lejos el
        // clasificador se queda con la velocidad, que no depende del relieve.
        double terrainY = ownY - (double)_body.AglMeters;
        double flatM = Math.Sqrt((t.X - ownX) * (t.X - ownX) + (t.Z - ownZ) * (t.Z - ownZ));
        CheckRangeJump(flatM, dt, ownX, ownZ, ownGsMps, t);
        TargetState = t.Classify(terrainY, flatM < TerrainReferenceRangeM);
        TargetSpeedKt = t.GroundSpeedKt;

        // --- Blanco en tierra / estado dudoso --------------------------------
        // En tierra ya no se orbita esperando: se forma encima a ~2 km de
        // altura con el mismo puesto (y los mismos cambios de estacion) que
        // en vuelo. Solo "sin determinar" se queda en espera nivelada.
        if (TargetState == TargetAirState.Unknown)
        {
            if (_phase != InterceptPhase.WaitingTakeoff)
            {
                SetPhase(InterceptPhase.WaitingTakeoff);
                LogAction($"Interceptacion: estado de {_targetLabel} sin determinar. " +
                          "Mantengo vuelo nivelado.");
            }
            MeasureOnly(t, ownX, ownY, ownZ, ownVx, ownVy, ownVz);
            FlyLevelHold(st, dt, HoldingBankDeg);
            return;
        }
        bool targetOnGround = TargetState == TargetAirState.OnGround;
        if (targetOnGround && _phase == InterceptPhase.WaitingTakeoff)
        {
            // Salimos de la espera antigua (orbita) hacia persecucion/formacion
            // sobre el puesto a 2 km.
            LogAction($"Interceptacion: {_targetLabel} en tierra — formacion a " +
                      $"{GroundFormationAltM:0} m de altura.");
            SetPhase(InterceptPhase.Pursuit);
        }
        else if (targetOnGround && _statusLogTimer >= StatusLogSeconds &&
                 _phase is InterceptPhase.Pursuit or InterceptPhase.Closing
                          or InterceptPhase.Station)
        {
            _statusLogTimer = 0f;
            LogAction($"Interceptacion: {_targetLabel} sigue en tierra " +
                      $"({t.GroundSpeedKt:0} kt, a {flatM * TargetSnapshot.MetersToNm:0.0} NM) — " +
                      $"puesto a +{GroundFormationAltM:0} m.");
        }
        if (!targetOnGround && _phase == InterceptPhase.WaitingTakeoff)
        {
            LogAction($"Interceptacion: {_targetLabel} esta en el aire " +
                      $"({t.GroundSpeedKt:0} kt). Voy a por el.");
            SetPhase(InterceptPhase.Pursuit);
        }

        // --- Geometria ------------------------------------------------------
        (float aft, float right, float up) = _tuning.ResolveStation();
        TrackStationChange((aft, right, up), dt);
        // Blanco parado: el puesto de formacion se eleva a ~2 km para que el
        // interceptor no intente "formar" a ras de pista. El offset del
        // catalogo (p. ej. Abajo, UpFactor negativo) no puede dejar el puesto
        // por debajo de ese minimo.
        if (targetOnGround)
        {
            up += GroundFormationAltM;
            up = Math.Max(up, GroundFormationAltM);
        }

        // Dos geometrias: la de AHORA, que es la que mide y decide de fase, y
        // la adelantada, que es la que se vuela. Mezclarlas haria que en plena
        // persecucion la "distancia" fuera la del punto futuro (kilometros de
        // mas) y las transiciones no llegaran nunca.
        InterceptGeometry now = InterceptGeometry.Solve(ownX, ownY, ownZ, t, aft, right, up, 0.0);

        RangeM = now.RangeM;
        SeparationM = now.SeparationM;
        AlongM = now.AlongM;
        CrossM = now.CrossM;
        VerticalM = now.UpM;
        ClosureKt = ClosureRateKt(t, ownX, ownY, ownZ, ownVx, ownVy, ownVz, now.SeparationM);
        // Zona de seguridad (cilindro alrededor del AVION blanco).
        _insideSafety =
            Math.Sqrt((t.X - ownX) * (t.X - ownX) + (t.Z - ownZ) * (t.Z - ownZ)) < _tuning.SafeHorizontalM &&
            Math.Abs(t.Y - ownY) < _tuning.SafeVerticalM;

        // --- Plan de aproximacion -----------------------------------------
        double tgtTurnDegS = _turnEst.Update(t.TrackDeg, dt, t.GroundSpeedMps);
        float minSafeClean0 = F14Aero.StallIasKt(st.WeightLb) * F14Aero.StallMarginFactor;
        float maxIas0 = MathF.Min(_tuning.MaxTargetIasKt, st.VneKt * VneMarginFactor);
        double iasPerGs0 = Math.Clamp(ownGsKt > 30.0 ? st.IasKt / ownGsKt : 1.0, 0.3, 1.6);
        (float maxBank, float maxVsFpm, _) = PhaseLimits(_phase);
        float gBudget = MathF.Min(st.UsableG, _tuning.GSoftHigh * GBudgetFraction);
        maxBank = MathF.Min(maxBank, F14Aero.BankForLoadFactorDeg(gBudget));

        if (_plan == null || _planTimer >= PlanPeriodSec)
        {
            OwnLimits limits = OwnLimits.F14 with
            {
                // Con flaps fuera la perdida baja: el suelo de velocidad que
                // se planifica es el de flaps completos; la ley de flaps los
                // saca cuando la velocidad pedida se acerca al suelo limpio.
                VminGsMps = minSafeClean0 * (1f - FlapStallReduction) / iasPerGs0 / TargetSnapshot.MpsToKnots,
                VmaxGsMps = maxIas0 / iasPerGs0 / TargetSnapshot.MpsToKnots,
                MaxG = Math.Max(gBudget, 2.0),
                MaxBankDeg = maxBank,
            };
            _plan = InterceptPlanner.Plan(new InterceptPlanInput
            {
                OwnX = ownX, OwnY = ownY, OwnZ = ownZ,
                OwnVx = ownVx, OwnVy = ownVy, OwnVz = ownVz,
                TgtX = t.X, TgtY = t.Y, TgtZ = t.Z,
                TgtVx = t.Vx, TgtVy = t.Vy, TgtVz = t.Vz,
                TgtHeadingDeg = t.HeadingDeg,
                TgtTurnRateDegPerS = tgtTurnDegS,
                AftM = aft, RightM = right, UpM = up,
                SafeHorizM = _tuning.SafeHorizontalM, SafeVertM = _tuning.SafeVerticalM,
                Limits = limits,
                IasPerGs = iasPerGs0,
                DtSec = _planTimer,
                Memory = _plannerMem,
            });
            _plannerMem = _plan.Memory;
            _planTimer = 0f;
        }
        InterceptPlan plan = _plan;

        // La fase la sugiere el planner; se exige un minimo de permanencia
        // para que un candidato que baila en el umbral no resiembre el
        // autothrottle varias veces por segundo.
        if (plan.SuggestedPhase != _phase && _phaseDwell >= PhaseMinDwellSec)
            SetPhase(plan.SuggestedPhase,
                $"planner: {plan.Regime}, a {BlackBoxSnap.F(now.RangeM, "0")} m, " +
                $"ETA {(plan.Reaches ? BlackBoxSnap.F(plan.EtaSec, "0") + " s" : "?")}");
        (maxBank, maxVsFpm, _) = PhaseLimits(_phase);
        maxBank = MathF.Min(maxBank, F14Aero.BankForLoadFactorDeg(gBudget));

        // --- 1. Velocidad: es la que decide si hacen falta flaps y serpenteo -
        float iasCmd = SolveSpeedCommand(st, t, now, plan, ownGsKt, dt, out bool tooSlowForUs);

        // --- 2. Rumbo y alabeo ----------------------------------------------
        float desiredRaw = SolveTrack(t, now, plan, tooSlowForUs, ownGsKt);
        // Paso bajo del rumbo pedido: el planner recalcula a 10 Hz y el zigzag
        // del serpenteo cambia de signo de golpe; sin filtro cada salto llega
        // entero al alabeo.
        if (float.IsNaN(_trackFiltDeg)) _trackFiltDeg = ownTrack;
        float filtStep = Pid.NormalizeAngleDeg180(desiredRaw - _trackFiltDeg);
        _trackFiltDeg = Pid.NormalizeAngleDeg360(
            _trackFiltDeg + filtStep * (1f - MathF.Exp(-dt / TrackFilterTauSec)));
        float desiredTrack = _trackFiltDeg;
        float trackErr = Pid.NormalizeAngleDeg180(desiredTrack - ownTrack);
        LastDesiredTrack = desiredTrack;
        LastTrackErr = trackErr;

        // Feed-forward del giro del blanco cerca de la estacion (termino de
        // blanco maniobrante del paper): sin el, un blanco que vira a 3 deg/s
        // se escapa por fuera del puesto.
        float bankFf = plan.Regime == InterceptRegime.Station
            ? (float)(Math.Atan(ownGsMps * tgtTurnDegS * F14Aero.Deg2Rad / 9.81) * F14Aero.Rad2Deg)
            : 0f;
        // En formacion el lazo rumbo->alabeo con 1.2 deg/deg era demasiado lento
        // (constante de tiempo ~17 s a 200 m/s: 4 deg de error daban 5 deg de
        // alabeo y un cambio de puesto de 200 m tardaba 35 s). La ganancia se
        // escala con la velocidad para una constante de tiempo fija.
        float trackGain = TrackToBankGain;
        if (plan.Regime is InterceptRegime.Station or InterceptRegime.Formation)
            trackGain = Math.Clamp((float)ownGsMps / (9.81f * FormationTrackTauSec),
                                   TrackToBankGain, FormationTrackGainMax);
        float bankRaw = Math.Clamp(trackGain * trackErr + bankFf, -maxBank, maxBank);
        // Alivio de G: el backstop de las maniobras ABORTA, pero aqui abortar
        // seria soltar el avion en mitad de una persecucion. Se afloja el
        // alabeo, que es de donde sale la G en un viraje, y se avisa.
        if (st.GNormal > _tuning.GSoftHigh)
        {
            bankRaw *= GReliefFactor;
            if (_gWarnTimer >= WarnRepeatSeconds)
            {
                _gWarnTimer = 0f;
                LogAction($"AVISO: G={st.GNormal:0.0} en la persecucion -> aflojo el viraje.");
            }
        }
        // Rampa propia y mas suave que la de las maniobras: 45 deg/s metia
        // 67 deg de alabeo en 1.5 s y con ellos las G raras del log.
        _bankTargetRamp.MaxRate = MathF.Min(_tuning.ManeuverBankRampDegPerSec, InterceptBankRampDegPerSec);
        float bankTarget = _bankTargetRamp.Update(bankRaw, dt);
        LastBankTarget = bankTarget;
        _bankPid.GainScale = AttitudeGainScale(st);
        _body.SetRollInput(_bankPid.Update(bankTarget - st.BankDeg, dt, st.RollRateDegPerSec), dt);

        // --- 3. Vertical ------------------------------------------------------
        float vsCmd = (float)(plan.DesiredVsMps * TargetSnapshot.MpsToFpm);
        // Vertical acoplado al viraje: el ala solo da sustentacion vertical
        // ~cos(alabeo), y con un error de rumbo grande el viraje manda. Sin
        // esto el log mostraba un picado de -9000 fpm en pleno viraje de 180
        // con G negativa y el viraje invertido.
        float bankCos = Math.Clamp(MathF.Cos(st.BankDeg * F14Aero.Deg2Rad), 0f, 1f);
        maxVsFpm *= MathF.Max(bankCos, 0.25f);
        float turnScale = Math.Clamp(1f - (MathF.Abs(trackErr) - 20f) / 40f, 0f, 1f);
        if (vsCmd < 0f) vsCmd *= turnScale;
        vsCmd = Math.Clamp(vsCmd, -maxVsFpm, maxVsFpm);
        // Proteccion de G baja: si el avion ya esta descargado no se le pide
        // bajar mas.
        if (st.GNormal < LowGGuard && vsCmd < st.VsFpm) vsCmd = st.VsFpm;
        // Suelo de terreno: no se sigue a nadie contra el suelo. Si el blanco
        // vuela bajo, se le acompana por encima y se dice.
        if (st.AglFt < _tuning.TerrainFloorAglFt && vsCmd < 0f)
        {
            vsCmd = 0f;
            if (_lowAglWarnTimer >= WarnRepeatSeconds)
            {
                _lowAglWarnTimer = 0f;
                LogAction($"AVISO: {st.AglFt:0} ft sobre el terreno; no bajo mas para seguir " +
                          $"a {_targetLabel}.");
            }
        }
        // Guardia de perdida: pedir ascenso es justo lo que se come la
        // velocidad, asi que cerca del minimo seguro el ascenso se va
        // desvaneciendo en vez de cortarse de golpe. Sin esto, perseguir a un
        // blanco 3.000 ft mas arriba con poca energia acaba en entrada en
        // perdida con el morro arriba y el gas ya a fondo.
        float minSafeClean = F14Aero.StallIasKt(st.WeightLb) * F14Aero.StallMarginFactor;
        float speedMargin = st.IasKt - minSafeClean;
        if (vsCmd > 0f && speedMargin < StallGuardBandKt)
            vsCmd *= Math.Clamp(speedMargin / StallGuardBandKt, 0f, 1f);

        LastVsTarget = vsCmd;
        ApplyPitchForVerticalSpeed(st, vsCmd, dt);

        // --- 4. Motor, flaps y aerofrenos -------------------------------------
        LastIasTarget = iasCmd;
        float throttle = _speedPid.Update(iasCmd - st.IasKt, dt);
        LastThrottleCmd = throttle;
        _body.SetThrottle(throttle, dt);
        FlushSlowControls();

        TelemetryText = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"int rng={now.RangeM:0} sep={now.SeparationM:0} cl={ClosureKt:0} " +
            $"al={now.AlongM:0} cr={now.CrossM:0} vt={now.UpM:0} " +
            $"trkObj={desiredTrack:0.0} trkErr={trackErr:0.0} iasObj={iasCmd:0} vsObj={vsCmd:0} " +
            $"tgtGS={t.GroundSpeedKt:0} tgtTrk={t.TrackDeg:0} tgtTurn={tgtTurnDegS:0.0} " +
            $"reg={plan.Regime} eta={(plan.Reaches ? plan.EtaSec.ToString("0") : "-")} " +
            $"ovt={plan.PredictedOvertakeM:0} minSep={plan.PredictedMinSepM:0} pG={plan.PredictedPeakG:0.0} " +
            $"brk={bankFf:0.0} wv={_weaveDeg:0} side={plan.ApproachSide} lk={(plan.SideLocked ? 1 : 0)} " +
            $"cor={(plan.Corridor ? 1 : 0)} sit={plan.Situation}");

        _trace.Row(dt, plan.Memory.Stage, plan.Regime, _phase, plan.Detouring ? 1 : 0,
            plan.ApproachSide, plan.SideLocked ? 1 : 0, plan.Corridor ? 1 : 0,
            ownX, ownY, ownZ, ownVx, ownVy, ownVz,
            t.X, t.Y, t.Z, t.Vx, t.Vy, t.Vz, t.HeadingDeg, tgtTurnDegS,
            now.RangeM, plan.RendezvousRangeM, now.AlongM, now.CrossM, now.UpM,
            st.IasKt, st.BankDeg, st.GNormal,
            plan.DesiredTrackDeg, desiredTrack, plan.DesiredGsMps, plan.DesiredIasMps,
            iasCmd, vsCmd, bankTarget, plan.PredictedMinSepM, plan.EtaSec);

        // --- 5. Llegada --------------------------------------------------------
        if (_repositioning && now.RangeM < RepositionDoneM &&
            Math.Abs(ClosureKt) < SettledClosureKt)
        {
            _repositioning = false;
            LogAction("Interceptacion: puesto alcanzado — igualo velocidad.");
        }

        if (_phase == InterceptPhase.Station && now.RangeM < CaptureRangeM &&
            Math.Abs(ClosureKt) < SettledClosureKt)
        {
            _settledElapsed += dt;
            if (!_announced && _settledElapsed >= SettleSeconds)
            {
                _announced = true;
                _repositioning = false;
                LogAction(BlackBoxSnap.Join(
                    $"Fin: interceptacion establecida sobre {_targetLabel} — {_tuning.Station.Summary()}",
                    $"{now.SeparationM:0} m sep, blanco {t.GroundSpeedKt:0} kt",
                    tooSlowForUs
                        ? "va demasiado lento: mantengo minimo seguro serpenteando"
                        : null,
                    InterceptLiveExtras(),
                    BlackBoxSnap.Short(st)));
            }
        }
        else
        {
            _settledElapsed = 0f;
        }
    }

    // --- Piezas del lazo ------------------------------------------------------

    // Velocidad indicada que hay que pedirle al motor, y si esa velocidad es
    // mas lenta de lo que este avion puede volar.
    private float SolveSpeedCommand(in FlightState st, in TargetSnapshot t,
                                    in InterceptGeometry now, in InterceptPlan plan,
                                    double ownGsKt, float dt, out bool tooSlowForUs)
    {
        // Techo: ni la VNE del avion cargado ni el tope que el usuario haya
        // puesto en Config. Es lo que impide que "lo mas rapido posible" se
        // convierta en desarmar el avion en un picado.
        float maxIas = MathF.Min(_tuning.MaxTargetIasKt, st.VneKt * VneMarginFactor);

        // Relacion IAS/velocidad de suelo MEDIDA ahora mismo: mete la altura y
        // el viento en la cuenta sin modelar ninguno de los dos. El blanco da
        // su velocidad de suelo, nosotros mandamos IAS.
        double iasPerGs = ownGsKt > 30.0 ? st.IasKt / ownGsKt : 1.0;
        iasPerGs = Math.Clamp(iasPerGs, 0.3, 1.6);
        // Con blanco parado en tierra, GS≈0 no debe convertirse en "iguala 0 kt"
        // ni en un cap de reposicion negativo; el suelo seguro manda mas abajo.
        bool slowGroundTarget = TargetState == TargetAirState.OnGround &&
                                t.GroundSpeedKt < SlowGroundTargetGsKt;
        float targetIasEquivalent = slowGroundTarget
            ? float.NaN
            : (float)(t.GroundSpeedKt * iasPerGs);

        // La velocidad la pide el planner: perfil de frenado con la
        // deceleracion real del avion medida sobre el CAMINO de aproximacion
        // (no sobre la recta), comprobado por adelantado con una simulacion
        // del cierre completo. Aqui solo se suaviza (rampa) y se recorta al
        // envolvente. El zigzag de los blancos lentos avanza sobre la linea
        // del blanco a su misma velocidad.
        _desiredAlongGsKt = t.GroundSpeedKt;
        float planIasKt = (float)(plan.DesiredIasMps * TargetSnapshot.MpsToKnots);
        // Transicion de puesto: rampa algo mas viva para no eternizar el
        // frenado / la recuperacion, pero sin golpe de gas.
        _iasCmdRamp.MaxRate = _repositioning ? RepositionIasRateKtPerSec : IasCmdRateKtPerSec;
        float iasCmd = MathF.Min(maxIas, _iasCmdRamp.Update(planIasKt, dt));

        // Cambio de estacion en formacion: se pierde un poco de velocidad
        // respecto al blanco, se desliza al puesto nuevo y luego se iguala.
        // (Sin tope de IAS bajo el blanco: el planner ya cruza por detras del
        // blanco con zona segura; ese tope dejaba al avion 500 m atras.)

        // Guardia de proximidad: por debajo de esto ya no es una formacion,
        // es un riesgo de colision. Se pide ir mas despacio que el blanco
        // pase lo que pase con la geometria, para abrirse por detras.
        if (_insideSafety)
        {
            if (!float.IsNaN(targetIasEquivalent))
                iasCmd = MathF.Min(iasCmd, targetIasEquivalent - BackOffKt);
            _speedbrakeRamp.MaxRate = SpeedbrakeRatePerSec;
            _speedbrakeCmd = _speedbrakeRamp.Update(1f, dt);
        }

        // --- Flaps: la primera herramienta para volar despacio ---------------
        float minSafeClean = F14Aero.StallIasKt(st.WeightLb) * F14Aero.StallMarginFactor;
        float flapWant = 0f;
        if (iasCmd < minSafeClean * FlapTriggerFactor)
        {
            flapWant = Math.Clamp(
                (minSafeClean * FlapTriggerFactor - iasCmd) / (minSafeClean * FlapStallReduction),
                0f, 1f);
        }
        // No se sacan por encima de su limite de extension: se arrancarian.
        if (st.IasKt > FlapExtendLimitKt) flapWant = 0f;
        _flapRamp.MaxRate = FlapRatePerSec;
        _flapCmd = _flapRamp.Update(flapWant, dt);
        if (_flapCmd > 0.02f) _flapsAreOurs = true;

        // Suelo de velocidad con los flaps que haya fuera en este momento.
        float minSafeNow = minSafeClean * (1f - FlapStallReduction * _flapCmd);
        tooSlowForUs = iasCmd < minSafeNow - 1f;
        iasCmd = Math.Clamp(iasCmd, minSafeNow, maxIas);

        // --- Aerofrenos: para quitar el exceso sin tocar la actitud ----------
        if (!_insideSafety)
        {
            // Sobra velocidad sobre la pedida -> aerofrenos, proporcional al
            // exceso. En persecucion la pedida ES el maximo, asi que esto da
            // cero solo: no hace falta preguntar por la fase.
            // En formacion la banda muerta de 12 kt dejaba que el avion pasara
            // 30 kt del blanco antes de frenar (oscilacion larga): se estrecha.
            bool tight = _phase == InterceptPhase.Station;
            float sbDead = tight ? SpeedbrakeDeadbandStationKt : SpeedbrakeDeadbandKt;
            float sbSpan = tight ? SpeedbrakeSpanStationKt : SpeedbrakeSpanKt;
            float sbWant = Math.Clamp((st.IasKt - iasCmd - sbDead) / sbSpan,
                                      0f, 1f);
            _speedbrakeRamp.MaxRate = SpeedbrakeRatePerSec;
            _speedbrakeCmd = _speedbrakeRamp.Update(sbWant, dt);
        }

        return iasCmd;
    }

    // Rumbo (de trayectoria, no de morro) al que hay que volar este frame: el
    // del planner, que ya rodea al blanco para llegar por detras, salvo el
    // serpenteo de los blancos que no se pueden igualar en velocidad.
    private float SolveTrack(in TargetSnapshot t, in InterceptGeometry now,
                             in InterceptPlan plan, bool tooSlowForUs, double ownGsKt)
    {
        if (_phase == InterceptPhase.Station && tooSlowForUs && ownGsKt > 1.0)
        {
            // El avance neto por la linea del blanco es V*cos(angulo), asi que
            // el angulo que iguala los dos avances sale del cociente de
            // velocidades. El zigzag se invierte al pasarse de ancho, no con
            // un reloj: queda acotado lateralmente por construccion.
            double ratio = Math.Clamp(_desiredAlongGsKt / ownGsKt, 0.05, 1.0);
            _weaveDeg = MathF.Min((float)(Math.Acos(ratio) * F14Aero.Rad2Deg), MaxWeaveDeg);
            if (Math.Abs(now.CrossM) > WeaveHalfWidthM) _weaveSign = Math.Sign(now.CrossM);
            return Pid.NormalizeAngleDeg360(t.TrackDeg + _weaveSign * _weaveDeg);
        }
        _weaveDeg = 0f;
        return (float)plan.DesiredTrackDeg;
    }

    // V/S pedida -> morro. El grueso lo pone un feedforward de trayectoria
    // (el morro que hace falta para esa V/S a esta velocidad es geometria,
    // no algo que haya que aprender integrando el error) y el PID solo
    // corrige la diferencia. Un PID solo tardaria segundos en encontrar el
    // morro de un ascenso de 8.000 fpm, y en una persecucion esos segundos
    // son el blanco escapandose.
    private void ApplyPitchForVerticalSpeed(in FlightState st, float vsCmdFpm, float dt)
    {
        float tasFpm = MathF.Max(st.TasKt * F14Aero.KtToFtPerSec * 60f, 600f);
        float gammaCmd = (float)(Math.Asin(Math.Clamp(vsCmdFpm / tasFpm, -0.6, 0.6)) *
                                 F14Aero.Rad2Deg);
        // theta = gamma + alpha en vuelo simetrico. Con alabeo, el AoA vive en
        // el plano del ala y solo su proyeccion vertical levanta el morro: por
        // eso va multiplicado por el coseno del alabeo. Sin ese coseno, un
        // viraje cerrado pediria mucho mas morro del que toca.
        float alpha = st.AoaDeg * MathF.Cos(st.BankDeg * F14Aero.Deg2Rad);
        // El lazo V/S -> morro es el "ola" lenta (~9 s) a baja velocidad: el
        // error de V/S se satura al tope de trim (+-3 deg) y el avion cabecea
        // arriba y abajo con +-1400 fpm (caja negra 14:52). Sus ganancias eran
        // demasiado altas para el retardo del morro -> V/S; se bajan y ademas
        // se atenuan con la velocidad (fpm por grado de trayectoria ~ TAS).
        _vsPid.GainScale = VsGainBase * MathF.Sqrt(VsGainRefTasKt / MathF.Max(st.TasKt, VsGainRefTasKt));
        float trim = _vsPid.Update(vsCmdFpm - st.VsFpm, dt);
        float pitchRaw = Math.Clamp(gammaCmd + alpha + trim, -MaxPitchDeg, MaxPitchDeg);

        _pitchTargetRamp.MaxRate = _tuning.ManeuverPitchRampDegPerSec;
        // Bajar el morro descarga el ala mucho mas rapido de lo que la carga
        // al subirlo: la bajada se limita aparte.
        _pitchTargetRamp.MaxRateFalling = PitchFallRateDegPerSec;
        float pitchTarget = _pitchTargetRamp.Update(pitchRaw, dt);
        LastPitchTarget = pitchTarget;
        _pitchPid.GainScale = AttitudeGainScale(st);
        _body.SetPitchInput(_pitchPid.Update(pitchTarget - st.PitchDeg, dt, st.PitchRateDegPerSec), dt);
    }

    // Que hacer mientras no hay nada que perseguir. No se sueltan los
    // overrides a proposito: la interceptacion sigue pedida, solo esta
    // esperando.
    //
    // orbitBankDeg != 0 es la diferencia entre esperar y marcharse: volando
    // recto a 280 kt, cinco minutos de espera a que el blanco despegue son 23
    // NM de alejamiento, y cuando por fin despega hay que recorrerlos otra
    // vez. Con un alabeo suave y sostenido el avion se queda dando vueltas en
    // la zona, que es lo que hace un interceptor en un punto de espera.
    private void FlyLevelHold(in FlightState st, float dt, float orbitBankDeg = 0f)
    {
        _weaveDeg = 0f;
        _bankTargetRamp.MaxRate = _tuning.ManeuverBankRampDegPerSec;
        float bankTarget = _bankTargetRamp.Update(orbitBankDeg, dt);
        LastBankTarget = bankTarget;
        _bankPid.GainScale = AttitudeGainScale(st);
        _body.SetRollInput(_bankPid.Update(bankTarget - st.BankDeg, dt, st.RollRateDegPerSec), dt);

        LastVsTarget = 0f;
        ApplyPitchForVerticalSpeed(st, 0f, dt);

        float minSafe = F14Aero.StallIasKt(st.WeightLb) * F14Aero.StallMarginFactor;
        float iasCmd = Math.Clamp(LoiterIasKt, minSafe,
                                  MathF.Min(_tuning.MaxTargetIasKt, st.VneKt * VneMarginFactor));
        LastIasTarget = iasCmd;
        LastThrottleCmd = _speedPid.Update(iasCmd - st.IasKt, dt);
        _body.SetThrottle(LastThrottleCmd, dt);

        // Limpio mientras se espera: ni flaps ni aerofrenos fuera.
        _flapRamp.MaxRate = FlapRatePerSec;
        _flapCmd = _flapRamp.Update(0f, dt);
        _speedbrakeRamp.MaxRate = SpeedbrakeRatePerSec;
        _speedbrakeCmd = _speedbrakeRamp.Update(0f, dt);
        if (_flapCmd < 0.02f) _flapsAreOurs = false;
        FlushSlowControls();
    }

    // Mide sin mandar: sirve para que el panel siga diciendo a que distancia
    // esta el blanco mientras se le espera en tierra.
    private void MeasureOnly(in TargetSnapshot t,
                             double ownX, double ownY, double ownZ,
                             double ownVx, double ownVy, double ownVz)
    {
        (float aft, float right, float up) = _tuning.ResolveStation();
        InterceptGeometry g = InterceptGeometry.Solve(ownX, ownY, ownZ, t, aft, right, up, 0.0);
        RangeM = g.RangeM;
        SeparationM = g.SeparationM;
        AlongM = g.AlongM;
        CrossM = g.CrossM;
        VerticalM = g.UpM;
        ClosureKt = ClosureRateKt(t, ownX, ownY, ownZ, ownVx, ownVy, ownVz, g.SeparationM);
    }

    // Velocidad de acercamiento: la componente de la velocidad relativa sobre
    // la linea de mira. Positiva = nos acercamos.
    private static double ClosureRateKt(in TargetSnapshot t,
                                        double ownX, double ownY, double ownZ,
                                        double ownVx, double ownVy, double ownVz,
                                        double separationM)
    {
        if (separationM < 1.0) return 0.0;
        double ux = (t.X - ownX) / separationM;
        double uy = (t.Y - ownY) / separationM;
        double uz = (t.Z - ownZ) / separationM;
        double rel = (t.Vx - ownVx) * ux + (t.Vy - ownVy) * uy + (t.Vz - ownVz) * uz;
        return -rel * TargetSnapshot.MpsToKnots;
    }

    // Flaps y aerofrenos van por Set (el simulador los anima y el usuario
    // puede seguir tocandolos), pero eso no significa que haya que mandarlos
    // 60 veces por segundo: se manda cuando el valor se ha movido de verdad,
    // o una vez por segundo para que un Set perdido no deje el mando a medias.
    private void FlushSlowControls()
    {
        bool periodic = _slowWriteTimer >= SlowControlRefreshSeconds;
        if (periodic) _slowWriteTimer = 0f;

        if (periodic || float.IsNaN(_flapSent) || MathF.Abs(_flapCmd - _flapSent) > 0.01f)
        {
            _body.SetFlaps(_flapCmd);
            _flapSent = _flapCmd;
        }
        if (periodic || float.IsNaN(_speedbrakeSent) ||
            MathF.Abs(_speedbrakeCmd - _speedbrakeSent) > 0.01f)
        {
            _body.SetSpeedbrake(_speedbrakeCmd);
            _speedbrakeSent = _speedbrakeCmd;
        }
    }

    // --- Fases -----------------------------------------------------------------

    private void UpdatePhaseByRange(double rangeM)
    {
        switch (_phase)
        {
            case InterceptPhase.Pursuit when rangeM < ClosingEnterM:
                SetPhase(InterceptPhase.Closing,
                    $"a {BlackBoxSnap.F(rangeM * TargetSnapshot.MetersToNm, "0.0")} NM — " +
                    $"empiezo a frenar (umbral cierre {ClosingEnterM:0} m)");
                break;
            case InterceptPhase.Closing when rangeM > PursuitEnterM:
                SetPhase(InterceptPhase.Pursuit,
                    $"se abre a {BlackBoxSnap.F(rangeM * TargetSnapshot.MetersToNm, "0.0")} NM — " +
                    $"vuelvo a cerrar (umbral persecucion {PursuitEnterM:0} m)");
                break;
            case InterceptPhase.Closing when rangeM < StationEnterM && Math.Abs(ClosureKt) < StationEnterClosureKt:
                SetPhase(InterceptPhase.Station,
                    $"en posicion ({BlackBoxSnap.F(rangeM, "0")} m, cierre {BlackBoxSnap.F(ClosureKt, "0")} kt) — " +
                    $"mantengo formacion (entrar <{StationEnterM:0} m y |cierre|<{StationEnterClosureKt:0} kt)");
                break;
            case InterceptPhase.Station when rangeM > StationExitM:
                SetPhase(InterceptPhase.Closing,
                    $"perdi el puesto ({BlackBoxSnap.F(rangeM, "0")} m > salida {StationExitM:0} m) — recupero");
                break;
        }
    }

    private void SetPhase(InterceptPhase phase, string? reason = null)
    {
        if (_phase == phase) return;
        InterceptPhase from = _phase;
        _phase = phase;
        _settledElapsed = 0f;
        if (phase != InterceptPhase.Station) _announced = false;
        // El autothrottle cambia de regimen con la fase (de "todo a fondo" a
        // "copia esta velocidad"): sin resembrar, el integral que acumulo
        // persiguiendo 600 kt tarda en soltar y el avion se pasa de largo.
        _speedPid.SeedTrim(_body.ThrottleReadback);

        // Marca en caja negra: el Idle→Pursuit del Start ya lo cuenta el
        // "Inicio: interceptar...", no hace falta duplicarlo.
        if (from == InterceptPhase.Idle && phase == InterceptPhase.Pursuit) return;

        (float maxBank, float maxVs, double maxLead) = PhaseLimits(phase);
        FlightState own = _body.State;
        LogAction(BlackBoxSnap.Join(
            $"Fase: {_targetLabel} {PhaseName(from)} → {PhaseName(phase)}",
            reason,
            $"limites fase bank≤{maxBank:0}° VS≤{maxVs:0} fpm lead≤{maxLead:0}s",
            InterceptLiveExtras(),
            BlackBoxSnap.Short(own)));
    }

    private string StationSummary() => _tuning.Station.Summary();

    private string InterceptConfigExtras() =>
        $"cfg {StationSummary()} · zona segura {_tuning.SafeHorizontalM:0}m/{_tuning.SafeVerticalM:0}m · Gsoft={_tuning.GSoftHigh:0.0} " +
        $"sueloAGL={_tuning.TerrainFloorAglFt:0}ft · " +
        $"umbrales cierre<{ClosingEnterM:0}m formacion<{StationEnterM:0}m salida>{StationExitM:0}m";

    private string InterceptLiveExtras()
    {
        var parts = new List<string>(8);
        if (!double.IsNaN(RangeM))
            parts.Add($"rango={BlackBoxSnap.F(RangeM, "0")}m/{BlackBoxSnap.F(RangeM * TargetSnapshot.MetersToNm, "0.00")}NM");
        if (!double.IsNaN(SeparationM))
            parts.Add($"sep={BlackBoxSnap.F(SeparationM, "0")}m");
        if (!double.IsNaN(ClosureKt))
            parts.Add($"cierre={BlackBoxSnap.F(ClosureKt, "+0;-0;0")}kt");
        if (!double.IsNaN(AlongM) || !double.IsNaN(CrossM) || !double.IsNaN(VerticalM))
            parts.Add($"err long={BlackBoxSnap.F(AlongM, "+0;-0;0")} lat={BlackBoxSnap.F(CrossM, "+0;-0;0")} " +
                      $"vert={BlackBoxSnap.F(VerticalM, "+0;-0;0")}m");
        if (!double.IsNaN(TargetSpeedKt))
            parts.Add($"blancoGS={BlackBoxSnap.F(TargetSpeedKt, "0")}kt ({TargetSnapshot.StateName(TargetState)})");
        if (!float.IsNaN(LastIasTarget))
            parts.Add($"IASobj={LastIasTarget:0}");
        if (!float.IsNaN(LastBankTarget))
            parts.Add($"bankObj={LastBankTarget:0.0}");
        if (!float.IsNaN(LastVsTarget))
            parts.Add($"VSobj={LastVsTarget:0}");
        if (_weaveDeg != 0f)
            parts.Add($"serpenteo={_weaveDeg:0.0}°");
        if (_flapsAreOurs || _flapCmd > 0.01f)
            parts.Add($"flaps={_flapCmd * 100f:0}%");
        if (_speedbrakeCmd > 0.01f)
            parts.Add($"SB={_speedbrakeCmd * 100f:0}%");
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    private void LogAction(string msg) => ActionLogged?.Invoke(msg);

    // Checklist de una linea (mismo formato que TakeoffSequence): [x] hechas,
    // [>] actual, [ ] pendientes. includeOwnTakeoff añade "Despegue" al
    // principio (mision que salio de tierra). ownTakeoffActive = aun estamos
    // en ese despegue (el director lo marca; esta secuencia sigue Idle).
    private static readonly (InterceptPhase Phase, string Label)[] ChecklistSteps =
    {
        (InterceptPhase.OwnTakeoff, "Despegue"),
        (InterceptPhase.WaitingTakeoff, "Espera blanco"),
        (InterceptPhase.Pursuit, "Persecucion"),
        (InterceptPhase.Closing, "Acercamiento"),
        (InterceptPhase.Station, "Formacion"),
    };

    public static string BuildChecklistLine(InterceptPhase current, bool includeOwnTakeoff,
                                            bool ownTakeoffActive = false)
    {
        InterceptPhase effective = ownTakeoffActive ? InterceptPhase.OwnTakeoff
            : current == InterceptPhase.Lost ? InterceptPhase.Pursuit
            : current == InterceptPhase.Idle ? InterceptPhase.Idle
            : current;

        var sb = new System.Text.StringBuilder();
        foreach ((InterceptPhase phase, string label) in ChecklistSteps)
        {
            if (phase == InterceptPhase.OwnTakeoff && !includeOwnTakeoff) continue;
            char marker;
            if (effective == InterceptPhase.Idle)
                marker = ' ';
            else if (effective == phase)
                marker = '>';
            else if (effective > phase)
                marker = 'x';
            else
                marker = ' ';
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(marker).Append("] ").Append(label);
        }
        return sb.ToString();
    }

    // --- Ganancias de los PID ---------------------------------------------------
    // Cabeceo y alabeo: las mismas que TakeoffSequence/ManeuverSequence, que
    // son las que estan rodadas en este avion.
    // Las ganancias de arriba estan afinadas a velocidad de aproximacion.
    // La autoridad del yugo crece con la presion dinamica (~IAS^2): a
    // 620 kt (caja negra 14:41) el mismo Kp daba un lazo de alabeo con
    // cruce ~5 rad/s + retardo de muestreo/rampa = oscilacion de ~1.5 s con
    // el yugo saturando entre -1 y +1, y cabeceo alternando cada muestra.
    // Se atenuan con (Vref/IAS)^1.5, sin subir de 1 por debajo de Vref.
    private const float VsGainBase = 0.4f;
    private const float VsGainRefTasKt = 300f;
    private const float GainRefIasKt = 320f;
    private const float GainScaleMin = 0.2f;
    private static float AttitudeGainScale(in FlightState st) =>
        Math.Clamp(MathF.Pow(GainRefIasKt / MathF.Max(st.IasKt, 1f), 1.5f), GainScaleMin, 1f);

    private const float PitchKp = 0.09f;
    private const float PitchKi = 0.015f;
    private const float PitchKd = 0.03f;

    private const float BankKp = 0.035f;
    private const float BankKi = 0.004f;
    private const float BankKd = 0.012f;

    // Correccion fina de V/S sobre el morro que ya da el feedforward.
    private const float VsKp = 0.0025f;
    private const float VsKi = 0.0004f;
    private const float VsKd = 0.0008f;
    private const float VsTrimLimitDeg = 3f;

    // Autothrottle. Mas vivo que el de las maniobras: en una interceptacion
    // la velocidad ES la maniobra, y 50 kt de error tienen que dar gas a
    // fondo sin esperar a que el integral se entere.
    private const float SpeedKp = 0.02f;
    private const float SpeedKi = 0.004f;
    private const float SpeedKd = 0.002f;

    // --- Geometria de las fases (metros) -----------------------------------------
    // Con histeresis entre entrada y salida: sin ella, volar justo en el
    // umbral haria saltar la fase varias veces por segundo, y cada salto
    // resiembra el autothrottle.
    private const double ClosingEnterM = 3500.0;
    private const double PursuitEnterM = 6000.0;
    private const double StationEnterM = 200.0;
    private const double StationExitM = 500.0;
    private const double StationEnterClosureKt = 50.0;
    private const double CaptureRangeM = 60.0;
    private const double SettledClosureKt = 15.0;
    private const float SettleSeconds = 2.5f;

    // Separacion minima con el AVION (no con el puesto) antes de considerarlo
    // riesgo de colision y abrirse por detras.
    private const float BackOffKt = 20f;

    // Limites por fase: alabeo (deg), V/S (fpm) y cuanto se adelanta el punto
    // de mira (s).
    private static (float MaxBank, float MaxVsFpm, double MaxLead) PhaseLimits(InterceptPhase p) =>
        p switch
        {
            InterceptPhase.Pursuit => (72f, 9000f, 120.0),
            InterceptPhase.Closing => (50f, 4000f, 15.0),
            // 45 deg: seguir a un blanco que vira a 3 deg/s y 150 m/s exige ~39 deg.
            InterceptPhase.Station => (45f, 2000f, 0.0),
            _ => (25f, LoiterMaxVsFpm, 0.0),
        };

    // Fraccion del limite BLANDO de G que se permite gastar en el viraje:
    // deja margen para que la turbulencia o un tiron del blanco no lleguen al
    // backstop estructural (GHardHigh).
    private const float GBudgetFraction = 0.85f;
    private const float GReliefFactor = 0.6f;

    private const float FormationTrackTauSec = 4.5f;
    private const float FormationTrackGainMax = 6f;
    private const float TrackToBankGain = 1.2f;   // deg de alabeo por deg de error de rumbo
    private const float MaxPitchDeg = 45f;
    private const float VerticalTauSec = 6f;      // en cuantos segundos se quiere borrar el error vertical

    // Formacion.
    private const double StationAlongTauSec = 4.0;
    private const double StationTrimMps = 25.0;           // ~49 kt de correccion maxima
    private const double StationCrossGainDegPerM = 0.15;
    private const double StationMaxOffsetDeg = 30.0;
    private const double StationBlendM = 250.0;

    // Deceleracion con la que se raciona el exceso de velocidad (m/s^2).
    // 2.5 m/s^2 son ~5 kt/s: lo que da un F-14 con gases al ralenti y
    // aerofrenos fuera en la mitad baja del envolvente, que es donde importa
    // (arriba, a 600 kt, la resistencia frena bastante mas, asi que quedarse
    // corto aqui solo hace que empiece a frenar un poco antes de la cuenta).
    private const double ClosingDecelMps2 = 2.5;

    // Banda de velocidad sobre el minimo seguro en la que el ascenso pedido se
    // va desvaneciendo.
    private const float StallGuardBandKt = 30f;

    // Serpenteo para blancos lentos.
    private const float MaxWeaveDeg = 50f;
    private const double WeaveHalfWidthM = 250.0;

    // Flaps y aerofrenos.
    // FlapStallReduction: cuanto baja la velocidad de perdida con flaps
    // completos. ~18% es lo tipico de un caza con flaps de maniobra + borde
    // de ataque; el F-14 ademas barre el ala solo a baja velocidad, asi que
    // es una estimacion conservadora.
    private const float FlapStallReduction = 0.18f;
    private const float FlapTriggerFactor = 1.08f;
    private const float FlapExtendLimitKt = 225f;
    private const float FlapRatePerSec = 0.15f;
    private const float SpeedbrakeRatePerSec = 0.8f;
    private const float SpeedbrakeDeadbandKt = 12f;
    private const float SpeedbrakeSpanKt = 50f;
    private const float SpeedbrakeDeadbandStationKt = 4f;
    private const float SpeedbrakeSpanStationKt = 20f;
    private const float SlowControlRefreshSeconds = 1f;

    // Espera solo si el estado del blanco es desconocido (alabeo sostenido
    // para no alejarse del campo). Con blanco en tierra se forma a 2 km.
    private const float HoldingBankDeg = 20f;
    private const float LoiterIasKt = 280f;
    private const float LoiterMaxVsFpm = 1500f;
    // Altura del puesto sobre un blanco parado (metros sobre su elevacion).
    private const float GroundFormationAltM = 2000f;
    // Por debajo de esto el blanco cuenta como parado para caps de IAS relativos.
    private const float SlowGroundTargetGsKt = 40f;

    // Transicion entre puestos de formacion: un poco mas lento que el blanco
    // (~20 kt) y rampa de IAS mas viva para que el desliz no se eternice
    // (~8-20 s tipicos entre puestos a 200-400 m).
    private const float RepositionIasRateKtPerSec = 40f;
    private const double RepositionDoneM = 80.0;
    private const double RepositionArmRangeM = 2500.0;

    // Lazo suave: planner a 10 Hz, rumbo filtrado, alabeo y velocidad
    // con rampa, bajada de morro limitada, vertical protegido de G baja.
    private const float PlanPeriodSec = 0.1f;
    private const float PhaseMinDwellSec = 1.5f;
    private const float TrackFilterTauSec = 0.25f;
    private const float InterceptBankRampDegPerSec = 25f;
    private const float IasCmdRateKtPerSec = 25f;
    private const float PitchFallRateDegPerSec = 6f;
    private const float LowGGuard = 0.5f;

    private const float VneMarginFactor = 0.92f;
    private const float MinOwnIasKt = 120f;
    private const double TerrainReferenceRangeM = 40000.0;
    private const float StatusLogSeconds = 10f;
    private const float WarnRepeatSeconds = 5f;
}
