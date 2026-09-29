namespace AICopilotCore.Domain;

// Aerodinamica del F-14A/B reducida a funciones puras: lo minimo para poder
// ANTICIPAR si una maniobra cabe en el estado actual del avion (velocidad,
// altura, peso, actitud) y, si no cabe, cuanto hay que adaptarla para que si.
//
// Por que existe este fichero: hasta ahora el catalogo (Maneuvers.cs) era una
// tabla de objetivos fijos -- "picado = -40 deg, gas 0.9", "looping = palanca
// 0.85 durante 15 s" -- y el unico limite en marcha era un backstop que
// CORTABA la maniobra y caia a nivelar cuando la G ya se habia salido. Eso es
// un fallback: el avion acaba haciendo algo distinto de lo que se pidio, y
// ademas tarde. Con estas funciones el planificador (ManeuverPlanner.cs)
// calcula ANTES de mandar nada que actitud, que ritmo, que G y que gases dan
// la maniobra pedida sin salirse, y la maniobra se ejecuta adaptada.
//
// Alcance y honestidad de los numeros: son modelos de libro de texto (vuelo
// simetrico, avion limpio, atmosfera estandar) con constantes del F-14 de
// fuentes publicas. No son un modelo de rendimiento certificado ni clavan el
// del simulador; sirven para decidir "esto cabe / esto no cabe, y por cuanto".
// Todo lo estimado aqui se contrasta ademas contra la telemetria real cada
// frame (G, AoA, Mach son datarefs, no estimaciones), asi que un error del
// modelo se corrige solo en el lazo.
//
// Unidades, siempre en el nombre: Kt = nudos IAS salvo que diga Tas, Ft =
// pies, Deg = grados, Fpm = pies/min, Lbf = libras de fuerza, G = veces la
// gravedad.
public static class F14Aero
{
    // --- Constantes fisicas y del avion -------------------------------------

    public const float GravityFtPerSec2 = 32.174f;
    public const float KtToFtPerSec = 1.68781f;
    public const float MetersToFeet = 3.28084f;
    public const float Deg2Rad = MathF.PI / 180f;
    public const float Rad2Deg = 180f / MathF.PI;

    // Gravedad en "nudos por segundo": pasar de g a ritmo de giro del vector
    // velocidad se hace tantas veces que tenerlo convertido evita repetir el
    // factor por todo el fichero. 9.80665 m/s^2 = 19.0626 kt/s.
    public const float GravityKtPerSec = 19.0626f;

    // Ala y polar. AR 7.27 con el ala desplegada, e ~0.8.
    public const float WingAreaFt2 = 565f;
    public const float ClMaxClean = 1.6f;
    public const float InducedK = 0.0547f;      // 1/(pi*AR*e)
    public const float Cd0Subsonic = 0.022f;
    public const float Cd0WavePeak = 0.028f;    // incremento maximo, en M~1.05
    public const float Cd0WaveHigh = 0.016f;    // asintota por encima de M1.4
    public const float SpeedbrakeDeltaCd0 = 0.045f;

    // Empuje de los dos motores a nivel del mar. El F-14B/D monta F110; el A
    // monta TF30 y da bastante menos, pero para decidir "esto se alcanza o no"
    // el orden de magnitud es lo que cuenta.
    public const float MilThrustSlLbf = 33200f;
    public const float AbThrustSlLbf = 54000f;
    public const float IdleThrustFraction = 0.05f;

    // Peso de referencia (lb) cuando el simulador no da el real.
    public const float ReferenceWeightLb = 58000f;

    // Este factor existe para cuadrar el modelo con el avion del simulador sin
    // tocar la polar: el add-on que se vuele lleva pilones, rieles y un ajuste
    // propio, y la resistencia de libro se queda corta. Se calibra con una
    // aceleracion nivelada grabada con el DataLogger (250->450 kt a 5.000 ft) y
    // se compara con SecondsToChangeSpeed. Mientras no se calibre, el modelo
    // sirve para el SIGNO (alcanzable / no alcanzable), no para clavar tiempos.
    public static float DragCalibration = 1.0f;

    // --- Limites de envolvente (F-14A/B, fuentes publicas) -------------------

    // Limite estructural limpio. NO es el limite al que se vuela.
    public const float GStructuralMax = 7.5f;
    public const float GStructuralMin = -3.0f;

    // Limite de trabajo del planificador: todo lo que se planifica cabe aqui.
    public const float GOperationalMax = 5.5f;
    public const float GOperationalMin = -1.5f;

    // G minima de un pushover normal (bajar el morro, entrar en un picado):
    // flotar a 0 g ya es incomodo y en negativo el motor y la tripulacion lo
    // pasan mal. Un piloto baja el morro descargando, no empujando. Esta es la
    // constante que arregla el caso que motivo todo esto: con el ritmo de
    // objetivo fijo de antes (12 deg/s) un picado pedido a 300 kt daba
    // n = 1 - 12*300/1092 = -2.3 g y saltaba el corte.
    public const float GPushoverComfort = 0.3f;

    // Margen sobre la perdida con el que se planifica, y margen de buffet: por
    // debajo de eso no se sostiene una maniobra, se sostiene un intento.
    public const float StallMarginFactor = 1.15f;
    public const float BuffetMarginFactor = 0.85f;

    // AoA de perdida (deg). El F-14 avisa con buffet bastante antes y se
    // mantiene controlable a AoA alto gracias al barrido automatico del ala; se
    // trabaja contra el extremo bajo del rango publicado (20-24 deg).
    public const float StallAoaDeg = 20f;
    // AoA maximo que se permite el piloto automatico en vuelo normal: pasar de
    // aqui no sube el avion, solo aumenta la resistencia.
    public const float MaxAutoAoaDeg = 12f;

    public const float ServiceCeilingFt = 50000f;
    public const float VneSeaLevelKt = 750f;
    public const float MachLimit = 2.34f;
    // Mach de trabajo: por encima el avion es otro (ondas de choque, mando
    // distinto) y ninguna maniobra del catalogo tiene sentido ahi.
    public const float MachOperational = 0.95f;

    // Velocidad de alabeo: el F-14 es pesado y con ala de geometria variable.
    // A deflexion plena p es casi proporcional a la TAS (pb/2V constante) hasta
    // que satura en el tope mecanico.
    public const float MaxRollRateDegPerSec = 175f;
    public const float RollRatePerTasKt = 0.40f;

    // G por unidad de palanca de cabeceo a 350 KIAS. Es la unica constante
    // "calibrable" del lazo de G y se mide en vuelo (tiron de 0.30 sostenido);
    // un error del 30% solo hace la entrada menos limpia, porque el lazo cierra
    // contra la G real medida.
    public static float GPerStickAt350Kt = 4.5f;

    // --- Atmosfera estandar -------------------------------------------------

    public static float DensityRatio(float altFt)
    {
        if (altFt <= 0f) return 1f;
        if (altFt < 36089f)
            return MathF.Pow(1f - 6.87535e-6f * altFt, 4.2561f);
        const float SigmaTropopause = 0.29707f;
        return SigmaTropopause * MathF.Exp(-(altFt - 36089f) / 20806f);
    }

    public static float TrueAirspeedKt(float iasKt, float altFt) =>
        iasKt / MathF.Sqrt(MathF.Max(DensityRatio(altFt), 0.05f));

    public static float IndicatedAirspeedKt(float tasKt, float altFt) =>
        tasKt * MathF.Sqrt(MathF.Max(DensityRatio(altFt), 0.05f));

    public static float SpeedOfSoundKt(float altFt)
    {
        float tRatio = altFt < 36089f
            ? MathF.Max(1f - 6.87535e-6f * altFt, 0.1f)
            : 0.7519f;
        return 661.5f * MathF.Sqrt(tRatio);
    }

    public static float MachFromIas(float iasKt, float altFt) =>
        TrueAirspeedKt(iasKt, altFt) / SpeedOfSoundKt(altFt);

    public static float IasFromMach(float mach, float altFt) =>
        mach * SpeedOfSoundKt(altFt) * MathF.Sqrt(MathF.Max(DensityRatio(altFt), 0.05f));

    // Presion dinamica (lb/ft2) a partir de la IAS: en IAS la densidad ya va
    // dentro del instrumento, asi que no depende de la altura.
    public static float DynamicPressurePsf(float iasKt) => iasKt * iasKt / 295.4f;

    // VNE efectiva en KIAS a esta altura: abajo manda el limite estructural de
    // presion dinamica, arriba manda el Mach.
    public static float VneKt(float altFt) =>
        MathF.Min(VneSeaLevelKt, IasFromMach(MachLimit, altFt));

    // --- Perdida, G disponible y geometria de viraje -------------------------

    // Velocidad de perdida (KIAS) al peso y factor de carga dados. Sale de la
    // definicion de sustentacion, no de una tabla: asi el peso real del
    // simulador entra en la cuenta (un F-14 va de 40.000 lb vacio a 74.000
    // cargado, o sea de 114 a 155 kt de perdida: la diferencia entre que una
    // maniobra salga o entre en buffet).
    public static float StallIasKt(float weightLb, float loadFactor = 1f)
    {
        float w = weightLb > 1000f ? weightLb : ReferenceWeightLb;
        float n = MathF.Max(loadFactor, 0.05f);
        return MathF.Sqrt(295.4f * n * w / (ClMaxClean * WingAreaFt2));
    }

    // G maxima que el ala puede dar a esta IAS y peso, con el margen de buffet.
    public static float MaxLiftLoadFactor(float iasKt, float weightLb)
    {
        float vs = StallIasKt(weightLb);
        float ratio = iasKt / MathF.Max(vs, 40f);
        return MathF.Max(BuffetMarginFactor * ratio * ratio, 0.2f);
    }

    // G INSTANTANEA utilizable: lo que da el ala, acotado por el limite de
    // trabajo y degradado solo por Mach (CLmax cae en transonico).
    //
    // Ojo con la tentacion de penalizar esto por altitud: en IAS el limite de
    // sustentacion NO depende de la altura -- el instrumento ya lleva la
    // densidad dentro, y ese es justo el motivo de que todo este fichero
    // trabaje en IAS. Lo que si se degrada con la altura es el empuje, y eso
    // limita la G SOSTENIDA (SustainedLoadFactor), no la instantanea. Meterlo
    // aqui recortaria mas de 1 g que el avion tiene de verdad a 30.000 ft.
    public static float UsableLoadFactor(float iasKt, float weightLb, float mach)
    {
        float aero = MaxLiftLoadFactor(iasKt, weightLb);
        float machFactor = Ramp(mach, 0.92f, 1f, 1.15f, 0.80f);
        return Math.Clamp(aero * machFactor, 1.05f, GOperationalMax);
    }

    // G que se puede SOSTENER sin perder velocidad ni altura: aqui si manda el
    // empuje, y por eso si depende de la altura. Es la que decide si un viraje
    // cerrado se puede mantener un minuto o si va a ir sangrando velocidad.
    public static float SustainedLoadFactor(float iasKt, float altFt, float weightLb,
                                            float throttle01)
    {
        float w = weightLb > 1000f ? weightLb : ReferenceWeightLb;
        float q = DynamicPressurePsf(MathF.Max(iasKt, 80f));
        float mil = ThrustAvailableLbf(altFt, afterburner: false);
        float ab = ThrustAvailableLbf(altFt, afterburner: true);
        float t01 = Math.Clamp(throttle01, 0f, 1f);
        float thrust = t01 <= 0.8f ? mil * t01 / 0.8f : mil + (ab - mil) * ((t01 - 0.8f) / 0.2f);
        // T = D = (Cd0 + k*CL^2)*q*S, con CL = n*W/(q*S). Se despeja n.
        float parasite = Cd0(MachFromIas(iasKt, altFt)) * q * WingAreaFt2 * DragCalibration;
        float margin = thrust - parasite;
        if (margin <= 0f) return 1f;
        float nSquared = margin * q * WingAreaFt2 / (DragCalibration * InducedK * w * w);
        return Math.Clamp(MathF.Sqrt(MathF.Max(nSquared, 0f)), 1f, GOperationalMax);
    }

    // Velocidad de maxima maniobrabilidad ("corner"): donde el ala llega justo
    // al limite estructural. Por debajo manda la sustentacion, por encima la G.
    // Con el margen de buffet incluido sale mas alta que la cifra publicada de
    // 300-350 KCAS, que es la de CLmax puro.
    public static float CornerIasKt(float weightLb) =>
        StallIasKt(weightLb) * MathF.Sqrt(GStructuralMax);

    public static float LoadFactorForBank(float bankDeg)
    {
        float c = MathF.Cos(Math.Clamp(MathF.Abs(bankDeg), 0f, 85f) * Deg2Rad);
        return 1f / MathF.Max(c, 0.08f);
    }

    public static float BankForLoadFactorDeg(float loadFactor)
    {
        float n = MathF.Max(loadFactor, 1.001f);
        return MathF.Acos(1f / n) * Rad2Deg;
    }

    // Alabeo maximo con el que todavia se puede SOSTENER la altura a esta
    // velocidad: el viraje pide n = 1/cos(phi), y el ala solo da lo que da.
    public static float MaxLevelTurnBankDeg(float iasKt, float weightLb, float nLimit)
    {
        float n = MathF.Min(nLimit, MaxLiftLoadFactor(iasKt, weightLb));
        return n <= 1.02f ? 0f : BankForLoadFactorDeg(n);
    }

    public static float TurnRateDegPerSec(float tasKt, float bankDeg)
    {
        float n = LoadFactorForBank(bankDeg);
        float horizontal = MathF.Sqrt(MathF.Max(n * n - 1f, 0f));
        return GravityKtPerSec * horizontal / MathF.Max(tasKt, 30f) * Rad2Deg;
    }

    public static float TurnRadiusFt(float tasKt, float bankDeg)
    {
        float n = LoadFactorForBank(bankDeg);
        float horizontal = MathF.Sqrt(MathF.Max(n * n - 1f, 0.02f));
        float v = tasKt * KtToFtPerSec;
        return v * v / (GravityFtPerSec2 * horizontal);
    }

    // --- Geometria vertical --------------------------------------------------

    // Radio de la trayectoria vertical (ft) tirando a esta G. El termino que
    // curva la trayectoria es (n - cos gamma), no (n - 1): la gravedad solo se
    // resta entera en vuelo horizontal. Con el morro 60 deg abajo y n=2 la
    // diferencia es del 50%, y ese es justo el caso que dimensiona el suelo de
    // seguridad de un picado.
    public static float VerticalRadiusFt(float tasKt, float loadFactor, float flightPathDeg = 0f)
    {
        float v = tasKt * KtToFtPerSec;
        float cosGamma = MathF.Cos(Math.Clamp(flightPathDeg, -90f, 90f) * Deg2Rad);
        float pull = MathF.Max(loadFactor - cosGamma, 0.1f);
        return v * v / (GravityFtPerSec2 * pull);
    }

    // Altura (ft) que se pierde recuperando de un picado de gamma grados
    // tirando a loadFactor: h = R * (1 - cos gamma), con R evaluado a la mitad
    // del arco (que es donde el (n - cos gamma) medio tiene sentido).
    public static float RecoveryAltitudeLossFt(float tasKt, float diveAngleDeg, float loadFactor)
    {
        float gamma = Math.Clamp(MathF.Abs(diveAngleDeg), 0f, 90f);
        float r = VerticalRadiusFt(tasKt, loadFactor, gamma * 0.5f);
        return r * (1f - MathF.Cos(gamma * Deg2Rad));
    }

    // El inverso util: angulo de picado maximo del que se sale con la altura
    // disponible tirando a esta G. Devuelve grados positivos.
    public static float MaxDiveAngleForAltitudeDeg(float tasKt, float availableFt, float loadFactor)
    {
        // Busqueda directa: la relacion no se invierte limpio con el radio
        // dependiente del propio gamma, y 18 pasos de 5 deg son de sobra.
        float best = 0f;
        for (float gamma = 5f; gamma <= 90f; gamma += 5f)
        {
            if (RecoveryAltitudeLossFt(tasKt, gamma, loadFactor) > availableFt) break;
            best = gamma;
        }
        return best;
    }

    // --- Prediccion de G y ritmos permitidos (el nucleo de la anticipacion) --

    // Factor de carga que va a producir un ritmo de cabeceo dado:
    //
    //   n = (cos gamma + V*gamma_punto/g) / cos phi
    //
    // Es la ecuacion que explica el problema que motivo todo esto: el angulo
    // final de un picado es inofensivo, lo que genera G negativa es la PRISA
    // con la que se baja el morro.
    public static float PredictedLoadFactor(float pitchRateDegPerSec, float bankDeg,
                                            float flightPathDeg, float tasKt)
    {
        float cosGamma = MathF.Cos(Math.Clamp(flightPathDeg, -90f, 90f) * Deg2Rad);
        float cosPhi = MathF.Max(MathF.Cos(Math.Clamp(bankDeg, -179f, 179f) * Deg2Rad), 0.1f);
        float vertical = cosGamma + pitchRateDegPerSec * MathF.Max(tasKt, 40f) / (GravityKtPerSec * Rad2Deg);
        return vertical / cosPhi;
    }

    // La inversa, que es la que se usa como limite de ritmo del objetivo:
    // cuanto puede moverse el objetivo de cabeceo por segundo sin que la G se
    // salga del presupuesto. Devuelve el valor con signo del presupuesto: con
    // nLimit alto sale positivo (ritmo maximo tirando), con nLimit bajo sale
    // negativo (ritmo maximo empujando).
    public static float MaxPitchRateDegPerSec(float loadFactorLimit, float tasKt,
                                              float bankDeg, float flightPathDeg)
    {
        float cosGamma = MathF.Cos(Math.Clamp(flightPathDeg, -90f, 90f) * Deg2Rad);
        float cosPhi = MathF.Max(MathF.Cos(Math.Clamp(bankDeg, -179f, 179f) * Deg2Rad), 0.1f);
        return (loadFactorLimit * cosPhi - cosGamma) * GravityKtPerSec * Rad2Deg / MathF.Max(tasKt, 40f);
    }

    // Ritmo de alabeo disponible (deg/s) con la palanca a fondo.
    public static float RollRateAvailableDegPerSec(float tasKt) =>
        Math.Clamp(RollRatePerTasKt * tasKt, 20f, MaxRollRateDegPerSec);

    // Ganancia de cabeceo: cuanta G da una unidad de palanca a esta IAS. Escala
    // con la presion dinamica, o sea con IAS^2 -- por eso el mismo gesto de
    // palanca que a 250 kt da 3 g, a 500 kt da 9 g y hacia saltar el corte.
    public static float GPerStickUnit(float iasKt)
    {
        float ratio = MathF.Max(iasKt, 60f) / 350f;
        return MathF.Max(GPerStickAt350Kt * ratio * ratio, 0.2f);
    }

    // --- Resistencia, empuje y prestaciones ---------------------------------

    public static float Cd0(float mach, float speedbrake01 = 0f)
    {
        float wave;
        if (mach <= 0.85f) wave = 0f;
        else if (mach <= 1.05f)
        {
            float t = (mach - 0.85f) / 0.20f;
            wave = Cd0WavePeak * t * t;
        }
        else if (mach <= 1.40f) wave = Cd0WavePeak - 0.012f * (mach - 1.05f) / 0.35f;
        else wave = Cd0WaveHigh;
        return Cd0Subsonic + wave + SpeedbrakeDeltaCd0 * Math.Clamp(speedbrake01, 0f, 1f);
    }

    public static float DragLbf(float iasKt, float altFt, float loadFactor, float weightLb,
                                float speedbrake01 = 0f)
    {
        float q = DynamicPressurePsf(MathF.Max(iasKt, 60f));
        float w = weightLb > 1000f ? weightLb : ReferenceWeightLb;
        float cl = loadFactor * w / (q * WingAreaFt2);
        float cd = Cd0(MachFromIas(iasKt, altFt), speedbrake01) + InducedK * cl * cl;
        return DragCalibration * cd * q * WingAreaFt2;
    }

    public static float ThrustAvailableLbf(float altFt, bool afterburner)
    {
        float sigma = DensityRatio(altFt);
        float lapse = altFt < 36089f
            ? MathF.Pow(sigma, 0.7f)
            : 0.4275f * sigma / 0.29707f;
        return (afterburner ? AbThrustSlLbf : MilThrustSlLbf) * lapse;
    }

    public static float IdleThrustLbf(float altFt) =>
        IdleThrustFraction * ThrustAvailableLbf(altFt, afterburner: false);

    // Aceleracion (kt IAS por segundo) con este mando de motor y esta
    // trayectoria. Negativa = decelera. Es lo que permite decir "frenar 50 kt
    // te va a costar 40 s" antes de pedirlo, y decidir si hace falta ayuda
    // (aerofrenos o morro arriba) en vez de dejar al PID de motor saturado.
    public static float AccelKtPerSec(float iasKt, float altFt, float throttle01,
                                      float flightPathDeg, float weightLb,
                                      float speedbrake01 = 0f, float loadFactor = 1f)
    {
        float w = weightLb > 1000f ? weightLb : ReferenceWeightLb;
        float idle = IdleThrustLbf(altFt);
        float mil = ThrustAvailableLbf(altFt, afterburner: false);
        float ab = ThrustAvailableLbf(altFt, afterburner: true);
        // 0..0.8 interpola ralenti->militar; 0.8..1 abre postcombustion.
        float t01 = Math.Clamp(throttle01, 0f, 1f);
        float thrust = t01 <= 0.8f
            ? idle + (mil - idle) * (t01 / 0.8f)
            : mil + (ab - mil) * ((t01 - 0.8f) / 0.2f);
        float drag = DragLbf(iasKt, altFt, loadFactor, w, speedbrake01);
        float gamma = Math.Clamp(flightPathDeg, -90f, 90f) * Deg2Rad;
        float accelFtPerSec2 = GravityFtPerSec2 * ((thrust - drag) / w - MathF.Sin(gamma));
        // ft/s^2 de TAS -> kt/s de IAS.
        return accelFtPerSec2 / KtToFtPerSec * MathF.Sqrt(MathF.Max(DensityRatio(altFt), 0.05f));
    }

    // Segundos en pasar de una IAS a otra, integrando en tramos (la
    // aceleracion cambia mucho con la velocidad). Devuelve infinito si el
    // avion no puede llegar (empuje insuficiente o deceleracion nula).
    public static float SecondsToChangeSpeed(float fromKt, float toKt, float altFt,
                                             float throttle01, float flightPathDeg,
                                             float weightLb, float speedbrake01 = 0f)
    {
        const int Steps = 6;
        float total = 0f;
        float step = (toKt - fromKt) / Steps;
        if (MathF.Abs(step) < 0.01f) return 0f;
        float v = fromKt;
        for (int i = 0; i < Steps; i++)
        {
            float a = AccelKtPerSec(v + step * 0.5f, altFt, throttle01, flightPathDeg,
                                    weightLb, speedbrake01);
            if (MathF.Sign(a) != MathF.Sign(step) || MathF.Abs(a) < 0.02f)
                return float.PositiveInfinity;
            total += step / a;
            v += step;
        }
        return total;
    }

    // IAS maxima sostenible en vuelo nivelado a esta altura (biseccion sobre
    // empuje = resistencia). Es lo que evita dejar el autothrottle saturado
    // persiguiendo una velocidad que no existe en esa altitud.
    public static float MaxLevelIasKt(float altFt, bool afterburner, float weightLb)
    {
        float lo = 120f, hi = 900f;
        float thrust = ThrustAvailableLbf(altFt, afterburner);
        for (int i = 0; i < 14; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (DragLbf(mid, altFt, 1f, weightLb) < thrust) lo = mid; else hi = mid;
        }
        return MathF.Min(lo, VneKt(altFt));
    }

    // Pitch (deg) que hace falta para sostener el vuelo nivelado a esta
    // velocidad: a poca velocidad el ala necesita mucho AoA, y si el
    // autopiloto no tiene autoridad para darlo, "nivelar" no nivela.
    public static float LevelAoaDeg(float iasKt, float weightLb, float loadFactor = 1f)
    {
        float q = DynamicPressurePsf(MathF.Max(iasKt, 60f));
        float w = weightLb > 1000f ? weightLb : ReferenceWeightLb;
        float cl = loadFactor * w / (q * WingAreaFt2);
        const float ClAlphaPerDeg = 0.086f;
        return Math.Clamp(cl / ClAlphaPerDeg, 0f, 25f);
    }

    // --- Prediccion de un arco vertical (looping, Immelmann, Split-S) --------

    // Lo que hace falta saber ANTES de empezar una acrobacia: cuanto sube o
    // baja, cuanto tarda, con que velocidad pasa por el punto mas lento y con
    // cual sale. Se integra el arco en pasos de angulo constante con la
    // ecuacion del plano vertical:
    //
    //   dgamma/dt = s*g*(n - s*cos gamma)/V     s=+1 tirando hacia arriba,
    //                                           s=-1 tirando hacia abajo
    //   dV/dt     = g*((T-D)/W - sin gamma)
    //
    // Es barato (unas decenas de pasos, una vez por maniobra) y es lo que
    // convierte "Split-S a 6.000 ft AGL" en un numero comprobable en vez de una
    // constante escrita a mano.
    public readonly record struct ArcPrediction(
        float Seconds,
        float ExitIasKt,
        float AltitudeChangeFt,   // positivo = sube
        float MaxAltitudeGainFt,
        float MaxAltitudeLossFt,
        float MinIasKt,
        float MaxIasKt);

    public static ArcPrediction PredictArc(float entryIasKt, float altFt, float arcDeg,
                                           int direction, float throttle01, float loadFactorCmd,
                                           float weightLb, float speedbrake01 = 0f)
    {
        const int Steps = 90;
        float ias = MathF.Max(entryIasKt, 80f);
        float alt = altFt;
        float t = 0f;
        float gain = 0f, loss = 0f;
        float minIas = ias, maxIas = ias;
        float gamma = 0f;
        float dGamma = MathF.Abs(arcDeg) * Deg2Rad / Steps;

        for (int i = 0; i < Steps; i++)
        {
            float tas = TrueAirspeedKt(ias, alt) * KtToFtPerSec;
            float nAvail = MaxLiftLoadFactor(ias, weightLb);
            float n = Math.Clamp(MathF.Min(loadFactorCmd, nAvail), 0.2f, GStructuralMax);
            // Ritmo de giro del vector velocidad. Cerca de la vertical
            // invertida la gravedad ayuda, asi que el denominador no baja de un
            // minimo -- si no, un tramo lento daria un dt infinito.
            float turn = MathF.Max(n - direction * MathF.Cos(gamma), 0.15f);
            float rate = GravityFtPerSec2 * turn / MathF.Max(tas, 150f);   // rad/s
            float dt = dGamma / rate;

            float accel = AccelKtPerSec(ias, alt, throttle01, gamma * Rad2Deg * direction,
                                        weightLb, speedbrake01, n);
            ias = MathF.Max(ias + accel * dt, 80f);
            float dAlt = tas * MathF.Sin(gamma * direction) * dt;
            alt += dAlt;
            if (alt - altFt > gain) gain = alt - altFt;
            if (altFt - alt > loss) loss = altFt - alt;
            minIas = MathF.Min(minIas, ias);
            maxIas = MathF.Max(maxIas, ias);
            t += dt;
            gamma += dGamma;
        }

        return new ArcPrediction(t, ias, alt - altFt, gain, loss, minIas, maxIas);
    }

    // --- Utilidades ----------------------------------------------------------

    // Interpolacion lineal saturada: x<=x0 -> y0, x>=x1 -> y1. Se usa en todas
    // las bandas de guarda, para entrar y salir de una proteccion de forma
    // progresiva y no con un escalon (un escalon en mitad de una maniobra se
    // siente como un tiron, justo lo que este trabajo quita).
    public static float Ramp(float x, float x0, float y0, float x1, float y1)
    {
        if (MathF.Abs(x1 - x0) < 1e-6f) return x < x0 ? y0 : y1;
        float t = Math.Clamp((x - x0) / (x1 - x0), 0f, 1f);
        return y0 + t * (y1 - y0);
    }

    // Suavizado con derivada nula en los extremos: para escalar un mando sin
    // que se note el momento en que la proteccion empieza a morder.
    public static float SmoothStep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }
}
