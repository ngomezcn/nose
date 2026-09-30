namespace AICopilotCore.Domain;

// Como se comporta el tramo de vuelo de una Ruta (despues del despegue,
// o enseguida si el avion ya esta en el aire).
public enum CruiseModeId
{
    // Recto y nivelado: mantiene rumbo, altitud y ~300 kt.
    Straight,
    // Movedizo: tramos cortos y virajes frecuentes / amplios.
    Wanderer,
    // Vuelo normal: tramos largos con alguna vuelta de vez en cuando.
    Normal,
}

public sealed record CruiseMode
{
    public required CruiseModeId Id { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }

    // Segundos de tramo recto entre virajes (aleatorio en [Min, Max]).
    public required float StraightMinSec { get; init; }
    public required float StraightMaxSec { get; init; }

    // Cuanto cambia el rumbo en cada vuelta (aleatorio en [Min, Max]).
    public required float TurnDeltaMinDeg { get; init; }
    public required float TurnDeltaMaxDeg { get; init; }

    // Maniobras del catalogo usadas para el viraje (izq / der).
    public required ManeuverKind TurnLeft { get; init; }
    public required ManeuverKind TurnRight { get; init; }

    public bool DoesTurns => Id != CruiseModeId.Straight;
}

public static class CruiseModes
{
    public static readonly CruiseMode Straight = new()
    {
        Id = CruiseModeId.Straight,
        Name = "Recto estabilizado",
        Summary = "Vuela recto y nivelado a ~300 kt, sin cambiar de rumbo. " +
                  "El modo mas estable: bueno para observar o enganchar otra orden.",
        StraightMinSec = float.PositiveInfinity,
        StraightMaxSec = float.PositiveInfinity,
        TurnDeltaMinDeg = 0f,
        TurnDeltaMaxDeg = 0f,
        TurnLeft = ManeuverKind.TurnGentleLeft,
        TurnRight = ManeuverKind.TurnGentleRight,
    };

    public static readonly CruiseMode Wanderer = new()
    {
        Id = CruiseModeId.Wanderer,
        Name = "Movedizo",
        Summary = "Tramos cortos y muchas vueltas: cambia de rumbo a menudo " +
                  "(~60-120°) con viraje cerrado. Parece que no sabe a donde va.",
        StraightMinSec = 12f,
        StraightMaxSec = 28f,
        TurnDeltaMinDeg = 60f,
        TurnDeltaMaxDeg = 120f,
        TurnLeft = ManeuverKind.TurnTightLeft,
        TurnRight = ManeuverKind.TurnTightRight,
    };

    public static readonly CruiseMode Normal = new()
    {
        Id = CruiseModeId.Normal,
        Name = "Vuelo normal",
        Summary = "Crucero tipico: va recto un buen rato y de vez en cuando " +
                  "hace una vuelta suave (~25-50°), como un transito real.",
        StraightMinSec = 70f,
        StraightMaxSec = 160f,
        TurnDeltaMinDeg = 25f,
        TurnDeltaMaxDeg = 50f,
        TurnLeft = ManeuverKind.TurnGentleLeft,
        TurnRight = ManeuverKind.TurnGentleRight,
    };

    public static CruiseMode Get(CruiseModeId id) => id switch
    {
        CruiseModeId.Wanderer => Wanderer,
        CruiseModeId.Normal => Normal,
        _ => Straight,
    };
}
