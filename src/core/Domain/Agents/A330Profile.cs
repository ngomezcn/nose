using AICopilotCore.Domain;

namespace AICopilotCore.Domain.Agents;

// Perfil del Airbus A330-AI: reutiliza la aero de libro de F14Aero (el
// integrador cinematico no tiene curvas Airbus), pero con ritmos y limites
// de piloto comercial. Sin esto la IA rodaba como un caza (alabeo a 60-75°
// y 100+ deg/s) aunque el modelo 3D sea un airliner.
public sealed class A330Profile : IFlightProfile
{
    public static readonly A330Profile Instance = new();

    // ~25° es el alabeo tipico de pasajeros; Airbus normal law corta a ~33°.
    public const float CommercialMaxBankDeg = 25f;
    // Ritmo de alabeo de linea: entrada/salida sin marear a nadie.
    private const float MaxRollRateDegPerSec = 10f;
    private const float MinRollRateDegPerSec = 5f;
    private const float RollRatePerTasKt = 0.025f;

    public A330Profile(float pitchLagSec = 0.70f, float rollLagSec = 0.55f)
    {
        PitchLagSec = pitchLagSec;
        RollLagSec = rollLagSec;
    }

    public string Name => "A330";
    // La cinematica reusa el esqueleto F-14 (ver Accel/AoA/UsableLF abajo).
    // El State.WeightLb alimenta ManeuverPlanner / EnvelopeProtection via
    // F14Aero, asi que DEBE ser el peso de referencia del caza: poner aqui
    // el peso de placa del A330 (~380.000 lb) deja UsableLoadFactor en el
    // suelo (1.05 g) y el planificador manda alabeo 0 / subida 0 — las
    // acciones de ruta "no hacen nada". El caracter airliner viene del
    // tope de alabeo, ritmos y el *0.45 de aceleracion.
    public float ReferenceWeightLb => F14Aero.ReferenceWeightLb;
    public float VneKt => 365f;
    public float PitchLagSec { get; }
    public float RollLagSec { get; }
    public float MaxOperationalBankDeg => CommercialMaxBankDeg;

    // Palanca mas "pesada": menos G por unidad que un caza.
    public float GPerStickUnit(float iasKt) =>
        Math.Clamp(1.4f * (iasKt / 280f), 0.5f, 2.0f);

    public float RollRateAvailableDegPerSec(float tasKt) =>
        Math.Clamp(RollRatePerTasKt * tasKt, MinRollRateDegPerSec, MaxRollRateDegPerSec);

    // Empuje/resistencia del F-14 a este peso no escalan bien; se atenua para
    // que no acelere como un reactor de combate.
    public float AccelKtPerSec(float iasKt, float altFt, float throttle01, float flightPathDeg,
                               float weightLb, float speedbrake01, float loadFactor) =>
        F14Aero.AccelKtPerSec(iasKt, altFt, throttle01, flightPathDeg,
                              F14Aero.ReferenceWeightLb, speedbrake01, loadFactor) * 0.45f;

    public float LevelAoaDeg(float iasKt, float weightLb, float loadFactor) =>
        F14Aero.LevelAoaDeg(iasKt, F14Aero.ReferenceWeightLb, loadFactor);

    // Tope de trabajo de un airliner (~2.5 g estructurales; 1.5 g de pasajeros).
    public float UsableLoadFactor(float iasKt, float weightLb, float mach) =>
        MathF.Min(1.5f, F14Aero.UsableLoadFactor(iasKt, F14Aero.ReferenceWeightLb, mach));

    public float IndicatedAirspeedKt(float tasKt, float altFt) => F14Aero.IndicatedAirspeedKt(tasKt, altFt);
    public float TrueAirspeedKt(float iasKt, float altFt) => F14Aero.TrueAirspeedKt(iasKt, altFt);
    public float DensityRatio(float altFt) => F14Aero.DensityRatio(altFt);
    public float SpeedOfSoundKt(float altFt) => F14Aero.SpeedOfSoundKt(altFt);
}
