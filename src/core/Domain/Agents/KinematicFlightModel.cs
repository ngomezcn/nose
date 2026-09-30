using AICopilotCore.Domain;

namespace AICopilotCore.Domain.Agents;

// Integrador cinematico puro (sin ConnectorClient ni Datarefs) que usa
// KinematicAiBody en modo Simulating. Separado para poder probarlo sin sim
// (ver KinematicModelSelfCheck).
//
// Marcos y unidades (igual que AiStraightHold / Kinematics):
//   - X/Y/Z: local OGL en metros (X este, Y arriba, Z sur).
//   - Heading psi verdadero en grados; velocidad horizontal
//     vx = Vh*sin(psi), vz = -Vh*cos(psi); vy arriba.
//   - Palanca: cabeceo +1 = tirar (morro arriba), alabeo +1 = ala derecha
//     abajo, guinada +1 = derecha (mismo signo que los PID de las secuencias).
//
// Aire (vuelo simetrico, ecuaciones del enunciado):
//   n         = 1 + stick*GPerStickUnit(IAS), acotada a [GOperationalMin, UsableLoadFactor]
//   gamma_dot = g (n cos(phi) - cos(gamma)) / V
//   psi_dot   = g n sin(phi) / (V cos(gamma))
//   phi_dot   = stickRoll * RollRateAvailable, con retardo de 1er orden
//   V_dot     = AccelKtPerSec(gamma = 0) / sqrt(sigma) - g sin(gamma)   (en TAS)
//   pitch     = gamma + AoA, AoA -> LevelAoaDeg(IAS, W, n) con retardo
// El recorte de G NO aborta nada: se anota en AdaptationText y se sigue.
public sealed class KinematicFlightModel
{
    public const double G = 9.80665;
    public const double MpsToKt = 1.943844;
    public const double MToFt = 3.28084;
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    // --- constantes de rodadura / despegue (calibrables, ver informe) -------
    private const double RollingFriction = 0.02;      // fraccion de g
    private const double BrakeDecelG = 0.40;
    private const double NoseWheelBaseM = 7.0;
    private const double MaxSteerDeg = 12.0;
    private const double MaxYawRateDegPerSec = 25.0;
    private const double MaxGroundPitchDeg = 14.0;    // tail strike
    private const double LiftoffAoaOffsetDeg = 5.0;   // incidencia/curvatura: CL(alpha=0) > 0
    private const double FlapLiftFactor = 0.35;       // flap 1.0 = -35% de "peso efectivo"
    private const double GearRatePerSec = 0.30;
    private const double FlapRatePerSec = 0.15;
    private const double SpeedbrakeRatePerSec = 1.0;
    private const double EngineSpoolSec = 1.2;
    private const double AoaLagSec = 0.4;

    private readonly IFlightProfile _p;

    // Pose / velocidad
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Z { get; private set; }
    private double _psi, _gamma, _phi;     // rad
    private double _v;                     // aire: TAS m/s; suelo: velocidad sobre el suelo m/s
    private double _aoaDeg;
    private double _groundPitchDeg;
    private double _n = 1.0;
    private double _pDegPerSec, _qDegPerSec;
    private double _pitchDeg;
    private double _originY, _originElevM;

    public bool OnGround { get; private set; }
    public double GroundY { get; private set; }
    public double AglM => Y - GroundY;
    public double AltMslM => _originElevM + (Y - _originY);

    // Mandos y sistemas
    public float PitchStick { get; set; }
    public float RollStick { get; set; }
    public float YawStick { get; set; }
    public float ThrottleCmd { get; set; }
    public float Throttle { get; private set; }
    public bool ParkingBrake { get; set; }
    public float GearTarget { get; set; } = 1f;
    public float Gear { get; private set; } = 1f;
    public float FlapTarget { get; set; }
    public float Flap { get; private set; }
    public float SpeedbrakeTarget { get; set; }
    public float Speedbrake { get; private set; }

    // Texto de adaptacion vigente ("" = nada recortado) y aviso en el flanco.
    public string AdaptationText { get; private set; } = "";
    public Action<string>? Adapted { get; set; }
    private double _adaptCooldown;

    public KinematicFlightModel(IFlightProfile profile) => _p = profile;

    public IFlightProfile Profile => _p;

    // Salidas derivadas ------------------------------------------------------
    public float AltFt => (float)(AltMslM * MToFt);
    public float TasKt => (float)(_v * MpsToKt);
    public float IasKt => _p.IndicatedAirspeedKt(TasKt, AltFt);
    public float Mach => TasKt / _p.SpeedOfSoundKt(AltFt);
    public float PitchDeg => (float)_pitchDeg;
    public float BankDeg => (float)(_phi * Rad2Deg);
    public float HeadingDeg => (float)Norm360(_psi * Rad2Deg);
    public float AoaDeg => OnGround ? (float)_groundPitchDeg : (float)_aoaDeg;
    public float GNormal => OnGround ? 1f : (float)_n;
    public float PitchRateDegPerSec => (float)_qDegPerSec;
    public float RollRateDegPerSec => (float)_pDegPerSec;
    public double Vh => OnGround ? _v : _v * Math.Cos(_gamma);
    public double Vx => Vh * Math.Sin(_psi);
    public double Vz => -Vh * Math.Cos(_psi);
    public double Vy => OnGround ? 0.0 : _v * Math.Sin(_gamma);
    public float VsFpm => (float)(Vy * 196.8504);
    public float WeightLb => _p.ReferenceWeightLb;

    // Captura ---------------------------------------------------------------

    // groundY NaN => se decide: velocidad baja y sin V/S => en suelo (groundY = y);
    // si no, aire con suelo asumido assumedAglM por debajo.
    public void Capture(double x, double y, double z, double vx, double vy, double vz,
                        double headingDeg, double pitchDeg, double bankDeg,
                        double elevM, double gearRatio, double groundY, double assumedAglM)
    {
        X = x; Y = y; Z = z;
        _originY = y;
        _originElevM = double.IsNaN(elevM) ? 0.0 : elevM;

        double vh = Math.Sqrt(vx * vx + vz * vz);
        double speed = Math.Sqrt(vh * vh + vy * vy);
        _psi = (vh > 5.0 ? Math.Atan2(vx, -vz) * Rad2Deg : headingDeg) * Deg2Rad;

        bool ground = double.IsNaN(groundY)
            ? speed < 45.0 && Math.Abs(vy) < 1.5
            : y - groundY < 1.0 && Math.Abs(vy) < 1.5;
        GroundY = double.IsNaN(groundY) ? (ground ? y : y - assumedAglM) : groundY;

        OnGround = ground;
        if (ground)
        {
            Y = GroundY;
            _v = vh;
            _gamma = 0; _phi = 0;
            _groundPitchDeg = Math.Clamp(double.IsNaN(pitchDeg) ? 0 : pitchDeg, 0, MaxGroundPitchDeg);
            _pitchDeg = _groundPitchDeg;
            ParkingBrake = vh < 1.0;
            _n = 1.0;
        }
        else
        {
            _v = Math.Max(speed, 20.0);
            _gamma = vh > 1e-3 || Math.Abs(vy) > 1e-3 ? Math.Atan2(vy, vh) : 0.0;
            _phi = (double.IsNaN(bankDeg) ? 0 : bankDeg) * Deg2Rad;
            _pitchDeg = double.IsNaN(pitchDeg) ? _gamma * Rad2Deg : pitchDeg;
            _aoaDeg = Math.Clamp(_pitchDeg - _gamma * Rad2Deg, -5.0, 25.0);
            ParkingBrake = false;
            _n = 1.0;
        }

        _pDegPerSec = 0; _qDegPerSec = 0;
        PitchStick = RollStick = YawStick = 0f;
        Gear = GearTarget = float.IsNaN((float)gearRatio) ? 1f : (float)Math.Clamp(gearRatio, 0, 1);
        Flap = FlapTarget = 0f;
        Speedbrake = SpeedbrakeTarget = 0f;
        AdaptationText = "";
        // Acelerador inicial = el de crucero estable: no hay salto de velocidad
        // al tomar el control, y solo cambia cuando la secuencia lo pide.
        Throttle = ThrottleCmd = ground ? 0f : TrimThrottle();
    }

    // Empuje que mantiene la IAS en vuelo recto y nivelado (biseccion).
    public float TrimThrottle()
    {
        float alt = AltFt, tas = TasKt, ias = _p.IndicatedAirspeedKt(tas, alt);
        float w = _p.ReferenceWeightLb;
        if (_p.AccelKtPerSec(ias, alt, 1f, 0f, w, 0f, 1f) <= 0f) return 1f;
        if (_p.AccelKtPerSec(ias, alt, 0f, 0f, w, 0f, 1f) >= 0f) return 0f;
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 18; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (_p.AccelKtPerSec(ias, alt, mid, 0f, w, 0f, 1f) > 0f) hi = mid; else lo = mid;
        }
        return 0.5f * (lo + hi);
    }

    // Integracion ------------------------------------------------------------

    public void Step(float dt)
    {
        if (!(dt > 0f)) return;
        double left = Math.Min(dt, 0.5);
        _adaptCooldown = Math.Max(0, _adaptCooldown - left);
        double prevPitch = _pitchDeg;
        double total = left;
        while (left > 1e-9)
        {
            double h = Math.Min(left, 0.05);
            StepSystems(h);
            if (OnGround) StepGround(h); else StepAir(h);
            left -= h;
        }
        double qRaw = Wrap180(_pitchDeg - prevPitch) / total;
        double a = 1.0 - Math.Exp(-total / 0.15);
        _qDegPerSec += (qRaw - _qDegPerSec) * a;
    }

    private void StepSystems(double h)
    {
        Throttle += (float)((Math.Clamp(ThrottleCmd, 0f, 1f) - Throttle) * (1.0 - Math.Exp(-h / EngineSpoolSec)));
        Gear = Approach(Gear, Math.Clamp(GearTarget, 0f, 1f), (float)(GearRatePerSec * h));
        Flap = Approach(Flap, Math.Clamp(FlapTarget, 0f, 1f), (float)(FlapRatePerSec * h));
        Speedbrake = Approach(Speedbrake, Math.Clamp(SpeedbrakeTarget, 0f, 1f), (float)(SpeedbrakeRatePerSec * h));
    }

    private static float Approach(float cur, float target, float step) =>
        cur < target ? Math.Min(cur + step, target) : Math.Max(cur - step, target);

    // "Peso efectivo": los flaps bajan la velocidad a la que el ala sostiene el
    // avion, lo modelamos como menos peso para LevelAoaDeg.
    private float EffectiveWeightLb() =>
        _p.ReferenceWeightLb * (float)(1.0 - FlapLiftFactor * Flap);

    private void StepAir(double h)
    {
        float altFt = AltFt;
        float tasKt = TasKt;
        float ias = _p.IndicatedAirspeedKt(tasKt, altFt);
        float mach = tasKt / _p.SpeedOfSoundKt(altFt);
        float w = _p.ReferenceWeightLb;

        // Carga: palanca -> n deseada -> acotada por el ala (se anota, no se aborta).
        float stick = Math.Clamp(PitchStick, -1f, 1f);
        float nCmd = 1f + stick * _p.GPerStickUnit(ias);
        float nMax = _p.UsableLoadFactor(ias, w, mach);
        float nMin = F14Aero.GOperationalMin;
        float nUse = Math.Clamp(nCmd, nMin, nMax);
        if (Math.Abs(nUse - nCmd) > 0.05f)
            NoteAdaptation($"palanca {stick:+0.00;-0.00} pedia {nCmd:0.0} g; el ala da " +
                           $"{nUse:0.0} g a {ias:0} kt (maniobra suavizada, no abortada)");
        else
            AdaptationText = "";

        _n += (nUse - _n) * (1.0 - Math.Exp(-h / Math.Max(_p.PitchLagSec, 0.02f)));

        // Alabeo con retardo de 1er orden.
        double pTarget = Math.Clamp(RollStick, -1f, 1f) * _p.RollRateAvailableDegPerSec(tasKt);
        _pDegPerSec += (pTarget - _pDegPerSec) * (1.0 - Math.Exp(-h / Math.Max(_p.RollLagSec, 0.02f)));
        _phi = WrapPi(_phi + _pDegPerSec * Deg2Rad * h);

        // Trayectoria.
        double vc = Math.Max(_v, 25.0);
        double cosG = Math.Cos(_gamma), sinG = Math.Sin(_gamma);
        double gammaDot = G * (_n * Math.Cos(_phi) - cosG) / vc;
        double psiDot = G * _n * Math.Sin(_phi) / (vc * Math.Max(cosG, 0.1));
        _gamma = Math.Clamp(_gamma + gammaDot * h, -89.0 * Deg2Rad, 89.0 * Deg2Rad);
        _psi = WrapPi(_psi + psiDot * h);

        // Velocidad: empuje - resistencia (gamma=0 para no contar dos veces la
        // gravedad) en TAS, mas -g sin(gamma).
        float sigma = Math.Max(_p.DensityRatio(altFt), 0.05f);
        double accelIas = _p.AccelKtPerSec(ias, altFt, Throttle, 0f, w, Speedbrake, (float)_n);
        double dTasKt = accelIas / Math.Sqrt(sigma) - F14Aero.GravityKtPerSec * Math.Sin(_gamma);
        _v = Math.Max(_v + dTasKt / MpsToKt * h, 20.0);

        double vh = _v * Math.Cos(_gamma);
        X += vh * Math.Sin(_psi) * h;
        Z -= vh * Math.Cos(_psi) * h;
        Y += _v * Math.Sin(_gamma) * h;

        // Actitud.
        double aoaTarget = _p.LevelAoaDeg(ias, EffectiveWeightLb(), (float)Math.Max(_n, 0.0));
        _aoaDeg += (aoaTarget - _aoaDeg) * (1.0 - Math.Exp(-h / AoaLagSec));
        _pitchDeg = _gamma * Rad2Deg + _aoaDeg;

        if (Y <= GroundY)
        {
            double sink = -_v * Math.Sin(_gamma);
            if (sink > 6.0)
                NoteAdaptation($"toma con V/S {sink * 196.85:0} fpm; no se modela dano, se sigue rodando");
            Y = GroundY;
            OnGround = true;
            _v = Math.Max(vh, 0.0);
            _groundPitchDeg = Math.Clamp(_pitchDeg, 0, MaxGroundPitchDeg);
            _pitchDeg = _groundPitchDeg;
            _gamma = 0; _phi = 0; _pDegPerSec = 0; _n = 1.0;
        }
    }

    private void StepGround(double h)
    {
        float altFt = AltFt;
        float tasKt = TasKt;
        float ias = _p.IndicatedAirspeedKt(tasKt, altFt);
        float w = _p.ReferenceWeightLb;
        float sigma = Math.Max(_p.DensityRatio(altFt), 0.05f);

        // Longitudinal: empuje-resistencia - friccion - freno.
        double aKt = _p.AccelKtPerSec(ias, altFt, Throttle, 0f, w, Speedbrake, 1f) / Math.Sqrt(sigma);
        aKt -= RollingFriction * F14Aero.GravityKtPerSec;
        if (ParkingBrake) aKt -= BrakeDecelG * F14Aero.GravityKtPerSec;
        if (_v <= 0.0 && aKt < 0) aKt = 0;
        _v = Math.Max(_v + aKt / MpsToKt * h, 0.0);
        if (ParkingBrake && _v < 0.3) _v = 0.0;

        // Rueda de morro: w = V tan(delta) / L.
        double steer = Math.Clamp(YawStick, -1f, 1f) * MaxSteerDeg * Deg2Rad;
        double yawRate = Math.Clamp(_v * Math.Tan(steer) / NoseWheelBaseM,
                                    -MaxYawRateDegPerSec * Deg2Rad, MaxYawRateDegPerSec * Deg2Rad);
        _psi = WrapPi(_psi + yawRate * h);
        X += _v * Math.Sin(_psi) * h;
        Z -= _v * Math.Cos(_psi) * h;
        Y = GroundY;

        // Rotacion: rate ~ q; sin velocidad el morro cae.
        double rot = Math.Clamp(PitchStick, -1f, 1f) * Math.Min(8.0, 10.0 * Math.Pow(ias / 140.0, 2));
        if (ias < 100f) rot -= 6.0 * (1.0 - ias / 100.0);
        double newPitch = _groundPitchDeg + rot * h;
        if (newPitch > MaxGroundPitchDeg)
        {
            newPitch = MaxGroundPitchDeg;
            NoteAdaptation($"morro limitado a {MaxGroundPitchDeg:0} deg en tierra (cola en el suelo)");
        }
        _groundPitchDeg = Math.Max(newPitch, 0.0);
        _pitchDeg = _groundPitchDeg;
        _pDegPerSec = 0;

        // Despegue: el ala sostiene cuando pitch + incidencia >= AoA de 1 g.
        double required = _p.LevelAoaDeg(ias, EffectiveWeightLb(), 1f) - LiftoffAoaOffsetDeg;
        if (ias > 60f && _groundPitchDeg > 2.0 && _groundPitchDeg >= required)
        {
            OnGround = false;
            _gamma = 0; _phi = 0; _n = 1.0;
            _aoaDeg = _groundPitchDeg;
            // Sin el paso por nCmd=1 el primer frame aereo cabecearia solo.
            _v = Math.Max(_v, 20.0);
        }
    }

    private void NoteAdaptation(string text)
    {
        AdaptationText = text;
        if (_adaptCooldown > 0) return;
        _adaptCooldown = 2.0;
        Adapted?.Invoke(text);
    }

    private static double WrapPi(double a)
    {
        a %= 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI;
        else if (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
    private static double Wrap180(double d) => WrapPi(d * Deg2Rad) * Rad2Deg;
    private static double Norm360(double d) { d %= 360.0; return d < 0 ? d + 360.0 : d; }
}
