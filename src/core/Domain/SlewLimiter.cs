namespace AICopilotCore.Domain;

// Limita la velocidad de cambio de un valor de control. Puerto directo de
// src/SlewLimiter.h.
//
// Un PID puede pedir de golpe -1 o +1 en cuanto ve un error grande (al
// arrancar la secuencia, o justo al cambiar de fase y de objetivo). Sin
// esto, eso se traduce en el avion tirando el gas a fondo o el yugo a tope
// de un frame para otro, como si alguien manoseara los mandos a lo bestia
// en vez de moverlos como lo haria un piloto.
//
// Nota de por que sigue viviendo en el core y no en el connector: el
// connector tiene su propia rampa opcional (ratePerSec en HOLD), pero esta
// va pegada al PID que la necesita, y dejarlas juntas permite afinar las
// dos a la vez sin recompilar el plugin.
public sealed class SlewLimiter
{
    private float _maxRate;
    private float _current;

    // maxRatePerSecond: cuanto puede cambiar el valor de salida como maximo
    // en un segundo. En una escala -1..1, un valor de 1.0 significa "de un
    // extremo al otro en 2 segundos".
    public SlewLimiter(float maxRatePerSecond) => _maxRate = maxRatePerSecond;

    // Mutable a proposito: ControlTuning vive fuera de este objeto y puede
    // cambiar en caliente desde la pestana de Config, asi que quien use el
    // limitador reescribe esto (barato, un float) justo antes de cada
    // Update() en vez de reconstruir el SlewLimiter cada vez que cambia el
    // ajuste.
    public float MaxRate { get => _maxRate; set => _maxRate = value; }

    // Ritmo distinto para BAJAR, cuando hace falta que el limitador sea
    // asimetrico. NaN (el valor de fabrica) = el mismo que MaxRate, o sea el
    // comportamiento simetrico de siempre.
    //
    // Por que hace falta: el limite de ritmo del objetivo de cabeceo sale del
    // presupuesto de G, y ese presupuesto NO es simetrico -- subir el morro
    // carga el ala (hasta +6 g) y bajarlo la descarga (hasta +0.3 g antes de
    // que la tripulacion empiece a flotar), asi que el ritmo permitido en un
    // sentido es varias veces el del otro. Con un solo MaxRate habria que
    // quedarse con el mas restrictivo de los dos: la proteccion tardaria lo
    // mismo en SOLTAR que en morder, y la G se pasaria igual justo cuando lo
    // que hace falta es aliviar deprisa.
    private float _maxRateFalling = float.NaN;
    public float MaxRateFalling { get => _maxRateFalling; set => _maxRateFalling = value; }

    public float Update(float target, float dt)
    {
        if (dt <= 0f) return _current;
        float rate = target < _current && !float.IsNaN(_maxRateFalling)
            ? _maxRateFalling
            : _maxRate;
        float maxDelta = MathF.Max(rate, 0f) * dt;
        float delta = Math.Clamp(target - _current, -maxDelta, maxDelta);
        _current += delta;
        return _current;
    }

    public void Reset(float value = 0f) => _current = value;

    public float Value => _current;
}
