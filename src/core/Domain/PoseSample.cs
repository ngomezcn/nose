using AICopilotCore.Domain.Agents;

namespace AICopilotCore.Domain;

// Pose y ritmo de frame de un avion para las columnas extra del CSV de la caja
// negra. NaN = dato no disponible (se escribe vacio).
public readonly record struct PoseSample(
    string Body,                      // Local / Observando / Simulando
    double X, double Y, double Z,     // posicion local OGL (m)
    double Vx, double Vy, double Vz,  // velocidad local (m/s)
    float GsKt,
    TickCadenceSummary Cadence,
    float HeadingObservedDeg,         // rumbo leido de X-Plane (vs el del modelo en State)
    string PhysicsAdaptation)
{
    public static PoseSample None => new("", double.NaN, double.NaN, double.NaN,
        double.NaN, double.NaN, double.NaN, float.NaN, TickCadenceSummary.Empty, float.NaN, "");
}
