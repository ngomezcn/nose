using AICopilotCore.Domain;

namespace AICopilotCore.Domain.Agents;

// Prestaciones de un tipo de avion que necesita el cuerpo cinematico
// (KinematicAiBody) para integrar el vuelo de una IA. Costura minima: solo lo
// que consume KinematicFlightModel. Ownship = F14Profile; IAs = A330Profile.
public interface IFlightProfile
{
    string Name { get; }
    float ReferenceWeightLb { get; }
    float VneKt { get; }               // IAS estructural a nivel del mar
    float PitchLagSec { get; }         // retardo 1er orden de la G respecto a la palanca
    float RollLagSec { get; }          // retardo 1er orden del ritmo de alabeo
    // Tope de alabeo de trabajo (piloto / leyes de vuelo). El catalogo puede
    // pedir mas; ManeuverSequence lo recorta a esto.
    float MaxOperationalBankDeg { get; }

    float GPerStickUnit(float iasKt);
    float RollRateAvailableDegPerSec(float tasKt);
    float AccelKtPerSec(float iasKt, float altFt, float throttle01, float flightPathDeg,
                        float weightLb, float speedbrake01, float loadFactor);
    float LevelAoaDeg(float iasKt, float weightLb, float loadFactor);
    float UsableLoadFactor(float iasKt, float weightLb, float mach);

    float IndicatedAirspeedKt(float tasKt, float altFt);
    float TrueAirspeedKt(float iasKt, float altFt);
    float DensityRatio(float altFt);
    float SpeedOfSoundKt(float altFt);
}
