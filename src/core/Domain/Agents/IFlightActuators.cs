namespace AICopilotCore.Domain.Agents;

// API publica de AircraftControls (mismas firmas), como contrato.
public interface IFlightActuators
{
    void EnableThrottleOverride();
    void DisableThrottleOverride();
    void SetThrottle(float target, float dt);
    float ThrottleReadback { get; }

    void SetFlaps(float value);
    void SetSpeedbrake(float ratio01);

    void ReleaseParkingBrake();
    void SetParkingBrake();
    void ApplyGroundIdle();

    void SetGearDown(bool down);
    void SetBarometer(float inHg);

    void EnablePitchOverride();
    void DisablePitchOverride();
    void SetPitchInput(float target, float dt);
    float PitchInputCmd { get; }

    void EnableRollOverride();
    void DisableRollOverride();
    void SetRollInput(float target, float dt);
    float RollInputCmd { get; }

    void EnableYawOverride();
    void DisableYawOverride();
    void SetYawInput(float target, float dt);
    float YawInputCmd { get; }

    void ReleaseAllOverrides();
    void ForgetOverrideState();
}
