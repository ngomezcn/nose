using System.Diagnostics;

namespace AICopilotCore.Domain.Agents;

// Lo que vio un avion entre dos muestras de la caja negra (~10 Hz) sobre el
// ritmo de sus Tick (un frame de sim cada uno). La caja negra muestrea a 10 Hz y
// por eso no ve un tiron de frame: este resumen si. Solo mide, no decide nada.
public readonly record struct TickCadenceSummary(
    int Ticks,              // Tick recibidos desde la muestra anterior (60 fps -> ~6)
    float DtMinMs,          // dt de sim minimo / maximo entre frames
    float DtMaxMs,
    float GapMaxMs,         // hueco maximo de PARED entre dos Tick (jitter del pipe / core)
    float JumpMaxM,         // mayor desplazamiento de posicion entre dos Tick
    float JumpErrMaxM)      // mayor |desplazamiento - v*dt|: el tiron real, sin la velocidad
{
    public static TickCadenceSummary Empty => new(0, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
}

// Acumulador por avion. Observe() lo llama AircraftAgent.Tick (hilo del pipe);
// Drain() lo llama la UI al muestrear. Un lock basta: ~60 llamadas/s.
public sealed class TickCadence
{
    private readonly object _gate = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();

    private double _lastWallMs = double.NaN;
    private bool _hasPrev;
    private double _px, _py, _pz, _pvx, _pvy, _pvz;

    private int _ticks;
    private float _dtMin = float.PositiveInfinity, _dtMax;
    private float _gapMax, _jumpMax, _jumpErrMax;

    public void Observe(float dt, bool hasKin, in Kinematics k)
    {
        lock (_gate)
        {
            double now = _wall.Elapsed.TotalMilliseconds;
            if (!double.IsNaN(_lastWallMs))
                _gapMax = Math.Max(_gapMax, (float)(now - _lastWallMs));
            _lastWallMs = now;

            _ticks++;
            float dtMs = dt * 1000f;
            _dtMin = Math.Min(_dtMin, dtMs);
            _dtMax = Math.Max(_dtMax, dtMs);

            if (!hasKin) { _hasPrev = false; return; }
            if (_hasPrev && dt > 0f)
            {
                double dx = k.X - _px, dy = k.Y - _py, dz = k.Z - _pz;
                double jump = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double ex = dx - _pvx * dt, ey = dy - _pvy * dt, ez = dz - _pvz * dt;
                double err = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                _jumpMax = Math.Max(_jumpMax, (float)jump);
                _jumpErrMax = Math.Max(_jumpErrMax, (float)err);
            }
            _px = k.X; _py = k.Y; _pz = k.Z;
            _pvx = k.Vx; _pvy = k.Vy; _pvz = k.Vz;
            _hasPrev = true;
        }
    }

    // Devuelve lo acumulado y empieza de cero. Sin Tick desde la ultima vez
    // (core sin telemetria) devuelve Ticks = 0 y el resto NaN.
    public TickCadenceSummary Drain()
    {
        lock (_gate)
        {
            TickCadenceSummary s = _ticks == 0
                ? TickCadenceSummary.Empty
                : new TickCadenceSummary(_ticks, _dtMin, _dtMax,
                    _gapMax > 0f ? _gapMax : float.NaN,
                    _hasPrev ? _jumpMax : float.NaN,
                    _hasPrev ? _jumpErrMax : float.NaN);
            _ticks = 0;
            _dtMin = float.PositiveInfinity; _dtMax = 0f;
            _gapMax = 0f; _jumpMax = 0f; _jumpErrMax = 0f;
            return s;
        }
    }
}
