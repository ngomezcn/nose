using AICopilotCore.Connector;

namespace AICopilotCore.Domain.Agents;

// Ownship: envuelve Datarefs + AircraftControls. Delegacion explicita, sin
// depender de que AircraftControls implemente IFlightActuators.
public sealed class LocalAircraftBody : IAircraftBody
{
    private const float PascalsPerInHg = 3386.389f;
    private readonly Datarefs _d;
    private readonly AircraftControls _ctl;
    private FlightState _state;

    public LocalAircraftBody(Datarefs datarefs, AircraftControls controls)
    {
        _d = datarefs;
        _ctl = controls;
        _state = FlightState.Capture(datarefs);
    }

    public int XplmIndex => 0;
    public bool IsLocal => true;
    public BodyCaps Caps => BodyCaps.Full with { HasGearReadback = false };

    public void Sense() => _state = FlightState.Capture(_d);
    public FlightState State => _state;

    public bool TryGetKinematics(out Kinematics k)
    {
        k = default;
        if (!_d.LocalX.HasValue || !_d.LocalY.HasValue || !_d.LocalZ.HasValue) return false;
        // Vy y elevacion MSL reales (local_vy / elevation).
        k = new Kinematics(
            _d.LocalX.Value, _d.LocalY.Value, _d.LocalZ.Value,
            _d.LocalVx.Value, _d.LocalVy.Value, _d.LocalVz.Value,
            _state.HeadingDeg,
            _d.GearHandleDown.Float,   // palanca, no despliegue real
            _d.ElevMeters.Value);
        return true;
    }

    public float AglMeters => _d.AglMeters.Float;
    public string AglSource => "sim/flightmodel/position/y_agl";
    public float FlapRatio => _d.FlapHandle.Float;
    public float SpeedbrakeRatio => _d.SpeedbrakeHandle.Float;
    public float QnhInHg => _d.QnhPas.Float / PascalsPerInHg;

    public void Step(float dt) { }
    public void OnConnectionLost() => _ctl.ForgetOverrideState();

    public void EnableThrottleOverride() => _ctl.EnableThrottleOverride();
    public void DisableThrottleOverride() => _ctl.DisableThrottleOverride();
    public void SetThrottle(float target, float dt) => _ctl.SetThrottle(target, dt);
    public float ThrottleReadback => _ctl.ThrottleReadback;
    public void SetFlaps(float value) => _ctl.SetFlaps(value);
    public void SetSpeedbrake(float ratio01) => _ctl.SetSpeedbrake(ratio01);
    public void ReleaseParkingBrake() => _ctl.ReleaseParkingBrake();
    public void SetParkingBrake() => _ctl.SetParkingBrake();
    public void ApplyGroundIdle() => _ctl.ApplyGroundIdle();
    public void SetGearDown(bool down) => _ctl.SetGearDown(down);
    public void SetBarometer(float inHg) => _ctl.SetBarometer(inHg);
    public void EnablePitchOverride() => _ctl.EnablePitchOverride();
    public void DisablePitchOverride() => _ctl.DisablePitchOverride();
    public void SetPitchInput(float target, float dt) => _ctl.SetPitchInput(target, dt);
    public float PitchInputCmd => _ctl.PitchInputCmd;
    public void EnableRollOverride() => _ctl.EnableRollOverride();
    public void DisableRollOverride() => _ctl.DisableRollOverride();
    public void SetRollInput(float target, float dt) => _ctl.SetRollInput(target, dt);
    public float RollInputCmd => _ctl.RollInputCmd;
    public void EnableYawOverride() => _ctl.EnableYawOverride();
    public void DisableYawOverride() => _ctl.DisableYawOverride();
    public void SetYawInput(float target, float dt) => _ctl.SetYawInput(target, dt);
    public float YawInputCmd => _ctl.YawInputCmd;
    public void ReleaseAllOverrides() => _ctl.ReleaseAllOverrides();
    public void ForgetOverrideState() => _ctl.ForgetOverrideState();
}
