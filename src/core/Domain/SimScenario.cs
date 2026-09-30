namespace AICopilotCore.Domain;

// Inicios de simulacion. El usuario (ownship, idx 0) es siempre el caza /
// interceptor y la IA (idx 1) un Airbus A330. Los numeros viven aqui
// (dominio); el connector solo aplica lo que le mandemos por Op.PlaceScenario.
public enum SimStart
{
    // Cada avion en el inicio de una pista, parados.
    RunwayPair,
    // Caza en pista 06R; el A330 ya vuela a 10 km alejandose (ruta vuelo normal).
    InterceptFromGround,
    // Los dos en el aire a 15 km; el caza ya interceptando, el A330 en ruta.
    InterceptAirborne,
    // Igual pero cerca: 5 km y la mitad de diferencia de altura.
    InterceptAirborneClose,
}

public sealed record SimStartPlan
{
    public required SimStart Id { get; init; }
    public required string Name { get; init; }
    public required string Label { get; init; }

    public required double UserLat { get; init; }
    public required double UserLon { get; init; }
    public required float UserElevMsl { get; init; }
    public required float UserHdgTrue { get; init; }
    public required float UserSpeedMps { get; init; }
    public required bool UserOnGround { get; init; }

    public required double AiLat { get; init; }
    public required double AiLon { get; init; }
    public required float AiElevMsl { get; init; }
    public required float AiHdgTrue { get; init; }
    public required float AiSpeedMps { get; init; }
    public required bool AiOnGround { get; init; }

    // Ordenes que se dan al llegar ScenarioReady.
    public required bool AiRoute { get; init; }
    public required bool UserIntercept { get; init; }

    // Relativo a la raiz de X-Plane. A330 AI viene de serie en XP12.
    public string AiAircraftRelPath { get; init; } = SimScenarios.AiAircraftRelPath;
}

public static class SimScenarios
{
    public const string AiAircraftRelPath =
        "Aircraft/Laminar Research/Airbus A330-300/A330_AI.acf";

    // LEBL (apt.dat): 06R 41.2823122 / 2.0743500 ; 06L 41.2932451 / 2.0672749.
    // Rumbo verdadero de las pistas 06: 65.59° (24L/24R = 245.59°).
    private const double Rwy06RLat = 41.2823122;
    private const double Rwy06RLon = 2.0743500;
    private const double Rwy06LLat = 41.2932451;
    private const double Rwy06LLon = 2.0672749;
    private const float Rwy06Hdg = 65.59f;
    private const float RunwayElevMsl = 3.0f;

    // El origen del .acf del A330 va ~4.5 m por encima del suelo con el tren fuera.
    private const float AiOriginAboveGroundM = 4.5f;

    // Velocidad "de crucero" del A330 a FL210 (~300 KIAS) y de ambos aviones a
    // FL280 en el inicio en el aire (mismo TAS: velocidad similar).
    private const float AiSpeedFl210Mps = 218.7f;
    private const float SpeedFl280Mps = 230f;

    private const float Fl210M = 6400.8f;
    private const float Fl280M = 8534.4f;
    private const float Fl200M = 6096f;
    private const float Fl240M = 7315.2f;

    public static SimStartPlan Get(SimStart id) => id switch
    {
        SimStart.InterceptFromGround => InterceptFromGround,
        SimStart.InterceptAirborne => InterceptAirborne,
        SimStart.InterceptAirborneClose => InterceptAirborneClose,
        _ => RunwayPair,
    };

    public static readonly SimStartPlan RunwayPair = new()
    {
        Id = SimStart.RunwayPair,
        Name = "Inicio en pista: dos aviones",
        Label = "LEBL: caza en 06R y A330 en 06L, parados",
        UserLat = Rwy06RLat, UserLon = Rwy06RLon, UserElevMsl = RunwayElevMsl,
        UserHdgTrue = Rwy06Hdg, UserSpeedMps = 0f, UserOnGround = true,
        AiLat = Rwy06LLat, AiLon = Rwy06LLon, AiElevMsl = RunwayElevMsl + AiOriginAboveGroundM,
        AiHdgTrue = Rwy06Hdg, AiSpeedMps = 0f, AiOnGround = true,
        AiRoute = false, UserIntercept = false,
    };

    public static readonly SimStartPlan InterceptFromGround = BuildInterceptFromGround();
    public static readonly SimStartPlan InterceptAirborne = BuildInterceptAirborne();
    public static readonly SimStartPlan InterceptAirborneClose = BuildInterceptAirborneClose();

    // Como InterceptAirborne pero a 5 km en total (4,33 km delante + 2,5 km a la
    // derecha) y 4.000 ft de diferencia (la mitad de los 8.000 ft del lejano).
    private static SimStartPlan BuildInterceptAirborneClose()
    {
        (double aLat, double aLon) = Destination(Rwy06RLat, Rwy06RLon, Rwy06Hdg, 4_330);
        (double lat, double lon) = Destination(aLat, aLon, Rwy06Hdg + 90.0, 2_500);
        return new SimStartPlan
        {
            Id = SimStart.InterceptAirborneClose,
            Name = "Interceptacion en el aire: cerca",
            Label = "Caza a 20.000 ft y A330 a 24.000 ft, a 5 km (delante y a la derecha), caza interceptando",
            UserLat = Rwy06RLat, UserLon = Rwy06RLon, UserElevMsl = Fl200M,
            UserHdgTrue = Rwy06Hdg, UserSpeedMps = SpeedFl280Mps, UserOnGround = false,
            AiLat = lat, AiLon = lon, AiElevMsl = Fl240M,
            AiHdgTrue = Rwy06Hdg, AiSpeedMps = SpeedFl280Mps, AiOnGround = false,
            AiRoute = true, UserIntercept = true,
        };
    }

    // Caza en 06R; A330 a 10 km por el rumbo de pista, FL210, misma proa (se
    // aleja), ruta vuelo normal.
    private static SimStartPlan BuildInterceptFromGround()
    {
        (double lat, double lon) = Destination(Rwy06RLat, Rwy06RLon, Rwy06Hdg, 10_000);
        return new SimStartPlan
        {
            Id = SimStart.InterceptFromGround,
            Name = "Interceptacion desde tierra",
            Label = "LEBL 06R + A330 a 10 km, FL210, alejandose (ruta vuelo normal)",
            UserLat = Rwy06RLat, UserLon = Rwy06RLon, UserElevMsl = RunwayElevMsl,
            UserHdgTrue = Rwy06Hdg, UserSpeedMps = 0f, UserOnGround = true,
            AiLat = lat, AiLon = lon, AiElevMsl = Fl210M,
            AiHdgTrue = Rwy06Hdg, AiSpeedMps = AiSpeedFl210Mps, AiOnGround = false,
            AiRoute = true, UserIntercept = false,
        };
    }

    // Los dos en el aire a FL280, 15 km entre ellos, misma proa y velocidad;
    // el caza intercepta y el A330 sigue su ruta vuelo normal.
    private static SimStartPlan BuildInterceptAirborne()
    {
        // 15 km por delante y 5 km a la derecha del caza (visto desde arriba).
        (double aLat, double aLon) = Destination(Rwy06RLat, Rwy06RLon, Rwy06Hdg, 15_000);
        (double lat, double lon) = Destination(aLat, aLon, Rwy06Hdg + 90.0, 5_000);
        return new SimStartPlan
        {
            Id = SimStart.InterceptAirborne,
            Name = "Interceptacion en el aire",
            Label = "Caza a 20.000 ft y A330 a 28.000 ft, 15 km delante y 5 km a la derecha, caza interceptando",
            UserLat = Rwy06RLat, UserLon = Rwy06RLon, UserElevMsl = Fl200M,
            UserHdgTrue = Rwy06Hdg, UserSpeedMps = SpeedFl280Mps, UserOnGround = false,
            AiLat = lat, AiLon = lon, AiElevMsl = Fl280M,
            AiHdgTrue = Rwy06Hdg, AiSpeedMps = SpeedFl280Mps, AiOnGround = false,
            AiRoute = true, UserIntercept = true,
        };
    }

    // Punto a distM metros de (lat, lon) siguiendo el rumbo verdadero hdgDeg
    // (esfera; de sobra a 15 km).
    private static (double lat, double lon) Destination(double lat, double lon, double hdgDeg, double distM)
    {
        const double R = 6_371_000.0;
        double d = distM / R;
        double h = hdgDeg * Math.PI / 180.0;
        double p1 = lat * Math.PI / 180.0;
        double l1 = lon * Math.PI / 180.0;
        double p2 = Math.Asin(Math.Sin(p1) * Math.Cos(d) + Math.Cos(p1) * Math.Sin(d) * Math.Cos(h));
        double l2 = l1 + Math.Atan2(Math.Sin(h) * Math.Sin(d) * Math.Cos(p1),
                                    Math.Cos(d) - Math.Sin(p1) * Math.Sin(p2));
        return (p2 * 180.0 / Math.PI, l2 * 180.0 / Math.PI);
    }
}
