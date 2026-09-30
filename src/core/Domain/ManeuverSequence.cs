using AICopilotCore.Domain.Agents;
using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

// Ejecuta UNA accion del catalogo de Maneuvers.cs a la vez. Es al catalogo de
// maniobras lo que TakeoffSequence es al guion de despegue: un lazo de control
// a ritmo de frame del simulador (lo llama App.xaml.cs desde TelemetryReceived)
// que traduce "que quiere Jev" en HOLD sobre AircraftControls.
//
// -- Que cambio aqui y por que ---------------------------------------------
//
// Este fichero decidia por su cuenta: leia los numeros fijos del catalogo, los
// perseguia con el PID, y si la G real se salia de margen marcaba la maniobra
// como abortada y caia a recuperar nivelado. Ese fallback es justo lo que no
// se quiere: el avion acababa haciendo algo distinto de lo que se pidio.
//
// Ahora hay un reparto claro de responsabilidades:
//
//   ManeuverPlanner decide QUE se puede volar aqui y ahora (trayectoria,
//   alabeo, morro, gases), replanificando cada frame contra el estado real.
//   EnvelopeProtection decide CUANTO mando se permite, de forma continua y
//   anticipada, para que la G no llegue nunca al limite.
//   Este fichero solo EJECUTA: persigue los objetivos del plan dentro de los
//   limites de la proteccion.
//
// Dos modos, uno por tipo de accion del catalogo:
//
//   AttitudeHold -> persigue la trayectoria y el alabeo del plan y se queda
//   ahi indefinidamente como un autopiloto: subir, bajar y virar no "terminan"
//   solos, terminan cuando Jev pide otra cosa.
//
//   Aerobatic -> recorre ANGULOS (360 deg de alabeo, media vuelta de arco
//   vertical) con mando de G. Antes era palanca fija durante N segundos, y ese
//   era su fallo de raiz: la misma palanca da 3 g a 250 kt y 9 g a 500 kt, y
//   los mismos segundos dan un rollo distinto a cada velocidad. Al acabar el
//   ultimo tramo pasa sola a recuperar nivelado.
//
// Una unica maniobra activa a la vez: pedir una nueva mientras hay otra en
// marcha la sustituye sin pasar por "Abortar" (es lo natural para Jev
// encadenando ordenes). Solo un "Abortar" de verdad suelta los overrides y
// devuelve el avion a mando manual.
public sealed class ManeuverSequence
{
    private readonly IAircraftBody _body;
    private readonly IFlightProfile _profile;
    private readonly ControlTuning _tuning;
    private readonly ManeuverPlanner _planner;
    private readonly EnvelopeProtection _protection;

    // Mismo candado que TakeoffSequence y por la misma razon: Start()/Abort()
    // los llama la UI y Update() el hilo de lectura del pipe.
    private readonly object _gate = new();

    private readonly Pid _pitchPid = new(PitchKp, PitchKi, PitchKd, -1f, 1f);
    private readonly Pid _bankPid = new(BankKp, BankKi, BankKd, -1f, 1f);
    // Lazo fino de V/S: corrige lo que el morro calculado por el planificador
    // no clava (peso, barrido del ala, error del modelo de AoA). Ya no es el
    // que nivela -- eso lo hace el plan -- sino el trim de encima.
    private readonly Pid _vsPid = new(VsKp, VsKi, VsKd, -15f, 15f);
    private readonly Pid _speedPid = new(SpeedKp, SpeedKi, SpeedKd, -0.5f, 0.5f);
    // Lazo de G de las acrobacias. Las ganancias se programan dividiendo el
    // error por la G que da una unidad de palanca a esta velocidad: asi el
    // lazo se comporta igual a 250 que a 550 kt, que es lo que un mando de
    // palanca fijo no podia hacer.
    private readonly Pid _gPid = new(GainKp, GainKi, 0f, -1f, 1f);

    private readonly SlewLimiter _pitchTargetRamp = new(12f);
    private readonly SlewLimiter _bankTargetRamp = new(45f);
    private readonly SlewLimiter _pitchStickRamp = new(8f);
    private readonly SlewLimiter _rollStickRamp = new(8f);

    private ManeuverDefinition? _active;
    private bool _recovering;
    private float _recoverElapsed;

    // --- Estado de una acrobacia en curso -----------------------------------
    // Los dos acumuladores son el corazon del cambio: la maniobra termina
    // cuando el avion ha RECORRIDO el angulo, no cuando pasa el tiempo.
    private int _segIndex;
    private float _segElapsed;
    private float _rollAccDeg;
    private float _arcAccDeg;
    private float _prevBankDeg = float.NaN;
    private ManeuverPlanner.AcroPlan _acroPlan;

    // IAS objetivo persistente de Acelerar/Frenar.
    private float _targetIasKt;
    private float _latchedIas = float.NaN;
    private string _lastAdaptation = "";

    public ManeuverSequence(IAircraftBody body, ControlTuning tuning,
                            IFlightProfile? profile = null)
    {
        _body = body;
        _profile = profile ?? F14Profile.Instance;
        _tuning = tuning;
        _planner = new ManeuverPlanner(tuning, _profile);
        _protection = new EnvelopeProtection(tuning);
    }

    private FlightState SenseState()
    {
        _body.Sense();
        return _body.State;
    }

    public event Action<string>? ActionLogged;

    public bool IsRunning => _active is not null;
    public string ActiveLabel => _active?.Label ?? "(ninguna)";

    // Ancla de altitud que defienden LevelWings / virajes (NaN si no hay
    // maniobra). CruisePilot la reescribe tras cada Start para no perder el
    // objetivo ±1000 ft al reiniciar LevelWings tras un viraje.
    public float ReferenceAltitudeFt
    {
        get { lock (_gate) return _planner.ReferenceAltFt; }
    }

    public void SetReferenceAltitude(float altFt)
    {
        lock (_gate)
        {
            if (_active is null) return;
            _planner.SyncReferenceAltitude(altFt);
        }
    }

    // Lo que se adapto en el ultimo frame (vacio si la maniobra sale tal cual).
    public string AdaptationText { get; private set; } = "";
    // Que proteccion esta actuando, para la pantalla.
    public string ProtectionText { get; private set; } = "";

    public string PhaseText
    {
        get
        {
            if (_active is null) return "Sin accion activa";
            if (_recovering) return $"{_active.Label} -> recuperando nivelado";

            string label = _active.Label;
            if (_active.Mode == ManeuverMode.Aerobatic && _active.Segments is { Count: > 0 } segs)
            {
                AcroSegment seg = segs[Math.Clamp(_segIndex, 0, segs.Count - 1)];
                label = $"{label} [{seg.Label} {_segIndex + 1}/{segs.Count}]";
            }
            else if (_active.IsSpeedHold)
            {
                label = $"{label} (IAS obj {_targetIasKt:0} kt)";
            }
            else if (!float.IsNaN(LastIasTarget))
            {
                label = $"{label} (IAS obj {LastIasTarget:0} kt)";
            }

            if (AdaptationText.Length > 0) label = $"{label} · adaptada: {AdaptationText}";
            return label;
        }
    }

    public float LastPitchTarget { get; private set; } = float.NaN;
    public float LastBankTarget { get; private set; } = float.NaN;
    public float LastThrottleCmd { get; private set; }
    public float LastIasTarget { get; private set; } = float.NaN;
    public float LastVsTarget { get; private set; } = float.NaN;
    public float LastGCommand { get; private set; } = float.NaN;
    public float LastSpeedbrakeCmd { get; private set; }
    public float PredictedG => _protection.PredictedG;
    public float GLoad => _body.State.GNormal;

    public void Start(ManeuverKind kind)
    {
        lock (_gate)
        {
            ManeuverDefinition def = ManeuverCatalog.Get(kind);
            FlightState s = SenseState();

            // Capturado ANTES de pisar _active: es el unico sitio donde se
            // puede saber si veniamos ya de Acelerar/Frenar (para que el
            // escalon de IAS sea relativo al objetivo que se perseguia) o de
            // otra maniobra (para que sea relativo al IAS real).
            bool wasSpeedHold = _active?.IsSpeedHold == true;
            _active = def;
            _recovering = false;
            _recoverElapsed = 0f;
            _segIndex = 0;
            _segElapsed = 0f;
            _rollAccDeg = 0f;
            _arcAccDeg = 0f;
            _prevBankDeg = s.BankDeg;
            _lastAdaptation = "";
            AdaptationText = "";
            ProtectionText = "";

            _pitchPid.Reset();
            _bankPid.Reset();
            _vsPid.Reset();
            _speedPid.Reset();
            _gPid.Reset();
            _pitchTargetRamp.Reset(s.PitchDeg);
            _bankTargetRamp.Reset(s.BankDeg);
            _pitchStickRamp.Reset(0f);
            _rollStickRamp.Reset(0f);
            _latchedIas = float.NaN;
            LastIasTarget = float.NaN;
            LastVsTarget = float.NaN;
            LastGCommand = float.NaN;

            _planner.Reset(s);
            _protection.Reset(s.GNormal);

            if (def.IsSpeedHold)
            {
                float baseIasKt = wasSpeedHold ? _targetIasKt : s.IasKt;
                float step = kind == ManeuverKind.Accelerate ? _tuning.SpeedStepKt : -_tuning.SpeedStepKt;
                _targetIasKt = Math.Clamp(baseIasKt + step, _tuning.MinTargetIasKt, _tuning.MaxTargetIasKt);
            }

            // Las acrobacias se planifican enteras antes de empezar: cuanta G
            // hace falta para que el arco quepa en la altura que hay y se
            // llegue arriba con velocidad de sobra. Eso es lo que antes no
            // existia -- se tiraba a ciegas y se descubria a mitad.
            if (def.Mode == ManeuverMode.Aerobatic && s.IsUsable)
            {
                _acroPlan = _planner.PlanAerobatic(def, s);
                if (_acroPlan.Explanation.Length > 0)
                    LogAction($"{def.Label}: {_acroPlan.Explanation}.");
            }

            _body.EnablePitchOverride();
            _body.EnableRollOverride();
            _body.EnableThrottleOverride();

            string planBit = def.Mode == ManeuverMode.Aerobatic
                ? $"plan G={_acroPlan.LoadFactorCmd:0.00} ~{_acroPlan.PredictedSeconds:0.0}s " +
                  $"dALT={_acroPlan.RequiredAltitudeFt:0}ft"
                : def.IsSpeedHold
                    ? $"IAS obj={_targetIasKt:0} kt"
                    : "";
            LogAction(BlackBoxSnap.Join(
                $"Inicio: {def.Label}",
                planBit,
                BlackBoxSnap.Of(s)));
        }
    }

    // Lo que la UI necesita para cada boton: si la maniobra sale tal cual, sale
    // adaptada (y en que), o no sale de ninguna forma segura. Sustituye al
    // "boton en gris" de antes, que solo sabia decir que no.
    public (AdaptationLevel Level, string Text) Preview(ManeuverKind kind)
    {
        lock (_gate)
        {
            FlightState s = SenseState();
            return _planner.Preview(ManeuverCatalog.Get(kind), s);
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (_active is null) return;
            FlightState s = SenseState();
            LogAction(BlackBoxSnap.Join(
                $"Fin: {_active.Label} abortada — control manual",
                ManeuverLiveExtras(),
                BlackBoxSnap.Of(s)));
            _body.SetSpeedbrake(0f);
            _body.ReleaseAllOverrides();
            ClearActive();
        }
    }

    // Se pierde la conexion con el plugin: el SafetyGuard del connector ya
    // habra soltado los overrides por su cuenta.
    public void OnConnectionLost()
    {
        lock (_gate)
        {
            if (_active is null) return;
            _body.ForgetOverrideState();
            ClearActive();
        }
    }

    private void ClearActive()
    {
        _active = null;
        _latchedIas = float.NaN;
        LastIasTarget = float.NaN;
        LastVsTarget = float.NaN;
        LastPitchTarget = float.NaN;
        LastBankTarget = float.NaN;
        LastGCommand = float.NaN;
        AdaptationText = "";
        ProtectionText = "";
    }

    public void Update(float dt)
    {
        lock (_gate) { UpdateLocked(dt); }
    }

    private void UpdateLocked(float dt)
    {
        if (_active is null || dt <= 0f) return;

        FlightState s = SenseState();
        // Un frame sin telemetria util (reconexion a medias) no debe producir
        // mandos calculados con NaN: mejor dejar en pie lo del frame anterior,
        // que ya esta puesto por Hold en el connector.
        if (!s.IsUsable) return;

        _planner.UpdateFilters(s, dt);

        // Ritmos que siguen siendo "estilo" y se resincronizan en caliente
        // desde la pestana de Config.
        _pitchStickRamp.MaxRate = _tuning.ManeuverStickRampPerSecond;
        _rollStickRamp.MaxRate = _tuning.ManeuverStickRampPerSecond;
        _vsPid.OutMax = _tuning.LevelFlightMaxPitchAdjustDeg;
        _vsPid.OutMin = -_tuning.LevelFlightMaxPitchAdjustDeg;

        // 1. QUE se quiere volar en este frame.
        ManeuverDefinition target = _recovering ? RecoveryDefinition : _active;
        ManeuverPlan plan = _planner.Plan(target, s, _targetIasKt);
        if (_active.Mode == ManeuverMode.Aerobatic && !_recovering)
            plan = PlanForAerobatic(plan, s);

        // 2. CUANTO mando se permite. Este es el reemplazo del corte: la
        // anticipacion esta en el limite de RITMO del objetivo que sale de aqui
        // (PitchRateUp/Down, despejados del presupuesto de G), no en mirar si
        // la G ya se paso.
        CommandLimits limits = _protection.Evaluate(s, plan, dt);
        ProtectionText = limits.Reason;

        // 3. El abort duro, que ahora es solo lo estructural y con permanencia.
        if (_protection.HardAbort != HardAbortReason.None && !_recovering)
        {
            LogAction(BlackBoxSnap.Join(
                $"Fin: ABORTO {_protection.HardAbortText} durante '{_active.Label}' → recuperando nivelado",
                ManeuverLiveExtras(),
                BlackBoxSnap.Of(s)));
            EnterRecovery(s);
        }

        // 4. Los limites entran por donde tienen que entrar, cada uno en un
        // solo sitio: el ritmo sobre la rampa del objetivo, la autoridad sobre
        // la saturacion del PID (que ya lleva anti-windup contra su propio
        // limite, asi que el integrador no se carga mientras la proteccion
        // actua).
        _pitchTargetRamp.MaxRate = limits.PitchRateUpDegPerSec;
        _pitchTargetRamp.MaxRateFalling = limits.PitchRateDownDegPerSec;
        // Ritmo de entrada al alabeo: el menor entre el plan, la rampa humana
        // de Config y lo que el perfil del avion puede (A330 ~10 deg/s).
        _bankTargetRamp.MaxRate = MathF.Min(plan.BankRateDegPerSec,
            MathF.Min(_tuning.ManeuverBankRampDegPerSec,
                      _profile.RollRateAvailableDegPerSec(s.TasKt)));
        _pitchPid.OutMin = limits.PitchStickMin;
        _pitchPid.OutMax = limits.PitchStickMax;

        LogAdaptation(plan);

        // 5. Ejecucion.
        if (_recovering) UpdateRecovering(plan, limits, s, dt);
        else if (_active.Mode == ManeuverMode.AttitudeHold) UpdateAttitudeHold(plan, limits, s, dt);
        else UpdateAerobatic(plan, limits, s, dt);

        // 6. Motor y aerofrenos.
        UpdateThrottle(plan, limits, s, dt);
    }

    // --- AttitudeHold ---------------------------------------------------------

    private void UpdateAttitudeHold(in ManeuverPlan plan, in CommandLimits limits,
                                    in FlightState s, float dt)
    {
        // Trim fino sobre el morro calculado: absorbe el error del modelo de
        // AoA (peso, barrido del ala) sin ser el que nivela.
        //
        // Va SUMADO ANTES de la rampa, no despues, y eso no es un detalle: la
        // rampa es la que hace cumplir el presupuesto de G, asi que cualquier
        // cosa que se sume despues se lo salta. Sumandolo despues -- como
        // estaba -- este lazo metia escalones de varios grados de golpe en el
        // objetivo (se vio en vuelo: el objetivo saltando de 2.5 a 7.4 deg
        // entre dos muestras), el PID veia un error enorme y respondia con la
        // palanca casi a fondo. De ahi las puntas de 6 g seguidas de -2 g.
        float vsTrim = 0f;
        if (!float.IsNaN(plan.VsTargetFpm) && MathF.Abs(plan.VsTargetFpm - s.VsFpm) < 4000f)
            vsTrim = _vsPid.Update(plan.VsTargetFpm - s.VsFpm, dt);
        else
            _vsPid.Reset();

        float pitchTarget = _pitchTargetRamp.Update(plan.PitchTargetDeg + vsTrim, dt);

        float bankTarget = _bankTargetRamp.Update(
            ClampMagnitude(plan.BankTargetDeg,
                MathF.Min(limits.BankMagnitudeMaxDeg, _profile.MaxOperationalBankDeg)), dt);

        LastPitchTarget = pitchTarget;
        LastBankTarget = bankTarget;
        LastVsTarget = plan.VsTargetFpm;
        LastGCommand = plan.LoadFactorCmd;

        ApplyAttitude(pitchTarget, bankTarget, s, dt);
    }

    // Rodar primero y tirar despues. Con el avion muy inclinado -- y sobre todo
    // invertido -- tirar del morro no sube: hunde la trayectoria. Asi que la
    // autoridad de cabeceo se abre progresivamente conforme las alas se
    // acercan a la horizontal, y mientras tanto manda el alabeo. Esto es lo que
    // hace que "Nivelar" desde invertido salga por donde tiene que salir.
    private void ApplyAttitude(float pitchTarget, float bankTarget, in FlightState s, float dt)
    {
        float kPitch = Math.Clamp(
            (MathF.Cos(s.BankDeg * F14Aero.Deg2Rad) + 0.5f) / 1.20711f, 0f, 1f);

        float pitchErr = pitchTarget - s.PitchDeg;
        _body.SetPitchInput(kPitch * _pitchPid.Update(pitchErr, dt), dt);

        float bankErr = Pid.NormalizeAngleDeg180(bankTarget - s.BankDeg);
        _body.SetRollInput(_bankPid.Update(bankErr, dt), dt);
    }

    // --- Acrobacias -----------------------------------------------------------

    // El plan de un tramo de acrobacia: G objetivo y gases del tramo, sobre el
    // plan general. El resto (presupuestos, limites) ya viene del planificador.
    private ManeuverPlan PlanForAerobatic(in ManeuverPlan basePlan, in FlightState s)
    {
        IReadOnlyList<AcroSegment> segs = _active!.Segments!;
        AcroSegment seg = segs[Math.Clamp(_segIndex, 0, segs.Count - 1)];

        float nCmd = !float.IsNaN(seg.LoadFactorCmd) ? seg.LoadFactorCmd : _acroPlan.LoadFactorCmd;
        float throttle = !float.IsNaN(seg.Throttle) ? seg.Throttle : _active.ThrottleTarget;

        // Aerofrenos en la mitad descendente: es lo que evita que un Split-S o
        // la bajada de un looping salgan por abajo contra la VNE. Solo cuando
        // el tramo ya va a ralenti -- si el tramo quiere potencia, sacarlos
        // seria pelearse consigo mismo.
        float speedbrake = _tuning.AutoSpeedbrake && throttle <= 0.1f && s.VneMarginKt < 150f
            ? Math.Clamp((150f - s.VneMarginKt) / 100f, 0f, 1f)
            : 0f;

        return basePlan with
        {
            ThrottleTarget = throttle,
            SpeedTargetKt = float.NaN,
            SpeedbrakeTarget = speedbrake,
            LoadFactorCmd = nCmd,
            GBudgetMax = MathF.Min(_active.GBudgetMax, MathF.Max(nCmd + 0.5f, 2f)),
            GBudgetMin = _active.GPushMin,
        };
    }

    private void UpdateAerobatic(in ManeuverPlan plan, in CommandLimits limits,
                                 in FlightState s, float dt)
    {
        IReadOnlyList<AcroSegment> segs = _active!.Segments!;
        AcroSegment seg = segs[Math.Clamp(_segIndex, 0, segs.Count - 1)];
        _segElapsed += dt;

        // Angulo de alabeo recorrido de verdad. phi es continuo modulo 360, asi
        // que la diferencia normalizada entre frames se puede acumular sin
        // singularidades mientras no se gire mas de media vuelta por frame (a
        // 175 deg/s y 60 Hz son 3 deg: sobra).
        if (!float.IsNaN(_prevBankDeg))
            _rollAccDeg += Pid.NormalizeAngleDeg180(s.BankDeg - _prevBankDeg);
        _prevBankDeg = s.BankDeg;

        // Arco vertical recorrido: se integra la velocidad de cabeceo REAL que
        // da el simulador (dataref Q), que no tiene el problema de theta (que
        // se dobla en +-90 deg y no sirve para contar un looping).
        //
        // Sin signo de Direction a proposito: el arco se recorre TIRANDO, y
        // tirar es Q positiva tanto en un looping normal como en la mitad
        // invertida de un Split-S (donde el morro va hacia el suelo pero el
        // gesto sigue siendo el mismo). Direction dice hacia donde lleva ese
        // arco -- eso lo usa el planificador para saber si la maniobra gana o
        // pierde altura -- no como se cuenta.
        _arcAccDeg += s.PitchRateDegPerSec * dt;

        // --- Mando de alabeo: se persigue el angulo que falta, frenando por
        // distancia para no pasarse (la parada desde 175 deg/s son ~38 deg).
        float rollStick = 0f;
        if (MathF.Abs(seg.RollAngleDeg) > 1f)
        {
            float remaining = seg.RollAngleDeg - _rollAccDeg;
            float rollAvail = F14Aero.RollRateAvailableDegPerSec(s.TasKt);
            float braking = MathF.Sqrt(2f * RollDecelDegPerSec2 * MathF.Abs(remaining));
            float rateCmd = MathF.Sign(remaining) * MathF.Min(rollAvail, braking);
            rollStick = Math.Clamp(rateCmd / MathF.Max(rollAvail, 10f), -1f, 1f);
        }

        // --- Mando de cabeceo: se manda G, no palanca. La palanca que hace
        // falta para una G dada escala con 1/IAS^2, y por eso el gesto fijo de
        // antes daba 3 g a 250 kt y 9 a 500 -- que es exactamente lo que hacia
        // saltar el corte en cuanto se pedia un looping rapido.
        float nCmd = plan.LoadFactorCmd;
        if (seg.UnloadedRoll)
        {
            // En un rollo de aleron no se persigue G constante: se acompana el
            // alabeo descargando, que es lo que hace que el morro no se caiga
            // al pasar por invertido sin llegar a empujar en negativo.
            float phi = _rollAccDeg * F14Aero.Deg2Rad;
            nCmd = 0.3f + 0.7f * MathF.Cos(phi);
        }
        LastGCommand = nCmd;

        float gainPerStick = F14Aero.GPerStickUnit(s.IasKt);
        float feedForward = (nCmd - 1f) / gainPerStick;
        float correction = _gPid.Update((nCmd - _protection.FilteredG) / gainPerStick, dt);
        float pitchStick = Math.Clamp(feedForward + correction,
                                      limits.PitchStickMin, limits.PitchStickMax);

        _body.SetPitchInput(_pitchStickRamp.Update(pitchStick, dt), dt);
        _body.SetRollInput(_rollStickRamp.Update(rollStick, dt), dt);

        LastPitchTarget = float.NaN;
        LastBankTarget = float.NaN;
        LastVsTarget = float.NaN;

        // --- Fin de tramo: por ANGULO recorrido. El tiempo solo es una red de
        // seguridad por si el avion no puede completarlo (y entonces se dice).
        bool rollDone = MathF.Abs(seg.RollAngleDeg) < 1f ||
                        MathF.Abs(_rollAccDeg) >= MathF.Abs(seg.RollAngleDeg) - RollCaptureDeg;
        bool arcDone = MathF.Abs(seg.ArcAngleDeg) < 1f ||
                       _arcAccDeg >= MathF.Abs(seg.ArcAngleDeg) - ArcCaptureDeg;
        bool timedOut = _segElapsed >= seg.MaxSeconds;

        if (timedOut && !(rollDone && arcDone))
            LogAction(BlackBoxSnap.Join(
                $"{_active.Label}: '{seg.Label}' se corta por tiempo " +
                $"(alabeo {_rollAccDeg:0}/{seg.RollAngleDeg:0} arco {_arcAccDeg:0}/{seg.ArcAngleDeg:0} " +
                $"t={_segElapsed:0.0}/{seg.MaxSeconds:0}s)",
                ManeuverLiveExtras(),
                BlackBoxSnap.Short(s)));

        if ((rollDone && arcDone) || timedOut)
        {
            _segIndex++;
            _segElapsed = 0f;
            _rollAccDeg = 0f;
            _arcAccDeg = 0f;
            _gPid.Reset();
            if (_segIndex >= segs.Count)
            {
                LogAction(BlackBoxSnap.Join(
                    $"Fin: {_active.Label} completada — recuperando nivelado",
                    ManeuverLiveExtras(),
                    BlackBoxSnap.Of(s)));
                EnterRecovery(s);
            }
        }
    }

    // --- Recuperacion ----------------------------------------------------------

    // La recuperacion es volar LevelWings con el mismo planificador: rodar a
    // alas niveles, poner la trayectoria a cero con el ritmo que permita la G
    // disponible y sostener la velocidad. Antes era una rampa fija de 12 deg/s
    // que, con el morro 60 deg abajo, tardaba mas de cinco segundos solo en
    // mover el objetivo -- miles de pies en un Split-S.
    private static readonly ManeuverDefinition RecoveryDefinition =
        ManeuverCatalog.Get(ManeuverKind.LevelWings);

    private void EnterRecovery(in FlightState s)
    {
        _recovering = true;
        _recoverElapsed = 0f;
        _pitchTargetRamp.Reset(s.PitchDeg);
        _bankTargetRamp.Reset(s.BankDeg);
        _pitchPid.Reset();
        _bankPid.Reset();
        _vsPid.Reset();
        _gPid.Reset();
        _planner.SyncReferenceAltitude(s.AltFt);
    }

    private void UpdateRecovering(in ManeuverPlan plan, in CommandLimits limits,
                                  in FlightState s, float dt)
    {
        _recoverElapsed += dt;
        UpdateAttitudeHold(plan, limits, s, dt);

        // Capturar por TRAYECTORIA y alabeo, no por angulo de morro: el morro
        // de vuelo nivelado va de 2 deg a 500 kt a 12 deg a 160 kt, asi que el
        // criterio anterior (morro dentro de +-3 deg de 2.5) no se cumplia
        // nunca por debajo de ~200 kt y la recuperacion se quedaba colgada ahi
        // para siempre.
        bool captured = MathF.Abs(s.BankDeg) < _tuning.RecoverBankCaptureDeg &&
                        MathF.Abs(s.VsFpm) < RecoverVsCaptureFpm &&
                        MathF.Abs(s.FlightPathDeg) < 1.5f;

        if (_recoverElapsed >= _tuning.RecoverMinSeconds && captured &&
            _protection.HardAbort == HardAbortReason.None)
        {
            string finished = _active?.Label ?? "maniobra";
            LogAction(BlackBoxSnap.Join(
                $"Fin: nivelado recuperado tras '{finished}' — en espera",
                ManeuverLiveExtras(),
                BlackBoxSnap.Of(s)));
            // Se queda volando nivelado en vez de soltar los overrides: soltar
            // aqui devolveria el avion a mando manual sin que nadie lo haya
            // pedido, justo lo contrario de lo que hace falta para que Jev
            // pueda seguir encadenando ordenes.
            _active = RecoveryDefinition;
            _recovering = false;
            _segIndex = 0;
            _pitchPid.Reset();
            _bankPid.Reset();
            _planner.Reset(s);
        }
    }

    // --- Motor y aerofrenos ----------------------------------------------------

    private void UpdateThrottle(in ManeuverPlan plan, in CommandLimits limits,
                                in FlightState s, float dt)
    {
        float throttleCmd;
        if (!float.IsNaN(plan.SpeedTargetKt))
        {
            // El PID de motor ya no tiene que integrarlo todo desde cero: el
            // modelo dice que palanca sostiene esta velocidad con esta carga y
            // esta pendiente, y el PID solo corrige alrededor. Eso arregla de
            // paso la asimetria que tenia el arranque por SeedTrim (mucha mas
            // autoridad para bajar que para subir).
            if (float.IsNaN(_latchedIas) || MathF.Abs(_latchedIas - plan.SpeedTargetKt) > 0.5f)
            {
                _speedPid.Reset();
                _latchedIas = plan.SpeedTargetKt;
            }
            LastIasTarget = plan.SpeedTargetKt;
            throttleCmd = plan.ThrottleFeedForward +
                          _speedPid.Update(plan.SpeedTargetKt - s.IasKt, dt);
        }
        else
        {
            _latchedIas = float.NaN;
            LastIasTarget = float.NaN;
            throttleCmd = plan.ThrottleTarget;
        }

        throttleCmd = Math.Clamp(throttleCmd, limits.ThrottleMin, limits.ThrottleMax);
        LastThrottleCmd = throttleCmd;
        _body.SetThrottle(throttleCmd, dt);
        LastSpeedbrakeCmd = plan.SpeedbrakeTarget;
        _body.SetSpeedbrake(plan.SpeedbrakeTarget);
    }

    // --- Log -------------------------------------------------------------------

    // Numeros de control vivos en el instante de la marca: G pedida/predicha,
    // objetivos de actitud/IAS y textos de adaptacion/proteccion si hay.
    private string ManeuverLiveExtras()
    {
        var parts = new List<string>(6);
        if (!float.IsNaN(LastGCommand))
            parts.Add($"Gobj={LastGCommand:0.00} Gpred={_protection.PredictedG:0.00}");
        if (!float.IsNaN(LastPitchTarget))
            parts.Add($"pitchObj={LastPitchTarget:0.0}");
        if (!float.IsNaN(LastBankTarget))
            parts.Add($"bankObj={LastBankTarget:0.0}");
        if (!float.IsNaN(LastIasTarget))
            parts.Add($"IASobj={LastIasTarget:0}");
        if (!float.IsNaN(LastVsTarget))
            parts.Add($"VSobj={LastVsTarget:0}");
        if (_recovering)
            parts.Add($"recup={_recoverElapsed:0.0}s");
        if (_active?.Mode == ManeuverMode.Aerobatic && !_recovering)
            parts.Add($"alabeoAcc={_rollAccDeg:0} arcoAcc={_arcAccDeg:0}");
        if (!string.IsNullOrEmpty(AdaptationText))
            parts.Add($"adapt={AdaptationText}");
        if (!string.IsNullOrEmpty(ProtectionText))
            parts.Add($"prot={ProtectionText}");
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    // Solo se escribe cuando el motivo CAMBIA: una adaptacion permanente
    // ("alabeo recortado porque no hay velocidad") llenaria el log sesenta
    // veces por segundo.
    private void LogAdaptation(in ManeuverPlan plan)
    {
        AdaptationText = plan.Adaptation;
        if (plan.Adaptation == _lastAdaptation) return;
        _lastAdaptation = plan.Adaptation;
        if (plan.Adaptation.Length > 0 && _active is not null)
            LogAction($"{_active.Label} adaptada: {plan.Adaptation}.");
    }

    private void LogAction(string msg) => ActionLogged?.Invoke(msg);

    private static float ClampMagnitude(float value, float maxMagnitude)
    {
        float m = MathF.Min(MathF.Abs(value), MathF.Abs(maxMagnitude));
        return MathF.Sign(value) * m;
    }

    // --- Constantes del lazo ----------------------------------------------------

    private const float PitchKp = 0.09f;
    private const float PitchKi = 0.015f;
    private const float PitchKd = 0.03f;

    private const float BankKp = 0.035f;
    private const float BankKi = 0.004f;
    private const float BankKd = 0.012f;

    // Trim fino de V/S sobre el morro que calcula el planificador. El limite de
    // salida es ControlTuning.LevelFlightMaxPitchAdjustDeg.
    private const float VsKp = 0.0015f;
    private const float VsKi = 0.0003f;
    private const float VsKd = 0f;

    // Autothrottle: ahora es una CORRECCION sobre el feed-forward del modelo,
    // no el mando entero, asi que su rango de salida es +-0.5 y no 0..1.
    private const float SpeedKp = 0.01f;
    private const float SpeedKi = 0.002f;
    private const float SpeedKd = 0f;

    // Lazo de G de las acrobacias. El error se divide por la G que da una
    // unidad de palanca a esta velocidad antes de entrar aqui, asi que estas
    // ganancias valen en todo el envolvente.
    private const float GainKp = 0.25f;
    private const float GainKi = 0.5f;

    // Amortiguamiento de alabeo con el que se calcula la distancia de frenado
    // del rollo: sin esto, un rollo se pasa del orden de 38 deg a alta
    // velocidad.
    private const float RollDecelDegPerSec2 = 400f;
    private const float RollCaptureDeg = 5f;
    private const float ArcCaptureDeg = 6f;
    private const float RecoverVsCaptureFpm = 400f;
}
