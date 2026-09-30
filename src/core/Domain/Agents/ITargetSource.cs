using AICopilotCore.Connector;

namespace AICopilotCore.Domain.Agents;

// De donde saca una InterceptSequence la foto de su blanco. Resuelve CUALQUIER
// indice XPLM 0..19 (0 = ownship, 1..19 = IA). Watch/Unwatch piden/sueltan
// telemetria a ritmo de frame para ese indice (refcount en quien implemente).
public interface ITargetSource
{
    bool TryCapture(int xplmIndex, out TargetSnapshot snapshot);
    void Watch(int xplmIndex);
    void Unwatch(int xplmIndex);
}

// Fuente por defecto: Datarefs (ownship por local_*, IAs por planeN_*).
public sealed class DatarefsTargetSource : ITargetSource
{
    private readonly Datarefs _d;
    public DatarefsTargetSource(Datarefs datarefs) => _d = datarefs;

    public bool TryCapture(int xplmIndex, out TargetSnapshot snapshot)
    {
        snapshot = TargetSnapshot.Capture(_d, xplmIndex);
        return snapshot.Valid;
    }

    public void Watch(int xplmIndex) { if (xplmIndex >= 1) _d.WatchPlane(xplmIndex - 1); }
    public void Unwatch(int xplmIndex) { if (xplmIndex >= 1) _d.UnwatchPlane(xplmIndex - 1); }
}

// Fuente inyectable por delegados (p. ej. el AircraftWorld).
public sealed class DelegateTargetSource : ITargetSource
{
    public delegate bool CaptureFn(int xplmIndex, out TargetSnapshot snapshot);

    private readonly CaptureFn _capture;
    private readonly Action<int>? _watch;
    private readonly Action<int>? _unwatch;

    public DelegateTargetSource(CaptureFn capture, Action<int>? watch = null, Action<int>? unwatch = null)
    {
        _capture = capture;
        _watch = watch;
        _unwatch = unwatch;
    }

    public bool TryCapture(int xplmIndex, out TargetSnapshot snapshot) =>
        _capture(xplmIndex, out snapshot);
    public void Watch(int xplmIndex) => _watch?.Invoke(xplmIndex);
    public void Unwatch(int xplmIndex) => _unwatch?.Invoke(xplmIndex);
}
