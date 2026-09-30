using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

// Orquesta el tramo de vuelo de una Ruta sobre ManeuverSequence:
// LevelWings en recto y, segun el CruiseMode, virajes del catalogo cuando
// toca. No escribe mandos: solo Decide cuando Start() de LevelWings /
// Turn*. El director aborta este piloto al pedir otra secuencia.
public sealed class CruisePilot
{
    private readonly ManeuverSequence _maneuvers;
    private readonly Datarefs _d;
    private readonly object _gate = new();
    private readonly Random _rng = new();

    private bool _running;
    private CruiseMode _mode = CruiseModes.Straight;
    private bool _turning;
    private float _straightElapsed;
    private float _nextTurnAtSec = float.PositiveInfinity;
    private float _turnStartHdgDeg;
    private float _turnDeltaTargetDeg;

    public CruisePilot(ManeuverSequence maneuvers, Datarefs datarefs)
    {
        _maneuvers = maneuvers;
        _d = datarefs;
    }

    public event Action<string>? ActionLogged;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public CruiseMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    public string PhaseText
    {
        get
        {
            lock (_gate)
            {
                if (!_running) return "En espera";
                if (_turning)
                    return $"{_mode.Name} · virando {_turnDeltaTargetDeg:0}°";
                if (!_mode.DoesTurns)
                    return $"{_mode.Name} · recto";
                float left = MathF.Max(0f, _nextTurnAtSec - _straightElapsed);
                return $"{_mode.Name} · recto ({left:0}s)";
            }
        }
    }

    // Arranca LevelWings y programa el primer tramo recto. Sustituye
    // cualquier maniobra previa (ManeuverSequence.Start ya lo hace).
    public void Start(CruiseMode mode)
    {
        lock (_gate)
        {
            _mode = mode;
            _turning = false;
            _straightElapsed = 0f;
            ScheduleNextStraight();
            _running = true;
            _maneuvers.Start(ManeuverKind.LevelWings);
            LogAction($"Ruta: vuelo [{_mode.Name}]");
        }
    }

    public void Update(float dt)
    {
        if (dt <= 0f) return;
        lock (_gate)
        {
            if (!_running) return;

            // Si el usuario (u otra orden) corto la maniobra, este piloto muere.
            if (!_maneuvers.IsRunning)
            {
                _running = false;
                return;
            }

            if (!_mode.DoesTurns) return;

            if (_turning)
            {
                float turned = MathF.Abs(Pid.NormalizeAngleDeg180(
                    _d.HeadingDeg.Float - _turnStartHdgDeg));
                if (turned >= _turnDeltaTargetDeg)
                {
                    _turning = false;
                    _straightElapsed = 0f;
                    ScheduleNextStraight();
                    _maneuvers.Start(ManeuverKind.LevelWings);
                    LogAction($"Ruta [{_mode.Name}]: fin viraje " +
                              $"(+{turned:0}°) → recto ~{_nextTurnAtSec:0}s");
                }
                return;
            }

            _straightElapsed += dt;
            if (_straightElapsed < _nextTurnAtSec) return;

            bool left = _rng.Next(2) == 0;
            _turnDeltaTargetDeg = RandRange(_mode.TurnDeltaMinDeg, _mode.TurnDeltaMaxDeg);
            _turnStartHdgDeg = _d.HeadingDeg.Float;
            _turning = true;
            ManeuverKind kind = left ? _mode.TurnLeft : _mode.TurnRight;
            _maneuvers.Start(kind);
            LogAction($"Ruta [{_mode.Name}]: viraje {(left ? "izq" : "der")} " +
                      $"~{_turnDeltaTargetDeg:0}°");
        }
    }

    // Deja de orquestar. No aborta ManeuverSequence: el director lo hace
    // cuando corresponde (AbortAll / StartManeuver / etc.).
    public void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _turning = false;
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (!_running) return;
            LogAction($"Ruta [{_mode.Name}]: abortado");
            _running = false;
            _turning = false;
        }
    }

    private void ScheduleNextStraight()
    {
        if (!_mode.DoesTurns)
        {
            _nextTurnAtSec = float.PositiveInfinity;
            return;
        }
        _nextTurnAtSec = RandRange(_mode.StraightMinSec, _mode.StraightMaxSec);
    }

    private float RandRange(float min, float max)
    {
        if (float.IsInfinity(min) || float.IsInfinity(max)) return float.PositiveInfinity;
        if (max <= min) return min;
        return min + (float)_rng.NextDouble() * (max - min);
    }

    private void LogAction(string msg) => ActionLogged?.Invoke(msg);
}
