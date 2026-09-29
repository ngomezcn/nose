namespace AICopilotCore.Domain;

// Controlador PID minimalista con anti-windup por clamp del termino
// integral. Puerto directo del src/PID.h del plugin viejo: mismo
// comportamiento, misma firma, solo que ahora corre en el core -- que es
// donde tiene que estar, porque decidir un valor de mando es logica, no
// transporte.
public sealed class Pid
{
    private readonly float _kp, _ki, _kd;
    private float _outMin, _outMax;

    private float _integral;
    private float _prevError;
    private bool _hasPrev;
    private float _lastOutput;

    public Pid(float kp, float ki, float kd, float outMin, float outMax)
    {
        _kp = kp; _ki = ki; _kd = kd;
        _outMin = outMin; _outMax = outMax;
    }

    // Mutables a proposito (a diferencia de Kp/Ki/Kd, que se quedan fijas):
    // algunos limites de salida SI son un "limite humano" tuneable desde la
    // pestana de Config (p. ej. cuanto puede ajustar el pitch la cascada de
    // nivelado de ManeuverSequence), a diferencia de las ganancias del PID
    // en si, que no se exponen -- ver ControlTuning.cs.
    public float OutMin { get => _outMin; set => _outMin = value; }
    public float OutMax { get => _outMax; set => _outMax = value; }

    public void Reset()
    {
        _integral = 0f;
        _prevError = 0f;
        _hasPrev = false;
    }

    // error = valor_deseado - valor_actual. Devuelve la salida ya saturada
    // entre outMin/outMax.
    public float Update(float error, float dt)
    {
        if (dt <= 0f) return _lastOutput;

        // Anti-windup en dos capas. El tope evita que Ki*integral se salga
        // del rango de salida el solo. Y si la salida ya esta saturada y el
        // error empuja mas hacia ese lado, esta muestra no se integra: si
        // no, un rato largo a fondo (la carrera de despegue, un tiron) deja
        // el integral clavado y el eje tarda mucho en soltar al llegar.
        if (_ki > 1e-6f)
        {
            _integral += error * dt;
            float limit = (_outMax - _outMin) / _ki;
            _integral = Math.Clamp(_integral, -limit, limit);
        }
        else
        {
            _integral = 0f;
        }

        float derivative = _hasPrev ? (error - _prevError) / dt : 0f;
        _prevError = error;
        _hasPrev = true;

        float raw = _kp * error + _ki * _integral + _kd * derivative;
        float output = Math.Clamp(raw, _outMin, _outMax);
        if (output != raw && Math.Sign(error) == Math.Sign(raw))
            _integral -= error * dt;

        _lastOutput = output;
        return output;
    }

    // Deja el integral de forma que, con error 0, la siguiente salida sea
    // aproximadamente `output`. Sirve para enganchar un autothrottle sin
    // tirar la potencia a cero: el termino P corrige alrededor de la
    // potencia que ya habia, en vez de empezar a integrar desde cero.
    public void SeedTrim(float output)
    {
        output = Math.Clamp(output, _outMin, _outMax);
        _integral = _ki > 1e-6f ? output / _ki : 0f;
        _prevError = 0f;
        _hasPrev = false;
        _lastOutput = output;
    }

    // Normaliza un angulo (p. ej. diferencia de rumbo) al rango [-180, 180).
    public static float NormalizeAngleDeg180(float deg)
    {
        while (deg > 180f) deg -= 360f;
        while (deg < -180f) deg += 360f;
        return deg;
    }

    public static float NormalizeAngleDeg360(float deg)
    {
        while (deg < 0f) deg += 360f;
        while (deg >= 360f) deg -= 360f;
        return deg;
    }
}
