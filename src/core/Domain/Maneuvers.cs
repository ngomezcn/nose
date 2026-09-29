using System.Linq;

namespace AICopilotCore.Domain;

// El catalogo de "acciones" que Jev podra pedir: maniobras discretas del
// F-14 Tomcat, pensadas como algo que un piloto pediria por radio
// ("baja el morro un poco", "vira cerrado a la izquierda", "dame un rollo"),
// no como setpoints numericos. Jev no es buena con los numeros -- por eso
// esto existe: traduce una intencion humana a algo ejecutable.
//
// -- Que cambio aqui y por que --------------------------------------------
//
// Este catalogo era una tabla de OBJETIVOS FIJOS: "picado = morro a -40 deg y
// gas 0.9", "looping = palanca de cabeceo a 0.85 durante 15 s". Esos numeros
// solo valen en un punto del envolvente, y fuera de el la maniobra salia mal
// de dos maneras distintas:
//
//   - Se pasaba de G y el ejecutor la CORTABA para pasar a recuperar nivelado.
//     El caso real: pedir "Caer en picado" volando a 300 kt movia el objetivo
//     de morro a 12 deg/s, y eso son n = 1 - 12*300/1092 = -2.3 g -- fuera del
//     margen, maniobra abortada. El angulo pedido no tenia nada de malo; lo
//     que estaba mal era la PRISA con la que se pedia.
//   - O no se pasaba de nada y simplemente no hacia lo que decia: un "viraje
//     de crucero" a 170 kt perdia 2.200 fpm, y un looping a 350 kt no daba la
//     vuelta entera en los 15 s del guion.
//
// Ahora cada maniobra declara su INTENCION (que trayectoria, cuanta G se
// permite, con que brusquedad, que hacer con la velocidad) y ManeuverPlanner
// la traduce cada frame a objetivos concretos usando el estado real del avion
// (velocidad, altura, peso, actitud, Mach, AoA). Si algo no cabe, se adapta
// -- alabeo menor, tiron mas suave, gases distintos, arco mas abierto -- y se
// dice por que. No se cae a un fallback.
//
// -- Numeros de referencia del F-14A/B (fuentes publicas, no clasificadas) --
//
//   Limite estructural          +7.5 g / -3.0 g (limpio)
//   Velocidad de virajes sostenidos ("corner speed")  ~300-350 KCAS
//   Techo de servicio            ~50.000-53.000 ft
//   VNE estructural a nivel del mar   ~750 KCAS; maxima en altura Mach ~2.34
//   Perdida limpia ~130 KIAS a 55.000 lb (escala con la raiz del peso real)
//   Angulo de ataque de perdida       ~20-24 deg
//   Ritmo de alabeo maximo            ~150-180 deg/s
public enum ManeuverCategory
{
    Altitud,
    Virajes,
    Combinadas,
    Acrobacias,
    Velocidad,
}

public enum ManeuverKind
{
    DescendGentle,
    DescendAggressive,
    Dive,
    ClimbGentle,
    ClimbAggressive,
    LevelWings,

    TurnGentleLeft,
    TurnGentleRight,
    TurnTightLeft,
    TurnTightRight,

    ClimbAggressiveTurnLeft,
    ClimbAggressiveTurnRight,
    DescendAggressiveTurnLeft,
    DescendAggressiveTurnRight,
    CombatBreakLeft,
    CombatBreakRight,

    RollLeft,
    RollRight,
    BarrelRollLeft,
    BarrelRollRight,
    LoopBackflip,
    ImmelmannLeft,
    ImmelmannRight,
    SplitSLeft,
    SplitSRight,

    Accelerate,
    Decelerate,
}

// Como se ejecuta la maniobra:
//
//   AttitudeHold: se persigue una TRAYECTORIA (angulo de subida/bajada y
//   alabeo) con el PID en cascada, y se mantiene indefinidamente como un
//   autopiloto -- hasta que llega otra accion o un Abortar. Vale para todo lo
//   que es "vuela asi" (subir, bajar, virar).
//
//   Aerobatic: la maniobra recorre ANGULOS (360 deg de alabeo, media vuelta de
//   arco vertical) con mando de G, no un gesto de palanca durante N segundos.
//   Antes era RateCommand -- palanca fija y cronometro -- y ese era su fallo
//   de raiz: la misma palanca da 3 g a 250 kt y 9 g a 500 kt, y los mismos
//   segundos dan 250 deg de alabeo a 200 kt y 620 deg a 500 kt. Cerrando por
//   angulo, un rollo es un rollo a cualquier velocidad, y mandando G en vez de
//   palanca, el tiron es el mismo tiron en todo el envolvente.
public enum ManeuverMode
{
    AttitudeHold,
    Aerobatic,
}

// Un tramo de una acrobacia, cerrado por ANGULO RECORRIDO y no por tiempo.
//
//   RollAngleDeg: cuanto alabeo hay que recorrer (signo = sentido). 0 = este
//   tramo no rueda.
//   ArcAngleDeg: cuanto arco vertical hay que recorrer. 0 = no hay tiron.
//   Direction: +1 el arco va "hacia arriba" (looping normal), -1 el arco va
//   hacia abajo estando invertido (la segunda mitad de un Split-S).
//   LoadFactorCmd: G objetivo del tramo. NaN = la calcula el planificador con
//   la energia y la altura disponibles (loopings, Split-S).
//   UnloadedRoll: true en los rollos de aleron y en la entrada del Split-S --
//   ahi el cabeceo no persigue G, sigue el coseno del alabeo recorrido, que es
//   lo que hace un piloto para que el morro no se caiga al pasar por invertido
//   sin llegar a empujar en negativo.
//   Throttle: NaN = el del plan.
//   MaxSeconds: red de seguridad, no criterio de fin.
public sealed record AcroSegment(
    string Label,
    float RollAngleDeg = 0f,
    float ArcAngleDeg = 0f,
    int Direction = 1,
    float LoadFactorCmd = float.NaN,
    bool UnloadedRoll = false,
    float Throttle = float.NaN,
    float MaxSeconds = 25f);

// Una maniobra del catalogo. Los campos se leen como la INTENCION de un
// piloto, no como mandos: "quiero bajar a 12 grados de trayectoria sin pasar
// de 2.5 g ni bajar de 0.4 g, sin acelerar, y sin bajar de 1.000 ft".
// ManeuverPlanner es quien convierte eso en pitch/bank/gases de este frame.
public sealed record ManeuverDefinition(
    ManeuverKind Kind,
    string Label,
    ManeuverCategory Category,
    ManeuverMode Mode,
    string Description,

    // Trayectoria pedida (grados de subida/bajada del VECTOR VELOCIDAD, no del
    // morro). Se pide trayectoria y no actitud porque el morro necesario para
    // una misma trayectoria cambia con la velocidad, el peso y el alabeo: el
    // angulo de ataque va con 1/V^2, asi que un "pitch 1.5 deg" calibrado a
    // 300 kt se convierte en un descenso de 2.200 fpm a 170 kt. El planificador
    // suma el AoA real (dataref) para sacar el morro de cada frame.
    float FlightPathTargetDeg = 0f,
    float BankTargetDeg = 0f,

    // Mando de motor cuando la maniobra NO persigue una velocidad concreta.
    float ThrottleTarget = 0.7f,

    // --- Envolvente de entrada -------------------------------------------
    // Ya no es "el boton se apaga": el planificador adapta dentro de estos
    // limites y solo se niega cuando la maniobra no existe de ninguna forma
    // segura (ver ManeuverPlanner.Preview). MinAglFt es un suelo minimo; la
    // altura que de verdad hace falta la calcula el planificador con la
    // velocidad real (un Split-S pide 4.600 ft a 250 kt y 13.800 a 500).
    float MinIasKt = 0f,
    float MaxIasKt = float.PositiveInfinity,
    float MinAglFt = 0f,

    // --- Presupuesto de G y brusquedad ------------------------------------
    // GBudgetMax: la G maxima que esta maniobra puede llegar a pedir. Es lo que
    // la separa de las demas: un viraje de crucero no pasa de 2 g aunque haya
    // 6 disponibles, y un break defensivo llega a 5.5 -- un g por debajo del
    // backstop, para que el backstop no salte nunca en operacion normal.
    // GPushMin: la G minima al bajar el morro. 0.3 g es "descargado pero sin
    // que flote nada en la cabina"; es el numero que impide que un picado
    // pedido desde nivelado se vaya a G negativa.
    // GOnsetGPerSec: con que rapidez se deja cargar el ala. Un viraje suave
    // carga a 0.5 g/s (imperceptible), un break a 4 g/s (el tiron seco de un
    // piloto rompiendo una persecucion).
    float GBudgetMax = 3f,
    float GPushMin = 0.4f,
    float GOnsetGPerSec = 1.5f,
    float BankRateCapDegPerSec = 45f,
    // Fraccion del presupuesto de G que se lleva el VIRAJE; el resto queda
    // para cabecear, para las rafagas y para el lazo de altitud. Con el alabeo
    // muy alto casi no queda margen de cabeceo (a 75 deg, cos phi = 0.26), y
    // por eso un break tiene que aceptar perder algo de altura en vez de pedir
    // una G que no existe.
    float PitchBudgetFraction = 0.80f,

    // --- Que hacer con la altura y con la velocidad ------------------------
    // HoldsEntryAltitude: la maniobra no quiere cambiar de altura (virajes,
    // nivelar, acelerar/frenar). El planificador ancla la altitud que habia al
    // pulsar el boton y la defiende con una cascada altitud -> V/S -> gamma.
    // HoldsEntryIas: la maniobra no quiere cambiar de velocidad: el
    // autothrottle persigue la IAS de entrada. Sin esto, un viraje cerrado a
    // gas 0.95 acelera 14 kt/s y se sale de su propio envolvente en 6 s.
    // VsIntentFpm: V/S pedida cuando la maniobra si quiere cambiar de altura
    // (NaN = se deriva de FlightPathTargetDeg).
    // MinVsFpm: hasta donde se le permite hundirse a esta maniobra cuando no
    // hay G para sostener el viraje (solo el break canjea altura por giro).
    bool HoldsEntryAltitude = false,
    bool HoldsEntryIas = false,
    float VsIntentFpm = float.NaN,
    float MinVsFpm = -500f,

    // --- Suelo y recuperacion ---------------------------------------------
    // FloorAglFt: altura sobre el terreno por debajo de la cual esta maniobra
    // deja de bajar, pase lo que pase. RecoveryG: con cuanta G se dimensiona
    // el espacio necesario para salir del picado (mas G = menos altura, pero
    // tiene que haberla disponible).
    float FloorAglFt = 600f,
    float RecoveryG = 3.5f,

    // --- Velocidad como objetivo ------------------------------------------
    // IsSpeedHold: Acelerar/Frenar. Cada pulsacion mueve un escalon el IAS
    // objetivo y el autothrottle lo persigue. SpeedTargetKt: IAS fija que
    // persigue la maniobra (crucero).
    bool IsSpeedHold = false,
    float SpeedTargetKt = float.NaN,

    // --- Acrobacias --------------------------------------------------------
    IReadOnlyList<AcroSegment>? Segments = null)
{
    // Angulo total de alabeo y de arco de una acrobacia: lo usa el
    // planificador para estimar cuanto va a durar y cuanta altura pide.
    public float TotalRollDeg => Segments?.Sum(s => MathF.Abs(s.RollAngleDeg)) ?? 0f;
    public float TotalArcDeg => Segments?.Sum(s => MathF.Abs(s.ArcAngleDeg)) ?? 0f;
}

public static class ManeuverCatalog
{
    // Pitch de crucero/nivelado de referencia. Sigue existiendo como valor de
    // partida de las rampas, pero ya no es el objetivo de nivelar: nivelar es
    // perseguir trayectoria cero, y el morro que eso pide depende de la
    // velocidad (2 deg a 500 kt, 12 deg a 160 kt).
    public const float LevelPitchDeg = 2.5f;

    // IAS de "volar en crucero". ~300 KIAS es un crucero bajo razonable para
    // un F-14, bien por debajo de la corner y de la VNE.
    public const float CruiseIasKt = 300f;

    public static readonly IReadOnlyList<ManeuverDefinition> All = new[]
    {
        // --- Altitud: cabeceo puro, sin alabeo -----------------------------
        new ManeuverDefinition(ManeuverKind.DescendGentle, "Bajar altitud (leve)",
            ManeuverCategory.Altitud, ManeuverMode.AttitudeHold,
            "Descenso de crucero: baja el morro lo justo, sin acelerar y sin que se " +
            "note en el asiento.",
            FlightPathTargetDeg: -3f, ThrottleTarget: 0.55f,
            MinIasKt: 150f, MaxIasKt: 600f,
            GBudgetMax: 1.8f, GPushMin: 0.6f, GOnsetGPerSec: 0.5f,
            HoldsEntryIas: true, FloorAglFt: 700f, RecoveryG: 3.0f),

        new ManeuverDefinition(ManeuverKind.DescendAggressive, "Bajar altitud (agresivo)",
            ManeuverCategory.Altitud, ManeuverMode.AttitudeHold,
            "Perder altura rapido sin picar del todo: motor bajo y trayectoria " +
            "claramente descendente, frenando con aerofrenos si hace falta para no acelerar.",
            FlightPathTargetDeg: -12f, ThrottleTarget: 0.3f,
            MinIasKt: 170f, MaxIasKt: 650f, MinAglFt: 1500f,
            GBudgetMax: 2.5f, GPushMin: 0.4f, GOnsetGPerSec: 1.0f,
            HoldsEntryIas: true, FloorAglFt: 1000f, RecoveryG: 3.5f),

        // El caso que motivo todo este trabajo. Lo que lo arreglaba no era
        // bajar el angulo, sino limitar el RITMO al que se baja el morro: con
        // GPushMin 0.15 el morro baja a ~3 deg/s a 300 kt (14 s hasta -40),
        // que es exactamente lo que hace un piloto, y la G nunca baja de 0.15.
        new ManeuverDefinition(ManeuverKind.Dive, "Caer en picado",
            ManeuverCategory.Altitud, ManeuverMode.AttitudeHold,
            "Picado pronunciado para ganar energia: el morro baja descargando " +
            "(nunca en G negativa) y la trayectoria se recorta sola si no hay altura " +
            "para salir o si la velocidad se acerca a la VNE.",
            FlightPathTargetDeg: -40f, ThrottleTarget: 0.9f,
            MinIasKt: 170f, MaxIasKt: 700f, MinAglFt: 5000f,
            GBudgetMax: 4.5f, GPushMin: 0.15f, GOnsetGPerSec: 2.0f,
            FloorAglFt: 1200f, RecoveryG: 4.0f),

        new ManeuverDefinition(ManeuverKind.ClimbGentle, "Subir morro (leve)",
            ManeuverCategory.Altitud, ManeuverMode.AttitudeHold,
            "Ascenso suave de crucero, como corregir un poco de altitud, sin perder velocidad.",
            FlightPathTargetDeg: 6f, ThrottleTarget: 0.75f,
            MinIasKt: 180f, MaxIasKt: 550f,
            GBudgetMax: 1.6f, GPushMin: 0.8f, GOnsetGPerSec: 0.5f,
            HoldsEntryIas: true),

        new ManeuverDefinition(ManeuverKind.ClimbAggressive, "Subir morro (agresivo)",
            ManeuverCategory.Altitud, ManeuverMode.AttitudeHold,
            "El tipico tiron para ganar altura ya, a potencia militar. Si el empuje " +
            "no da para sostener el angulo (altura o poca velocidad), se lava solo " +
            "hasta el que si se sostiene en vez de quedarse colgado.",
            FlightPathTargetDeg: 22f, ThrottleTarget: 1f,
            MinIasKt: 220f, MaxIasKt: 600f,
            GBudgetMax: 3.5f, GPushMin: 0.7f, GOnsetGPerSec: 2.0f),

        // El boton de "esto siempre tiene sentido": lo usa la recuperacion
        // automatica y el boton "Volar en crucero".
        new ManeuverDefinition(ManeuverKind.LevelWings, "Nivelar (alas y morro)",
            ManeuverCategory.Altitud, ManeuverMode.AttitudeHold,
            "Suelta el alabeo, pone la trayectoria a cero y sostiene 300 kt. Si entra " +
            "invertido, rueda primero y tira despues -- nunca tira estando boca abajo.",
            FlightPathTargetDeg: 0f, HoldsEntryAltitude: true,
            SpeedTargetKt: CruiseIasKt,
            GBudgetMax: 4f, GPushMin: 0.5f, GOnsetGPerSec: 1.5f,
            FloorAglFt: 500f, RecoveryG: 4f),

        // --- Virajes ---------------------------------------------------------
        // Un viraje coordinado pide n = 1/cos(alabeo) solo para no perder
        // altura, y ese n exige mas angulo de ataque, o sea mas morro. Por eso
        // aqui no hay "pitch objetivo": hay trayectoria cero y altitud anclada,
        // y el planificador pone el morro que haga falta a cada velocidad.
        new ManeuverDefinition(ManeuverKind.TurnGentleLeft, "Virar suave, izquierda",
            ManeuverCategory.Virajes, ManeuverMode.AttitudeHold,
            "Viraje de crucero a ~20 deg de alabeo, manteniendo altitud y velocidad.",
            BankTargetDeg: -20f, ThrottleTarget: 0.75f,
            MinIasKt: 170f, MaxIasKt: 600f,
            GBudgetMax: 2f, GPushMin: 0.6f, GOnsetGPerSec: 0.5f, BankRateCapDegPerSec: 30f,
            HoldsEntryAltitude: true, HoldsEntryIas: true),

        new ManeuverDefinition(ManeuverKind.TurnGentleRight, "Virar suave, derecha",
            ManeuverCategory.Virajes, ManeuverMode.AttitudeHold,
            "Viraje de crucero a ~20 deg de alabeo, manteniendo altitud y velocidad.",
            BankTargetDeg: 20f, ThrottleTarget: 0.75f,
            MinIasKt: 170f, MaxIasKt: 600f,
            GBudgetMax: 2f, GPushMin: 0.6f, GOnsetGPerSec: 0.5f, BankRateCapDegPerSec: 30f,
            HoldsEntryAltitude: true, HoldsEntryIas: true),

        // 60 deg de alabeo son 2 g exactos. El ala los da a partir de ~211 kt;
        // por debajo el planificador recorta el alabeo en vez de dejar que el
        // avion se hunda o entre en buffet.
        new ManeuverDefinition(ManeuverKind.TurnTightLeft, "Virar cerrado, izquierda",
            ManeuverCategory.Virajes, ManeuverMode.AttitudeHold,
            "Viraje sostenido a ~60 deg de alabeo (2 g): gira rapido manteniendo " +
            "altitud y velocidad. Si falta sustentacion, afloja el alabeo en vez de hundirse.",
            BankTargetDeg: -60f, ThrottleTarget: 0.95f,
            MinIasKt: 230f, MaxIasKt: 380f,
            GBudgetMax: 3f, GPushMin: 0.5f, GOnsetGPerSec: 1.5f, BankRateCapDegPerSec: 45f,
            HoldsEntryAltitude: true, HoldsEntryIas: true),

        new ManeuverDefinition(ManeuverKind.TurnTightRight, "Virar cerrado, derecha",
            ManeuverCategory.Virajes, ManeuverMode.AttitudeHold,
            "Viraje sostenido a ~60 deg de alabeo (2 g): gira rapido manteniendo " +
            "altitud y velocidad. Si falta sustentacion, afloja el alabeo en vez de hundirse.",
            BankTargetDeg: 60f, ThrottleTarget: 0.95f,
            MinIasKt: 230f, MaxIasKt: 380f,
            GBudgetMax: 3f, GPushMin: 0.5f, GOnsetGPerSec: 1.5f, BankRateCapDegPerSec: 45f,
            HoldsEntryAltitude: true, HoldsEntryIas: true),

        // --- Combinadas -------------------------------------------------------
        new ManeuverDefinition(ManeuverKind.ClimbAggressiveTurnLeft, "Subir agresivo + virar izquierda",
            ManeuverCategory.Combinadas, ManeuverMode.AttitudeHold,
            "Ascenso combinado con viraje cerrado a la izquierda. El angulo de subida " +
            "se ajusta a la potencia que queda: arriba sube menos, pero no se cuelga.",
            FlightPathTargetDeg: 18f, BankTargetDeg: -45f, ThrottleTarget: 1f,
            MinIasKt: 250f, MaxIasKt: 550f,
            GBudgetMax: 3f, GPushMin: 0.7f, GOnsetGPerSec: 1.5f, BankRateCapDegPerSec: 60f),

        new ManeuverDefinition(ManeuverKind.ClimbAggressiveTurnRight, "Subir agresivo + virar derecha",
            ManeuverCategory.Combinadas, ManeuverMode.AttitudeHold,
            "Ascenso combinado con viraje cerrado a la derecha. El angulo de subida " +
            "se ajusta a la potencia que queda: arriba sube menos, pero no se cuelga.",
            FlightPathTargetDeg: 18f, BankTargetDeg: 45f, ThrottleTarget: 1f,
            MinIasKt: 250f, MaxIasKt: 550f,
            GBudgetMax: 3f, GPushMin: 0.7f, GOnsetGPerSec: 1.5f, BankRateCapDegPerSec: 60f),

        new ManeuverDefinition(ManeuverKind.DescendAggressiveTurnLeft, "Bajar agresivo + virar izquierda",
            ManeuverCategory.Combinadas, ManeuverMode.AttitudeHold,
            "Descenso rapido virando a la izquierda, como escapar de algo. La bajada " +
            "se va aplanando sola conforme se acerca el terreno.",
            FlightPathTargetDeg: -10f, BankTargetDeg: -35f, ThrottleTarget: 0.35f,
            MinIasKt: 200f, MaxIasKt: 600f, MinAglFt: 1500f,
            GBudgetMax: 3f, GPushMin: 0.4f, GOnsetGPerSec: 1.5f, BankRateCapDegPerSec: 60f,
            HoldsEntryIas: true, FloorAglFt: 900f, RecoveryG: 3.5f),

        new ManeuverDefinition(ManeuverKind.DescendAggressiveTurnRight, "Bajar agresivo + virar derecha",
            ManeuverCategory.Combinadas, ManeuverMode.AttitudeHold,
            "Descenso rapido virando a la derecha, como escapar de algo. La bajada " +
            "se va aplanando sola conforme se acerca el terreno.",
            FlightPathTargetDeg: -10f, BankTargetDeg: 35f, ThrottleTarget: 0.35f,
            MinIasKt: 200f, MaxIasKt: 600f, MinAglFt: 1500f,
            GBudgetMax: 3f, GPushMin: 0.4f, GOnsetGPerSec: 1.5f, BankRateCapDegPerSec: 60f,
            HoldsEntryIas: true, FloorAglFt: 900f, RecoveryG: 3.5f),

        // El break es la unica maniobra a la que se le permite canjear altura
        // por velocidad de viraje (MinVsFpm -3000, y mas fraccion de G para el
        // alabeo): es lo que hace un piloto de verdad al romper una
        // persecucion. 75 deg son 3.86 g, que el ala solo da por encima de ~294
        // kt -- por debajo el alabeo baja progresivamente en vez de mentir.
        new ManeuverDefinition(ManeuverKind.CombatBreakLeft, "Break defensivo, izquierda",
            ManeuverCategory.Combinadas, ManeuverMode.AttitudeHold,
            "Viraje de alta G muy cerrado (~75 deg) para romper una persecucion. " +
            "Entra seco, acepta perder algo de altura, y afloja el alabeo solo si el " +
            "ala deja de dar la G que 75 deg piden.",
            BankTargetDeg: -75f, ThrottleTarget: 0.9f,
            MinIasKt: 250f, MaxIasKt: 450f,
            GBudgetMax: 5.5f, GPushMin: 0.5f, GOnsetGPerSec: 4f, BankRateCapDegPerSec: 90f,
            PitchBudgetFraction: 0.90f,
            HoldsEntryAltitude: true, MinVsFpm: -3000f),

        new ManeuverDefinition(ManeuverKind.CombatBreakRight, "Break defensivo, derecha",
            ManeuverCategory.Combinadas, ManeuverMode.AttitudeHold,
            "Viraje de alta G muy cerrado (~75 deg) para romper una persecucion. " +
            "Entra seco, acepta perder algo de altura, y afloja el alabeo solo si el " +
            "ala deja de dar la G que 75 deg piden.",
            BankTargetDeg: 75f, ThrottleTarget: 0.9f,
            MinIasKt: 250f, MaxIasKt: 450f,
            GBudgetMax: 5.5f, GPushMin: 0.5f, GOnsetGPerSec: 4f, BankRateCapDegPerSec: 90f,
            PitchBudgetFraction: 0.90f,
            HoldsEntryAltitude: true, MinVsFpm: -3000f),

        // --- Acrobacias: recorren angulos, no cronometros --------------------
        // Antes: "palanca a fondo 4 s" = 355 deg de alabeo a 250 kt, pero 620
        // deg a 500 kt (acababa de cuchillo) y 250 deg a 200 kt. Ahora el tramo
        // termina cuando el alabeo recorrido llega a 360, sea cual sea la
        // velocidad, y el ritmo lo pone la velocidad de alabeo disponible.
        new ManeuverDefinition(ManeuverKind.RollLeft, "Rollo (aleron), izquierda",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Vuelta completa de 360 deg de alabeo. Sube un poco el morro antes para " +
            "salir a la misma altura, y descarga al pasar por invertido.",
            ThrottleTarget: 0.85f,
            MinIasKt: 180f, MaxIasKt: 620f, MinAglFt: 1000f,
            GBudgetMax: 2.5f, GPushMin: 0f, GOnsetGPerSec: 2f,
            Segments: new[]
            {
                new AcroSegment("morro arriba", ArcAngleDeg: 6f, LoadFactorCmd: 1.8f, MaxSeconds: 4f),
                new AcroSegment("rollo", RollAngleDeg: -360f, UnloadedRoll: true, MaxSeconds: 12f),
            }),

        new ManeuverDefinition(ManeuverKind.RollRight, "Rollo (aleron), derecha",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Vuelta completa de 360 deg de alabeo. Sube un poco el morro antes para " +
            "salir a la misma altura, y descarga al pasar por invertido.",
            ThrottleTarget: 0.85f,
            MinIasKt: 180f, MaxIasKt: 620f, MinAglFt: 1000f,
            GBudgetMax: 2.5f, GPushMin: 0f, GOnsetGPerSec: 2f,
            Segments: new[]
            {
                new AcroSegment("morro arriba", ArcAngleDeg: 6f, LoadFactorCmd: 1.8f, MaxSeconds: 4f),
                new AcroSegment("rollo", RollAngleDeg: 360f, UnloadedRoll: true, MaxSeconds: 12f),
            }),

        // Un barrel roll es G constante + alabeo constante: eso es justo una
        // helice. Con 2.8 g la G en la parte alta sigue siendo ~+0.8 (nunca
        // negativa), que es lo que diferencia un barrel roll de una caida.
        new ManeuverDefinition(ManeuverKind.BarrelRollLeft, "Barrel roll, izquierda",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Rollo amplio en helice, tirando a G constante durante toda la vuelta: " +
            "sale a la misma altura y al mismo rumbo, sin pasar nunca por G negativa.",
            ThrottleTarget: 0.9f,
            MinIasKt: 220f, MaxIasKt: 600f, MinAglFt: 1500f,
            GBudgetMax: 3.2f, GPushMin: 0.3f, GOnsetGPerSec: 1.5f,
            Segments: new[]
            {
                new AcroSegment("morro arriba", ArcAngleDeg: 10f, LoadFactorCmd: 2f, MaxSeconds: 5f),
                new AcroSegment("helice", RollAngleDeg: -360f, LoadFactorCmd: 2.8f, MaxSeconds: 20f),
            }),

        new ManeuverDefinition(ManeuverKind.BarrelRollRight, "Barrel roll, derecha",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Rollo amplio en helice, tirando a G constante durante toda la vuelta: " +
            "sale a la misma altura y al mismo rumbo, sin pasar nunca por G negativa.",
            ThrottleTarget: 0.9f,
            MinIasKt: 220f, MaxIasKt: 600f, MinAglFt: 1500f,
            GBudgetMax: 3.2f, GPushMin: 0.3f, GOnsetGPerSec: 1.5f,
            Segments: new[]
            {
                new AcroSegment("morro arriba", ArcAngleDeg: 10f, LoadFactorCmd: 2f, MaxSeconds: 5f),
                new AcroSegment("helice", RollAngleDeg: 360f, LoadFactorCmd: 2.8f, MaxSeconds: 20f),
            }),

        // El looping va en dos tramos por los GASES, no por el mando: subiendo
        // hace falta toda la potencia, y en la bajada hay que quitarla o se
        // sale por abajo contra la VNE. La G la decide el planificador con la
        // energia disponible (mas G = arco mas pequeno = cabe en menos altura).
        new ManeuverDefinition(ManeuverKind.LoopBackflip, "Looping (backflip)",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Vuelta vertical completa. El tiron se calcula con la velocidad de entrada " +
            "para que el arco quepa en la altura disponible y no se quede sin velocidad " +
            "arriba; si entra justo, cierra el radio en vez de no salir.",
            ThrottleTarget: 1f,
            MinIasKt: 320f, MaxIasKt: 620f, MinAglFt: 2000f,
            GBudgetMax: 5.5f, GPushMin: 0.3f, GOnsetGPerSec: 3f,
            Segments: new[]
            {
                new AcroSegment("subida e invertido", ArcAngleDeg: 200f, Throttle: 1f, MaxSeconds: 30f),
                new AcroSegment("bajada y salida", ArcAngleDeg: 160f, Throttle: 0.35f, MaxSeconds: 25f),
            }),

        new ManeuverDefinition(ManeuverKind.ImmelmannLeft, "Immelmann (sale a la izquierda)",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Medio looping hacia arriba y, cerca de la cima, medio rollo para salir " +
            "nivelado al rumbo contrario y mas alto.",
            ThrottleTarget: 1f,
            MinIasKt: 320f, MaxIasKt: 620f, MinAglFt: 1500f,
            GBudgetMax: 5.5f, GPushMin: 0.2f, GOnsetGPerSec: 3f,
            Segments: new[]
            {
                new AcroSegment("medio looping", ArcAngleDeg: 165f, Throttle: 1f, MaxSeconds: 25f),
                new AcroSegment("medio rollo", RollAngleDeg: -180f, UnloadedRoll: true,
                                Throttle: 0.85f, MaxSeconds: 10f),
            }),

        new ManeuverDefinition(ManeuverKind.ImmelmannRight, "Immelmann (sale a la derecha)",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Medio looping hacia arriba y, cerca de la cima, medio rollo para salir " +
            "nivelado al rumbo contrario y mas alto.",
            ThrottleTarget: 1f,
            MinIasKt: 320f, MaxIasKt: 620f, MinAglFt: 1500f,
            GBudgetMax: 5.5f, GPushMin: 0.2f, GOnsetGPerSec: 3f,
            Segments: new[]
            {
                new AcroSegment("medio looping", ArcAngleDeg: 165f, Throttle: 1f, MaxSeconds: 25f),
                new AcroSegment("medio rollo", RollAngleDeg: 180f, UnloadedRoll: true,
                                Throttle: 0.85f, MaxSeconds: 10f),
            }),

        // Split-S: media vuelta invertida hacia abajo. Es la maniobra que mas
        // altura se come y la que peor estaba: la altura que pide va con el
        // cuadrado de la velocidad (4.600 ft a 250 kt, 13.800 a 500), asi que
        // un MinAglFt constante de 6.000 ft mentia en los dos extremos. Ahora
        // la altura necesaria se calcula antes de empezar y, si no cabe, se
        // frena primero o se abre el rollo a menos de 180 deg.
        new ManeuverDefinition(ManeuverKind.SplitSLeft, "Split-S (entra a la izquierda)",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Medio rollo para quedar invertido y medio looping hacia abajo: pierde " +
            "altura rapido y sale al rumbo contrario. Frena antes si entra demasiado " +
            "rapido para la altura que hay.",
            ThrottleTarget: 0.05f,
            MinIasKt: 220f, MaxIasKt: 550f, MinAglFt: 3500f,
            GBudgetMax: 5.5f, GPushMin: 0.2f, GOnsetGPerSec: 3f,
            Segments: new[]
            {
                new AcroSegment("medio rollo a invertido", RollAngleDeg: -180f, UnloadedRoll: true,
                                Throttle: 0.05f, MaxSeconds: 10f),
                new AcroSegment("medio looping abajo", ArcAngleDeg: 180f, Direction: -1,
                                Throttle: 0.05f, MaxSeconds: 30f),
            }),

        new ManeuverDefinition(ManeuverKind.SplitSRight, "Split-S (entra a la derecha)",
            ManeuverCategory.Acrobacias, ManeuverMode.Aerobatic,
            "Medio rollo para quedar invertido y medio looping hacia abajo: pierde " +
            "altura rapido y sale al rumbo contrario. Frena antes si entra demasiado " +
            "rapido para la altura que hay.",
            ThrottleTarget: 0.05f,
            MinIasKt: 220f, MaxIasKt: 550f, MinAglFt: 3500f,
            GBudgetMax: 5.5f, GPushMin: 0.2f, GOnsetGPerSec: 3f,
            Segments: new[]
            {
                new AcroSegment("medio rollo a invertido", RollAngleDeg: 180f, UnloadedRoll: true,
                                Throttle: 0.05f, MaxSeconds: 10f),
                new AcroSegment("medio looping abajo", ArcAngleDeg: 180f, Direction: -1,
                                Throttle: 0.05f, MaxSeconds: 30f),
            }),

        // --- Velocidad: solo motor, altitud anclada ---------------------------
        // Acelerar/Frenar decian "sin cambiar de altura" y no lo cumplian: con
        // un pitch fijo de 2.5 deg, frenar de 300 a 200 kt se iba en -1.500 fpm.
        // Ahora anclan la altitud de entrada como los virajes.
        new ManeuverDefinition(ManeuverKind.Accelerate, "Acelerar",
            ManeuverCategory.Velocidad, ManeuverMode.AttitudeHold,
            "Sube el IAS objetivo un escalon y lo persigue con el motor, manteniendo " +
            "la altitud. El escalon se ajusta a la ventana de velocidad que hay a esta altura.",
            IsSpeedHold: true, HoldsEntryAltitude: true,
            MaxIasKt: 650f,
            GBudgetMax: 2f, GPushMin: 0.7f, GOnsetGPerSec: 0.5f),

        new ManeuverDefinition(ManeuverKind.Decelerate, "Frenar",
            ManeuverCategory.Velocidad, ManeuverMode.AttitudeHold,
            "Baja el IAS objetivo un escalon manteniendo la altitud, con aerofrenos " +
            "si el ralenti solo no basta. No baja de la velocidad a la que ya no habria " +
            "morro para sostener el vuelo.",
            IsSpeedHold: true, HoldsEntryAltitude: true,
            MinIasKt: 150f,
            GBudgetMax: 2f, GPushMin: 0.7f, GOnsetGPerSec: 0.5f),
    };

    private static readonly Dictionary<ManeuverKind, ManeuverDefinition> ByKind =
        All.ToDictionary(m => m.Kind);

    public static ManeuverDefinition Get(ManeuverKind kind) => ByKind[kind];

    // Orden de categorias tal y como se quiere ensenar en la UI.
    public static readonly IReadOnlyList<ManeuverCategory> CategoryOrder = new[]
    {
        ManeuverCategory.Altitud,
        ManeuverCategory.Virajes,
        ManeuverCategory.Combinadas,
        ManeuverCategory.Acrobacias,
        ManeuverCategory.Velocidad,
    };
}
