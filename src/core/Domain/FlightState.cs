using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

// Foto del avion en un instante, con todo lo derivado ya calculado. Es lo que
// consume el planificador de maniobras (ManeuverPlanner) para adaptar la
// maniobra pedida al estado real: velocidad, altura, actitud y energia.
//
// Es un record struct inmutable a proposito: se construye una vez por frame
// desde los datarefs y se pasa por `in` a funciones puras. Asi el planificador
// no puede leer un dataref a medio frame y encontrarselo cambiado a mitad de
// un calculo, y se puede probar en frio dandole numeros a mano sin simulador.
//
// Casi todo lo de aqui sale de un dataref real y no de una estimacion: AoA,
// Mach, peso y VNE los da X-Plane, y el modelo de F14Aero solo se usa para lo
// que el simulador no dice (cuanta G queda, cuanto radio pide un tiron). Un
// estimador que se equivoca en el AoA hace que la proteccion de perdida muerda
// donde no toca; el dataref no se equivoca.
public readonly record struct FlightState(
    float IasKt,
    float AltFt,
    float AglFt,
    float VsFpm,
    float PitchDeg,
    float BankDeg,
    float HeadingDeg,
    float GNormal,
    float AoaDeg,
    float MachNo,
    float PitchRateDegPerSec,
    float RollRateDegPerSec,
    float WeightLb,
    float AcfVneKt,
    bool OnGround)
{
    public static FlightState Capture(Datarefs d) => new(
        IasKt: d.IasKt.Float,
        AltFt: d.AltFt.Float,
        AglFt: d.AglMeters.Float * F14Aero.MetersToFeet,
        VsFpm: d.VsFpm.Float,
        PitchDeg: d.PitchDeg.Float,
        BankDeg: d.BankDeg.Float,
        HeadingDeg: d.HeadingDeg.Float,
        GNormal: d.GNormal.Float,
        AoaDeg: d.AoaDeg.Float,
        MachNo: d.Mach.Float,
        PitchRateDegPerSec: d.PitchRateDegPerSec.Float,
        RollRateDegPerSec: d.RollRateDegPerSec.Float,
        WeightLb: d.TotalWeightKg.Float * 2.20462f,
        AcfVneKt: d.VneKias.Float,
        OnGround: d.OnGround.Bool);

    public float TasKt => F14Aero.TrueAirspeedKt(IasKt, AltFt);
    public float DensityRatio => F14Aero.DensityRatio(AltFt);

    // Angulo de la TRAYECTORIA (no del morro): es el que manda en la geometria
    // de un picado o un looping. gamma = theta - alpha en vuelo simetrico; con
    // los dos datarefs reales sale directo y sigue valiendo con alabeo, cosa
    // que la version por V/S no hace (con 60 deg de alabeo la V/S no dice nada
    // del angulo de trayectoria en el plano de la maniobra).
    public float FlightPathDeg => PitchDeg - AoaDeg;

    // Velocidad de perdida ahora mismo, con el peso REAL: Vs va con la raiz
    // del peso, y un F-14 pasa de ~40.000 lb vacio a ~74.000 cargado -- la
    // diferencia entre 119 y 162 KIAS de perdida a 1 g. Volar con la Vs de un
    // peso supuesto es justo el tipo de suposicion que hace que una maniobra
    // salga mal solo en la mitad del envolvente.
    public float StallSpeed1gKt => F14Aero.StallIasKt(WeightLb);

    // Velocidad de perdida con la carga que ya lleva el ala (el alabeo actual).
    public float StallSpeedNowKt =>
        F14Aero.StallIasKt(WeightLb, F14Aero.LoadFactorForBank(BankDeg));

    // G INSTANTANEA que el ala puede dar aqui y ahora, con la Vs real de este
    // peso. No se penaliza por altura a proposito: en IAS el limite de
    // sustentacion no depende de la altura (ver el comentario de
    // F14Aero.UsableLoadFactor). Lo que si depende es la G SOSTENIDA, que es
    // otra cosa y tiene su propia funcion.
    public float UsableG => F14Aero.UsableLoadFactor(IasKt, WeightLb, MachNo);

    public float SustainedG(float throttle01) =>
        F14Aero.SustainedLoadFactor(IasKt, AltFt, WeightLb, throttle01);

    // VNE efectiva: la del .acf del avion cargado si X-Plane la da, y si no la
    // del F-14 de las tablas; en los dos casos acotada por el limite de Mach,
    // que es el que manda en altura.
    public float VneKt
    {
        get
        {
            float structural = AcfVneKt > 50f ? AcfVneKt : F14Aero.VneSeaLevelKt;
            float machLimitIasKt = F14Aero.MachLimit * F14Aero.SpeedOfSoundKt(AltFt) *
                                   MathF.Sqrt(MathF.Max(DensityRatio, 0.05f));
            return MathF.Min(structural, machLimitIasKt);
        }
    }

    // Margenes, en nudos. Negativo = ya se paso.
    public float StallMarginKt => IasKt - StallSpeedNowKt * F14Aero.StallMarginFactor;
    public float VneMarginKt => VneKt - IasKt;

    // Cuanto AoA queda antes del buffet/perdida. El F-14 entra en perdida
    // alrededor de 20-24 deg de AoA; se trabaja contra el limite bajo.
    public float AoaMarginDeg => F14Aero.StallAoaDeg - AoaDeg;

    // "Energia total" como la altura que se tendria cambiando toda la
    // velocidad por altura (energia especifica, ft). Es lo que decide si un
    // looping o un Immelmann caben: por debajo de cierta energia la maniobra no
    // se completa por mucho que se tire.
    public float SpecificEnergyFt
    {
        get
        {
            float tasFtPerSec = TasKt * F14Aero.KtToFtPerSec;
            return AltFt + tasFtPerSec * tasFtPerSec / (2f * F14Aero.GravityFtPerSec2);
        }
    }

    // Si algo llega a NaN (frame sin telemetria, reconexion a medias) toda la
    // planificacion que salga de aqui seria basura: el ejecutor lo comprueba
    // antes de mandar nada y, si no vale, deja el frame anterior en pie en vez
    // de escribir un mando calculado con un NaN.
    public bool IsUsable =>
        !float.IsNaN(IasKt) && !float.IsNaN(AltFt) && !float.IsNaN(PitchDeg) &&
        !float.IsNaN(BankDeg) && !float.IsNaN(GNormal) && !float.IsNaN(AoaDeg) &&
        IasKt > 1f;
}
