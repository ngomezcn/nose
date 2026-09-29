namespace AICopilotCore.Domain;

// Escenario fijo de prueba: usuario en umbral LEBL 24L (ex-25L) y una IA
// ~15 km por el rumbo de pista, en vuelo, alejandose. Los numeros viven
// aqui (dominio); el connector solo aplica lo que le mandemos por
// Op.PlaceScenario.
public static class SimScenario
{
    // THR RWY 24L LEBL (AIP ENAIRE): 41°17'31.99"N 002°06'11.78"E, 2.4 m,
    // rumbo verdadero 245.59°.
    public const double UserLat = 41.292219;
    public const double UserLon = 2.103272;
    public const float UserElevMsl = 2.4f;
    public const float UserHdgTrue = 245.59f;
    public const float UserSpeedMps = 0f;

    // ~15 km por el rumbo de pista, ~2000 m MSL, misma proa (alejandose).
    public const double AiLat = 41.236355;
    public const double AiLon = 1.939920;
    public const float AiElevMsl = 2000f;
    public const float AiHdgTrue = 245.59f;
    public const float AiSpeedMps = 100f;  // ~194 kt

    // Relativo a la raiz de X-Plane. Citation X viene de serie en XP12.
    public const string AiAircraftRelPath =
        "Aircraft/Laminar Research/Cessna Citation X/Cessna_CitationX.acf";

    public const string Label =
        "LEBL 24L + Citation X a ~15 km (rumbo pista, en vuelo)";
}
