using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

// Gestor de cajas negras por avion (XPLM 0 = ownship, 1..19 = IAs).
// Cada DataLogger se crea bajo demanda; por defecto ninguno graba.
// Empezar/Detener/Borrar de la UI actuan sobre el indice en foco; Refresh
// alimenta todos los que tengan IsRecording.
public sealed class FlightDataLogs
{
    // Ownship + slots de multiplayer.
    public const int MaxPlanes = 1 + Datarefs.OtherPlaneSlots;

    private readonly DataLogger?[] _logs = new DataLogger?[MaxPlanes];

    public DataLogger For(int xplmIndex)
    {
        if (xplmIndex < 0) xplmIndex = 0;
        if (xplmIndex >= MaxPlanes) xplmIndex = MaxPlanes - 1;
        return _logs[xplmIndex] ??= Create(xplmIndex);
    }

    // Loggers ya creados que estan grabando (varios a la vez permitidos).
    public IEnumerable<(int XplmIndex, DataLogger Logger)> Recording
    {
        get
        {
            for (int i = 0; i < _logs.Length; i++)
            {
                DataLogger? log = _logs[i];
                if (log is { IsRecording: true })
                    yield return (i, log);
            }
        }
    }

    public void CloseAll()
    {
        for (int i = 0; i < _logs.Length; i++)
            _logs[i]?.Close();
    }

    private static DataLogger Create(int xplmIndex) =>
        xplmIndex == 0 ? DataLogger.ForOwnship() : DataLogger.ForPlane(xplmIndex);
}
