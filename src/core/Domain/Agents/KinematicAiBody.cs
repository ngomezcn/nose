using AICopilotCore.Connector;

namespace AICopilotCore.Domain.Agents;

// Cuerpo de una IA de X-Plane (indice XPLM 1..19) que se pilota con las MISMAS
// secuencias que el ownship. X-Plane no simula su vuelo (la IA es cinematica),
// asi que lo integramos aqui con las prestaciones de un IFlightProfile.
//
// Dos modos:
//   Observing (por defecto): no se toca nada. State es reducido (IAS/TAS/Mach/
//     rumbo/pitch/bank/V-S desde planeN_*; G y AoA = NaN, ritmos = 0, peso y
//     VNE del perfil) y TryGetKinematics lee Other*.
//   Simulating: se activa al primer Enable*Override o al primer comando de
//     mando (SetThrottle/SetPitchInput/SetGearDown/...). Toma AiControl(idx),
//     captura pose+velocidad y desde entonces Step(dt) integra
//     (KinematicFlightModel) y reescribe la pose por Hold como AiStraightHold.
//     Si aun no hay telemetria, la captura queda pendiente y se reintenta en Step.
//
// Suelo: terreno plano en la Y capturada (AglSource lo declara). Si se adquiere
// en el aire no se conoce el terreno: se asume AssumedAglOnAirCaptureM por
// debajo (ajustable con SetGroundLevelY).
//
// Al soltar (ReleaseAllOverrides): Holds.h restaura al soltar el valor que el
// dataref tenia ANTES del primer Hold (la pose capturada), y un Hold final con
// el valor actual NO lo evita (un Hold sobre un id ya activo no recaptura
// `previous`). Por eso se suelta cada Hold y, DESPUES, se escribe la pose
// actual con SetMany (pisa el valor restaurado en el mismo tick) y por ultimo
// ReleaseAiControl(idx). Ojo: X-Plane no reactiva la IA nativa de un solo
// avion; solo lo hace cuando se vacia el conjunto de aviones tomados, asi que
// el avion suelto sigue su ultimo vector de velocidad hasta entonces.
//
// Tren: se escribe planeN_gear_deploy (existe en Datarefs.OtherGearRatio).
// Flaps y aerofrenos son solo estado interno (afectan a sustentacion/
// resistencia): Datarefs.cs no tiene planeN_flap_ratio/speedbrake_ratio.
public sealed class KinematicAiBody : IAircraftBody
{
    public enum BodyMode { Observing, Simulating }

    public const double AssumedAglOnAirCaptureM = 1500.0;

    private readonly ConnectorClient _c;
    private readonly Datarefs _d;
    private readonly int _slot;
    private readonly IFlightProfile _profile;
    private readonly KinematicFlightModel _model;
    private readonly object _gate = new();

    private BodyMode _mode = BodyMode.Observing;
    private bool _wantSim;
    private bool _holding;
    private double _forcedGroundY = double.NaN;
    private FlightState _state;

    public KinematicAiBody(ConnectorClient client, Datarefs datarefs, int xplmIndex,
                           IFlightProfile? profile = null)
    {
        if (xplmIndex < 1 || xplmIndex > Datarefs.OtherPlaneSlots)
            throw new ArgumentOutOfRangeException(nameof(xplmIndex), "1..19");
        _c = client;
        _d = datarefs;
        _slot = xplmIndex - 1;
        XplmIndex = xplmIndex;
        _profile = profile ?? A330Profile.Instance;
        _model = new KinematicFlightModel(_profile);
        _model.Adapted = t => ActionLogged?.Invoke($"IA {XplmIndex}: {t}");
        _state = BuildObserved();
    }

    public event Action<string>? ActionLogged;

    public int XplmIndex { get; }
    public bool IsLocal => false;
    public IFlightProfile Profile => _profile;

    public BodyMode Mode { get { lock (_gate) return _mode; } }

    // Ultimo recorte/adaptacion de la fisica ("" = ninguno).
    public string AdaptationText { get { lock (_gate) return _model.AdaptationText; } }

    public BodyCaps Caps
    {
        get
        {
            lock (_gate)
                return BodyCaps.Full with
                {
                    HasBarometer = false,
                    HasTerrainProbe = false,
                    HasAoa = _mode == BodyMode.Simulating,
                };
        }
    }

    // Fija el nivel del terreno (Y local) si se conoce; NaN = el de la captura.
    public void SetGroundLevelY(double y) { lock (_gate) _forcedGroundY = y; }

    // --- Sensores -----------------------------------------------------------

    public void Sense()
    {
        lock (_gate)
            _state = _mode == BodyMode.Simulating ? BuildSimulated() : BuildObserved();
    }

    public FlightState State { get { lock (_gate) return _state; } }

    public bool TryGetKinematics(out Kinematics k)
    {
        lock (_gate)
        {
            if (_mode == BodyMode.Simulating)
            {
                k = new Kinematics(_model.X, _model.Y, _model.Z,
                                   _model.Vx, _model.Vy, _model.Vz,
                                   _model.HeadingDeg, _model.Gear, _model.AltMslM);
                return true;
            }
            DataHandle x = _d.OtherLocalX[_slot], y = _d.OtherLocalY[_slot], z = _d.OtherLocalZ[_slot];
            DataHandle vx = _d.OtherVelX[_slot], vz = _d.OtherVelZ[_slot], psi = _d.OtherHeadingDeg[_slot];
            if (!x.HasValue || !y.HasValue || !z.HasValue || !vx.HasValue || !vz.HasValue || !psi.HasValue)
            {
                k = default;
                return false;
            }
            DataHandle vy = _d.OtherVelY[_slot], el = _d.OtherElevMeters[_slot], gear = _d.OtherGearRatio[_slot];
            k = new Kinematics(x.Value, y.Value, z.Value, vx.Value, vy.HasValue ? vy.Value : 0.0, vz.Value,
                               (float)psi.Value, gear.HasValue ? gear.Float : 1f,
                               el.HasValue ? el.Value : double.NaN);
            return true;
        }
    }

    public float AglMeters
    {
        get { lock (_gate) return _mode == BodyMode.Simulating ? (float)_model.AglM : float.NaN; }
    }
    public string AglSource => "terreno plano en el punto de captura";
    public float FlapRatio { get { lock (_gate) return _mode == BodyMode.Simulating ? _model.Flap : 0f; } }
    public float SpeedbrakeRatio { get { lock (_gate) return _mode == BodyMode.Simulating ? _model.Speedbrake : 0f; } }
    public float QnhInHg => 29.92f;

    private FlightState BuildObserved()
    {
        DataHandle vx = _d.OtherVelX[_slot], vz = _d.OtherVelZ[_slot], vy = _d.OtherVelY[_slot];
        DataHandle el = _d.OtherElevMeters[_slot], psi = _d.OtherHeadingDeg[_slot];
        DataHandle the = _d.OtherPitchDeg[_slot], phi = _d.OtherBankDeg[_slot];
        if (!vx.HasValue || !vz.HasValue || !el.HasValue)
            return new FlightState(float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                                   float.NaN, float.NaN, float.NaN, float.NaN, 0f, 0f,
                                   _profile.ReferenceWeightLb, _profile.VneKt, false);

        double vyv = vy.HasValue ? vy.Value : 0.0;
        double speed = Math.Sqrt(vx.Value * vx.Value + vz.Value * vz.Value + vyv * vyv);
        float altFt = (float)(el.Value * KinematicFlightModel.MToFt);
        float tasKt = (float)(speed * KinematicFlightModel.MpsToKt);
        float iasKt = _profile.IndicatedAirspeedKt(tasKt, altFt);
        return new FlightState(
            IasKt: iasKt, AltFt: altFt, AglFt: float.NaN,
            VsFpm: (float)(vyv * 196.8504),
            PitchDeg: the.HasValue ? the.Float : float.NaN,
            BankDeg: phi.HasValue ? phi.Float : float.NaN,
            HeadingDeg: psi.HasValue ? psi.Float : float.NaN,
            // Observando no hay G ni AoA medidos: se estiman como vuelo nivelado
            // (1 g) para que el planificador trate igual a todos los aviones.
            GNormal: 1f, AoaDeg: _profile.LevelAoaDeg(iasKt, _profile.ReferenceWeightLb, 1f),
            MachNo: tasKt / _profile.SpeedOfSoundKt(altFt),
            PitchRateDegPerSec: 0f, RollRateDegPerSec: 0f,
            WeightLb: _profile.ReferenceWeightLb, AcfVneKt: _profile.VneKt,
            OnGround: false);
    }

    private FlightState BuildSimulated() => new(
        IasKt: _model.IasKt, AltFt: _model.AltFt, AglFt: (float)(_model.AglM * KinematicFlightModel.MToFt),
        VsFpm: _model.VsFpm, PitchDeg: _model.PitchDeg, BankDeg: _model.BankDeg,
        HeadingDeg: _model.HeadingDeg, GNormal: _model.GNormal, AoaDeg: _model.AoaDeg,
        MachNo: _model.Mach, PitchRateDegPerSec: _model.PitchRateDegPerSec,
        RollRateDegPerSec: _model.RollRateDegPerSec, WeightLb: _model.WeightLb,
        AcfVneKt: _profile.VneKt, OnGround: _model.OnGround);

    // --- Paso ---------------------------------------------------------------

    public void Step(float dt)
    {
        lock (_gate)
        {
            if (_mode == BodyMode.Observing && _wantSim) TryAcquire();
            if (_mode != BodyMode.Simulating || !(dt > 0f)) return;
            _model.Step(dt);
            WriteHolds();
        }
    }

    public void OnConnectionLost() => ForgetOverrideState();

    // Pipe caido: el SafetyGuard del connector ya solto los holds.
    public void ForgetOverrideState()
    {
        lock (_gate)
        {
            _mode = BodyMode.Observing;
            _wantSim = false;
            _holding = false;
        }
    }

    // --- Modo Simulating ----------------------------------------------------

    // Llamar con _gate cogido.
    private void RequestSim()
    {
        _wantSim = true;
        if (_mode == BodyMode.Observing) TryAcquire();
    }

    private bool TryAcquire()
    {
        DataHandle x = _d.OtherLocalX[_slot], y = _d.OtherLocalY[_slot], z = _d.OtherLocalZ[_slot];
        DataHandle vx = _d.OtherVelX[_slot], vz = _d.OtherVelZ[_slot], psi = _d.OtherHeadingDeg[_slot];
        if (!x.HasValue || !y.HasValue || !z.HasValue || !vx.HasValue || !vz.HasValue || !psi.HasValue)
            return false;

        DataHandle vy = _d.OtherVelY[_slot], el = _d.OtherElevMeters[_slot];
        DataHandle the = _d.OtherPitchDeg[_slot], phi = _d.OtherBankDeg[_slot], gear = _d.OtherGearRatio[_slot];

        _c.TakeAiControl(XplmIndex);
        _model.Capture(x.Value, y.Value, z.Value, vx.Value, vy.HasValue ? vy.Value : 0.0, vz.Value,
                       psi.Value, the.HasValue ? the.Value : double.NaN, phi.HasValue ? phi.Value : 0.0,
                       el.HasValue ? el.Value : double.NaN, gear.HasValue ? gear.Value : 1.0,
                       _forcedGroundY, AssumedAglOnAirCaptureM);
        _mode = BodyMode.Simulating;
        _state = BuildSimulated();
        ActionLogged?.Invoke($"IA {XplmIndex}: control cinematico tomado " +
                             $"({(_model.OnGround ? "en tierra" : "en el aire")}, perfil {_profile.Name}, " +
                             $"IAS {_model.IasKt:0} kt, alt {_model.AltFt:0} ft)");
        WriteHolds();
        return true;
    }

    private void WriteHolds()
    {
        _c.Hold(_d.OtherLocalX[_slot], _model.X);
        _c.Hold(_d.OtherLocalY[_slot], _model.Y);
        _c.Hold(_d.OtherLocalZ[_slot], _model.Z);
        _c.Hold(_d.OtherHeadingDeg[_slot], _model.HeadingDeg);
        _c.Hold(_d.OtherPitchDeg[_slot], _model.PitchDeg);
        _c.Hold(_d.OtherBankDeg[_slot], _model.BankDeg);
        _c.Hold(_d.OtherVelX[_slot], _model.Vx);
        _c.Hold(_d.OtherVelY[_slot], _model.Vy);
        _c.Hold(_d.OtherVelZ[_slot], _model.Vz);
        _c.Hold(_d.OtherGearRatio[_slot], _model.Gear);
        _holding = true;
    }

    // Suelta la IA sin salto: ver cabecera.
    private void Release()
    {
        if (_mode != BodyMode.Simulating)
        {
            _wantSim = false;
            return;
        }
        DataHandle[] hs =
        {
            _d.OtherLocalX[_slot], _d.OtherLocalY[_slot], _d.OtherLocalZ[_slot],
            _d.OtherHeadingDeg[_slot], _d.OtherPitchDeg[_slot], _d.OtherBankDeg[_slot],
            _d.OtherVelX[_slot], _d.OtherVelY[_slot], _d.OtherVelZ[_slot], _d.OtherGearRatio[_slot],
        };
        if (_holding) foreach (DataHandle h in hs) _c.Release(h);
        _c.SetMany(
            (hs[0], _model.X), (hs[1], _model.Y), (hs[2], _model.Z),
            (hs[3], _model.HeadingDeg), (hs[4], _model.PitchDeg), (hs[5], _model.BankDeg),
            (hs[6], _model.Vx), (hs[7], _model.Vy), (hs[8], _model.Vz), (hs[9], _model.Gear));
        _c.ReleaseAiControl(XplmIndex);
        _holding = false;
        _mode = BodyMode.Observing;
        _wantSim = false;
        ActionLogged?.Invoke($"IA {XplmIndex}: control cinematico soltado en la pose actual " +
                             "(X-Plane no reactiva la IA nativa hasta vaciarse el conjunto de tomadas)");
    }

    // --- IFlightActuators ---------------------------------------------------
    // Todo comando de mando pasa a Simulating; con el modelo aun sin capturar
    // (sin telemetria) el valor pedido se aplica tras la captura en el
    // siguiente comando de la secuencia (las secuencias los repiten cada frame).

    public void EnableThrottleOverride() { lock (_gate) RequestSim(); }
    public void DisableThrottleOverride() { }

    public void SetThrottle(float target, float dt)
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.ThrottleCmd = Math.Clamp(target, 0f, 1f); }
    }
    public float ThrottleReadback { get { lock (_gate) return _mode == BodyMode.Simulating ? _model.Throttle : 0f; } }

    public void SetFlaps(float value)
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.FlapTarget = Math.Clamp(value, 0f, 1f); }
    }
    public void SetSpeedbrake(float ratio01)
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.SpeedbrakeTarget = Math.Clamp(ratio01, 0f, 1f); }
    }

    public void ReleaseParkingBrake()
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.ParkingBrake = false; }
    }
    public void SetParkingBrake()
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.ParkingBrake = true; }
    }
    public void ApplyGroundIdle() => SetThrottle(0f, 0f);

    public void SetGearDown(bool down)
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.GearTarget = down ? 1f : 0f; }
    }
    public void SetBarometer(float inHg) { }   // Caps.HasBarometer = false

    public void EnablePitchOverride() { lock (_gate) RequestSim(); }
    public void DisablePitchOverride() { lock (_gate) if (_mode == BodyMode.Simulating) _model.PitchStick = 0f; }
    public void SetPitchInput(float target, float dt)
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.PitchStick = Math.Clamp(target, -1f, 1f); }
    }
    public float PitchInputCmd { get { lock (_gate) return _model.PitchStick; } }

    public void EnableRollOverride() { lock (_gate) RequestSim(); }
    public void DisableRollOverride() { lock (_gate) if (_mode == BodyMode.Simulating) _model.RollStick = 0f; }
    public void SetRollInput(float target, float dt)
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.RollStick = Math.Clamp(target, -1f, 1f); }
    }
    public float RollInputCmd { get { lock (_gate) return _model.RollStick; } }

    public void EnableYawOverride() { lock (_gate) RequestSim(); }
    public void DisableYawOverride() { lock (_gate) if (_mode == BodyMode.Simulating) _model.YawStick = 0f; }
    public void SetYawInput(float target, float dt)
    {
        lock (_gate) { RequestSim(); if (_mode == BodyMode.Simulating) _model.YawStick = Math.Clamp(target, -1f, 1f); }
    }
    public float YawInputCmd { get { lock (_gate) return _model.YawStick; } }

    public void ReleaseAllOverrides() { lock (_gate) Release(); }
}
