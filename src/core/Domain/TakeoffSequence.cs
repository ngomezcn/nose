using System.Globalization;
using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

public enum TakeoffPhase
{
    Idle,
    Prep,
    ThrottleUp,
    GroundRoll,
    Rotate,
    Climb,
    ClimbToTurnAltitude,
    Turn,
    ClimbToAltitude,
    LevelOff,
    Done,
}

// Maquina de estados que hace el paripe de un despegue completo: suelta
// freno, mantiene el eje de pista con un PID de rumbo/timon, persigue la
// IAS de la fase (el motor es solo el actuador: no hay un porcentaje de
// potencia objetivo), espera velocidad de rotacion, levanta la nariz (PID
// de pitch), sube tren, va limpiando flaps, hace un viraje de salida a una
// altitud intermedia (PID de rumbo/alabeo en cascada) y sigue subiendo
// hasta la altitud objetivo, donde nivela el morro. El perfil concreto
// (velocidades, morro, viraje) lo elige un TakeoffStyle.
//
// Puerto de src/TakeoffSequence.h, que vivia dentro del plugin. Mismas
// fases, mismas ganancias, mismos watchdogs. Lo unico que cambia de verdad
// es de donde viene el tiempo: Update() ya no lo llama el flight loop de
// X-Plane sino el evento de telemetria del connector, con el dt que reporta
// el propio simulador. Eso importa -- atarlo a un timer de Windows haria
// que los PID corrieran a un ritmo distinto del sim en cuanto el sim vaya
// en pausa, a camara lenta o con el frame rate bajo.
public sealed class TakeoffSequence
{
    private readonly AircraftControls _controls;
    private readonly Datarefs _d;

    // Update() corre en el hilo de lectura del pipe (llega un TELEM, se
    // tickea la secuencia) y Start()/Abort() los llama el usuario desde el
    // hilo de la UI. Sin candado, pulsar "Iniciar" justo mientras el lazo
    // esta a mitad de un Update reinicia los PID y el rumbo base por debajo
    // del propio Update, que sigue con valores a medio cambiar. En el plugin
    // viejo esto no podia pasar: los START llegaban por UDP y se procesaban
    // dentro del mismo flight loop que el Update, todo en un hilo.
    private readonly object _gate = new();

    private readonly Pid _pitchPid = new(PitchKp, PitchKi, PitchKd, -1f, 1f);
    private readonly Pid _bankPid = new(BankKp, BankKi, BankKd, -1f, 1f);
    private readonly Pid _yawPid = new(YawKp, YawKi, YawKd, -1f, 1f);
    // Autothrottle: error de IAS (kt) -> potencia 0..1. Kp alto para que,
    // lejos del objetivo (la carrera), pida potencia a fondo; el anti-windup
    // del PID evita que ese rato a fondo deje el integral clavado.
    private readonly Pid _speedPid = new(SpeedKp, SpeedKi, SpeedKd, 0f, 1f);
    // Valor inicial sin importancia: HoldPitch lo reescribe con la rampa
    // del estilo activo.
    private readonly SlewLimiter _pitchTargetRamp = new(2.5f);

    private float _phaseElapsed;
    private bool _watchdogWarned;
    private float _reactionTimer;

    private float _baseHeadingDeg;
    private float _holdHeadingDeg;
    private float _fieldAltFt;
    private float _iasTarget = float.NaN;
    private float _flapTimer;
    private TakeoffStyle _style = TakeoffStyles.Relaxed;
    // Si true, al llegar a la altitud de viraje se da por terminado el
    // despegue (Done) sin viraje fijo ni subida a techo: el llamador
    // (FlightDirector) encadena la interceptacion, que es quien gira.
    private bool _handoffAtTurnAltitude;

    private bool _baroLogged;
    private bool _baroWarnedOutOfRange;

    public TakeoffSequence(AircraftControls controls, Datarefs datarefs)
    {
        _controls = controls;
        _d = datarefs;
    }

    // Una linea corta cada vez que la secuencia toma una decision discreta
    // (cambia de fase, mueve flaps, sube tren...). Alimenta el panel de
    // logs, no la telemetria continua.
    public event Action<string>? ActionLogged;

    public TakeoffPhase Phase { get; private set; } = TakeoffPhase.Idle;
    public bool IsRunning => Phase != TakeoffPhase.Idle && Phase != TakeoffPhase.Done;

    // Copiados del estilo al arrancar (altitudes ya en ft indicados, sumada
    // la del campo). La UI no los edita: elige un TakeoffStyle.
    public float VrKt { get; private set; } = 145f;
    public float TargetAltFt { get; private set; } = 10000f;
    public float RotatePitchDeg { get; private set; } = 12f;
    public float TurnAltFt { get; private set; } = 3000f;
    public float TurnDeltaDeg { get; private set; } = 90f;
    public string StyleName => _style.Name;

    // --- Ultimos objetivos pedidos, para enseñarlos junto al valor real ---
    // NaN = esta fase no persigue ese eje (en tierra no hay pitch objetivo,
    // en manual no hay ninguno). La telemetria pinta "--".
    public float LastPitchTarget { get; private set; } = float.NaN;
    public float LastBankTarget { get; private set; } = float.NaN;
    public float LastThrottleCmd { get; private set; }
    public float LastFlapCmd { get; private set; }
    public float LastIasTarget => IsRunning && !float.IsNaN(_iasTarget) ? _iasTarget : float.NaN;
    public float LastAltTarget => IsRunning ? TargetAltFt : float.NaN;
    public float LastVsTarget { get; private set; } = float.NaN;
    public float HeadingTargetDeg => IsRunning ? _holdHeadingDeg : float.NaN;
    public bool GearDownCommanded { get; private set; } = true;

    // El estilo entra entero, bajo el mismo candado que el arranque.
    // handoffAtTurnAltitude: corta al alcanzar la altura de viraje (sin
    // viraje de pista ni subida a techo) para encadenar otra secuencia.
    public void Start(TakeoffStyle style, bool handoffAtTurnAltitude = false)
    {
        lock (_gate)
        {
            _style = style;
            _handoffAtTurnAltitude = handoffAtTurnAltitude;
            _fieldAltFt = _d.AltFt.Float;
            VrKt = style.VrKt;
            RotatePitchDeg = style.RotatePitchDeg;
            TurnDeltaDeg = style.TurnDeltaDeg;
            TurnAltFt = _fieldAltFt + style.TurnHeightFt;
            TargetAltFt = _fieldAltFt + style.LevelHeightFt;
            StartLocked();
        }
    }

    private void StartLocked()
    {
        VrKt = Math.Max(40f, VrKt);
        TargetAltFt = Math.Max(1000f, TargetAltFt);
        RotatePitchDeg = Math.Clamp(RotatePitchDeg, 3f, 20f);
        TurnDeltaDeg = Math.Clamp(TurnDeltaDeg, -170f, 170f);
        TurnAltFt = Math.Max(500f, TurnAltFt);
        // La altitud de viraje siempre por debajo de la objetivo, por si el
        // usuario ha metido valores raros. Con handoff no importa el techo:
        // se corta en TurnAlt y no se sigue subiendo.
        if (!_handoffAtTurnAltitude)
            TurnAltFt = Math.Min(TurnAltFt, TargetAltFt - 500f);

        _pitchPid.Reset();
        _bankPid.Reset();
        _yawPid.Reset();
        _speedPid.Reset();
        _iasTarget = float.NaN;
        _flapTimer = 0f;
        LastPitchTarget = float.NaN;
        LastBankTarget = float.NaN;
        LastVsTarget = float.NaN;
        // La rampa de objetivo de pitch arranca en la actitud actual (en
        // tierra, ~0deg) para que el primer HoldPitch no la vea saltar
        // desde un 0 "de fabrica" hasta ahi de golpe.
        _pitchTargetRamp.Reset(_d.PitchDeg.Float);
        _baroLogged = false;
        _baroWarnedOutOfRange = false;
        _baseHeadingDeg = _d.HeadingDeg.Float;
        _holdHeadingDeg = _baseHeadingDeg;

        string handoffNote = _handoffAtTurnAltitude
            ? $"handoff@{TurnAltFt:0}ft→intercept "
            : $"viraje={TurnAltFt:0}ft ({TurnDeltaDeg:0}deg) nivel={TargetAltFt:0}ft ";
        LogAction($"Inicio: despegue [{_style.Name}] Vr={VrKt:0}kt rot={RotatePitchDeg:0}deg " +
                  $"IAS pista={_style.RollIasKt:0} limpieza={_style.CleanupIasKt:0} " +
                  $"subida={_style.ClimbIasKt:0} crucero={_style.CruiseIasKt:0} " +
                  handoffNote +
                  $"rumboPista={_baseHeadingDeg:0}deg | " +
                  BlackBoxSnap.Of(FlightState.Capture(_d)));

        // El rumbo de pista se mantiene con el timon/rueda de morro desde
        // ya, con el avion aun parado, asi que ese eje se activa ahora.
        _controls.EnableYawOverride();
        _controls.EnableThrottleOverride();

        SetPhase(TakeoffPhase.Prep);
    }

    public void Abort()
    {
        lock (_gate)
        {
            LogAction(BlackBoxSnap.Join(
                $"Fin: despegue abortado — fase era '{PhaseName(Phase)}'",
                BlackBoxSnap.Of(FlightState.Capture(_d))));
            _controls.ReleaseAllOverrides();
            _iasTarget = float.NaN;
            LastPitchTarget = float.NaN;
            LastBankTarget = float.NaN;
            LastVsTarget = float.NaN;
            SetPhase(TakeoffPhase.Idle);
        }
    }

    // Se pierde la conexion con el plugin a mitad de secuencia: el connector
    // ya habra soltado los overrides por su cuenta (SafetyGuard), asi que
    // aqui solo hay que aceptar que la secuencia se acabo y no seguir
    // mandando ordenes a un pipe muerto.
    public void OnConnectionLost()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            LogAction("Conexion con el plugin perdida: secuencia cancelada.");
            _controls.ForgetOverrideState();
            SetPhase(TakeoffPhase.Idle);
        }
    }

    // Se llama una vez por frame de simulador, desde el evento de
    // telemetria del connector.
    public void Update(float dt)
    {
        lock (_gate) { UpdateLocked(dt); }
    }

    private void UpdateLocked(float dt)
    {
        if (Phase is TakeoffPhase.Idle or TakeoffPhase.Done)
        {
            LastVsTarget = float.NaN;
            return;
        }

        _phaseElapsed += dt;

        // El motor se pide TODOS los frames: el autothrottle persigue la IAS
        // de la fase y el SlewLimiter tiene que ver el mando cada frame para
        // llegar de verdad, no solo en la transicion.
        ApplySpeedThrottle(dt);
        if (Phase >= TakeoffPhase.Climb && Phase <= TakeoffPhase.LevelOff)
            UpdateFlapSchedule(dt);

        // El altimetro se reescribe TODOS los frames, igual que el throttle:
        // algunos aviones (el B737 de Zibo, por ejemplo) tienen su propia
        // logica de cabina que pisa el dataref generico del baro cada frame
        // segun su estado interno del boton QNH/STD, asi que pedirlo una
        // sola vez al entrar en Prep perdia la pelea el frame siguiente y el
        // altimetro se quedaba a 0.00 inHg (de ahi altitudes indicadas
        // absurdas como -26000ft).
        if (Phase is TakeoffPhase.Prep or TakeoffPhase.ThrottleUp or TakeoffPhase.GroundRoll)
        {
            SetBarometerToLocalQnh();
        }

        switch (Phase)
        {
            case TakeoffPhase.Prep:
                _controls.ReleaseParkingBrake();
                LogAction("Freno de parking suelto");
                _controls.SetGearDown(true);
                _controls.SetFlaps(_style.TakeoffFlapRatio);
                LastFlapCmd = _style.TakeoffFlapRatio;
                LogAction($"Flaps -> {_style.TakeoffFlapRatio * 100f:0}% (despegue)");
                HoldRunwayHeading(dt);
                SetPhase(TakeoffPhase.ThrottleUp);
                break;

            case TakeoffPhase.ThrottleUp:
                HoldRunwayHeading(dt);
                // El objetivo real es que el motor haya respondido de verdad
                // (leido del propio dataref que controlamos en exclusiva), no
                // solo que hayamos "pedido" el 100%.
                if (ConditionSustained(
                        _controls.ThrottleReadback >= 0.9f ||
                        _d.IasKt.Float >= _style.RollIasKt * 0.85f, dt))
                {
                    SetPhase(TakeoffPhase.GroundRoll);
                }
                else
                {
                    WatchdogCheck(ThrottleWatchdogSeconds,
                        "el motor no llega al 90% de potencia todavia. Con el override " +
                        "de motores activo esto deberia ser cuestion de un par de " +
                        "segundos de rampa; si tarda mucho mas, revisa si el avion " +
                        "tiene algun corte de combustible/arranque pendiente.");
                }
                break;

            case TakeoffPhase.GroundRoll:
                HoldRunwayHeading(dt);
                if (ConditionSustained(_d.IasKt.Float >= VrKt, dt))
                {
                    _controls.DisableYawOverride();
                    _controls.EnablePitchOverride();
                    _controls.EnableRollOverride();
                    LogAction($"Rotando (IAS={_d.IasKt.Float:0}kt >= Vr={VrKt:0}kt)");
                    SetPhase(TakeoffPhase.Rotate);
                }
                else
                {
                    WatchdogCheck(GroundRollWatchdogSeconds,
                        "el avion no alcanza Vr rodando por pista. Revisa IAS " +
                        "obj/real en la telemetria: si el motor no sube mientras la " +
                        "velocidad esta por debajo del objetivo, el override de motores " +
                        "no esta surtiendo efecto en este avion.");
                }
                break;

            case TakeoffPhase.Rotate:
                HoldPitch(_style.RotatePitchDeg, dt);
                HoldHeading(_holdHeadingDeg, dt);
                // "Positive rate, gear up": en cuanto el avion confirma que
                // ya no esta en pista y sube de verdad, tren arriba. No se
                // espera a ganar altura: un piloto sube el tren nada mas
                // confirmar el regimen de ascenso, no a 50ft.
                if (ConditionSustained(
                        _d.AglMeters.Float * MetersToFeet > _style.GearUpAglFt &&
                        _d.VsFpm.Float > 100f, dt))
                {
                    _controls.SetGearDown(false);
                    LogAction("Positive rate -> Tren arriba");
                    SetPhase(TakeoffPhase.Climb);
                }
                else
                {
                    WatchdogCheck(RotateWatchdogSeconds,
                        "el avion no despega tras rotar. Puede que el pitch objetivo " +
                        "sea insuficiente para este avion/peso, o que el PID de " +
                        "cabeceo no este moviendo el elevador de verdad.");
                }
                break;

            case TakeoffPhase.Climb:
            {
                HoldPitch(_style.ClimbPitchDeg, dt);
                HoldHeading(_holdHeadingDeg, dt);
                // Salir por velocidad (flaps ya pueden irse) o, en Emergencia,
                // por estar lejos del suelo aunque la IAS objetivo siga baja.
                bool speedClean = _d.IasKt.Float > _style.FlapRetract2Kt;
                bool obstacleClear = _style.ObstacleClearAglFt > 0f &&
                    _d.AglMeters.Float * MetersToFeet >= _style.ObstacleClearAglFt;
                if (ConditionSustained(speedClean || obstacleClear, dt))
                {
                    SetPhase(TakeoffPhase.ClimbToTurnAltitude);
                }
                break;
            }

            case TakeoffPhase.ClimbToTurnAltitude:
                HoldPitch(_style.EnroutePitchDeg, dt);
                HoldHeading(_holdHeadingDeg, dt);
                if (ConditionSustained(_d.AltFt.Float >= TurnAltFt, dt))
                {
                    if (_handoffAtTurnAltitude)
                    {
                        LogAction(BlackBoxSnap.Join(
                            $"Fin: despegue combate listo para interceptar — " +
                            $"ALT={_d.AltFt.Float:0}ft IAS={_d.IasKt.Float:0}kt",
                            BlackBoxSnap.Of(FlightState.Capture(_d))));
                        _controls.ReleaseAllOverrides();
                        SetPhase(TakeoffPhase.Done);
                        break;
                    }
                    _holdHeadingDeg = Pid.NormalizeAngleDeg360(_baseHeadingDeg + TurnDeltaDeg);
                    LogAction($"Iniciando viraje: rumbo objetivo nuevo = {_holdHeadingDeg:0}deg");
                    SetPhase(TakeoffPhase.Turn);
                }
                break;

            case TakeoffPhase.Turn:
            {
                HoldPitch(_style.EnroutePitchDeg, dt);
                HoldHeading(_holdHeadingDeg, dt);
                float headingErr = Pid.NormalizeAngleDeg180(_holdHeadingDeg - _d.HeadingDeg.Float);
                if (ConditionSustained(Math.Abs(headingErr) < _style.HeadingCaptureDeg, dt))
                {
                    LogAction($"Rumbo capturado ({_holdHeadingDeg:0}deg)");
                    SetPhase(TakeoffPhase.ClimbToAltitude);
                }
                else
                {
                    WatchdogCheck(TurnWatchdogSeconds,
                        "el viraje no captura el rumbo objetivo. Puede que el PID de " +
                        "alabeo necesite mas ganancia para este avion.");
                }
                break;
            }

            case TakeoffPhase.ClimbToAltitude:
                HoldPitch(_style.EnroutePitchDeg, dt);
                HoldHeading(_holdHeadingDeg, dt);
                if (ConditionSustained(_d.AltFt.Float >= TargetAltFt, dt))
                {
                    SetPhase(TakeoffPhase.LevelOff);
                }
                break;

            case TakeoffPhase.LevelOff:
                HoldPitch(_style.LevelPitchDeg, dt);
                HoldHeading(_holdHeadingDeg, dt);
                if (ConditionSustained(Math.Abs(_d.VsFpm.Float) < 300f, dt))
                {
                    LogAction(BlackBoxSnap.Join(
                        $"Fin: despegue completado — ALT={_d.AltFt.Float:0}ft IAS={_d.IasKt.Float:0}kt",
                        BlackBoxSnap.Of(FlightState.Capture(_d))));
                    _controls.ReleaseAllOverrides();
                    SetPhase(TakeoffPhase.Done);
                }
                break;
        }

        LastVsTarget = Phase == TakeoffPhase.LevelOff ? 0f : float.NaN;
    }

    // --- internos ---------------------------------------------------------

    // IAS de la fase -> potencia. Se llama todos los frames. Al cambiar el
    // objetivo se siembra el integral con la potencia que ya hay, para no
    // tirar el motor a cero; el termino P empuja alrededor de eso.
    private void ApplySpeedThrottle(float dt)
    {
        float target = IasTargetForPhase();
        if (float.IsNaN(target))
        {
            _iasTarget = float.NaN;
            LastThrottleCmd = 0f;
            _controls.SetThrottle(0f, dt);
            return;
        }

        if (float.IsNaN(_iasTarget) || Math.Abs(_iasTarget - target) > 0.5f)
        {
            _speedPid.SeedTrim(_controls.ThrottleReadback);
            _iasTarget = target;
            LogAction($"IAS objetivo -> {target:0} kt ({PhaseName(Phase)})");
        }

        LastThrottleCmd = _speedPid.Update(target - _d.IasKt.Float, dt);
        _controls.SetThrottle(LastThrottleCmd, dt);
    }

    private float IasTargetForPhase() => Phase switch
    {
        TakeoffPhase.ThrottleUp or TakeoffPhase.GroundRoll or TakeoffPhase.Rotate
            => _style.RollIasKt,
        TakeoffPhase.Climb => _style.CleanupIasKt,
        TakeoffPhase.ClimbToTurnAltitude or TakeoffPhase.Turn or TakeoffPhase.ClimbToAltitude
            => _style.ClimbIasKt,
        TakeoffPhase.LevelOff => _style.CruiseIasKt,
        _ => float.NaN,
    };

    // Retraccion por velocidad, en cualquier fase aerea. El paso a flaps 0
    // espera un poco (el mismo retraso del estilo) para no limpiar por un
    // pico de un frame. UpdateFlapSchedule no usa _reactionTimer: ese lo
    // gastan las transiciones de fase.
    private void UpdateFlapSchedule(float dt)
    {
        float ias = _d.IasKt.Float;
        if (LastFlapCmd > 0.1f + 1e-3f && ias > _style.FlapRetract1Kt)
        {
            _controls.SetFlaps(0.1f);
            LastFlapCmd = 0.1f;
            _flapTimer = 0f;
            LogAction($"Flaps -> 10% (IAS={ias:0}kt)");
        }

        if (LastFlapCmd > 0.001f && ias > _style.FlapRetract2Kt)
        {
            _flapTimer += dt;
            if (_flapTimer < _style.ReactionDelaySeconds) return;
            _controls.SetFlaps(0f);
            LastFlapCmd = 0f;
            LogAction($"Flaps -> 0% limpios (IAS={ias:0}kt)");
        }
        else
        {
            _flapTimer = 0f;
        }
    }

    private void SetPhase(TakeoffPhase newPhase)
    {
        if (newPhase == Phase) return;
        string iasObj = float.IsNaN(_iasTarget) ? "--" : _iasTarget.ToString("0", CultureInfo.InvariantCulture);
        LogAction($"fase {PhaseName(Phase)} → {PhaseName(newPhase)} " +
                  $"(IAS={_d.IasKt.Float:0}kt ALT={_d.AltFt.Float:0}ft " +
                  $"AGL={_d.AglMeters.Float * MetersToFeet:0}ft " +
                  $"pitch={_d.PitchDeg.Float:0.0} bank={_d.BankDeg.Float:0.0} " +
                  $"hdg={_d.HeadingDeg.Float:0} ias_obj={iasObj} " +
                  $"thr_mando={LastThrottleCmd:0.00} " +
                  $"thr_real={_controls.ThrottleReadback:0.00})");
        Phase = newPhase;
        _phaseElapsed = 0f;
        _watchdogWarned = false;
        _reactionTimer = 0f;
        _flapTimer = 0f;
        if (newPhase == TakeoffPhase.Climb) GearDownCommanded = false;
        if (newPhase == TakeoffPhase.Prep) GearDownCommanded = true;
    }

    // "Tiempo de reaccion humana": una condicion que dispara un paso del
    // guion (Vr alcanzada, altitud capturada, rumbo capturado...) tiene que
    // mantenerse cumplida durante ReactionDelaySeconds seguidos antes de que
    // actuemos, en vez de saltar el mismo frame en que se cumple. Sin esto
    // la IA reacciona con precision de simulador, que es justo lo que la
    // delata como robotica; un piloto real tarda un rato en darse cuenta y
    // mover la mano.
    private bool ConditionSustained(bool met, float dt)
    {
        if (!met) { _reactionTimer = 0f; return false; }
        _reactionTimer += dt;
        return _reactionTimer >= _style.ReactionDelaySeconds;
    }

    // Si llevamos demasiado tiempo en la fase actual sin cumplir su
    // objetivo, avisa UNA vez (no cada frame) y sigue intentandolo -- no
    // aborta solo, para que se pueda ver que pasa y decidir.
    private void WatchdogCheck(float maxSeconds, string hint)
    {
        if (_watchdogWarned || _phaseElapsed < maxSeconds) return;
        _watchdogWarned = true;
        LogAction($"AVISO: llevo {_phaseElapsed:0}s en fase '{PhaseName(Phase)}' " +
                  $"sin cumplir el objetivo. {hint}");
    }

    // --- PID de cabeceo: error de pitch (deg) -> yoke_pitch_ratio (-1..1) -
    //
    // El objetivo de pitch de cada fase cambia de golpe en las transiciones.
    // En vez de pasarselo tal cual al PID -- que perseguiria un salto de
    // hasta 12deg y daria una cabezada notoria aunque el SlewLimiter de
    // salida lo suavice despues -- se pasa primero por una rampa que avanza
    // el objetivo a un ritmo de pitch humano. Asi el objetivo que ve el PID
    // ya es progresivo, como un piloto tirando del yugo poco a poco.
    private void HoldPitch(float targetDeg, float dt)
    {
        _pitchTargetRamp.MaxRate = _style.PitchRampDegPerSec;
        float rampedTarget = _pitchTargetRamp.Update(targetDeg, dt);
        LastPitchTarget = rampedTarget;
        float error = rampedTarget - _d.PitchDeg.Float;
        _controls.SetPitchInput(_pitchPid.Update(error, dt), dt);
    }

    // --- Cascada de rumbo en vuelo: error de rumbo -> bank objetivo ->
    //     error de bank -> yoke_roll_ratio (-1..1) -------------------------
    private void HoldHeading(float targetHeadingDeg, float dt)
    {
        float headingErr = Pid.NormalizeAngleDeg180(targetHeadingDeg - _d.HeadingDeg.Float);
        float maxBankDeg = _style.MaxBankDeg;
        float targetBankDeg = Math.Clamp(HeadingToBankGain * headingErr, -maxBankDeg, maxBankDeg);
        LastBankTarget = targetBankDeg;
        float bankErr = targetBankDeg - _d.BankDeg.Float;
        _controls.SetRollInput(_bankPid.Update(bankErr, dt), dt);
    }

    // --- Mantener el eje de pista en tierra: PID directo de rumbo sobre el
    //     timon/rueda de morro, sin pasar por alabeo (con el avion en
    //     tierra, alabear no gira el morro) -------------------------------
    private void HoldRunwayHeading(float dt)
    {
        LastBankTarget = float.NaN;
        float headingErr = Pid.NormalizeAngleDeg180(_baseHeadingDeg - _d.HeadingDeg.Float);
        _controls.SetYawInput(_yawPid.Update(headingErr, dt), dt);
    }

    // Ajusta la subescala del altimetro (piloto y copiloto) al QNH que la
    // propia meteo de X-Plane tiene simulado en la posicion actual del
    // avion. No depende de ningun aeropuerto: usa la presion real de la
    // casilla de meteo en la que esta el avion, venga de "real weather" o
    // de un preset manual.
    private void SetBarometerToLocalQnh()
    {
        float qnhInHg = _d.QnhPas.Float / PascalsPerInHg;
        if (qnhInHg < MinPlausibleQnhInHg || qnhInHg > MaxPlausibleQnhInHg)
        {
            if (!_baroWarnedOutOfRange)
            {
                LogAction($"AVISO: QNH leido de la meteo fuera de rango " +
                          $"({qnhInHg.ToString("0.00", CultureInfo.InvariantCulture)} inHg); " +
                          "no se toca el altimetro.");
                _baroWarnedOutOfRange = true;
            }
            return;
        }
        // Set y no Hold: el objetivo es ganar la pelea frame a frame contra
        // la logica de cabina del avion, y eso ya lo consigue reescribirlo
        // cada frame desde aqui. Con Hold, ademas, "soltar" restauraria el
        // baro al valor viejo al terminar la secuencia, que es justo lo
        // contrario de lo que se quiere.
        _controls.SetBarometer(qnhInHg);
        if (!_baroLogged)
        {
            LogAction($"Altimetro -> {qnhInHg.ToString("0.00", CultureInfo.InvariantCulture)} " +
                      "inHg (QNH local de la meteo)");
            _baroLogged = true;
        }
    }

    private void LogAction(string msg) => ActionLogged?.Invoke(msg);

    public static string PhaseName(TakeoffPhase p) => p switch
    {
        TakeoffPhase.Idle => "En espera",
        TakeoffPhase.Prep => "Preparando (freno/flaps)",
        TakeoffPhase.ThrottleUp => "Aplicando potencia",
        TakeoffPhase.GroundRoll => "Carrera de despegue",
        TakeoffPhase.Rotate => "Rotando",
        TakeoffPhase.Climb => "Ascenso inicial / limpiando flaps",
        TakeoffPhase.ClimbToTurnAltitude => "Subiendo a altitud de viraje",
        TakeoffPhase.Turn => "Viraje de salida",
        TakeoffPhase.ClimbToAltitude => "Subiendo a altitud objetivo",
        TakeoffPhase.LevelOff => "Nivelando",
        TakeoffPhase.Done => "Secuencia completada",
        _ => "?",
    };

    // Codigos cortos para el checklist de una linea de la UI. El orden
    // coincide con el de ejecucion real.
    private static readonly (TakeoffPhase Phase, string Label)[] ChecklistSteps =
    {
        (TakeoffPhase.Prep, "Preparar"),
        (TakeoffPhase.ThrottleUp, "Potencia"),
        (TakeoffPhase.GroundRoll, "Pista"),
        (TakeoffPhase.Rotate, "Rotar"),
        (TakeoffPhase.Climb, "Flaps"),
        (TakeoffPhase.ClimbToTurnAltitude, "Subida"),
        (TakeoffPhase.Turn, "Viraje"),
        (TakeoffPhase.ClimbToAltitude, "Altitud"),
        (TakeoffPhase.LevelOff, "Nivelar"),
    };

    public static string BuildChecklistLine(TakeoffPhase current)
    {
        var sb = new System.Text.StringBuilder();
        foreach ((TakeoffPhase phase, string label) in ChecklistSteps)
        {
            char marker = current == TakeoffPhase.Done || current > phase ? 'x'
                        : current == phase ? '>'
                        : ' ';
            if (sb.Length > 0) sb.AppendLine();
            sb.Append('[').Append(marker).Append("] ").Append(label);
        }
        return sb.ToString();
    }

    // --- Constantes del PID (ajustables aqui si oscila demasiado) ---------
    private const float PitchKp = 0.09f;
    private const float PitchKi = 0.015f;
    private const float PitchKd = 0.03f;

    private const float BankKp = 0.035f;
    private const float BankKi = 0.004f;
    private const float BankKd = 0.012f;

    private const float YawKp = 0.04f;
    private const float YawKi = 0.002f;
    private const float YawKd = 0.02f;

    private const float HeadingToBankGain = 1.2f;  // deg de bank por deg de error de rumbo

    // Autothrottle. 0.04 * 25 kt = potencia a fondo: por debajo de eso el
    // termino P ya satura y la carrera sale con motor a tope. Ki solo
    // aprende el ajuste fino cuando ya se esta cerca de la IAS.
    private const float SpeedKp = 0.04f;
    private const float SpeedKi = 0.012f;
    private const float SpeedKd = 0f;

    // Alabeo, rampa de pitch, reaccion y captura de rumbo viven en el
    // TakeoffStyle, no aqui: cada salida (relajado / combate / emergencia)
    // trae los suyos.

    private const float MetersToFeet = 3.28084f;

    // --- Conversion/validacion de QNH -------------------------------------
    private const float PascalsPerInHg = 3386.389f;
    private const float MinPlausibleQnhInHg = 25f;  // ~845 hPa, huracan extremo
    private const float MaxPlausibleQnhInHg = 32f;  // ~1084 hPa, anticiclon extremo

    // --- Umbrales de los watchdogs de diagnostico (segundos) --------------
    private const float ThrottleWatchdogSeconds = 8f;
    private const float GroundRollWatchdogSeconds = 60f;
    private const float RotateWatchdogSeconds = 20f;
    private const float TurnWatchdogSeconds = 30f;
}
