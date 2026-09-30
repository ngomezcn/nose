namespace AICopilotCore.Domain;

public enum TakeoffStyleId
{
    Relaxed,
    Combat,
    Emergency,
}

// Tres salidas completas, no una lista de numeros sueltos. Cada una fija
// la velocidad de cada fase (el motor solo persigue esa IAS: no hay un
// "throttle objetivo"), el morro, el viraje y a que altura se nivela.
// Las alturas son sobre la altitud a la que arranca la secuencia, no un
// numero de QNH absoluto: un campo a 5.000 ft no puede tener el viraje
// pedido a 3.000.
//
// Referencias publicas (no clasificadas), pensadas para un caza del tipo
// del F-14 que ya usa el resto del core:
//
//   Relajado — salida de entrenamiento. Rotacion en torno a 145 KIAS
//   (campo, peso medio), subida a 250 KIAS, que es el regimen estandar
//   por debajo de 10.000 ft en operaciones militares y civiles de EE.UU.,
//   y viraje de salida suave (~25 deg) bastante despues de irse al aire.
//
//   Combate — salida tactica / ascenso sin restriccion. Mas energia antes
//   de rotar, morro arriba y objetivo ~350 KIAS, el entorno de la corner
//   speed publicada del F-14 (~300-350 KCAS). El viraje sale pronto y mas
//   cerrado para no quedarse en el eje de pista.
//
//   Emergencia — pista corta u obstaculo. Se rota pronto, con morro alto
//   y flaps afuera, para ganar angulo (la idea de Vx: actitud para el
//   angulo, motor para sostener esa velocidad baja). Se sigue el rumbo de
//   pista hasta estar lejos del suelo; luego se acelera y se nivela bajo.
public sealed record TakeoffStyle
{
    public required TakeoffStyleId Id { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }

    public required float VrKt { get; init; }
    public required float RotatePitchDeg { get; init; }
    public required float ClimbPitchDeg { get; init; }
    public required float EnroutePitchDeg { get; init; }
    public required float LevelPitchDeg { get; init; }

    // Alturas sobre el campo (ft), no altitud indicada absoluta.
    public required float TurnHeightFt { get; init; }
    public required float TurnDeltaDeg { get; init; }
    public required float LevelHeightFt { get; init; }
    public required float MaxBankDeg { get; init; }

    public required float PitchRampDegPerSec { get; init; }
    public required float ReactionDelaySeconds { get; init; }
    public required float HeadingCaptureDeg { get; init; }

    public required float TakeoffFlapRatio { get; init; }
    public required float FlapRetract1Kt { get; init; }
    public required float FlapRetract2Kt { get; init; }
    public required float GearUpAglFt { get; init; }

    // IAS que el autothrottle persigue. El porcentaje de motor sale de ahi.
    public required float RollIasKt { get; init; }
    public required float CleanupIasKt { get; init; }
    public required float ClimbIasKt { get; init; }
    public required float CruiseIasKt { get; init; }

    // Si es > 0, el ascenso inicial (flaps) puede darse por terminado al
    // llegar a esta AGL aunque aun no se haya acelerado hasta la velocidad
    // de flaps arriba. Lo usa Emergencia para no quedarse lento esperando
    // una IAS que el propio estilo no persigue.
    public float ObstacleClearAglFt { get; init; }

    public string Spec =>
        $"Vr {VrKt:0} kt · rotacion {RotatePitchDeg:0}°\n" +
        $"Pista {RollIasKt:0} kt · limpieza {CleanupIasKt:0} kt · subida {ClimbIasKt:0} kt\n" +
        $"Viraje {TurnDeltaDeg:0}° a +{TurnHeightFt:0} ft (alabeo {MaxBankDeg:0}°)\n" +
        $"Nivel +{LevelHeightFt:0} ft a {CruiseIasKt:0} kt";
}

public static class TakeoffStyles
{
    public static readonly TakeoffStyle Relaxed = new()
    {
        Id = TakeoffStyleId.Relaxed,
        Name = "Relajado",
        Summary = "Salida de entrenamiento. Rotacion tranquila, subida a 250 kt " +
                  "y viraje suave bien lejos del suelo. El motor empuja lo que " +
                  "haga falta para esa velocidad.",
        VrKt = 145f,
        RotatePitchDeg = 12f,
        ClimbPitchDeg = 12f,
        EnroutePitchDeg = 12f,
        LevelPitchDeg = 2.5f,
        TurnHeightFt = 3000f,
        TurnDeltaDeg = 90f,
        LevelHeightFt = 10000f,
        MaxBankDeg = 25f,
        PitchRampDegPerSec = 2.5f,
        ReactionDelaySeconds = 0.4f,
        HeadingCaptureDeg = 3f,
        TakeoffFlapRatio = 0.2f,
        FlapRetract1Kt = 170f,
        FlapRetract2Kt = 200f,
        GearUpAglFt = 15f,
        RollIasKt = 250f,
        CleanupIasKt = 220f,
        ClimbIasKt = 250f,
        CruiseIasKt = 300f,
    };

    public static readonly TakeoffStyle Combat = new()
    {
        Id = TakeoffStyleId.Combat,
        Name = "Combate",
        Summary = "Salida tactica. Mas energia antes de rotar, morro arriba y " +
                  "subida a 350 kt (corner speed del F-14). El viraje sale pronto " +
                  "y mas cerrado para dejar el eje de pista.",
        VrKt = 160f,
        RotatePitchDeg = 15f,
        ClimbPitchDeg = 18f,
        EnroutePitchDeg = 15f,
        LevelPitchDeg = 2.5f,
        TurnHeightFt = 1500f,
        TurnDeltaDeg = 90f,
        LevelHeightFt = 15000f,
        MaxBankDeg = 45f,
        PitchRampDegPerSec = 8f,
        ReactionDelaySeconds = 0.2f,
        HeadingCaptureDeg = 5f,
        TakeoffFlapRatio = 0.15f,
        FlapRetract1Kt = 200f,
        FlapRetract2Kt = 250f,
        GearUpAglFt = 15f,
        RollIasKt = 350f,
        CleanupIasKt = 320f,
        ClimbIasKt = 350f,
        CruiseIasKt = 400f,
    };

    // Misma energia que Combate, pero la secuencia corta en cuanto hay
    // altura minima limpia: el giro lo hace ya la interceptacion hacia el
    // blanco, no un viraje fijo de salida de pista. No aparece en los
    // toggles de la UI de despegue; solo lo usa FlightDirector.
    public static readonly TakeoffStyle CombatIntercept = new()
    {
        Id = TakeoffStyleId.Combat,
        Name = "Combate",
        Summary = "Despegue combate previo a interceptacion: a minima altura " +
                  "se cede el control al interceptor.",
        VrKt = 160f,
        RotatePitchDeg = 15f,
        ClimbPitchDeg = 18f,
        EnroutePitchDeg = 15f,
        LevelPitchDeg = 2.5f,
        TurnHeightFt = 500f,
        TurnDeltaDeg = 0f,
        LevelHeightFt = 1000f,
        MaxBankDeg = 45f,
        PitchRampDegPerSec = 8f,
        ReactionDelaySeconds = 0.2f,
        HeadingCaptureDeg = 5f,
        TakeoffFlapRatio = 0.15f,
        FlapRetract1Kt = 200f,
        FlapRetract2Kt = 250f,
        GearUpAglFt = 15f,
        RollIasKt = 350f,
        CleanupIasKt = 320f,
        ClimbIasKt = 350f,
        CruiseIasKt = 400f,
    };

    public static readonly TakeoffStyle Emergency = new()
    {
        Id = TakeoffStyleId.Emergency,
        Name = "Emergencia",
        Summary = "Obstaculo o pista corta. Rotacion temprana, morro alto y flaps " +
                  "afuera para ganar angulo, siguiendo el rumbo de pista. Al quedar " +
                  "libre del suelo acelera y nivela bajo.",
        VrKt = 130f,
        RotatePitchDeg = 16f,
        ClimbPitchDeg = 15f,
        EnroutePitchDeg = 10f,
        LevelPitchDeg = 2.5f,
        TurnHeightFt = 2000f,
        TurnDeltaDeg = 0f,
        LevelHeightFt = 5000f,
        MaxBankDeg = 20f,
        PitchRampDegPerSec = 6f,
        ReactionDelaySeconds = 0.15f,
        HeadingCaptureDeg = 4f,
        TakeoffFlapRatio = 0.4f,
        FlapRetract1Kt = 190f,
        FlapRetract2Kt = 220f,
        GearUpAglFt = 15f,
        RollIasKt = 180f,
        CleanupIasKt = 165f,
        ClimbIasKt = 230f,
        CruiseIasKt = 260f,
        ObstacleClearAglFt = 1000f,
    };

    public static TakeoffStyle Get(TakeoffStyleId id) => id switch
    {
        TakeoffStyleId.Combat => Combat,
        TakeoffStyleId.Emergency => Emergency,
        _ => Relaxed,
    };
}
