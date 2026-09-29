namespace AICopilotCore.Domain;

// Una muestra tipada de la caja negra. Convive con la linea CSV de
// DataLogger: el CSV es para leer fuera de la UI; este struct es para
// pintar graficos sin tener que parsear texto a 10 Hz.
public readonly struct BlackBoxSample
{
    // Segundos desde el arranque del proceso (Stopwatch compartido del logger).
    // Eje X estable aunque el reloj del sistema salte.
    public readonly double TSec;

    public readonly float IasKt;
    public readonly float AltFt;
    public readonly float AglFt;
    public readonly float VsFpm;
    public readonly float PitchObjDeg;
    public readonly float PitchRealDeg;
    public readonly float BankObjDeg;
    public readonly float BankRealDeg;
    public readonly float HeadingObjDeg;
    public readonly float HeadingRealDeg;
    public readonly float GNormal;
    public readonly float GCommand;
    public readonly float GPredicted;
    public readonly float AoaDeg;
    public readonly float PitchCmd;
    public readonly float RollCmd;
    public readonly float YawCmd;
    public readonly float ThrottleObj01;
    public readonly float ThrottleReal01;
    public readonly float FlapsObj01;
    public readonly float FlapsReal01;
    public readonly float Speedbrake01;
    public readonly float WeightLb;
    public readonly float Mach;
    public readonly float PitchRateDps;
    public readonly float RollRateDps;

    public BlackBoxSample(
        double tSec,
        float iasKt, float altFt, float aglFt, float vsFpm,
        float pitchObjDeg, float pitchRealDeg,
        float bankObjDeg, float bankRealDeg,
        float headingObjDeg, float headingRealDeg,
        float gNormal, float gCommand, float gPredicted, float aoaDeg,
        float pitchCmd, float rollCmd, float yawCmd,
        float throttleObj01, float throttleReal01,
        float flapsObj01, float flapsReal01,
        float speedbrake01, float weightLb, float mach,
        float pitchRateDps, float rollRateDps)
    {
        TSec = tSec;
        IasKt = iasKt; AltFt = altFt; AglFt = aglFt; VsFpm = vsFpm;
        PitchObjDeg = pitchObjDeg; PitchRealDeg = pitchRealDeg;
        BankObjDeg = bankObjDeg; BankRealDeg = bankRealDeg;
        HeadingObjDeg = headingObjDeg; HeadingRealDeg = headingRealDeg;
        GNormal = gNormal; GCommand = gCommand; GPredicted = gPredicted;
        AoaDeg = aoaDeg;
        PitchCmd = pitchCmd; RollCmd = rollCmd; YawCmd = yawCmd;
        ThrottleObj01 = throttleObj01; ThrottleReal01 = throttleReal01;
        FlapsObj01 = flapsObj01; FlapsReal01 = flapsReal01;
        Speedbrake01 = speedbrake01; WeightLb = weightLb; Mach = mach;
        PitchRateDps = pitchRateDps; RollRateDps = rollRateDps;
    }
}
