namespace AICopilotCore.Domain;

// Escenario fijo de prueba: usuario en umbral LEBL 24L (ex-25L) y un
// Airbus A330 ~15 km por el rumbo de pista + 10 km a la derecha, a FL210,
// misma proa (siempre delante), ~300 KIAS. Los numeros viven aqui
// (dominio); el connector solo aplica lo que le mandemos por Op.PlaceScenario.
public static class SimScenario
{
    // THR RWY 24L LEBL (AIP ENAIRE): 41°17'31.99"N 002°06'11.78"E, 2.4 m,
    // rumbo verdadero 245.59°.
    public const double UserLat = 41.292219;
    public const double UserLon = 2.103272;
    public const float UserElevMsl = 2.4f;
    public const float UserHdgTrue = 245.59f;
    public const float UserSpeedMps = 0f;

    // 15 km por el rumbo de pista + 10 km lateral (derecha), FL210, misma
    // proa. Velocidad en TAS ≈ 300 KIAS a 21 000 ft (σ≈0.498 → ~425 kt).
    public const double AiLat = 41.318334;
    public const double AiLon = 1.890555;
    public const float AiElevMsl = 6400.8f;   // 21 000 ft
    public const float AiHdgTrue = 245.59f;
    public const float AiSpeedMps = 218.7f;   // ~300 KIAS @ FL210

    // Relativo a la raiz de X-Plane. A330 AI viene de serie en XP12.
    public const string AiAircraftRelPath =
        "Aircraft/Laminar Research/Airbus A330-300/A330_AI.acf";

    public const string Label =
        "LEBL 24L + A330 a FL210 (~15 km delante / 10 km lateral, ~300 KIAS)";
}
