namespace AICopilotCore.Domain;

// Que proteccion esta actuando ahora mismo. Es informativo: sirve para la UI,
// el log y el CSV. Ninguna de estas banderas corta una maniobra -- todas la
// suavizan.
[Flags]
public enum ProtectionFlags
{
    None = 0,
    GHigh = 1,
    GLow = 2,
    Stall = 4,
    Overspeed = 8,
    Ground = 16,
    Ceiling = 32,
}

// Lo unico que sigue matando una maniobra. Tiene que ser raro: si aparece en
// el log durante vuelo normal, es un fallo de las leyes de adaptacion, no una
// condicion de vuelo.
public enum HardAbortReason
{
    None,
    StructuralG,
    GroundProximity,
    Stall,
}

// El intervalo permitido de cada variable de mando en este frame.
//
// La idea que lo ordena todo: ninguna proteccion "toma el control". Todas
// escriben restricciones sobre las MISMAS variables y aqui se quedan las mas
// restrictivas. Asi el caso normal -- varias protecciones activas a la vez --
// se resuelve sin prioridades, sin maquina de estados y sin oscilar entre
// modos, que es como se comportaba el corte por G de antes (o la maniobra, o
// nivelar, nada en medio).
public readonly record struct CommandLimits(
    float PitchStickMin,
    float PitchStickMax,
    float RollStickMin,
    float RollStickMax,
    float ThrottleMin,
    float ThrottleMax,
    // Ritmos maximos (deg/s, siempre positivos) a los que se puede mover el
    // objetivo de cabeceo hacia arriba y hacia abajo. Son asimetricos porque
    // el presupuesto de G lo es: tirar carga el ala hasta 6 g, empujar la
    // descarga solo hasta 0.3 g antes de que la tripulacion flote.
    float PitchRateUpDegPerSec,
    float PitchRateDownDegPerSec,
    float BankMagnitudeMaxDeg,
    // Escala continua del gesto de cabeceo en acrobacias (1 = sin tocar).
    float PitchAuthority,
    ProtectionFlags Active,
    string Reason)
{
    public static CommandLimits Unrestricted => new(
        PitchStickMin: -1f, PitchStickMax: 1f,
        RollStickMin: -1f, RollStickMax: 1f,
        ThrottleMin: 0f, ThrottleMax: 1f,
        PitchRateUpDegPerSec: 30f, PitchRateDownDegPerSec: 30f,
        BankMagnitudeMaxDeg: 85f,
        PitchAuthority: 1f,
        Active: ProtectionFlags.None, Reason: "");
}

// La proteccion continua de envolvente: sustituye al corte por G.
//
// Como era antes: se leia la G real y, si salia de +6.5/-2.5, se marcaba la
// maniobra como abortada y se pasaba a recuperar nivelado. Tres problemas.
// (1) Es un DETECTOR, no un limitador: reacciona a una G que ya se produjo por
// un mando que se dio hace medio segundo, asi que llega tarde por definicion.
// (2) Es binario: no habia nada entre "la maniobra tal cual" y "cancelada".
// (3) La bandera no se limpiaba hasta la siguiente orden, asi que un pico de
// G en el primer segundo de un looping mataba los catorce restantes.
//
// Como es ahora: dos capas que trabajan juntas.
//   - Anticipacion: se predice la G que va a producir el ritmo de cabeceo que
//     se esta a punto de mandar (n = (cos gamma + V*gamma_punto/g)/cos phi) y
//     se limita el RITMO antes de mandarlo. La G no llega al limite porque el
//     objetivo no se mueve mas deprisa de lo que la G aguanta.
//   - Escalado continuo: conforme la G real se acerca al limite blando, la
//     autoridad del mando se va a cero de forma suave (con derivada nula en
//     los bordes, para que no se note un escalon). La maniobra sigue viva,
//     solo mas redonda.
//
// El abort duro sigue existiendo pero con umbrales estructurales de verdad y
// con tiempo de permanencia, para que un pico de un frame no cuente.
public sealed class EnvelopeProtection
{
    private readonly ControlTuning _tuning;

    private float _gFiltered = 1f;
    private float _prevG = float.NaN;
    private float _gRatePerSec;
    private float _kHigh = 1f;
    private float _kLow = 1f;
    private float _hardTimer;
    private float _clearTimer;
    private HardAbortReason _hardAbort;

    public EnvelopeProtection(ControlTuning tuning) => _tuning = tuning;

    public HardAbortReason HardAbort => _hardAbort;
    public string HardAbortText { get; private set; } = "";
    public ProtectionFlags ActiveFlags { get; private set; }
    public float FilteredG => _gFiltered;
    public float PredictedG { get; private set; } = 1f;

    public void Reset(float gNow = 1f)
    {
        _gFiltered = float.IsNaN(gNow) ? 1f : gNow;
        _prevG = float.NaN;
        _gRatePerSec = 0f;
        _kHigh = _kLow = 1f;
        _hardTimer = 0f;
        _clearTimer = 0f;
        _hardAbort = HardAbortReason.None;
        HardAbortText = "";
        ActiveFlags = ProtectionFlags.None;
    }

    // commandedPitchRate: el ritmo (deg/s) al que el plan quiere mover el
    // objetivo de cabeceo este frame. Es lo que permite anticipar: se evalua la
    // G que produciria ANTES de mandarlo.
    public CommandLimits Evaluate(in FlightState s, in ManeuverPlan plan, float dt)
    {
        // La G del simulador es ruidosa a nivel de frame; el limitador trabaja
        // con la filtrada y el abort duro exige ademas permanencia. Parte de
        // los cortes "inexplicables" de antes eran picos de un solo frame.
        float tau = MathF.Max(_tuning.GFilterSeconds, 1e-3f);
        float alpha = Math.Clamp(dt / tau, 0f, 1f);
        float gRaw = float.IsNaN(s.GNormal) ? 1f : s.GNormal;
        _gFiltered += (gRaw - _gFiltered) * alpha;

        // Hacia donde va la G, no donde esta: se adelanta con su propia
        // derivada filtrada.
        //
        // Aqui habia un fallo grave, visto en vuelo: se predecia la G a partir
        // de un "ritmo comandado" calculado como (objetivo - rampa)/dt. Con el
        // objetivo a unos grados de distancia y dt de 11 ms eso son cientos de
        // grados por segundo, que al saturar contra el tope de rampa y
        // dividirse por cos(alabeo) daba G predichas de 15-20 con el avion
        // volando a 1 g. La proteccion se creia al borde del limite
        // permanentemente y ponia la autoridad de cabeceo a cero, y entonces la
        // guardia de terreno la devolvia a uno: el mando saltaba entre -0.45 y
        // +0.09 de un frame a otro y el avion se ponia a cabecear solo.
        //
        // La anticipacion de verdad no estaba nunca aqui: esta en el limite de
        // RITMO del objetivo (PitchRateUp/Down), que sale de la misma ecuacion
        // pero despejada, y que si impide que la G suba. Esto de aqui solo
        // tiene que adelantar el retardo del filtro y del propio avion.
        if (!float.IsNaN(_prevG) && dt > 1e-4f)
        {
            float rawRate = (gRaw - _prevG) / dt;
            float aRate = Math.Clamp(dt / 0.25f, 0f, 1f);
            _gRatePerSec += (rawRate - _gRatePerSec) * aRate;
        }
        _prevG = gRaw;
        PredictedG = Math.Clamp(_gFiltered + _tuning.GLeadSeconds * _gRatePerSec,
                                F14Aero.GStructuralMin - 1f, F14Aero.GStructuralMax + 1.5f);

        // Para el lado alto se mira la peor de las dos (la medida, real pero
        // con retardo, y la adelantada); para el lado bajo, idem al reves.
        float nHigh = MathF.Max(_gFiltered, PredictedG);
        float nLow = MathF.Min(_gFiltered, PredictedG);

        // Cuanto margen de G se permite ANTES de empezar a quitar mando. Con el
        // suelo cerca sube hasta el limite duro: ahi no se ahorra margen, se
        // usa toda la G que el avion tenga para no chocar (ver mas abajo, donde
        // se calcula la urgencia).
        float groundUrgency = GroundUrgency(s);
        float softHigh = F14Aero.Ramp(groundUrgency, 0f, _tuning.GSoftHigh,
                                      1f, MathF.Max(_tuning.GHardHigh - 0.3f, _tuning.GSoftHigh));

        float bandHi = MathF.Max(_tuning.GGuardBandHigh, 0.1f);
        float bandLo = MathF.Max(_tuning.GGuardBandLow, 0.1f);
        float targetHigh = F14Aero.SmoothStep((softHigh - nHigh) / bandHi);
        float targetLow = F14Aero.SmoothStep((nLow - _tuning.GSoftLow) / bandLo);

        // Limitador de ritmo sobre la propia escala: que la proteccion entre y
        // salga tambien de forma progresiva.
        float maxStep = _tuning.LimiterMaxRatePerSec * dt;
        _kHigh += Math.Clamp(targetHigh - _kHigh, -maxStep, maxStep);
        _kLow += Math.Clamp(targetLow - _kLow, -maxStep, maxStep);

        var flags = ProtectionFlags.None;
        string reason = "";
        if (_kHigh < 0.98f) { flags |= ProtectionFlags.GHigh; reason = "G alta"; }
        if (_kLow < 0.98f) { flags |= ProtectionFlags.GLow; reason = "G baja"; }

        float pitchMax = _kHigh;
        float pitchMin = -_kLow;
        float throttleMin = 0f;
        float throttleMax = 1f;
        float bankMax = 85f;

        // --- Perdida / angulo de ataque -------------------------------------
        // Gana a todo lo demas salvo lo estructural, y la regla no es
        // intuitiva: tirar mas alla del AoA maximo NO sube el avion, aumenta la
        // resistencia y acelera la caida. Asi que aqui se quita autoridad de
        // tirar, se mete gas y se limita el alabeo -- nunca se tira mas.
        float aoaMargin = F14Aero.MaxAutoAoaDeg - s.AoaDeg;
        if (aoaMargin < 4f)
        {
            float kAlpha = Math.Clamp(aoaMargin / 4f, 0f, 1f);
            pitchMax = MathF.Min(pitchMax, kAlpha);
            flags |= ProtectionFlags.Stall;
            reason = "AoA alto";
        }
        float stallMargin = s.StallMarginKt;
        if (stallMargin < 25f)
        {
            throttleMin = MathF.Max(throttleMin, F14Aero.Ramp(stallMargin, 25f, 0f, 0f, 0.9f));
            // Menos alabeo: el viraje es lo que esta cargando el ala.
            bankMax = MathF.Min(bankMax, F14Aero.BankForLoadFactorDeg(
                MathF.Max(0.75f * s.UsableG, 1.02f)));
            flags |= ProtectionFlags.Stall;
            reason = "cerca de perdida";
        }

        // --- VNE / Mach -------------------------------------------------------
        float vneMargin = s.VneMarginKt;
        if (vneMargin < 60f)
        {
            throttleMax = MathF.Min(throttleMax, F14Aero.Ramp(vneMargin, 0f, 0f, 60f, 1f));
            flags |= ProtectionFlags.Overspeed;
            if (reason.Length == 0) reason = "cerca de VNE";
        }

        // --- Suelo -------------------------------------------------------------
        // La urgencia ya se calculo arriba, y alli hizo lo unico que tiene que
        // hacer: subir el margen de G permitido hasta el limite duro, para que
        // el avion pueda tirar todo lo que de el ala sin chocar.
        //
        // Lo que NO hace es forzar la autoridad de cabeceo a un valor: esa era
        // la otra mitad del problema visto en vuelo. Con la G predicha rota, el
        // limitador ponia pitchMax a 0 y esta guardia lo devolvia a 1 en el
        // mismo frame; el mando saltaba de un extremo al otro sesenta veces por
        // segundo. Ahora las dos escriben sobre la misma variable en el mismo
        // sentido (mas margen de G) y no pueden pelearse.
        if (groundUrgency > 0.02f)
        {
            // Prohibido seguir empujando hacia el suelo.
            pitchMin = MathF.Max(pitchMin, -0.2f + 0.3f * groundUrgency);
            throttleMin = MathF.Max(throttleMin, 0.6f * groundUrgency);
            // Nivelar alas primero gasta mucha menos altura que tirar
            // inclinado, asi que el suelo tambien recorta el alabeo.
            bankMax = MathF.Min(bankMax, F14Aero.Ramp(groundUrgency, 0.2f, 60f, 1f, 10f));
            flags |= ProtectionFlags.Ground;
            reason = "suelo cerca";
        }

        // --- Techo -------------------------------------------------------------
        if (s.AltFt > F14Aero.ServiceCeilingFt - 3000f && s.VsFpm > 0f)
        {
            float k = Math.Clamp((F14Aero.ServiceCeilingFt - s.AltFt) / 3000f, 0f, 1f);
            pitchMax = MathF.Min(pitchMax, MathF.Max(k, 0.15f));
            flags |= ProtectionFlags.Ceiling;
            if (reason.Length == 0) reason = "techo";
        }

        // Si los dos extremos se cruzan (las dos protecciones a cero), el mando
        // neutro es la respuesta fisicamente correcta.
        if (pitchMax < pitchMin) pitchMax = pitchMin = 0f;

        // --- Ritmos de objetivo permitidos por el presupuesto de G ------------
        float rateUp = F14Aero.MaxPitchRateDegPerSec(plan.GBudgetMax, s.TasKt, s.BankDeg,
                                                     s.FlightPathDeg);
        float rateDown = F14Aero.MaxPitchRateDegPerSec(plan.GBudgetMin, s.TasKt, s.BankDeg,
                                                       s.FlightPathDeg);

        UpdateHardAbort(s, dt);
        ActiveFlags = flags;

        return new CommandLimits(
            PitchStickMin: pitchMin,
            PitchStickMax: pitchMax,
            RollStickMin: -1f,
            RollStickMax: 1f,
            ThrottleMin: throttleMin,
            ThrottleMax: MathF.Max(throttleMax, throttleMin),
            PitchRateUpDegPerSec: Math.Clamp(rateUp, 0.4f, _tuning.ManeuverPitchRampDegPerSec),
            PitchRateDownDegPerSec: Math.Clamp(-rateDown, 0.4f, _tuning.ManeuverPitchRampDegPerSec),
            BankMagnitudeMaxDeg: bankMax,
            PitchAuthority: MathF.Min(_kHigh, 1f),
            Active: flags,
            Reason: reason);
    }

    // Cuanto aprieta el suelo, de 0 (no aprieta) a 1 (hay que usar todo lo que
    // de el avion). Se mira la AGL que va a haber dentro de unos segundos con
    // el ritmo de caida actual, no la de ahora: a 6.000 fpm, diez segundos son
    // mil pies.
    private float GroundUrgency(in FlightState s)
    {
        if (s.OnGround) return 0f;
        float floorFt = MathF.Max(_tuning.TerrainFloorAglFt, 100f);
        float sinkFtPerSec = MathF.Max(-s.VsFpm / 60f, 0f);
        if (sinkFtPerSec <= 0f) return 0f;
        float predictedAgl = s.AglFt - sinkFtPerSec * _tuning.GroundLookaheadSeconds;
        if (predictedAgl >= floorFt * 2f) return 0f;
        return 1f - Math.Clamp((predictedAgl - floorFt) / floorFt, 0f, 1f);
    }

    // El abort duro: lo que queda del backstop de antes, ahora con umbrales
    // estructurales de verdad, con permanencia (un pico de un frame no cuenta)
    // y con histeresis de salida en vez de una bandera pegada hasta la
    // siguiente orden.
    private void UpdateHardAbort(in FlightState s, float dt)
    {
        float g = s.GNormal;
        bool structural = g > _tuning.GHardHigh || g < _tuning.GHardLow;
        bool stalled = s.AoaDeg > F14Aero.StallAoaDeg && s.IasKt < s.StallSpeedNowKt * 1.05f;
        bool ground = !s.OnGround && s.AglFt < _tuning.TerrainFloorAglFt * 0.5f && s.VsFpm < -1000f;

        bool any = structural || stalled || ground;
        if (_hardAbort == HardAbortReason.None)
        {
            if (any)
            {
                _hardTimer += dt;
                if (_hardTimer >= _tuning.GHardDwellSeconds)
                {
                    _hardAbort = structural ? HardAbortReason.StructuralG
                               : ground ? HardAbortReason.GroundProximity
                               : HardAbortReason.Stall;
                    HardAbortText = structural ? $"G={g:0.0} fuera del limite estructural"
                                  : ground ? "proximidad de terreno"
                                  : "entrada en perdida";
                    _clearTimer = 0f;
                }
            }
            else _hardTimer = 0f;
        }
        else
        {
            // Para salir hace falta estar claramente dentro, no rozando el
            // umbral: si no, la proteccion parpadearia entrando y saliendo.
            bool clear = g < _tuning.GHardHigh - _tuning.GHardExitMargin &&
                         g > _tuning.GHardLow + _tuning.GHardExitMargin &&
                         !stalled && !ground;
            if (clear)
            {
                _clearTimer += dt;
                if (_clearTimer >= _tuning.GHardExitSeconds)
                {
                    _hardAbort = HardAbortReason.None;
                    HardAbortText = "";
                    _hardTimer = 0f;
                }
            }
            else _clearTimer = 0f;
        }
    }
}
