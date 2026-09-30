using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

// Vuelo recto y nivelado CINEMATICO de una IA (XPLM index 1..19).
//
// No usa PID ni AircraftControls: toma AiControl, captura posicion/rumbo/
// velocidad horizontal de la telemetria y reescribe cada frame con Hold
// Latch (x/z integrados, y/actitud/vy fijos). Puede correr EN PARALELO
// con Takeoff/Maneuver/Intercept del ownship: no comparte overrides de
// joystick.
public sealed class AiStraightHold
{
    // Morro tipico de crucero nivelado; bank y vy a cero.
    private const double HoldPitchDeg = 2.0;
    private const double HoldBankDeg = 0.0;
    private const double HoldVy = 0.0;

    private readonly ConnectorClient _c;
    private readonly Datarefs _d;
    private readonly object _gate = new();

    private bool _running;
    private bool _pendingCapture;
    private bool _holding;
    private int _xplmIndex = -1;
    private int _slot = -1;

    private double _x, _y, _z;
    private double _vx, _vz;
    private double _headingDeg;

    public AiStraightHold(ConnectorClient client, Datarefs datarefs)
    {
        _c = client;
        _d = datarefs;
    }

    public event Action<string>? ActionLogged;

    public bool IsRunning
    {
        get { lock (_gate) return _running; }
    }

    public int XplmIndex
    {
        get { lock (_gate) return _xplmIndex; }
    }

    // Arranca el hold sobre la IA indicada. Si no hay telemetria todavia,
    // queda pending y captura en el primer Update con datos.
    public bool Start(int xplmIndex, out string error)
    {
        lock (_gate)
        {
            if (xplmIndex < 1 || xplmIndex > Datarefs.OtherPlaneSlots)
            {
                error = $"indice IA fuera de rango (1..{Datarefs.OtherPlaneSlots})";
                return false;
            }

            if (_running) StopInternal(logAbort: true);

            _xplmIndex = xplmIndex;
            _slot = xplmIndex - 1;
            _holding = false;
            _c.TakeAiControl(xplmIndex);
            _d.FocusOtherPlane(_slot);

            if (TryCapture())
            {
                _pendingCapture = false;
                LogAction($"IA {_xplmIndex}: vuelo recto y nivelado " +
                          $"(hdg {_headingDeg:0.0}°, pitch {HoldPitchDeg:0}°)");
            }
            else
            {
                _pendingCapture = true;
                LogAction($"IA {_xplmIndex}: esperando telemetria para " +
                          "vuelo recto y nivelado");
            }

            _running = true;
            error = "";
            return true;
        }
    }

    public void Update(float dt)
    {
        if (dt <= 0f) return;
        lock (_gate)
        {
            if (!_running || _slot < 0) return;

            if (_pendingCapture)
            {
                if (!TryCapture()) return;
                _pendingCapture = false;
                LogAction($"IA {_xplmIndex}: telemetria lista — " +
                          $"vuelo recto y nivelado (hdg {_headingDeg:0.0}°)");
            }

            _x += _vx * dt;
            _z += _vz * dt;
            ApplyHolds();
            _holding = true;
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (!_running) return;
            StopInternal(logAbort: true);
        }
    }

    // Pipe caido: el SafetyGuard del connector suelta holds; aqui solo
    // olvidamos estado local y el foco de suscripcion.
    public void OnConnectionLost()
    {
        lock (_gate)
        {
            if (!_running) return;
            _holding = false;
            _pendingCapture = false;
            _xplmIndex = -1;
            _slot = -1;
            _running = false;
            _d.FocusOtherPlane(-1);
        }
    }

    private bool TryCapture()
    {
        DataHandle x = _d.OtherLocalX[_slot];
        DataHandle y = _d.OtherLocalY[_slot];
        DataHandle z = _d.OtherLocalZ[_slot];
        DataHandle psi = _d.OtherHeadingDeg[_slot];
        DataHandle vx = _d.OtherVelX[_slot];
        DataHandle vz = _d.OtherVelZ[_slot];
        if (!x.HasValue || !y.HasValue || !z.HasValue ||
            !psi.HasValue || !vx.HasValue || !vz.HasValue)
            return false;

        _x = x.Value;
        _y = y.Value;
        _z = z.Value;
        _headingDeg = psi.Value;
        _vx = vx.Value;
        _vz = vz.Value;
        return true;
    }

    private void ApplyHolds()
    {
        _c.Hold(_d.OtherLocalX[_slot], _x);
        _c.Hold(_d.OtherLocalY[_slot], _y);
        _c.Hold(_d.OtherLocalZ[_slot], _z);
        _c.Hold(_d.OtherHeadingDeg[_slot], _headingDeg);
        _c.Hold(_d.OtherPitchDeg[_slot], HoldPitchDeg);
        _c.Hold(_d.OtherBankDeg[_slot], HoldBankDeg);
        _c.Hold(_d.OtherVelX[_slot], _vx);
        _c.Hold(_d.OtherVelY[_slot], HoldVy);
        _c.Hold(_d.OtherVelZ[_slot], _vz);
    }

    private void ReleaseHolds()
    {
        if (_slot < 0 || !_holding) return;
        _c.Release(_d.OtherLocalX[_slot]);
        _c.Release(_d.OtherLocalY[_slot]);
        _c.Release(_d.OtherLocalZ[_slot]);
        _c.Release(_d.OtherHeadingDeg[_slot]);
        _c.Release(_d.OtherPitchDeg[_slot]);
        _c.Release(_d.OtherBankDeg[_slot]);
        _c.Release(_d.OtherVelX[_slot]);
        _c.Release(_d.OtherVelY[_slot]);
        _c.Release(_d.OtherVelZ[_slot]);
        _holding = false;
    }

    // Llamar con _gate cogido.
    private void StopInternal(bool logAbort)
    {
        int idx = _xplmIndex;
        if (logAbort && _running)
            LogAction($"IA {idx}: vuelo recto y nivelado abortado");
        ReleaseHolds();
        _c.ReleaseAiControl();
        _d.FocusOtherPlane(-1);
        _pendingCapture = false;
        _xplmIndex = -1;
        _slot = -1;
        _running = false;
    }

    private void LogAction(string msg) => ActionLogged?.Invoke(msg);
}
