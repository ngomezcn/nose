namespace AICopilotCore.Domain;

// El planificador: coge la INTENCION de una maniobra (Maneuvers.cs) y el
// estado real del avion (FlightState) y saca los objetivos que se pueden
// volar AHORA (ManeuverPlan). Corre entero cada frame.
//
// Es la pieza que faltaba. Antes, el catalogo mandaba sus numeros fijos
// directamente al PID y lo unico que miraba el estado era un corte por G que
// llegaba tarde. El resultado era el que motivo este trabajo: pedir un picado
// a 300 kt movia el objetivo de morro a 12 deg/s, eso son -2.3 g, y la
// maniobra se cancelaba sola. Ahora la pregunta que se hace en cada frame no
// es "me he pasado?", es "cuanto puedo pedir sin pasarme?".
//
// Cuatro leyes, en este orden (el orden importa: cada una usa el resultado de
// la anterior):
//
//   1. Presupuesto de G: cuanta G hay disponible de verdad a esta velocidad,
//      peso y Mach, y cuanta se permite esta maniobra en concreto.
//   2. Alabeo: el que la maniobra pide, recortado a lo que el ala sostiene.
//   3. Trayectoria: la que se pide, recortada por la potencia disponible
//      (arriba), por la altura que hace falta para salir (abajo) y por la
//      velocidad que no se quiere superar.
//   4. Morro y motor: la trayectoria mas el angulo de ataque que hace falta a
//      esta velocidad y alabeo, y los gases (o los aerofrenos) que sostienen
//      todo eso.
//
// Lo que NO hace: mover mandos. Solo decide numeros. Eso deja el fichero
// probable en frio y deja que la proteccion continua (EnvelopeProtection)
// trabaje sobre el resultado sin pelearse con nadie.
public sealed class ManeuverPlanner
{
    private readonly ControlTuning _tuning;

    // Estado con memoria: filtros y anclas que necesitan continuidad entre
    // frames. Se reinician al empezar cada maniobra (Reset).
    private float _prevIasKt = float.NaN;
    private float _iasRateKtPerSec;
    private float _refAltFt = float.NaN;
    private float _entryIasKt = float.NaN;
    private float _aoaFilteredDeg = 3f;

    public ManeuverPlanner(ControlTuning tuning) => _tuning = tuning;

    // Altitud anclada al empezar (la que defienden los virajes y
    // Acelerar/Frenar) y velocidad de entrada. Publicas para la pantalla.
    public float ReferenceAltFt => _refAltFt;
    public float EntryIasKt => _entryIasKt;

    public void Reset(in FlightState s)
    {
        _prevIasKt = s.IasKt;
        _iasRateKtPerSec = 0f;
        _refAltFt = s.AltFt;
        _entryIasKt = s.IasKt;
        _aoaFilteredDeg = float.IsNaN(s.AoaDeg) ? 3f : s.AoaDeg;
    }

    // El ancla de altitud se mueve cuando la maniobra SI quiere cambiar de
    // altura (un descenso deja de defender la altitud de entrada en cuanto
    // empieza a bajar a proposito).
    public void SyncReferenceAltitude(float altFt) => _refAltFt = altFt;

    public void UpdateFilters(in FlightState s, float dt)
    {
        if (dt <= 0f) return;
        if (!float.IsNaN(_prevIasKt))
        {
            float raw = (s.IasKt - _prevIasKt) / dt;
            float a = Math.Clamp(dt / 1.0f, 0f, 1f);       // filtro de 1 s
            _iasRateKtPerSec += (raw - _iasRateKtPerSec) * a;
        }
        _prevIasKt = s.IasKt;

        if (!float.IsNaN(s.AoaDeg))
        {
            float a = Math.Clamp(dt / 0.5f, 0f, 1f);
            _aoaFilteredDeg += (s.AoaDeg - _aoaFilteredDeg) * a;
        }
    }

    public float IasTrendKtPerSec => _iasRateKtPerSec;

    // --- El plan de un frame -------------------------------------------------

    public ManeuverPlan Plan(ManeuverDefinition def, in FlightState s, float speedHoldTargetKt)
    {
        var notes = new AdaptationNotes();

        // 1. PRESUPUESTO DE G ------------------------------------------------
        // Se mira la velocidad que va a haber dentro de unos segundos, no la de
        // ahora: si esta cayendo, el alabeo tiene que empezar a aflojar antes
        // de quedarse sin sustentacion, no cuando ya se quedo.
        float iasAhead = MathF.Max(s.IasKt + _iasRateKtPerSec * _tuning.SpeedLookaheadSeconds, 60f);
        float nAvailable = F14Aero.UsableLoadFactor(iasAhead, s.WeightLb, s.MachNo);
        float gBudgetMax = MathF.Min(def.GBudgetMax, nAvailable);
        float gBudgetMin = def.GPushMin;

        // 2. ALABEO ------------------------------------------------------------
        float bankRequested = def.BankTargetDeg;
        float bankCmd = bankRequested;
        if (MathF.Abs(bankRequested) > 1f)
        {
            // El viraje no puede llevarse todo el presupuesto: hace falta
            // margen para cabecear, para las rafagas y para el lazo de altitud.
            // Con el alabeo muy alto casi no queda (a 75 deg, cos phi = 0.26),
            // y por eso un break tiene que aceptar perder algo de altura.
            float nForTurn = def.PitchBudgetFraction * gBudgetMax;
            float bankLimit = nForTurn <= 1.02f ? 0f : F14Aero.BankForLoadFactorDeg(nForTurn);
            // Y por debajo de cierta velocidad no se alabea nada: ahi lo unico
            // que consigue el alabeo es acercar la perdida.
            float slowScale = F14Aero.Ramp(s.IasKt, _tuning.MinBankIasKt, 0f,
                                           _tuning.MinBankIasKt + 30f, 1f);
            float magnitude = MathF.Min(MathF.Abs(bankRequested), bankLimit) * slowScale;
            bankCmd = MathF.Sign(bankRequested) * magnitude;
            notes.AddIfChanged("alabeo", MathF.Abs(bankRequested), magnitude, "deg",
                               s.IasKt < _tuning.MinBankIasKt + 30f
                                   ? $"a {s.IasKt:0} kt no hay sustentacion para mas"
                                   : $"el ala da {nAvailable:0.0} g", 2f);
        }

        // Ritmo de alabeo: el fisico disponible, el tope de estilo de la
        // maniobra, y -- en los virajes suaves -- el que mantiene coordinada la
        // entrada (que la carga del ala crezca al ritmo que pide la maniobra y
        // no de golpe). Es lo que evita la comba de morro al entrar.
        float rollRateAvail = F14Aero.RollRateAvailableDegPerSec(s.TasKt);
        float bankRate = MathF.Min(rollRateAvail, def.BankRateCapDegPerSec);
        float phiNow = MathF.Abs(s.BankDeg) * F14Aero.Deg2Rad;
        if (def.GOnsetGPerSec < 3f && phiNow > 0.05f)
        {
            float cosPhi = MathF.Max(MathF.Cos(phiNow), 0.1f);
            float coordinated = def.GOnsetGPerSec * cosPhi * cosPhi /
                                MathF.Max(MathF.Sin(phiNow), 0.02f) * F14Aero.Rad2Deg;
            bankRate = MathF.Min(bankRate, MathF.Max(coordinated, 2f));
        }

        // 3. TRAYECTORIA --------------------------------------------------------
        float gammaRequested = def.FlightPathTargetDeg;
        float gammaCmd = gammaRequested;
        float vsTargetFpm = float.NaN;

        if (def.HoldsEntryAltitude)
        {
            // Cascada altitud -> V/S -> trayectoria. Se ancla la altitud que
            // habia al pulsar el boton: asi un viraje largo no va derivando
            // hacia arriba o hacia abajo sin que nadie lo note.
            // Plan() no escribe estado a proposito (la UI lo llama tambien para
            // la vista previa de los botones, y una vista previa no debe mover
            // el ancla de la maniobra que este en marcha).
            float refAltFt = float.IsNaN(_refAltFt) ? s.AltFt : _refAltFt;
            float vsWanted = Math.Clamp(_tuning.AltHoldGainFpmPerFt * (refAltFt - s.AltFt),
                                        -1500f, 1500f);
            vsTargetFpm = vsWanted;
            gammaCmd = VsToGammaDeg(vsWanted, s.TasKt);
        }
        else if (!float.IsNaN(def.VsIntentFpm))
        {
            vsTargetFpm = def.VsIntentFpm;
            gammaCmd = VsToGammaDeg(def.VsIntentFpm, s.TasKt);
        }

        // 3a. Techo por potencia: no se sube mas de lo que el empuje sostiene.
        // Sin esto, "subir agresivo" a 40.000 ft mantiene 22 deg de morro que
        // el motor no puede sostener, la velocidad cae hasta la perdida y el
        // corte por G ni se entera (colgarse no da G).
        if (gammaCmd > 0.5f)
        {
            float gammaSustainable = SustainableClimbAngleDeg(s, def.ThrottleTarget);
            if (gammaSustainable < gammaCmd)
            {
                notes.AddIfChanged("subida", gammaCmd, gammaSustainable, "deg",
                                   s.AltFt > 25000f ? "el empuje no da mas a esta altura"
                                                    : "no hay potencia para mas", 1.5f);
                gammaCmd = gammaSustainable;
            }
        }

        // 3b. Suelo: cuanto se puede bajar sabiendo lo que cuesta salir. La
        // altura de recuperacion va con el cuadrado de la velocidad, asi que
        // esto es justo lo que un minimo de AGL constante no puede expresar.
        if (gammaCmd < -0.5f && !s.OnGround)
        {
            float floorFt = MathF.Max(def.FloorAglFt, _tuning.TerrainFloorAglFt);
            float available = MathF.Max(s.AglFt - floorFt, 0f);
            // Un margen de reaccion: lo que se cae mientras el mando llega.
            float reactionFt = MathF.Max(-s.VsFpm / 60f, 0f) * _tuning.RecoveryLagSeconds;
            available = MathF.Max(available - reactionFt, 0f);
            float nRecover = MathF.Min(def.RecoveryG, nAvailable);
            float maxDive = F14Aero.MaxDiveAngleForAltitudeDeg(s.TasKt, available, nRecover);
            if (maxDive < -gammaCmd)
            {
                notes.AddIfChanged("bajada", -gammaCmd, maxDive, "deg",
                                   $"a {s.AglFt:0} ft AGL no hay mas sitio para salir", 1.5f);
                gammaCmd = -maxDive;
            }
        }

        // 3c. Gobernador de velocidad: si el picado va a llevar a VNE, se
        // aplana antes de llegar, no cuando ya se paso.
        float vneCeiling = MathF.Min(def.MaxIasKt, 0.94f * s.VneKt);
        if (gammaCmd < -0.5f && iasAhead > vneCeiling - 40f)
        {
            float scale = F14Aero.Ramp(iasAhead, vneCeiling, 0f, vneCeiling - 40f, 1f);
            float limited = gammaCmd * scale;
            notes.AddIfChanged("bajada", -gammaCmd, -limited, "deg",
                               "se acerca a la velocidad maxima", 1.5f);
            gammaCmd = limited;
        }

        // 3d. Suelo de V/S: lo que esta maniobra se permite hundirse cuando no
        // hay G para sostener el viraje pedido.
        if (def.HoldsEntryAltitude)
        {
            float vsFloor = def.MinVsFpm;
            float vsNow = GammaToVsFpm(gammaCmd, s.TasKt);
            if (vsNow < vsFloor) gammaCmd = VsToGammaDeg(vsFloor, s.TasKt);
        }

        if (float.IsNaN(vsTargetFpm)) vsTargetFpm = GammaToVsFpm(gammaCmd, s.TasKt);

        // 4. MORRO --------------------------------------------------------------
        // El morro no es la trayectoria: hay que sumarle el angulo de ataque, y
        // ese cambia con la velocidad (va con 1/V^2), con el peso y con el
        // alabeo (un viraje carga el ala). Por eso un "pitch objetivo" fijo del
        // catalogo solo valia a una velocidad: a 170 kt el mismo numero era un
        // descenso de 2.200 fpm.
        float nForBank = F14Aero.LoadFactorForBank(bankCmd);
        // Solo del MODELO, nunca del AoA medido.
        //
        // Mezclar aqui el AoA real parecia buena idea (corrige el error del
        // modelo) y es un lazo positivo: si el avion tira, el AoA sube, y si el
        // AoA entra en el objetivo de morro, el objetivo sube, y el avion tira
        // mas. En vuelo se vio exactamente eso -- el morro subiendo solo hasta
        // quedarse colgado a 84 kt. El error del modelo lo corrige el lazo de
        // V/S, que es el que tiene autoridad limitada y pasa por la rampa.
        float aoaCmd = Math.Clamp(F14Aero.LevelAoaDeg(s.IasKt, s.WeightLb, nForBank),
                                  0f, F14Aero.MaxAutoAoaDeg);
        float pitchCmd = gammaCmd + aoaCmd;

        // 5. MOTOR Y AEROFRENOS --------------------------------------------------
        float speedTarget = float.NaN;
        if (def.IsSpeedHold) speedTarget = speedHoldTargetKt;
        else if (!float.IsNaN(def.SpeedTargetKt)) speedTarget = def.SpeedTargetKt;
        else if (def.HoldsEntryIas) speedTarget = float.IsNaN(_entryIasKt) ? s.IasKt : _entryIasKt;

        if (!float.IsNaN(speedTarget))
        {
            float floor = SpeedFloorKt(s, nForBank);
            float ceiling = SpeedCeilingKt(s, def);
            float clamped = Math.Clamp(speedTarget, floor, MathF.Max(ceiling, floor));
            notes.AddIfChanged("IAS objetivo", speedTarget, clamped, "kt",
                               clamped > speedTarget ? "por debajo no hay morro para sostener el vuelo"
                                                     : "es el techo util a esta altura", 3f);
            speedTarget = clamped;
        }

        // Feed-forward de gases: la palanca que el modelo dice que sostiene
        // esta velocidad con esta carga y esta trayectoria. El PID de motor
        // solo corrige alrededor, en vez de tener que integrarlo todo desde
        // cero cada vez que cambia algo.
        float throttleFf = ThrottleForSteadyFlight(s, float.IsNaN(speedTarget) ? s.IasKt : speedTarget,
                                                   nForBank, gammaCmd);
        float throttleCmd = def.ThrottleTarget;
        float speedbrake = 0f;

        if (float.IsNaN(speedTarget))
        {
            // Sin objetivo de velocidad: se manda el gas de la maniobra, pero
            // desvanecido si la velocidad se va a acercar a la VNE.
            float vneFade = F14Aero.Ramp(vneCeiling - iasAhead, 0f, 0f, 60f, 1f);
            float faded = throttleCmd * vneFade;
            if (faded < throttleCmd - 0.05f)
                notes.Add($"gas {throttleCmd:0.00}->{faded:0.00} (velocidad cerca del maximo)");
            throttleCmd = faded;
            if (vneFade <= 0.05f && iasAhead > vneCeiling) speedbrake = 1f;
        }
        else if (speedTarget < s.IasKt - 15f)
        {
            // Hay que frenar de verdad. A ralenti, en nivelado, el F-14 pierde
            // del orden de 1 kt/s: sin aerofrenos, pedir -50 kt es pedir casi
            // un minuto. Se sacan proporcionalmente a lo que falte.
            speedbrake = Math.Clamp((s.IasKt - speedTarget - 15f) / 40f, 0f, 1f);
        }
        // El interruptor de Config los desactiva del todo (ver AutoSpeedbrake:
        // hace falta apagarlos para poder medir la resistencia del avion limpio).
        if (!_tuning.AutoSpeedbrake) speedbrake = 0f;

        // 6. NIVEL DE ADAPTACION ------------------------------------------------
        AdaptationLevel level = notes.IsEmpty ? AdaptationLevel.AsRequested : AdaptationLevel.Adapted;

        return new ManeuverPlan(
            PitchTargetDeg: pitchCmd,
            BankTargetDeg: bankCmd,
            BankRateDegPerSec: bankRate,
            ThrottleTarget: Math.Clamp(throttleCmd, 0f, 1f),
            SpeedTargetKt: speedTarget,
            ThrottleFeedForward: throttleFf,
            SpeedbrakeTarget: speedbrake,
            FlightPathTargetDeg: gammaCmd,
            VsTargetFpm: vsTargetFpm,
            GBudgetMax: gBudgetMax,
            GBudgetMin: gBudgetMin,
            LoadFactorCmd: nForBank,
            Level: level,
            Adaptation: notes.ToString());
    }

    // --- Acrobacias -----------------------------------------------------------

    // Lo que se calcula UNA vez, al empezar una acrobacia: si cabe, con cuanta
    // G hay que tirar para que quepa, y que hay que hacer antes si no cabe.
    public readonly record struct AcroPlan(
        float LoadFactorCmd,
        float PredictedSeconds,
        float RequiredAltitudeFt,   // altura que se va a perder (Split-S) o ganar
        AdaptationLevel Level,
        string Explanation);

    public AcroPlan PlanAerobatic(ManeuverDefinition def, in FlightState s)
    {
        var notes = new AdaptationNotes();
        float nAvailable = F14Aero.UsableLoadFactor(s.IasKt, s.WeightLb, s.MachNo);
        float nCmd = MathF.Min(def.GBudgetMax, nAvailable);

        // Las acrobacias sin arco vertical (rollos) no piden altura: el plan es
        // trivial y el unico numero que importa es cuanto va a tardar el rollo,
        // que sale del ritmo de alabeo disponible.
        float arc = def.TotalArcDeg;
        if (arc < 1f)
        {
            float rollRate = F14Aero.RollRateAvailableDegPerSec(s.TasKt);
            float seconds = def.TotalRollDeg / MathF.Max(rollRate, 10f) + 1.5f;
            return new AcroPlan(nCmd, seconds, 0f, AdaptationLevel.AsRequested, "");
        }

        bool descending = def.Segments!.Any(seg => seg.Direction < 0);
        int direction = descending ? -1 : 1;
        float throttle = descending ? 0.05f : 1f;

        F14Aero.ArcPrediction pred = F14Aero.PredictArc(s.IasKt, s.AltFt, arc, direction,
                                                        throttle, nCmd, s.WeightLb);

        if (descending)
        {
            // Split-S: lo que decide es la altura que se pierde, y esa va con el
            // cuadrado de la velocidad de entrada -- 4.600 ft a 250 kt, 13.800 a
            // 500. Un minimo de AGL constante mentia en los dos extremos.
            float floor = MathF.Max(def.FloorAglFt, _tuning.TerrainFloorAglFt);
            float available = s.AglFt - floor;
            float needed = pred.MaxAltitudeLossFt;

            if (needed > available)
            {
                // Primero, cerrar el radio: mas G = menos altura perdida.
                float nTight = MathF.Min(nAvailable, F14Aero.GOperationalMax);
                F14Aero.ArcPrediction tight = F14Aero.PredictArc(s.IasKt, s.AltFt, arc, direction,
                                                                 throttle, nTight, s.WeightLb);
                if (tight.MaxAltitudeLossFt <= available)
                {
                    notes.Add($"tiron {nCmd:0.0}->{nTight:0.0} g para que la bajada quepa " +
                              $"en {available:0} ft");
                    return new AcroPlan(nTight, tight.Seconds, tight.MaxAltitudeLossFt,
                                        AdaptationLevel.Adapted, notes.ToString());
                }
                // Ni asi: no hay Split-S posible aqui. Es el unico caso del
                // catalogo en el que se dice que no, y se dice con el numero.
                return new AcroPlan(nTight, tight.Seconds, tight.MaxAltitudeLossFt,
                                    AdaptationLevel.Impossible,
                                    $"harian falta {tight.MaxAltitudeLossFt:0} ft y hay " +
                                    $"{MathF.Max(available, 0f):0} ft: frena o sube antes");
            }
            return new AcroPlan(nCmd, pred.Seconds, needed, AdaptationLevel.AsRequested, "");
        }

        // Looping / Immelmann: lo que decide es la energia. El riesgo no es el
        // suelo, es quedarse sin velocidad arriba y caerse de la maniobra.
        float minTopIas = MathF.Max(1.25f * s.StallSpeed1gKt, 160f);
        if (pred.MinIasKt < minTopIas)
        {
            // Mas G cierra el arco y llega arriba con mas velocidad.
            float nTight = MathF.Min(nAvailable, F14Aero.GOperationalMax);
            F14Aero.ArcPrediction tight = F14Aero.PredictArc(s.IasKt, s.AltFt, arc, direction,
                                                             throttle, nTight, s.WeightLb);
            if (tight.MinIasKt >= minTopIas)
            {
                notes.Add($"tiron {nCmd:0.0}->{nTight:0.0} g para no quedarse sin velocidad arriba");
                return new AcroPlan(nTight, tight.Seconds, tight.MaxAltitudeGainFt,
                                    AdaptationLevel.Adapted, notes.ToString());
            }
            return new AcroPlan(nTight, tight.Seconds, tight.MaxAltitudeGainFt,
                                AdaptationLevel.Impossible,
                                $"llegaria arriba a {tight.MinIasKt:0} kt (hacen falta " +
                                $"{minTopIas:0}): acelera antes");
        }

        // Y el techo: un looping que se sale por arriba del envolvente tampoco
        // sale.
        if (s.AltFt + pred.MaxAltitudeGainFt > F14Aero.ServiceCeilingFt)
        {
            float nTight = MathF.Min(nAvailable, F14Aero.GOperationalMax);
            notes.Add($"tiron {nCmd:0.0}->{nTight:0.0} g: el arco no cabe bajo el techo");
            F14Aero.ArcPrediction tight = F14Aero.PredictArc(s.IasKt, s.AltFt, arc, direction,
                                                             throttle, nTight, s.WeightLb);
            return new AcroPlan(nTight, tight.Seconds, tight.MaxAltitudeGainFt,
                                AdaptationLevel.Adapted, notes.ToString());
        }

        return new AcroPlan(nCmd, pred.Seconds, pred.MaxAltitudeGainFt,
                            AdaptationLevel.AsRequested, "");
    }

    // --- Vista previa para la UI ---------------------------------------------

    // Lo que el boton de la UI necesita saber a 10 Hz: si la maniobra sale tal
    // cual, sale adaptada (y como), o no sale. Sustituye al "boton en gris" de
    // antes, que solo sabia decir que no y ni siquiera ponia el motivo.
    public (AdaptationLevel Level, string Text) Preview(ManeuverDefinition def, in FlightState s)
    {
        if (!s.IsUsable) return (AdaptationLevel.Impossible, "sin telemetria");
        if (s.OnGround) return (AdaptationLevel.Impossible, "el avion esta en tierra");

        // Velocidad de entrada: vale para todas. Adaptar tiene un limite -- por
        // debajo de la velocidad minima de la maniobra no sale una version
        // suave de ella, sale otra cosa. En vuelo se vio el caso: un "Break
        // defensivo" aceptado a 84 kt, que ahi no es un break, es el avion
        // colgado del ala.
        if (s.IasKt < def.MinIasKt)
            return (AdaptationLevel.Impossible,
                    $"hacen falta al menos {def.MinIasKt:0} kt (va a {s.IasKt:0})");

        // Y con el avion cerca de la perdida no hay ninguna maniobra que tenga
        // sentido salvo volver a volar: nivelar.
        if (def.Kind != ManeuverKind.LevelWings &&
            s.IasKt < s.StallSpeed1gKt * F14Aero.StallMarginFactor)
            return (AdaptationLevel.Impossible,
                    $"a {s.IasKt:0} kt esta al borde de la perdida: primero nivelar");

        if (def.Mode == ManeuverMode.Aerobatic)
        {
            AcroPlan acro = PlanAerobatic(def, s);
            return (acro.Level, acro.Explanation);
        }

        ManeuverPlan plan = Plan(def, s, def.IsSpeedHold ? s.IasKt : float.NaN);

        // Un viraje cuyo alabeo hay que recortar a menos de la mitad ya no es
        // ese viraje: "break defensivo" con 30 de los 75 deg pedidos es un
        // viraje suave con otro nombre. Mejor decirlo que fingir.
        if (MathF.Abs(def.BankTargetDeg) > 20f &&
            MathF.Abs(plan.BankTargetDeg) < 0.5f * MathF.Abs(def.BankTargetDeg))
            return (AdaptationLevel.Impossible,
                    $"a {s.IasKt:0} kt solo saldrian {MathF.Abs(plan.BankTargetDeg):0} de los " +
                    $"{MathF.Abs(def.BankTargetDeg):0} deg de alabeo: seria otra maniobra");

        return (plan.Level, plan.Adaptation);
    }

    // --- Utilidades -----------------------------------------------------------

    public static float VsToGammaDeg(float vsFpm, float tasKt)
    {
        float ratio = Math.Clamp(vsFpm / MathF.Max(tasKt * 101.269f, 1f), -1f, 1f);
        return MathF.Asin(ratio) * F14Aero.Rad2Deg;
    }

    public static float GammaToVsFpm(float gammaDeg, float tasKt) =>
        MathF.Sin(Math.Clamp(gammaDeg, -90f, 90f) * F14Aero.Deg2Rad) * tasKt * 101.269f;

    // Angulo de subida que el empuje sostiene sin perder velocidad:
    // sin(gamma) = (T - D)/W, con un margen para que no se quede justo.
    private float SustainableClimbAngleDeg(in FlightState s, float throttle01)
    {
        float w = s.WeightLb > 1000f ? s.WeightLb : F14Aero.ReferenceWeightLb;
        float mil = F14Aero.ThrustAvailableLbf(s.AltFt, afterburner: false);
        float ab = F14Aero.ThrustAvailableLbf(s.AltFt, afterburner: true);
        float t01 = Math.Clamp(throttle01, 0f, 1f);
        float thrust = t01 <= 0.8f ? mil * t01 / 0.8f : mil + (ab - mil) * ((t01 - 0.8f) / 0.2f);
        float drag = F14Aero.DragLbf(s.IasKt, s.AltFt, 1f, w);
        float sin = _tuning.ClimbPowerReserve * (thrust - drag) / w;
        return MathF.Asin(Math.Clamp(sin, -1f, 1f)) * F14Aero.Rad2Deg;
    }

    // Velocidad por debajo de la cual esta maniobra no debe bajar: ni cerca de
    // perdida con la carga que lleva, ni por debajo de donde ya no habria morro
    // suficiente para sostener el vuelo nivelado.
    private float SpeedFloorKt(in FlightState s, float loadFactor)
    {
        float stallFloor = F14Aero.StallIasKt(s.WeightLb, loadFactor) * 1.25f;
        // IAS a la que el AoA de vuelo nivelado llega al maximo automatico.
        float aoaFloor = MathF.Sqrt(295.4f * s.WeightLb /
                                    (0.086f * F14Aero.MaxAutoAoaDeg * F14Aero.WingAreaFt2));
        return MathF.Max(MathF.Max(stallFloor, aoaFloor), _tuning.MinTargetIasKt);
    }

    private float SpeedCeilingKt(in FlightState s, ManeuverDefinition def)
    {
        float machCeiling = F14Aero.IasFromMach(F14Aero.MachOperational, s.AltFt);
        float levelCeiling = 0.97f * F14Aero.MaxLevelIasKt(s.AltFt, afterburner: true, s.WeightLb);
        return MathF.Min(MathF.Min(def.MaxIasKt, _tuning.MaxTargetIasKt),
                         MathF.Min(machCeiling, levelCeiling));
    }

    // Palanca que sostiene este estado (empuje = resistencia + peso por la
    // pendiente). Es solo la semilla del autothrottle: si el modelo se
    // equivoca, el integrador del PID lo corrige.
    private static float ThrottleForSteadyFlight(in FlightState s, float targetIasKt,
                                                 float loadFactor, float gammaDeg)
    {
        float w = s.WeightLb > 1000f ? s.WeightLb : F14Aero.ReferenceWeightLb;
        float drag = F14Aero.DragLbf(targetIasKt, s.AltFt, loadFactor, w);
        float needed = drag + w * MathF.Sin(Math.Clamp(gammaDeg, -90f, 90f) * F14Aero.Deg2Rad);
        float mil = F14Aero.ThrustAvailableLbf(s.AltFt, afterburner: false);
        float ab = F14Aero.ThrustAvailableLbf(s.AltFt, afterburner: true);
        if (needed <= 0f) return 0f;
        if (needed <= mil) return Math.Clamp(0.8f * needed / mil, 0f, 0.8f);
        return Math.Clamp(0.8f + 0.2f * (needed - mil) / MathF.Max(ab - mil, 1f), 0.8f, 1f);
    }
}
