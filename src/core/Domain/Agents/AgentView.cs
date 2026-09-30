namespace AICopilotCore.Domain.Agents;

// Que esta haciendo un avion, a grandes rasgos (para pintar el modo).
public enum AgentMode
{
    Idle,        // sin orden: manual / IA nativa
    Takeoff,
    Route,       // CruisePilot en marcha (crucero / ruta)
    Maneuver,
    Intercept,
}

// Foto inmutable de un AircraftAgent para la UI: lo mismo que hoy arma
// ShellWindow a mano a partir de _sequence/_maneuvers/_intercept/_director,
// pero por avion. La UI la pide con AircraftAgent.View() (barato, sin estado).
public sealed record AgentView(
    int XplmIndex,
    string Label,
    string ProfileName,
    AgentMode Mode,
    string ModeText,               // "Manual", "Despegue", "Ruta · Recto", "Interceptar X"...
    string SequenceText,           // linea de secuencia del panel ("En espera" si nada)
    string ChecklistText,          // checklist de despegue
    // Despegue
    bool TakeoffRunning,
    TakeoffPhase TakeoffPhase,
    TakeoffStyleId TakeoffStyleId,
    string TakeoffStyleName,
    // Ruta / crucero
    bool CruiseRunning,
    CruiseModeId CruiseModeId,     // modo activo o el pendiente tras despegue
    string CruiseModeName,
    string CruisePhaseText,
    bool RoutePending,             // despegue en curso y luego CruisePilot
    // Maniobra
    bool ManeuverRunning,
    string ManeuverLabel,
    string ManeuverPhaseText,
    string Adaptation,             // recorte/adaptacion vigente ("" = ninguna)
    string Protection,             // proteccion de envolvente actuando ("" = ninguna)
    // Intercept
    bool InterceptRunning,
    bool InterceptPending,         // despegue combate y luego persecucion
    int InterceptTargetIndex,      // -1 = ninguno
    string InterceptTargetLabel,
    string InterceptPhaseText)
{
    public bool IsBusy => TakeoffRunning || CruiseRunning || ManeuverRunning ||
                          InterceptRunning || InterceptPending || RoutePending;
}
