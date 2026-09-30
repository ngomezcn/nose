using AICopilotCore.Domain;

namespace AICopilotCore.Domain.Agents;

// Perfil por defecto: delega en F14Aero (modelo de libro de texto, ver su
// cabecera). Los retardos son parametros del perfil, no de F14Aero.
public sealed class F14Profile : IFlightProfile
{
    public static readonly F14Profile Instance = new();

    public F14Profile(float pitchLagSec = 0.35f, float rollLagSec = 0.25f)
    {
        PitchLagSec = pitchLagSec;
        RollLagSec = rollLagSec;
    }

    public string Name => "F-14";
    public float ReferenceWeightLb => F14Aero.ReferenceWeightLb;
    public float VneKt => F14Aero.VneSeaLevelKt;
    public float PitchLagSec { get; }
    public float RollLagSec { get; }

    public float GPerStickUnit(float iasKt) => F14Aero.GPerStickUnit(iasKt);
    public float RollRateAvailableDegPerSec(float tasKt) => F14Aero.RollRateAvailableDegPerSec(tasKt);
    public float AccelKtPerSec(float iasKt, float altFt, float throttle01, float flightPathDeg,
                               float weightLb, float speedbrake01, float loadFactor) =>
        F14Aero.AccelKtPerSec(iasKt, altFt, throttle01, flightPathDeg, weightLb, speedbrake01, loadFactor);
    public float LevelAoaDeg(float iasKt, float weightLb, float loadFactor) =>
        F14Aero.LevelAoaDeg(iasKt, weightLb, loadFactor);
    public float UsableLoadFactor(float iasKt, float weightLb, float mach) =>
        F14Aero.UsableLoadFactor(iasKt, weightLb, mach);
    public float IndicatedAirspeedKt(float tasKt, float altFt) => F14Aero.IndicatedAirspeedKt(tasKt, altFt);
    public float TrueAirspeedKt(float iasKt, float altFt) => F14Aero.TrueAirspeedKt(iasKt, altFt);
    public float DensityRatio(float altFt) => F14Aero.DensityRatio(altFt);
    public float SpeedOfSoundKt(float altFt) => F14Aero.SpeedOfSoundKt(altFt);
}
