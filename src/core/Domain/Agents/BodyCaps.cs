namespace AICopilotCore.Domain.Agents;

// Lo que el cuerpo (avion) NO puede hacer o no expone. El body DECLARA la
// carencia; el piloto adapta la maniobra (regla "nada de fallbacks": nunca se
// corta una maniobra por esto). Default de los campos = capacidad presente.
public readonly record struct BodyCaps(
    bool HasBarometer,
    bool HasParkBrake,
    bool HasTerrainProbe,      // AGL real (y_agl) disponible
    bool HasFlaps,
    bool HasSpeedbrake,
    bool HasRetractableGear,
    bool HasGearReadback,      // Kinematics.GearRatio es lectura real y no la palanca
    bool HasThrottleReadback,
    bool HasAoa)
{
    public static BodyCaps Full => new(true, true, true, true, true, true, true, true, true);
}
