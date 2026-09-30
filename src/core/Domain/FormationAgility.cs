namespace AICopilotCore.Domain;

// Detector de "formacion agresiva": cuanto se esta moviendo el blanco (acelera,
// vira, sube/baja, serpentea) y cuanto nos cuesta seguirlo. Funcion PURA del
// tiempo: no vuela ni decide, solo mide. InterceptSequence lo usa para pasar de
// la formacion tranquila (ganancias suaves, precision fina) a una mas viva
// cuando el otro "no quiere ser escoltado", y el planner le pide su ley de
// estacion mas rapida (Level) y el feed-forward de la aceleracion del blanco.
//
// Por que hace falta (caja negra 23:0x, Airbus en modo rebelde): el blanco daba
// 0.5-1.2 m/s2 de aceleracion, 2.6 deg/s de viraje y +-2000 fpm, y con las
// ganancias de formacion tranquila el error longitudinal mediano era 94 m (p90
// 169) sobre un puesto a 200 m: solo 14 % del tiempo a menos de 60 m. Esas
// ganancias eran a proposito suaves (un puesto sobre un blanco recto oscilaba
// con mas); lo que sobraba era no distinguir un blanco recto de uno agitado.
//
// Level (0..1) sube rapido (tau ~0.7 s: hay que reaccionar YA a la primera
// maniobra) y baja despacio (tau ~8 s: un blanco que jinka no se calma en 2 s).
// Aggressive es la version discreta con histeresis, para el log.
public sealed class FormationAgility
{
    // Normalizadores: lo que cuenta como "maniobra plena" (1.0). Sacados de la
    // traza real: p90 de |accel| 0.55 m/s2, de |giro| 1.9 deg/s, de |dVy/dt| 1.1.
    private const double AccelFullMps2 = 0.8;
    private const double TurnFullDegS = 1.5;
    private const double VertAccelFullMps2 = 1.2;
    // Peso de las otras dos dimensiones sobre la mayor: un blanco que hace las
    // tres cosas a la vez es mas agitado que uno que solo gira.
    private const double OtherWeight = 0.35;
    // Error de seguimiento que tambien cuenta (sobre el puesto, en zona): si el
    // avion ya no aguanta el puesto, es formacion agresiva aunque el blanco sea
    // suave (p. ej. viento, o el puesto cambiado a mano).
    private const double StressAlongM = 80.0;
    private const double StressCrossM = 60.0;
    private const double StressZoneM = 500.0;
    private const double StressWeight = 0.30;

    private const double RiseTauSec = 0.7;
    private const double FallTauSec = 8.0;
    private const double DerivTauSec = 1.0;
    private const double OnLevel = 0.50;
    private const double OffLevel = 0.25;
    private const double MinHoldSec = 4.0;

    private double _prevGs = double.NaN, _prevVy = double.NaN;
    private double _accel, _vertAccel;
    private double _level;
    private bool _aggressive;
    private double _hold;
    private double _peak;

    // 0 = tranquilo, 1 = blanco muy agitado.
    public double Level => _level;
    public bool Aggressive => _aggressive;
    // Aceleracion del blanco a lo largo de su ruta (m/s2, filtrada; + = acelera).
    public double TgtAccelMps2 => _accel;
    public double TgtVertAccelMps2 => _vertAccel;
    // Cuanto ha llegado a subir desde el ultimo cambio de estado (para el log).
    public double Peak => _peak;

    public void Reset()
    {
        _prevGs = _prevVy = double.NaN;
        _accel = _vertAccel = _level = _peak = _hold = 0.0;
        _aggressive = false;
    }

    // Devuelve +1 si acaba de pasar a agresiva, -1 si acaba de calmarse, 0 si
    // no hay cambio.
    public int Update(double dt, double tgtGsMps, double tgtVyMps, double tgtTurnDegS,
                      double alongErrM, double crossErrM, double rangeM)
    {
        if (dt <= 1e-4) return 0;
        if (double.IsNaN(_prevGs))
        {
            _prevGs = tgtGsMps; _prevVy = tgtVyMps;
            return 0;
        }
        double a = Math.Clamp((tgtGsMps - _prevGs) / dt, -6.0, 6.0);
        double av = Math.Clamp((tgtVyMps - _prevVy) / dt, -8.0, 8.0);
        _prevGs = tgtGsMps; _prevVy = tgtVyMps;
        double k = 1.0 - Math.Exp(-dt / DerivTauSec);
        _accel += k * (a - _accel);
        _vertAccel += k * (av - _vertAccel);

        double aN = Math.Clamp(Math.Abs(_accel) / AccelFullMps2, 0.0, 1.0);
        double tN = Math.Clamp(Math.Abs(tgtTurnDegS) / TurnFullDegS, 0.0, 1.0);
        double vN = Math.Clamp(Math.Abs(_vertAccel) / VertAccelFullMps2, 0.0, 1.0);
        double big = Math.Max(aN, Math.Max(tN, vN));
        double raw = big + OtherWeight * (aN + tN + vN - big);
        if (rangeM < StressZoneM &&
            (Math.Abs(alongErrM) > StressAlongM || Math.Abs(crossErrM) > StressCrossM))
            raw += StressWeight;
        raw = Math.Clamp(raw, 0.0, 1.0);

        double tau = raw > _level ? RiseTauSec : FallTauSec;
        _level += (raw - _level) * (1.0 - Math.Exp(-dt / tau));
        _peak = Math.Max(_peak, _level);

        _hold += dt;
        if (!_aggressive && _level >= OnLevel)
        {
            _aggressive = true; _hold = 0.0; _peak = _level;
            return 1;
        }
        if (_aggressive && _level <= OffLevel && _hold >= MinHoldSec)
        {
            _aggressive = false; _hold = 0.0; _peak = 0.0;
            return -1;
        }
        return 0;
    }

    public static double Lerp(double calm, double aggressive, double level) =>
        calm + (aggressive - calm) * Math.Clamp(level, 0.0, 1.0);
}
