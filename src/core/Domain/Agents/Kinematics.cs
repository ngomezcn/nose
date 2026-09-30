namespace AICopilotCore.Domain.Agents;

// Cinematica de un avion en el marco local OGL de X-Plane (mismos campos que
// TargetSnapshot de Intercept.cs, sin XplmIndex/Valid: la validez la da
// IAircraftBody.TryGetKinematics).
public readonly record struct Kinematics(
    double X, double Y, double Z,        // posicion local OGL (m)
    double Vx, double Vy, double Vz,     // velocidad local OGL (m/s)
    float HeadingDeg,                    // psi verdadero
    float GearRatio,                     // 0 = recogido, 1 = fuera
    double ElevationM)                   // MSL (m)
{
    public double GroundSpeedMps => Math.Sqrt(Vx * Vx + Vz * Vz);
}
