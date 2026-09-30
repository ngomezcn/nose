using AICopilotCore.Connector;
using AICopilotCore.Domain.Agents;

namespace AICopilotCore.Domain;

// Orquesta el tramo de vuelo de una Ruta sobre ManeuverSequence:
// LevelWings en recto y, segun el CruiseMode, virajes del catalogo cuando
// toca. No escribe mandos: solo Decide cuando Start() de LevelWings /
// Turn*. El director aborta este piloto al pedir otra secuencia.
//
// Tambien admite ordenes manuales desde el panel RUTA (viraje ahora /
// nudges de ±1000 ft) sin soltar el crucero: el objetivo de altitud vive
// aqui y se reaplica tras cada Start de maniobra, porque ManeuverSequence
// re-ancla al Reset.
public sealed class CruisePilot
{
    private readonly ManeuverSequence _maneuvers;
    private readonly IAircraftBody _body;
    private readonly object _gate = new();
    private readonly Random _rng = new();

    private bool _running;
    private CruiseMode _mode = CruiseModes.Straight;
    private bool _turning;
    private float _straightElapsed;
    private float _nextTurnAtSec = float.PositiveInfinity;
    private float _turnStartHdgDeg;
    private float _turnDeltaTargetDeg;
    // Altitud MSL que defiende el crucero (LevelWings / virajes). NaN hasta Start.
    private float _targetAltFt = float.NaN;

    // Cambio de rumbo tipico al pulsar un boton de viraje en el panel RUTA.
    public const float ManualTurnDeltaDeg = 90f;
    public const float AltitudeNudgeFt = 1000f;

    public CruisePilot(ManeuverSequence maneuvers, IAircraftBody body)
    {
        _maneuvers = maneuvers;
        _body = body;
    }

    // Sense() para no leer un State viejo (el body puente de la ruta no lo refresca nadie mas).
    private float CurrentHeadingDeg()
    {
        _body.Sense();
        return _body.State.HeadingDeg;
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

    public float TargetAltitudeFt
    {
        get { lock (_gate) return _targetAltFt; }
    }

    public string PhaseText
    {
        get
        {
            lock (_gate)
            {
                if (!_running) return "En espera";
                string alt = float.IsNaN(_targetAltFt) ? "" : $" · ALT {_targetAltFt:0} ft";
                if (_turning)
                    return $"{_mode.Name} · virando {_turnDeltaTargetDeg:0}°{alt}";
                if (!_mode.DoesTurns)
                    return $"{_mode.Name} · recto{alt}";
                float left = MathF.Max(0f, _nextTurnAtSec - _straightElapsed);
                return $"{_mode.Name} · recto ({left:0}s){alt}";
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
            _body.Sense();
            _targetAltFt = _body.State.AltFt;
            StartManeuverPreservingAlt(ManeuverKind.LevelWings);
            LogAction($"Ruta: vuelo [{_mode.Name}] · ALT {_targetAltFt:0} ft");
        }
    }

    // Cambia el tipo de vuelo sin soltar el crucero (panel TIPO DE VUELO).
    // Si esta virando, el nuevo modo rige al acabar el viraje; si va recto,
    // reprograma el proximo tramo desde ahora.
    public bool SetMode(CruiseMode mode, out string error)
    {
        lock (_gate)
        {
            if (!_running)
            {
                error = "ruta no activa: pulsa Iniciar primero";
                return false;
            }
            if (_mode.Id == mode.Id)
            {
                error = "";
                return true;
            }

            _mode = mode;
            if (!_turning)
            {
                _straightElapsed = 0f;
                ScheduleNextStraight();
            }
            LogAction($"Ruta: tipo de vuelo → [{_mode.Name}]");
            error = "";
            return true;
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
                    CurrentHeadingDeg() - _turnStartHdgDeg));
                if (turned >= _turnDeltaTargetDeg)
                {
                    _turning = false;
                    _straightElapsed = 0f;
                    ScheduleNextStraight();
                    StartManeuverPreservingAlt(ManeuverKind.LevelWings);
                    LogAction($"Ruta [{_mode.Name}]: fin viraje " +
                              $"(+{turned:0}°) → recto ~{_nextTurnAtSec:0}s");
                }
                return;
            }

            _straightElapsed += dt;
            if (_straightElapsed < _nextTurnAtSec) return;

            bool left = _rng.Next(2) == 0;
            BeginTurnLocked(left ? _mode.TurnLeft : _mode.TurnRight,
                            RandRange(_mode.TurnDeltaMinDeg, _mode.TurnDeltaMaxDeg),
                            scheduled: true);
        }
    }

    // Viraje inmediato (botones del panel RUTA) sin soltar el crucero.
    // kind debe ser un TurnGentle*/TurnTight*.
    public bool RequestTurn(ManeuverKind kind, float deltaDeg, out string error)
    {
        lock (_gate)
        {
            if (!_running)
            {
                error = "ruta no activa: pulsa Iniciar primero";
                return false;
            }
            if (!IsTurnKind(kind))
            {
                error = "solo virajes (abierto/cerrado)";
                return false;
            }
            if (deltaDeg < 5f) deltaDeg = ManualTurnDeltaDeg;
            BeginTurnLocked(kind, deltaDeg, scheduled: false);
            error = "";
            return true;
        }
    }

    // Mueve el ancla de altitud ±deltaFt (p. ej. ±1000). El hold de
    // LevelWings / virajes persigue el nuevo objetivo.
    public bool NudgeAltitude(float deltaFt, out string error)
    {
        lock (_gate)
        {
            if (!_running)
            {
                error = "ruta no activa: pulsa Iniciar primero";
                return false;
            }

            _body.Sense();
            FlightState s = _body.State;
            float current = float.IsNaN(_targetAltFt) ? s.AltFt : _targetAltFt;
            // Mismo suelo AGL tipico que ControlTuning.TerrainFloorAglFt.
            float floorMsl = s.AltFt - MathF.Max(s.AglFt, 0f) + 600f;
            float next = Math.Clamp(current + deltaFt, floorMsl, F14Aero.ServiceCeilingFt);
            if (MathF.Abs(next - current) < 1f)
            {
                error = deltaFt < 0f
                    ? "ya estas cerca del suelo"
                    : "ya estas en el techo de servicio";
                return false;
            }

            _targetAltFt = next;
            _maneuvers.SetReferenceAltitude(_targetAltFt);
            string dir = deltaFt >= 0f ? "subir" : "bajar";
            LogAction($"Ruta [{_mode.Name}]: {dir} a ALT {_targetAltFt:0} ft " +
                      $"({deltaFt:+0;-0} ft)");
            error = "";
            return true;
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
            _targetAltFt = float.NaN;
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
            _targetAltFt = float.NaN;
        }
    }

    private void BeginTurnLocked(ManeuverKind kind, float deltaDeg, bool scheduled)
    {
        _turnDeltaTargetDeg = deltaDeg;
        _turnStartHdgDeg = CurrentHeadingDeg();
        _turning = true;
        StartManeuverPreservingAlt(kind);
        bool left = kind is ManeuverKind.TurnGentleLeft or ManeuverKind.TurnTightLeft;
        string how = scheduled ? "" : " (manual)";
        LogAction($"Ruta [{_mode.Name}]: viraje {(left ? "izq" : "der")} " +
                  $"~{_turnDeltaTargetDeg:0}°{how}");
    }

    // Start de ManeuverSequence re-ancla la altitud a la actual; reaplicamos
    // el objetivo del crucero para no perder nudges pendientes.
    private void StartManeuverPreservingAlt(ManeuverKind kind)
    {
        _maneuvers.Start(kind);
        if (!float.IsNaN(_targetAltFt))
            _maneuvers.SetReferenceAltitude(_targetAltFt);
    }

    private static bool IsTurnKind(ManeuverKind kind) => kind is
        ManeuverKind.TurnGentleLeft or ManeuverKind.TurnGentleRight or
        ManeuverKind.TurnTightLeft or ManeuverKind.TurnTightRight;

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
