namespace AICopilotCore.Domain;

// Todos los "limites humanos" del pipeline de control que antes vivian como
// constantes sueltas en AircraftControls / TakeoffSequence / ManeuverSequence,
// reunidos en un solo sitio para poder tocarlos en caliente desde la pestana
// de Config del shell (Ui/ShellWindow.xaml) sin recompilar.
//
// Que es un "limite humano" a efectos de esta clase: rampas/slew rates de
// los mandos, cuanto tarda una maniobra en perseguir su objetivo
// (agresividad), el backstop de G, y las tolerancias/tiempos con los que se
// considera "capturado" un objetivo (deadbands). A proposito NO incluye:
//
//   - Las ganancias de los PID (Kp/Ki/Kd) de TakeoffSequence/ManeuverSequence:
//     no son un limite "humano", son la sintonia interna del lazo de
//     control -- tocarlas a mano sin validar puede dejar el avion oscilando
//     o sin autoridad de mando, y no es lo que pide "que tan agresivo vuela"
//     (eso ya lo cubren las rampas de objetivo de aqui abajo).
//   - La coreografia del despegue (velocidades, morro, viraje, flaps) vive
//     en TakeoffStyles: tres perfiles, no un limite general de "que tan
//     humano" se comporta el piloto. El alabeo maximo, la rampa de pitch
//     y el tiempo de reaccion del despegue van dentro de cada estilo.
//   - Los watchdogs de diagnostico (ThrottleWatchdogSeconds y companeros) y
//     los rangos de plausibilidad del QNH: son avisos de log, no limites de
//     control.
//
// Cada propiedad valida/clampa en el setter a un rango que no rompe el
// pipeline (p. ej. una rampa a 0 congelaria el eje para siempre); los
// valores por defecto son exactamente los que estaban hardcodeados antes de
// esta clase, asi que crear un ControlTuning nuevo no cambia nada del
// comportamiento actual.
public sealed class ControlTuning
{
    // --- Mandos / rampas (AircraftControls) --------------------------------
    // Cuanto puede cambiar cada eje como maximo por segundo. En la escala
    // -1..1 de los ejes de vuelo, 1.0 = "de un extremo al otro en 2s"; en el
    // throttle (0..1), 1.0 = "de ralenti a maxima en 1s".
    private float _throttleSlewRate = 1.5f;
    public float ThrottleSlewRate
    {
        get => _throttleSlewRate;
        set => _throttleSlewRate = Math.Clamp(value, 0.1f, 10f);
    }

    private float _pitchSlewRate = 3.5f;
    public float PitchSlewRate
    {
        get => _pitchSlewRate;
        set => _pitchSlewRate = Math.Clamp(value, 0.1f, 15f);
    }

    private float _rollSlewRate = 3.5f;
    public float RollSlewRate
    {
        get => _rollSlewRate;
        set => _rollSlewRate = Math.Clamp(value, 0.1f, 15f);
    }

    private float _yawSlewRate = 3.5f;
    public float YawSlewRate
    {
        get => _yawSlewRate;
        set => _yawSlewRate = Math.Clamp(value, 0.1f, 15f);
    }

    // --- Agresividad de acciones/maniobras (ManeuverSequence) ---------------
    // Ritmo al que se le permite moverse al OBJETIVO de pitch/bank que
    // persigue el PID (AttitudeHold) o al gesto de palanca fijo
    // (RateCommand). Mas alto = maniobras mas "de piloto de combate", mas
    // bajo = mas suave/lento. El techo duro de agresividad sigue siendo el
    // backstop de G de aqui abajo, no esto.
    private float _maneuverPitchRampDegPerSec = 12f;
    public float ManeuverPitchRampDegPerSec
    {
        get => _maneuverPitchRampDegPerSec;
        set => _maneuverPitchRampDegPerSec = Math.Clamp(value, 0.5f, 60f);
    }

    private float _maneuverBankRampDegPerSec = 45f;
    public float ManeuverBankRampDegPerSec
    {
        get => _maneuverBankRampDegPerSec;
        set => _maneuverBankRampDegPerSec = Math.Clamp(value, 1f, 180f);
    }

    // Ritmo del gesto de palanca fijo en maniobras RateCommand (rollos,
    // loopings, Immelmann, Split-S...): no hay angulo objetivo en ese modo,
    // asi que esto es lo que hace que "meter alabeo a fondo" sea progresivo
    // y no un salto instantaneo.
    private float _maneuverStickRampPerSecond = 6f;
    public float ManeuverStickRampPerSecond
    {
        get => _maneuverStickRampPerSecond;
        set => _maneuverStickRampPerSecond = Math.Clamp(value, 0.1f, 20f);
    }

    // --- Limites de G: dos niveles, y esa es la diferencia -------------------
    //
    // Antes habia un solo par de numeros (GAbortHigh/GAbortLow, +6.5/-2.5) y
    // hacian de detector: en cuanto la G real se salia, la maniobra se cortaba
    // y el avion pasaba a recuperar nivelado. Eso llegaba tarde por definicion
    // (la G ya se habia producido) y era todo o nada.
    //
    // Ahora son dos capas:
    //
    //   GSoftHigh/GSoftLow: el limite de TRABAJO. La proteccion continua
    //   (EnvelopeProtection) empieza a quitar autoridad de mando de forma
    //   progresiva una banda antes de llegar aqui, y anticipa ademas la G que
    //   va a producir el mando que esta a punto de darse. La maniobra no se
    //   corta: se suaviza.
    //
    //   GHardHigh/GHardLow: el backstop estructural de verdad, con tiempo de
    //   permanencia para que un pico de un frame no cuente. Si esto salta
    //   durante vuelo normal, es un fallo de las leyes de adaptacion, no una
    //   condicion de vuelo.
    private float _gSoftHigh = 6f;
    public float GSoftHigh
    {
        get => _gSoftHigh;
        set => _gSoftHigh = Math.Clamp(value, 1f, 8f);
    }

    private float _gSoftLow = -1.5f;
    public float GSoftLow
    {
        get => _gSoftLow;
        set => _gSoftLow = Math.Clamp(value, -5f, -0.1f);
    }

    // Ancho de la banda en la que la autoridad de mando pasa de entera a cero.
    private float _gGuardBandHigh = 1.5f;
    public float GGuardBandHigh
    {
        get => _gGuardBandHigh;
        set => _gGuardBandHigh = Math.Clamp(value, 0.2f, 4f);
    }

    private float _gGuardBandLow = 1f;
    public float GGuardBandLow
    {
        get => _gGuardBandLow;
        set => _gGuardBandLow = Math.Clamp(value, 0.2f, 4f);
    }

    private float _gHardHigh = 7f;
    public float GHardHigh
    {
        get => _gHardHigh;
        set => _gHardHigh = Math.Clamp(value, 1f, 9f);
    }

    private float _gHardLow = -2.6f;
    public float GHardLow
    {
        get => _gHardLow;
        set => _gHardLow = Math.Clamp(value, -9f, -0.1f);
    }

    // Cuanto tiene que aguantarse la G fuera del limite duro para que cuente
    // como abort (y cuanto dentro, y con cuanto margen, para volver a salir).
    private float _gHardDwellSeconds = 0.3f;
    public float GHardDwellSeconds
    {
        get => _gHardDwellSeconds;
        set => _gHardDwellSeconds = Math.Clamp(value, 0f, 3f);
    }

    private float _gHardExitSeconds = 1f;
    public float GHardExitSeconds
    {
        get => _gHardExitSeconds;
        set => _gHardExitSeconds = Math.Clamp(value, 0.1f, 5f);
    }

    private float _gHardExitMargin = 0.4f;
    public float GHardExitMargin
    {
        get => _gHardExitMargin;
        set => _gHardExitMargin = Math.Clamp(value, 0.05f, 2f);
    }

    // Filtro de la G leida del simulador (es ruidosa frame a frame) y ritmo
    // maximo al que puede moverse la escala de la proteccion, para que entre y
    // salga sin escalones.
    private float _gFilterSeconds = 0.1f;
    public float GFilterSeconds
    {
        get => _gFilterSeconds;
        set => _gFilterSeconds = Math.Clamp(value, 0.01f, 1f);
    }

    // Cuanto se adelanta la G con su propia derivada antes de decidir si hay
    // que quitar mando. Compensa el retardo del filtro y el del propio avion
    // en responder; mas alto = la proteccion muerde antes, pero tambien hace
    // mas caso al ruido.
    private float _gLeadSeconds = 0.35f;
    public float GLeadSeconds
    {
        get => _gLeadSeconds;
        set => _gLeadSeconds = Math.Clamp(value, 0f, 1.5f);
    }

    private float _limiterMaxRatePerSec = 4f;
    public float LimiterMaxRatePerSec
    {
        get => _limiterMaxRatePerSec;
        set => _limiterMaxRatePerSec = Math.Clamp(value, 0.5f, 20f);
    }

    // --- Suelo, anticipacion y anclas del planificador -----------------------
    // Altura sobre el terreno por debajo de la cual ninguna maniobra sigue
    // bajando. Cada maniobra puede pedir mas (un picado pide 1.200 ft), nunca
    // menos.
    private float _terrainFloorAglFt = 600f;
    public float TerrainFloorAglFt
    {
        get => _terrainFloorAglFt;
        set => _terrainFloorAglFt = Math.Clamp(value, 100f, 5000f);
    }

    // Con cuanta antelacion se mira la trayectoria hacia el suelo, y cuanto se
    // tarda en que el mando de recuperacion haga efecto.
    private float _groundLookaheadSeconds = 10f;
    public float GroundLookaheadSeconds
    {
        get => _groundLookaheadSeconds;
        set => _groundLookaheadSeconds = Math.Clamp(value, 2f, 30f);
    }

    private float _recoveryLagSeconds = 1.2f;
    public float RecoveryLagSeconds
    {
        get => _recoveryLagSeconds;
        set => _recoveryLagSeconds = Math.Clamp(value, 0.2f, 5f);
    }

    // Con cuanta antelacion se mira la tendencia de la velocidad. Es lo que
    // hace que el alabeo empiece a aflojar ANTES de quedarse sin sustentacion.
    private float _speedLookaheadSeconds = 3f;
    public float SpeedLookaheadSeconds
    {
        get => _speedLookaheadSeconds;
        set => _speedLookaheadSeconds = Math.Clamp(value, 0f, 15f);
    }

    // Ganancia del ancla de altitud (fpm de correccion por cada pie de error):
    // 6 = 100 ft de desvio se corrigen pidiendo 600 fpm.
    private float _altHoldGainFpmPerFt = 6f;
    public float AltHoldGainFpmPerFt
    {
        get => _altHoldGainFpmPerFt;
        set => _altHoldGainFpmPerFt = Math.Clamp(value, 1f, 20f);
    }

    // Fraccion del exceso de potencia que se usa para subir; el resto queda de
    // reserva para no ir perdiendo velocidad en el ascenso.
    private float _climbPowerReserve = 0.9f;
    public float ClimbPowerReserve
    {
        get => _climbPowerReserve;
        set => _climbPowerReserve = Math.Clamp(value, 0.3f, 1f);
    }

    // Aerofrenos automaticos: el planificador los saca solo cuando hay que
    // perder velocidad y el ralenti no basta (frenar, la bajada de un Split-S,
    // un picado acercandose a la VNE). Se puede apagar por dos motivos: para
    // volar sin que el avion toque nada que el usuario no haya pedido, y para
    // CALIBRAR el modelo de resistencia -- una medida de deceleracion con los
    // aerofrenos entrando y saliendo no mide la resistencia del avion limpio,
    // que es lo que F14Aero.DragCalibration tiene que corregir.
    public bool AutoSpeedbrake { get; set; } = true;

    // Por debajo de esta IAS no se alabea: ahi el alabeo solo acerca la
    // perdida. Entre esta y +30 kt, el alabeo entra progresivamente.
    private float _minBankIasKt = 160f;
    public float MinBankIasKt
    {
        get => _minBankIasKt;
        set => _minBankIasKt = Math.Clamp(value, 100f, 250f);
    }

    // Cuanto puede corregir el pitch el lazo fino de V/S por encima del morro
    // que ya calcula el planificador. Sube de 8 a 15 deg porque 8 no llegaba:
    // sostener 75 deg de alabeo a 250 kt pide mas de 10 deg de correccion, asi
    // que en un break el lazo saturaba y el avion se hundia igual.
    private float _levelFlightMaxPitchAdjustDeg = 15f;
    public float LevelFlightMaxPitchAdjustDeg
    {
        get => _levelFlightMaxPitchAdjustDeg;
        set => _levelFlightMaxPitchAdjustDeg = Math.Clamp(value, 1f, 20f);
    }

    // --- Recuperacion de nivelado tras una maniobra (ManeuverSequence) ------
    // Se considera "nivelado recuperado" cuando bank/pitch llevan dentro de
    // estas tolerancias durante al menos RecoverMinSeconds seguidos.
    private float _recoverBankCaptureDeg = 4f;
    public float RecoverBankCaptureDeg
    {
        get => _recoverBankCaptureDeg;
        set => _recoverBankCaptureDeg = Math.Clamp(value, 0.5f, 15f);
    }

    private float _recoverPitchCaptureDeg = 3f;
    public float RecoverPitchCaptureDeg
    {
        get => _recoverPitchCaptureDeg;
        set => _recoverPitchCaptureDeg = Math.Clamp(value, 0.5f, 15f);
    }

    private float _recoverMinSeconds = 1.5f;
    public float RecoverMinSeconds
    {
        get => _recoverMinSeconds;
        set => _recoverMinSeconds = Math.Clamp(value, 0f, 10f);
    }

    // --- Envolvente de velocidad: Acelerar/Frenar (ManeuverSequence) -------
    // Cuanto sube/baja el IAS objetivo por cada pulsacion de Acelerar/Frenar.
    private float _speedStepKt = 15f;
    public float SpeedStepKt
    {
        get => _speedStepKt;
        set => _speedStepKt = Math.Clamp(value, 1f, 50f);
    }

    private float _minTargetIasKt = 130f;
    public float MinTargetIasKt
    {
        get => _minTargetIasKt;
        set => _minTargetIasKt = Math.Clamp(value, 50f, 300f);
    }

    private float _maxTargetIasKt = 650f;
    public float MaxTargetIasKt
    {
        get => _maxTargetIasKt;
        set => _maxTargetIasKt = Math.Clamp(value, 200f, 900f);
    }

    // Vuelve a poner todo en los valores de fabrica (los que estaban
    // hardcodeados antes de que existiera esta clase).
    // --- Interceptacion (InterceptSequence) ---------------------------------
    //
    // Cada puesto del catalogo (Domain/Intercept.cs) tiene SU PROPIA
    // configuracion de distancias: metros por detras del blanco, separacion
    // lateral y separacion vertical (en modulo; el lado / signo vertical lo
    // pone el puesto). Se leen cada frame, asi que un cambio se aplica en
    // caliente, incluso con la interceptacion en marcha.
    public sealed record StationOffsets(float AftM, float LateralM, float VerticalM);

    private static readonly StationOffsets[] StationDefaults =
    {
        /* TailHigh      */ new(200f, 0f, 40f),
        /* ParallelRight */ new(50f, 100f, 0f),
        /* ParallelLeft  */ new(50f, 100f, 0f),
        /* Above         */ new(100f, 0f, 80f),
        /* Below         */ new(100f, 0f, 80f),
    };

    private readonly StationOffsets[] _stationOffsets = (StationOffsets[])StationDefaults.Clone();

    public StationOffsets GetStationOffsets(InterceptStation id) =>
        Volatile.Read(ref _stationOffsets[(int)id]);

    public void SetStationOffsets(InterceptStation id, float aftM, float lateralM, float verticalM) =>
        Volatile.Write(ref _stationOffsets[(int)id], new StationOffsets(
            Math.Clamp(aftM, 0f, 2000f),
            Math.Clamp(lateralM, 0f, 1000f),
            Math.Clamp(verticalM, 0f, 500f)));

    // Zona de seguridad alrededor del blanco: cilindro de radio horizontal y
    // semialtura vertical. Dentro, el planner empuja hacia fuera y la
    // secuencia frena / abre aerofrenos.
    private float _safeHorizontalM = 25f;
    public float SafeHorizontalM
    {
        get => _safeHorizontalM;
        set => _safeHorizontalM = Math.Clamp(value, 5f, 300f);
    }

    private float _safeVerticalM = 15f;
    public float SafeVerticalM
    {
        get => _safeVerticalM;
        set => _safeVerticalM = Math.Clamp(value, 2f, 150f);
    }

    // Puesto resuelto a (atras, derecha, arriba) con signo, listo para la
    // geometria. Un puesto configurado DENTRO de la zona de seguridad se
    // aleja (hacia atras) hasta su borde + 30 %: no tiene sentido pedir
    // formar en un sitio que el propio guiado esta empujando a abandonar.
    public (float Aft, float Right, float Up) ResolveStation(InterceptStation id)
    {
        InterceptStationDef def = InterceptCatalog.Get(id);
        StationOffsets o = GetStationOffsets(id);
        float aft = o.AftM;
        float right = Math.Sign(def.RightFactor) * o.LateralM;
        float up = Math.Sign(def.UpFactor) * o.VerticalM;
        float minH = SafeHorizontalM * 1.3f;
        if (MathF.Abs(up) < SafeVerticalM * 1.3f && MathF.Sqrt(aft * aft + right * right) < minH)
            aft = MathF.Sqrt(MathF.Max(minH * minH - right * right, 0f));
        return (aft, right, up);
    }

    public void ResetToDefaults()
    {
        ThrottleSlewRate = 1.5f;
        PitchSlewRate = 3.5f;
        RollSlewRate = 3.5f;
        YawSlewRate = 3.5f;

        ManeuverPitchRampDegPerSec = 12f;
        ManeuverBankRampDegPerSec = 45f;
        ManeuverStickRampPerSecond = 6f;

        GSoftHigh = 6f;
        GSoftLow = -1.5f;
        GGuardBandHigh = 1.5f;
        GGuardBandLow = 1f;
        GHardHigh = 7f;
        GHardLow = -2.6f;
        GHardDwellSeconds = 0.3f;
        GHardExitSeconds = 1f;
        GHardExitMargin = 0.4f;
        GFilterSeconds = 0.1f;
        GLeadSeconds = 0.35f;
        LimiterMaxRatePerSec = 4f;
        LevelFlightMaxPitchAdjustDeg = 15f;

        TerrainFloorAglFt = 600f;
        GroundLookaheadSeconds = 10f;
        RecoveryLagSeconds = 1.2f;
        SpeedLookaheadSeconds = 3f;
        AltHoldGainFpmPerFt = 6f;
        ClimbPowerReserve = 0.9f;
        MinBankIasKt = 160f;
        AutoSpeedbrake = true;

        RecoverBankCaptureDeg = 4f;
        RecoverPitchCaptureDeg = 3f;
        RecoverMinSeconds = 1.5f;

        SpeedStepKt = 15f;
        MinTargetIasKt = 130f;
        MaxTargetIasKt = 650f;

        for (int i = 0; i < StationDefaults.Length; i++) _stationOffsets[i] = StationDefaults[i];
        SafeHorizontalM = 25f;
        SafeVerticalM = 15f;
    }
}
