using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

public enum InterceptPhase
{
    Idle,
    // El blanco sigue en tierra: se vuela nivelado y se espera a que despegue.
    WaitingTakeoff,
    // Lejos: rumbo de colision, gas a fondo, sin mirar la separacion fina.
    Pursuit,
    // Cerca: se administra el exceso de velocidad para no pasarse de largo.
    Closing,
    // Encima: formacion, velocidad igualada y correcciones pequenas.
    Station,
    // El blanco ha desaparecido del listado o no manda posicion.
    Lost,
}

// Interceptacion militar de otro avion de la partida: ir a buscarlo lo mas
// rapido posible y quedarse pegado a el en la posicion que se haya pedido
// (por defecto, detras y un poco por encima).
//
// Es la tercera secuencia del core, al lado de TakeoffSequence (guion de
// despegue) y ManeuverSequence (una accion del catalogo). Las tres escriben
// sobre los mismos overrides, asi que son mutuamente excluyentes: quien
// arranca una corta las otras (lo hace el shell).
//
// -- Las tres fases, y por que son tres ---------------------------------
//
// Un solo lazo "vete a ese punto" no sirve para todo el problema. A 20 km
// lo unico que importa es la geometria de colision y llegar antes de que se
// escape; a 200 m lo unico que importa es no adelantarlo. Son dos
// problemas distintos con dos soluciones distintas, y pegarlas con
// histeresis es mas honesto que una unica ganancia de compromiso que hace
// las dos cosas mal:
//
//   Pursuit  -- se apunta a donde ESTARA el blanco (lead), no a donde esta.
//               Persecucion pura = llegar siempre por detras y por fuera.
//               Gas al maximo que permita la VNE, alabeo hasta el limite de
//               G utilizable, sin flaps.
//   Closing  -- la velocidad de acercamiento se raciona con la distancia
//               que queda (v = sqrt(2*a*d)): es la misma cuenta de un
//               frenado, y es lo que evita el clasico "pasar silbando por
//               delante del blanco" y tener que dar la vuelta.
//   Station  -- ya no se persigue un punto, se COPIA una velocidad: la del
//               blanco, mas una correccion pequena proporcional a lo
//               retrasado o adelantado que se va. Las correcciones laterales
//               salen por rumbo y las verticales por V/S.
//
// -- El problema de los blancos lentos ----------------------------------
//
// Un F-14 no vuela a 110 kt. Si el blanco es una avioneta, "igualar su
// velocidad" no es una orden que se pueda cumplir limpia, y hay tres
// herramientas, en este orden:
//
//   1. Flaps: bajan la velocidad de perdida ~18% y con ella el suelo de
//      velocidad segura. Salen solos cuando la velocidad pedida se acerca a
//      ese suelo, y solo por debajo de su limite de extension.
//   2. Aerofrenos: para QUITAR velocidad deprisa sin bajar el morro, que es
//      justo lo que hace falta en la fase de acercamiento.
//   3. Serpenteo: si ni con flaps se puede volar tan despacio, se vuela al
//      minimo seguro y se alarga el camino zigzagueando alrededor de la
//      linea del blanco. cos(angulo) = velocidad del blanco / la nuestra:
//      con eso, el avance NETO iguala al suyo aunque el avion siga volando
//      40 kt mas rapido. Es lo que hace un interceptor real con un blanco
//      lento, y es la unica solucion que no acaba en perdida.
public sealed class InterceptSequence
{
    private readonly AircraftControls _controls;
    private readonly Datarefs _d;
    private readonly ControlTuning _tuning;

    // Mismo candado y misma razon que en las otras dos secuencias: Start()/
    // Abort() los llama la UI y Update() el hilo de lectura del pipe.
    private readonly object _gate = new();

    private readonly Pid _pitchPid = new(PitchKp, PitchKi, PitchKd, -1f, 1f);
    private readonly Pid _bankPid = new(BankKp, BankKi, BankKd, -1f, 1f);
    // Cascada V/S (fpm) -> correccion de morro (deg) sobre el morro que ya
    // sale del feedforward de trayectoria. Limite pequeno a proposito: es un
    // ajuste fino, no el que manda.
    private readonly Pid _vsPid = new(VsKp, VsKi, VsKd, -VsTrimLimitDeg, VsTrimLimitDeg);
    private readonly Pid _speedPid = new(SpeedKp, SpeedKi, SpeedKd, 0f, 1f);

    private readonly SlewLimiter _pitchTargetRamp = new(12f);
    private readonly SlewLimiter _bankTargetRamp = new(45f);
    private readonly SlewLimiter _flapRamp = new(FlapRatePerSec);
    private readonly SlewLimiter _speedbrakeRamp = new(SpeedbrakeRatePerSec);

    private int _targetIndex = -1;
    private string _targetLabel = "";
    private InterceptStation _station = InterceptStation.TailHigh;

    private InterceptPhase _phase = InterceptPhase.Idle;
    private float _statusLogTimer;
    private float _settledElapsed;
    private bool _announced;
    private float _gWarnTimer;
    private float _lowAglWarnTimer;

    // Serpenteo: +1 = desviado a la derecha de la linea del blanco.
    private int _weaveSign = 1;
    private float _weaveDeg;
    // Velocidad de avance pedida sobre la linea del blanco (kt de suelo). La
    // calcula la ley de velocidad y la consume el serpenteo.
    private double _desiredAlongGsKt;

    // Mandos "lentos" (flaps/aerofrenos): se mandan por Set, asi que se
    // guardan aqui para no repetir el mismo valor 60 veces por segundo.
    private float _flapCmd;
    private float _speedbrakeCmd;
    private float _flapSent = float.NaN;
    private float _speedbrakeSent = float.NaN;
    private float _slowWriteTimer;
    private bool _flapsAreOurs;

    public InterceptSequence(AircraftControls controls, Datarefs datarefs, ControlTuning tuning)
    {
        _controls = controls;
        _d = datarefs;
        _tuning = tuning;
    }

    public event Action<string>? ActionLogged;

    public bool IsRunning => _phase != InterceptPhase.Idle;
    public InterceptPhase Phase => _phase;
    public int TargetIndex => _targetIndex;
    public InterceptStation Station => _station;
    public string TargetLabel => _targetLabel;

    // --- Lo ultimo medido/pedido, para la telemetria y el panel -----------
    public float LastPitchTarget { get; private set; } = float.NaN;
    public float LastBankTarget { get; private set; } = float.NaN;
    public float LastIasTarget { get; private set; } = float.NaN;
    public float LastVsTarget { get; private set; } = float.NaN;
    public float LastThrottleCmd { get; private set; }
    public float LastFlapCmd => _flapCmd;
    public float LastSpeedbrakeCmd => _speedbrakeCmd;

    public double RangeM { get; private set; } = double.NaN;
    public double SeparationM { get; private set; } = double.NaN;
    public double ClosureKt { get; private set; } = double.NaN;
    public double AlongM { get; private set; } = double.NaN;
    public double CrossM { get; private set; } = double.NaN;
    public double VerticalM { get; private set; } = double.NaN;
    public double TargetSpeedKt { get; private set; } = double.NaN;
    public TargetAirState TargetState { get; private set; } = TargetAirState.Unknown;
    public bool Weaving => _weaveDeg > 0.5f;

    public static string PhaseName(InterceptPhase p) => p switch
    {
        InterceptPhase.WaitingTakeoff => "Esperando a que despegue",
        InterceptPhase.Pursuit => "Persecucion (cierre rapido)",
        InterceptPhase.Closing => "Acercamiento",
        InterceptPhase.Station => "En formacion",
        InterceptPhase.Lost => "Blanco perdido",
        _ => "En espera",
    };

    public string PhaseText
    {
        get
        {
            if (_phase == InterceptPhase.Idle) return "Sin interceptacion activa";
            string label = string.IsNullOrEmpty(_targetLabel) ? $"IA {_targetIndex}" : _targetLabel;
            string station = InterceptCatalog.Get(_station).Label;
            return $"Interceptar {label} · {PhaseName(_phase)} [{station}]";
        }
    }

    // --- Arranque y parada --------------------------------------------------

    // Devuelve false y el motivo si la interceptacion no se puede pedir ahora
    // mismo. Se niega en vez de intentarlo a medias: enganchar los overrides
    // de cabeceo y alabeo con el avion rodando por la pista es exactamente el
    // tipo de cosa que deja al usuario sin poder pilotar preguntandose por que.
    public bool Start(int xplmIndex, InterceptStation station, string label, out string refusal)
    {
        lock (_gate)
        {
            if (xplmIndex < 1 || xplmIndex > Datarefs.OtherPlaneSlots)
            {
                refusal = "ese avion no esta en los 19 slots de IA de X-Plane";
                return false;
            }
            if (_d.OnGround.Bool)
            {
                refusal = "tu avion esta en tierra: despega primero";
                return false;
            }
            if (_d.IasKt.Float < MinOwnIasKt)
            {
                refusal = $"hace falta al menos {MinOwnIasKt:0} kt para maniobrar " +
                          $"(vas a {_d.IasKt.Float:0} kt)";
                return false;
            }

            _targetIndex = xplmIndex;
            _targetLabel = label;
            _station = station;

            _pitchPid.Reset();
            _bankPid.Reset();
            _vsPid.Reset();
            _speedPid.SeedTrim(_controls.ThrottleReadback);
            _pitchTargetRamp.Reset(_d.PitchDeg.Float);
            _bankTargetRamp.Reset(_d.BankDeg.Float);
            _flapRamp.Reset(_d.FlapHandle.Float);
            _speedbrakeRamp.Reset(_d.SpeedbrakeHandle.Float);
            _flapCmd = _d.FlapHandle.Float;
            _speedbrakeCmd = _d.SpeedbrakeHandle.Float;
            _flapSent = _speedbrakeSent = float.NaN;
            _flapsAreOurs = false;
            _weaveSign = 1;
            _weaveDeg = 0f;
            _announced = false;
            _settledElapsed = 0f;
            _statusLogTimer = StatusLogSeconds;
            _gWarnTimer = _lowAglWarnTimer = 0f;

            // La posicion del blanco entra en un PID: con datos a 10 Hz el
            // avion persigue donde estaba, no donde esta.
            _d.FocusOtherPlane(xplmIndex - 1);

            _controls.EnablePitchOverride();
            _controls.EnableRollOverride();
            _controls.EnableThrottleOverride();

            SetPhase(InterceptPhase.Pursuit);
            InterceptStationDef def = InterceptCatalog.Get(station);
            FlightState own = FlightState.Capture(_d);
            LogAction(BlackBoxSnap.Join(
                $"Inicio: interceptar {label} — {def.Label}",
                def.Summary(_tuning.InterceptDistanceM, _tuning.InterceptLateralM, _tuning.InterceptVerticalM),
                InterceptConfigExtras(def),
                BlackBoxSnap.Of(own)));
            refusal = "";
            return true;
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            if (_phase == InterceptPhase.Idle) return;
            FlightState own = FlightState.Capture(_d);
            LogAction(BlackBoxSnap.Join(
                $"Fin: interceptacion abortada ({_targetLabel}) — control manual",
                InterceptLiveExtras(),
                _flapsAreOurs
                    ? $"flaps se quedan al {_flapCmd * 100f:0}% (los puse yo)"
                    : null,
                BlackBoxSnap.Of(own)));
            // El aerofreno si se recoge: es el unico de los dos que no
            // sostiene al avion, y dejarlo fuera solo cuesta velocidad.
            _controls.SetSpeedbrake(0f);
            _controls.ReleaseAllOverrides();
            _d.FocusOtherPlane(-1);
            ResetOutputs();
            _phase = InterceptPhase.Idle;
        }
    }

    // Se pierde el pipe: el SafetyGuard del connector ya habra soltado los
    // overrides por su cuenta, aqui solo hay que olvidarse del estado.
    public void OnConnectionLost()
    {
        lock (_gate)
        {
            if (_phase == InterceptPhase.Idle) return;
            _controls.ForgetOverrideState();
            // El blanco vuelve al ritmo de pantalla. Con el pipe caido esto
            // solo apunta el divisor nuevo, que es justo lo que hace falta:
            // el cliente lo replica al reconectar.
            _d.FocusOtherPlane(-1);
            ResetOutputs();
            _phase = InterceptPhase.Idle;
        }
    }

    private void ResetOutputs()
    {
        _targetIndex = -1;
        _targetLabel = "";
        LastPitchTarget = LastBankTarget = LastIasTarget = LastVsTarget = float.NaN;
        RangeM = SeparationM = ClosureKt = AlongM = CrossM = VerticalM = double.NaN;
        TargetSpeedKt = double.NaN;
        TargetState = TargetAirState.Unknown;
        _weaveDeg = 0f;
        _flapCmd = _speedbrakeCmd = 0f;
        _flapsAreOurs = false;
    }

    // --- El lazo ------------------------------------------------------------

    public void Update(float dt)
    {
        lock (_gate) { UpdateLocked(dt); }
    }

    private void UpdateLocked(float dt)
    {
        if (_phase == InterceptPhase.Idle || dt <= 0f) return;

        _statusLogTimer += dt;
        _gWarnTimer += dt;
        _lowAglWarnTimer += dt;
        _slowWriteTimer += dt;

        FlightState st = FlightState.Capture(_d);
        // Un frame sin telemetria util no se "arregla" mandando un mando
        // calculado con NaN: se deja en pie lo del frame anterior, que los
        // holds del connector siguen manteniendo.
        if (!st.IsUsable) return;
        if (!_d.LocalX.HasValue || !_d.LocalY.HasValue || !_d.LocalZ.HasValue) return;

        double ownX = _d.LocalX.Value, ownY = _d.LocalY.Value, ownZ = _d.LocalZ.Value;
        double ownVx = _d.LocalVx.Value, ownVz = _d.LocalVz.Value;
        double ownVy = st.VsFpm / TargetSnapshot.MpsToFpm;
        double ownGsMps = Math.Sqrt(ownVx * ownVx + ownVz * ownVz);
        double ownGsKt = ownGsMps * TargetSnapshot.MpsToKnots;
        float ownTrack = ownGsMps < 5.0
            ? st.HeadingDeg
            : Pid.NormalizeAngleDeg360((float)(Math.Atan2(ownVx, -ownVz) * F14Aero.Rad2Deg));

        TargetSnapshot t = TargetSnapshot.Capture(_d, _targetIndex);
        if (!t.Valid)
        {
            if (_phase != InterceptPhase.Lost)
            {
                SetPhase(InterceptPhase.Lost);
                LogAction($"Interceptacion: {_targetLabel} ha dejado de mandar posicion " +
                          "(se ha ido de la partida?). Manteniendo vuelo nivelado.");
            }
            FlyLevelHold(st, dt);
            return;
        }
        if (_phase == InterceptPhase.Lost)
        {
            LogAction($"Interceptacion: {_targetLabel} vuelve a estar en pantalla.");
            SetPhase(InterceptPhase.Pursuit);
        }

        // Elevacion del terreno estimada con NUESTRA propia posicion: la Y
        // local menos nuestra AGL. Solo vale si el blanco esta cerca -- a
        // 100 km puede haber una sierra de por medio -- asi que de lejos el
        // clasificador se queda con la velocidad, que no depende del relieve.
        double terrainY = ownY - _d.AglMeters.Value;
        double flatM = Math.Sqrt((t.X - ownX) * (t.X - ownX) + (t.Z - ownZ) * (t.Z - ownZ));
        TargetState = t.Classify(terrainY, flatM < TerrainReferenceRangeM);
        TargetSpeedKt = t.GroundSpeedKt;

        // --- En tierra: no hay interceptacion que hacer todavia ------------
        if (TargetState == TargetAirState.OnGround)
        {
            if (_phase != InterceptPhase.WaitingTakeoff)
            {
                SetPhase(InterceptPhase.WaitingTakeoff);
                LogAction($"Interceptacion: {_targetLabel} esta en tierra " +
                          $"({t.GroundSpeedKt:0} kt de suelo). Espero a que despegue.");
            }
            else if (_statusLogTimer >= StatusLogSeconds)
            {
                _statusLogTimer = 0f;
                LogAction($"Interceptacion: {_targetLabel} sigue en tierra " +
                          $"({t.GroundSpeedKt:0} kt, a {flatM * TargetSnapshot.MetersToNm:0.0} NM).");
            }
            MeasureOnly(t, ownX, ownY, ownZ, ownVx, ownVy, ownVz);
            FlyLevelHold(st, dt, HoldingBankDeg);
            return;
        }
        if (_phase == InterceptPhase.WaitingTakeoff)
        {
            if (TargetState != TargetAirState.Airborne)
            {
                // "No lo se" no es "ha despegado": se sigue esperando.
                MeasureOnly(t, ownX, ownY, ownZ, ownVx, ownVy, ownVz);
                FlyLevelHold(st, dt, HoldingBankDeg);
                return;
            }
            LogAction($"Interceptacion: {_targetLabel} esta en el aire " +
                      $"({t.GroundSpeedKt:0} kt). Voy a por el.");
            SetPhase(InterceptPhase.Pursuit);
        }

        // --- Geometria ------------------------------------------------------
        InterceptStationDef def = InterceptCatalog.Get(_station);
        (float aft, float right, float up) = def.Resolve(
            _tuning.InterceptDistanceM, _tuning.InterceptLateralM, _tuning.InterceptVerticalM);

        // Dos geometrias: la de AHORA, que es la que mide y decide de fase, y
        // la adelantada, que es la que se vuela. Mezclarlas haria que en plena
        // persecucion la "distancia" fuera la del punto futuro (kilometros de
        // mas) y las transiciones no llegaran nunca.
        InterceptGeometry now = InterceptGeometry.Solve(ownX, ownY, ownZ, t, aft, right, up, 0.0);

        RangeM = now.RangeM;
        SeparationM = now.SeparationM;
        AlongM = now.AlongM;
        CrossM = now.CrossM;
        VerticalM = now.UpM;
        ClosureKt = ClosureRateKt(t, ownX, ownY, ownZ, ownVx, ownVy, ownVz, now.SeparationM);

        UpdatePhaseByRange(now.RangeM);

        (float maxBank, float maxVsFpm, double maxLead) = PhaseLimits(_phase);
        double lead = maxLead <= 0.0
            ? 0.0
            : InterceptGeometry.LeadSeconds(ownX, ownY, ownZ, t, aft, right, up, ownGsMps, maxLead);
        InterceptGeometry aim = lead <= 0.0
            ? now
            : InterceptGeometry.Solve(ownX, ownY, ownZ, t, aft, right, up, lead);

        // --- 1. Velocidad: es la que decide si hacen falta flaps y serpenteo -
        float iasCmd = SolveSpeedCommand(st, t, now, ownGsKt, dt, out bool tooSlowForUs);

        // --- 2. Rumbo y alabeo ----------------------------------------------
        float desiredTrack = SolveTrack(t, now, aim, tooSlowForUs, ownGsKt);
        float trackErr = Pid.NormalizeAngleDeg180(desiredTrack - ownTrack);

        // El alabeo maximo de la fase, acotado ademas por la G que el ala
        // puede dar de verdad a esta velocidad y peso: pedir 70 deg a 200 kt
        // no da un viraje cerrado, da un buffet.
        float gBudget = MathF.Min(st.UsableG, _tuning.GSoftHigh * GBudgetFraction);
        maxBank = MathF.Min(maxBank, F14Aero.BankForLoadFactorDeg(gBudget));

        float bankRaw = Math.Clamp(TrackToBankGain * trackErr, -maxBank, maxBank);
        // Alivio de G: el backstop de las maniobras ABORTA, pero aqui abortar
        // seria soltar el avion en mitad de una persecucion. Se afloja el
        // alabeo, que es de donde sale la G en un viraje, y se avisa.
        if (st.GNormal > _tuning.GSoftHigh)
        {
            bankRaw *= GReliefFactor;
            if (_gWarnTimer >= WarnRepeatSeconds)
            {
                _gWarnTimer = 0f;
                LogAction($"AVISO: G={st.GNormal:0.0} en la persecucion -> aflojo el viraje.");
            }
        }
        _bankTargetRamp.MaxRate = _tuning.ManeuverBankRampDegPerSec;
        float bankTarget = _bankTargetRamp.Update(bankRaw, dt);
        LastBankTarget = bankTarget;
        _controls.SetRollInput(_bankPid.Update(bankTarget - st.BankDeg, dt), dt);

        // --- 3. Vertical ------------------------------------------------------
        float vsCmd = (float)t.VerticalSpeedFpm +
                      (float)(now.UpM * TargetSnapshot.MetersToFeet) / VerticalTauSec * 60f;
        vsCmd = Math.Clamp(vsCmd, -maxVsFpm, maxVsFpm);
        // Suelo de terreno: no se sigue a nadie contra el suelo. Si el blanco
        // vuela bajo, se le acompana por encima y se dice.
        if (st.AglFt < _tuning.TerrainFloorAglFt && vsCmd < 0f)
        {
            vsCmd = 0f;
            if (_lowAglWarnTimer >= WarnRepeatSeconds)
            {
                _lowAglWarnTimer = 0f;
                LogAction($"AVISO: {st.AglFt:0} ft sobre el terreno; no bajo mas para seguir " +
                          $"a {_targetLabel}.");
            }
        }
        // Guardia de perdida: pedir ascenso es justo lo que se come la
        // velocidad, asi que cerca del minimo seguro el ascenso se va
        // desvaneciendo en vez de cortarse de golpe. Sin esto, perseguir a un
        // blanco 3.000 ft mas arriba con poca energia acaba en entrada en
        // perdida con el morro arriba y el gas ya a fondo.
        float minSafeClean = F14Aero.StallIasKt(st.WeightLb) * F14Aero.StallMarginFactor;
        float speedMargin = st.IasKt - minSafeClean;
        if (vsCmd > 0f && speedMargin < StallGuardBandKt)
            vsCmd *= Math.Clamp(speedMargin / StallGuardBandKt, 0f, 1f);

        LastVsTarget = vsCmd;
        ApplyPitchForVerticalSpeed(st, vsCmd, dt);

        // --- 4. Motor, flaps y aerofrenos -------------------------------------
        LastIasTarget = iasCmd;
        float throttle = _speedPid.Update(iasCmd - st.IasKt, dt);
        LastThrottleCmd = throttle;
        _controls.SetThrottle(throttle, dt);
        FlushSlowControls();

        // --- 5. Llegada --------------------------------------------------------
        if (_phase == InterceptPhase.Station && now.RangeM < CaptureRangeM &&
            Math.Abs(ClosureKt) < SettledClosureKt)
        {
            _settledElapsed += dt;
            if (!_announced && _settledElapsed >= SettleSeconds)
            {
                _announced = true;
                LogAction(BlackBoxSnap.Join(
                    $"Fin: interceptacion establecida sobre {_targetLabel} — {def.Label}",
                    $"{now.SeparationM:0} m sep, blanco {t.GroundSpeedKt:0} kt",
                    tooSlowForUs
                        ? "va demasiado lento: mantengo minimo seguro serpenteando"
                        : null,
                    InterceptLiveExtras(),
                    BlackBoxSnap.Short(st)));
            }
        }
        else
        {
            _settledElapsed = 0f;
        }
    }

    // --- Piezas del lazo ------------------------------------------------------

    // Velocidad indicada que hay que pedirle al motor, y si esa velocidad es
    // mas lenta de lo que este avion puede volar.
    private float SolveSpeedCommand(in FlightState st, in TargetSnapshot t,
                                    in InterceptGeometry now, double ownGsKt, float dt,
                                    out bool tooSlowForUs)
    {
        // Techo: ni la VNE del avion cargado ni el tope que el usuario haya
        // puesto en Config. Es lo que impide que "lo mas rapido posible" se
        // convierta en desarmar el avion en un picado.
        float maxIas = MathF.Min(_tuning.MaxTargetIasKt, st.VneKt * VneMarginFactor);

        // Relacion IAS/velocidad de suelo MEDIDA ahora mismo: mete la altura y
        // el viento en la cuenta sin modelar ninguno de los dos. El blanco da
        // su velocidad de suelo, nosotros mandamos IAS.
        double iasPerGs = ownGsKt > 30.0 ? st.IasKt / ownGsKt : 1.0;
        iasPerGs = Math.Clamp(iasPerGs, 0.3, 1.6);
        float targetIasEquivalent = (float)(t.GroundSpeedKt * iasPerGs);

        // Cuanto mas rapido que el blanco se puede ir AHORA MISMO sin pasarse
        // de largo. Es una sola cuenta -- la de un frenado, v = sqrt(2*a*d) --
        // y sustituye al corte duro que habia entre "gas a fondo" y "raciona
        // el exceso": con distancia de sobra la formula pide mas velocidad de
        // la que el avion tiene, el techo de VNE se la recorta, y sale gas a
        // fondo sin ningun caso especial. A 15 km da 540 kt de exceso, a 3 km
        // da 230, a 300 m da 70, a 50 m casi nada.
        double reachMps;
        if (_phase == InterceptPhase.Station)
        {
            // En formacion ya no se frena hacia un punto: se copia la
            // velocidad del blanco y se corrige con lo retrasado o adelantado
            // que se va (AlongM > 0 = el puesto esta por delante).
            reachMps = Math.Clamp(now.AlongM / StationAlongTauSec,
                                  -StationTrimMps, StationTrimMps);
        }
        else if (now.AlongM < 0.0 && now.RangeM < ClosingEnterM)
        {
            // Nos hemos pasado: el puesto queda por DETRAS en los ejes del
            // blanco. Con la distancia a secas se pediria acelerar -- que es
            // como se adelanta uno todavia mas -- asi que aqui la cuenta va
            // con signo: descolgarse hasta volver a quedar por detras.
            reachMps = -Math.Sqrt(2.0 * ClosingDecelMps2 * Math.Abs(now.AlongM));
        }
        else
        {
            double remaining = Math.Max(now.RangeM - CaptureRangeM, 0.0);
            reachMps = Math.Sqrt(2.0 * ClosingDecelMps2 * remaining);
        }
        _desiredAlongGsKt = t.GroundSpeedKt + reachMps * TargetSnapshot.MpsToKnots;
        float iasCmd = MathF.Min(maxIas,
                                 targetIasEquivalent +
                                 (float)(reachMps * TargetSnapshot.MpsToKnots));

        // Guardia de proximidad: por debajo de esto ya no es una formacion,
        // es un riesgo de colision. Se pide ir mas despacio que el blanco
        // pase lo que pase con la geometria, para abrirse por detras.
        if (now.SeparationM < MinSeparationM)
        {
            iasCmd = MathF.Min(iasCmd, targetIasEquivalent - BackOffKt);
            _speedbrakeRamp.MaxRate = SpeedbrakeRatePerSec;
            _speedbrakeCmd = _speedbrakeRamp.Update(1f, dt);
        }

        // --- Flaps: la primera herramienta para volar despacio ---------------
        float minSafeClean = F14Aero.StallIasKt(st.WeightLb) * F14Aero.StallMarginFactor;
        float flapWant = 0f;
        if (iasCmd < minSafeClean * FlapTriggerFactor)
        {
            flapWant = Math.Clamp(
                (minSafeClean * FlapTriggerFactor - iasCmd) / (minSafeClean * FlapStallReduction),
                0f, 1f);
        }
        // No se sacan por encima de su limite de extension: se arrancarian.
        if (st.IasKt > FlapExtendLimitKt) flapWant = 0f;
        _flapRamp.MaxRate = FlapRatePerSec;
        _flapCmd = _flapRamp.Update(flapWant, dt);
        if (_flapCmd > 0.02f) _flapsAreOurs = true;

        // Suelo de velocidad con los flaps que haya fuera en este momento.
        float minSafeNow = minSafeClean * (1f - FlapStallReduction * _flapCmd);
        tooSlowForUs = iasCmd < minSafeNow - 1f;
        iasCmd = Math.Clamp(iasCmd, minSafeNow, maxIas);

        // --- Aerofrenos: para quitar el exceso sin tocar la actitud ----------
        if (now.SeparationM >= MinSeparationM)
        {
            // Sobra velocidad sobre la pedida -> aerofrenos, proporcional al
            // exceso. En persecucion la pedida ES el maximo, asi que esto da
            // cero solo: no hace falta preguntar por la fase.
            float sbWant = Math.Clamp((st.IasKt - iasCmd - SpeedbrakeDeadbandKt) / SpeedbrakeSpanKt,
                                      0f, 1f);
            _speedbrakeRamp.MaxRate = SpeedbrakeRatePerSec;
            _speedbrakeCmd = _speedbrakeRamp.Update(sbWant, dt);
        }

        return iasCmd;
    }

    // Rumbo (de trayectoria, no de morro) al que hay que volar este frame.
    private float SolveTrack(in TargetSnapshot t, in InterceptGeometry now,
                             in InterceptGeometry aim, bool tooSlowForUs, double ownGsKt)
    {
        if (_phase != InterceptPhase.Station) { _weaveDeg = 0f; return aim.BearingDeg; }

        // Serpenteo: si no se puede volar tan despacio como el blanco, se
        // alarga el camino. El avance neto por la linea del blanco es
        // V*cos(angulo), asi que el angulo que iguala los dos avances sale
        // directo del cociente de velocidades.
        if (tooSlowForUs && ownGsKt > 1.0)
        {
            double ratio = Math.Clamp(_desiredAlongGsKt / ownGsKt, 0.05, 1.0);
            _weaveDeg = MathF.Min((float)(Math.Acos(ratio) * F14Aero.Rad2Deg), MaxWeaveDeg);
            // El zigzag se invierte al pasarse de ancho, no con un reloj: asi
            // el serpenteo queda acotado lateralmente por construccion y no
            // se va abriendo si el blanco cambia de rumbo.
            if (Math.Abs(now.CrossM) > WeaveHalfWidthM) _weaveSign = Math.Sign(now.CrossM);
            return Pid.NormalizeAngleDeg360(t.TrackDeg + _weaveSign * _weaveDeg);
        }

        _weaveDeg = 0f;
        // En formacion se vuela el rumbo del blanco mas una correccion
        // lateral pequena. Perseguir el punto directamente (el rumbo que
        // apunta a el) funciona lejos, pero de cerca hace que cada metro de
        // error lateral pida un viraje: el avion acaba culebreando alrededor
        // del puesto. Se mezclan los dos segun lo lejos que este.
        float hold = t.TrackDeg + (float)Math.Clamp(now.CrossM * StationCrossGainDegPerM,
                                                    -StationMaxOffsetDeg, StationMaxOffsetDeg);
        float w = (float)Math.Clamp(now.HorizontalRangeM / StationBlendM, 0.0, 1.0);
        return Pid.NormalizeAngleDeg360(
            hold + w * Pid.NormalizeAngleDeg180(aim.BearingDeg - hold));
    }

    // V/S pedida -> morro. El grueso lo pone un feedforward de trayectoria
    // (el morro que hace falta para esa V/S a esta velocidad es geometria,
    // no algo que haya que aprender integrando el error) y el PID solo
    // corrige la diferencia. Un PID solo tardaria segundos en encontrar el
    // morro de un ascenso de 8.000 fpm, y en una persecucion esos segundos
    // son el blanco escapandose.
    private void ApplyPitchForVerticalSpeed(in FlightState st, float vsCmdFpm, float dt)
    {
        float tasFpm = MathF.Max(st.TasKt * F14Aero.KtToFtPerSec * 60f, 600f);
        float gammaCmd = (float)(Math.Asin(Math.Clamp(vsCmdFpm / tasFpm, -0.6, 0.6)) *
                                 F14Aero.Rad2Deg);
        // theta = gamma + alpha en vuelo simetrico. Con alabeo, el AoA vive en
        // el plano del ala y solo su proyeccion vertical levanta el morro: por
        // eso va multiplicado por el coseno del alabeo. Sin ese coseno, un
        // viraje cerrado pediria mucho mas morro del que toca.
        float alpha = st.AoaDeg * MathF.Cos(st.BankDeg * F14Aero.Deg2Rad);
        float trim = _vsPid.Update(vsCmdFpm - st.VsFpm, dt);
        float pitchRaw = Math.Clamp(gammaCmd + alpha + trim, -MaxPitchDeg, MaxPitchDeg);

        _pitchTargetRamp.MaxRate = _tuning.ManeuverPitchRampDegPerSec;
        float pitchTarget = _pitchTargetRamp.Update(pitchRaw, dt);
        LastPitchTarget = pitchTarget;
        _controls.SetPitchInput(_pitchPid.Update(pitchTarget - st.PitchDeg, dt), dt);
    }

    // Que hacer mientras no hay nada que perseguir. No se sueltan los
    // overrides a proposito: la interceptacion sigue pedida, solo esta
    // esperando.
    //
    // orbitBankDeg != 0 es la diferencia entre esperar y marcharse: volando
    // recto a 280 kt, cinco minutos de espera a que el blanco despegue son 23
    // NM de alejamiento, y cuando por fin despega hay que recorrerlos otra
    // vez. Con un alabeo suave y sostenido el avion se queda dando vueltas en
    // la zona, que es lo que hace un interceptor en un punto de espera.
    private void FlyLevelHold(in FlightState st, float dt, float orbitBankDeg = 0f)
    {
        _weaveDeg = 0f;
        _bankTargetRamp.MaxRate = _tuning.ManeuverBankRampDegPerSec;
        float bankTarget = _bankTargetRamp.Update(orbitBankDeg, dt);
        LastBankTarget = bankTarget;
        _controls.SetRollInput(_bankPid.Update(bankTarget - st.BankDeg, dt), dt);

        LastVsTarget = 0f;
        ApplyPitchForVerticalSpeed(st, 0f, dt);

        float minSafe = F14Aero.StallIasKt(st.WeightLb) * F14Aero.StallMarginFactor;
        float iasCmd = Math.Clamp(LoiterIasKt, minSafe,
                                  MathF.Min(_tuning.MaxTargetIasKt, st.VneKt * VneMarginFactor));
        LastIasTarget = iasCmd;
        LastThrottleCmd = _speedPid.Update(iasCmd - st.IasKt, dt);
        _controls.SetThrottle(LastThrottleCmd, dt);

        // Limpio mientras se espera: ni flaps ni aerofrenos fuera.
        _flapRamp.MaxRate = FlapRatePerSec;
        _flapCmd = _flapRamp.Update(0f, dt);
        _speedbrakeRamp.MaxRate = SpeedbrakeRatePerSec;
        _speedbrakeCmd = _speedbrakeRamp.Update(0f, dt);
        if (_flapCmd < 0.02f) _flapsAreOurs = false;
        FlushSlowControls();
    }

    // Mide sin mandar: sirve para que el panel siga diciendo a que distancia
    // esta el blanco mientras se le espera en tierra.
    private void MeasureOnly(in TargetSnapshot t,
                             double ownX, double ownY, double ownZ,
                             double ownVx, double ownVy, double ownVz)
    {
        InterceptStationDef def = InterceptCatalog.Get(_station);
        (float aft, float right, float up) = def.Resolve(
            _tuning.InterceptDistanceM, _tuning.InterceptLateralM, _tuning.InterceptVerticalM);
        InterceptGeometry g = InterceptGeometry.Solve(ownX, ownY, ownZ, t, aft, right, up, 0.0);
        RangeM = g.RangeM;
        SeparationM = g.SeparationM;
        AlongM = g.AlongM;
        CrossM = g.CrossM;
        VerticalM = g.UpM;
        ClosureKt = ClosureRateKt(t, ownX, ownY, ownZ, ownVx, ownVy, ownVz, g.SeparationM);
    }

    // Velocidad de acercamiento: la componente de la velocidad relativa sobre
    // la linea de mira. Positiva = nos acercamos.
    private static double ClosureRateKt(in TargetSnapshot t,
                                        double ownX, double ownY, double ownZ,
                                        double ownVx, double ownVy, double ownVz,
                                        double separationM)
    {
        if (separationM < 1.0) return 0.0;
        double ux = (t.X - ownX) / separationM;
        double uy = (t.Y - ownY) / separationM;
        double uz = (t.Z - ownZ) / separationM;
        double rel = (t.Vx - ownVx) * ux + (t.Vy - ownVy) * uy + (t.Vz - ownVz) * uz;
        return -rel * TargetSnapshot.MpsToKnots;
    }

    // Flaps y aerofrenos van por Set (el simulador los anima y el usuario
    // puede seguir tocandolos), pero eso no significa que haya que mandarlos
    // 60 veces por segundo: se manda cuando el valor se ha movido de verdad,
    // o una vez por segundo para que un Set perdido no deje el mando a medias.
    private void FlushSlowControls()
    {
        bool periodic = _slowWriteTimer >= SlowControlRefreshSeconds;
        if (periodic) _slowWriteTimer = 0f;

        if (periodic || float.IsNaN(_flapSent) || MathF.Abs(_flapCmd - _flapSent) > 0.01f)
        {
            _controls.SetFlaps(_flapCmd);
            _flapSent = _flapCmd;
        }
        if (periodic || float.IsNaN(_speedbrakeSent) ||
            MathF.Abs(_speedbrakeCmd - _speedbrakeSent) > 0.01f)
        {
            _controls.SetSpeedbrake(_speedbrakeCmd);
            _speedbrakeSent = _speedbrakeCmd;
        }
    }

    // --- Fases -----------------------------------------------------------------

    private void UpdatePhaseByRange(double rangeM)
    {
        switch (_phase)
        {
            case InterceptPhase.Pursuit when rangeM < ClosingEnterM:
                SetPhase(InterceptPhase.Closing,
                    $"a {BlackBoxSnap.F(rangeM * TargetSnapshot.MetersToNm, "0.0")} NM — " +
                    $"empiezo a frenar (umbral cierre {ClosingEnterM:0} m)");
                break;
            case InterceptPhase.Closing when rangeM > PursuitEnterM:
                SetPhase(InterceptPhase.Pursuit,
                    $"se abre a {BlackBoxSnap.F(rangeM * TargetSnapshot.MetersToNm, "0.0")} NM — " +
                    $"vuelvo a cerrar (umbral persecucion {PursuitEnterM:0} m)");
                break;
            case InterceptPhase.Closing when rangeM < StationEnterM && Math.Abs(ClosureKt) < StationEnterClosureKt:
                SetPhase(InterceptPhase.Station,
                    $"en posicion ({BlackBoxSnap.F(rangeM, "0")} m, cierre {BlackBoxSnap.F(ClosureKt, "0")} kt) — " +
                    $"mantengo formacion (entrar <{StationEnterM:0} m y |cierre|<{StationEnterClosureKt:0} kt)");
                break;
            case InterceptPhase.Station when rangeM > StationExitM:
                SetPhase(InterceptPhase.Closing,
                    $"perdi el puesto ({BlackBoxSnap.F(rangeM, "0")} m > salida {StationExitM:0} m) — recupero");
                break;
        }
    }

    private void SetPhase(InterceptPhase phase, string? reason = null)
    {
        if (_phase == phase) return;
        InterceptPhase from = _phase;
        _phase = phase;
        _settledElapsed = 0f;
        if (phase != InterceptPhase.Station) _announced = false;
        // El autothrottle cambia de regimen con la fase (de "todo a fondo" a
        // "copia esta velocidad"): sin resembrar, el integral que acumulo
        // persiguiendo 600 kt tarda en soltar y el avion se pasa de largo.
        _speedPid.SeedTrim(_controls.ThrottleReadback);

        // Marca en caja negra: el Idle→Pursuit del Start ya lo cuenta el
        // "Inicio: interceptar...", no hace falta duplicarlo.
        if (from == InterceptPhase.Idle && phase == InterceptPhase.Pursuit) return;

        (float maxBank, float maxVs, double maxLead) = PhaseLimits(phase);
        FlightState own = FlightState.Capture(_d);
        LogAction(BlackBoxSnap.Join(
            $"Fase: {_targetLabel} {PhaseName(from)} → {PhaseName(phase)}",
            reason,
            $"limites fase bank≤{maxBank:0}° VS≤{maxVs:0} fpm lead≤{maxLead:0}s",
            InterceptLiveExtras(),
            BlackBoxSnap.Short(own)));
    }

    private string InterceptConfigExtras(InterceptStationDef _) =>
        $"cfg dist={_tuning.InterceptDistanceM:0}m lat={_tuning.InterceptLateralM:0}m " +
        $"vert={_tuning.InterceptVerticalM:0}m · Gsoft={_tuning.GSoftHigh:0.0} " +
        $"sueloAGL={_tuning.TerrainFloorAglFt:0}ft · " +
        $"umbrales cierre<{ClosingEnterM:0}m formacion<{StationEnterM:0}m salida>{StationExitM:0}m";

    private string InterceptLiveExtras()
    {
        var parts = new List<string>(8);
        if (!double.IsNaN(RangeM))
            parts.Add($"rango={BlackBoxSnap.F(RangeM, "0")}m/{BlackBoxSnap.F(RangeM * TargetSnapshot.MetersToNm, "0.00")}NM");
        if (!double.IsNaN(SeparationM))
            parts.Add($"sep={BlackBoxSnap.F(SeparationM, "0")}m");
        if (!double.IsNaN(ClosureKt))
            parts.Add($"cierre={BlackBoxSnap.F(ClosureKt, "+0;-0;0")}kt");
        if (!double.IsNaN(AlongM) || !double.IsNaN(CrossM) || !double.IsNaN(VerticalM))
            parts.Add($"err long={BlackBoxSnap.F(AlongM, "+0;-0;0")} lat={BlackBoxSnap.F(CrossM, "+0;-0;0")} " +
                      $"vert={BlackBoxSnap.F(VerticalM, "+0;-0;0")}m");
        if (!double.IsNaN(TargetSpeedKt))
            parts.Add($"blancoGS={BlackBoxSnap.F(TargetSpeedKt, "0")}kt ({TargetSnapshot.StateName(TargetState)})");
        if (!float.IsNaN(LastIasTarget))
            parts.Add($"IASobj={LastIasTarget:0}");
        if (!float.IsNaN(LastBankTarget))
            parts.Add($"bankObj={LastBankTarget:0.0}");
        if (!float.IsNaN(LastVsTarget))
            parts.Add($"VSobj={LastVsTarget:0}");
        if (_weaveDeg != 0f)
            parts.Add($"serpenteo={_weaveDeg:0.0}°");
        if (_flapsAreOurs || _flapCmd > 0.01f)
            parts.Add($"flaps={_flapCmd * 100f:0}%");
        if (_speedbrakeCmd > 0.01f)
            parts.Add($"SB={_speedbrakeCmd * 100f:0}%");
        return parts.Count == 0 ? "" : string.Join(" ", parts);
    }

    private void LogAction(string msg) => ActionLogged?.Invoke(msg);

    // --- Ganancias de los PID ---------------------------------------------------
    // Cabeceo y alabeo: las mismas que TakeoffSequence/ManeuverSequence, que
    // son las que estan rodadas en este avion.
    private const float PitchKp = 0.09f;
    private const float PitchKi = 0.015f;
    private const float PitchKd = 0.03f;

    private const float BankKp = 0.035f;
    private const float BankKi = 0.004f;
    private const float BankKd = 0.012f;

    // Correccion fina de V/S sobre el morro que ya da el feedforward.
    private const float VsKp = 0.0025f;
    private const float VsKi = 0.0004f;
    private const float VsKd = 0.0008f;
    private const float VsTrimLimitDeg = 8f;

    // Autothrottle. Mas vivo que el de las maniobras: en una interceptacion
    // la velocidad ES la maniobra, y 50 kt de error tienen que dar gas a
    // fondo sin esperar a que el integral se entere.
    private const float SpeedKp = 0.02f;
    private const float SpeedKi = 0.004f;
    private const float SpeedKd = 0.002f;

    // --- Geometria de las fases (metros) -----------------------------------------
    // Con histeresis entre entrada y salida: sin ella, volar justo en el
    // umbral haria saltar la fase varias veces por segundo, y cada salto
    // resiembra el autothrottle.
    private const double ClosingEnterM = 3500.0;
    private const double PursuitEnterM = 6000.0;
    private const double StationEnterM = 200.0;
    private const double StationExitM = 500.0;
    private const double StationEnterClosureKt = 50.0;
    private const double CaptureRangeM = 60.0;
    private const double SettledClosureKt = 15.0;
    private const float SettleSeconds = 2.5f;

    // Separacion minima con el AVION (no con el puesto) antes de considerarlo
    // riesgo de colision y abrirse por detras.
    private const double MinSeparationM = 60.0;
    private const float BackOffKt = 20f;

    // Limites por fase: alabeo (deg), V/S (fpm) y cuanto se adelanta el punto
    // de mira (s).
    private static (float MaxBank, float MaxVsFpm, double MaxLead) PhaseLimits(InterceptPhase p) =>
        p switch
        {
            InterceptPhase.Pursuit => (72f, 9000f, 120.0),
            InterceptPhase.Closing => (50f, 4000f, 15.0),
            InterceptPhase.Station => (32f, 2000f, 0.0),
            _ => (25f, LoiterMaxVsFpm, 0.0),
        };

    // Fraccion del limite BLANDO de G que se permite gastar en el viraje:
    // deja margen para que la turbulencia o un tiron del blanco no lleguen al
    // backstop estructural (GHardHigh).
    private const float GBudgetFraction = 0.85f;
    private const float GReliefFactor = 0.6f;

    private const float TrackToBankGain = 1.2f;   // deg de alabeo por deg de error de rumbo
    private const float MaxPitchDeg = 45f;
    private const float VerticalTauSec = 6f;      // en cuantos segundos se quiere borrar el error vertical

    // Formacion.
    private const double StationAlongTauSec = 4.0;
    private const double StationTrimMps = 25.0;           // ~49 kt de correccion maxima
    private const double StationCrossGainDegPerM = 0.15;
    private const double StationMaxOffsetDeg = 30.0;
    private const double StationBlendM = 250.0;

    // Deceleracion con la que se raciona el exceso de velocidad (m/s^2).
    // 2.5 m/s^2 son ~5 kt/s: lo que da un F-14 con gases al ralenti y
    // aerofrenos fuera en la mitad baja del envolvente, que es donde importa
    // (arriba, a 600 kt, la resistencia frena bastante mas, asi que quedarse
    // corto aqui solo hace que empiece a frenar un poco antes de la cuenta).
    private const double ClosingDecelMps2 = 2.5;

    // Banda de velocidad sobre el minimo seguro en la que el ascenso pedido se
    // va desvaneciendo.
    private const float StallGuardBandKt = 30f;

    // Serpenteo para blancos lentos.
    private const float MaxWeaveDeg = 50f;
    private const double WeaveHalfWidthM = 250.0;

    // Flaps y aerofrenos.
    // FlapStallReduction: cuanto baja la velocidad de perdida con flaps
    // completos. ~18% es lo tipico de un caza con flaps de maniobra + borde
    // de ataque; el F-14 ademas barre el ala solo a baja velocidad, asi que
    // es una estimacion conservadora.
    private const float FlapStallReduction = 0.18f;
    private const float FlapTriggerFactor = 1.08f;
    private const float FlapExtendLimitKt = 225f;
    private const float FlapRatePerSec = 0.15f;
    private const float SpeedbrakeRatePerSec = 0.8f;
    private const float SpeedbrakeDeadbandKt = 12f;
    private const float SpeedbrakeSpanKt = 50f;
    private const float SlowControlRefreshSeconds = 1f;

    // Espera con el blanco en tierra: alabeo sostenido para quedarse en la
    // zona. 20 deg a 280 kt son unas 3 NM de radio -- lo bastante amplio para
    // no cansar y lo bastante cerrado para no perder de vista el campo.
    private const float HoldingBankDeg = 20f;
    private const float LoiterIasKt = 280f;
    private const float LoiterMaxVsFpm = 1500f;

    private const float VneMarginFactor = 0.92f;
    private const float MinOwnIasKt = 120f;
    private const double TerrainReferenceRangeM = 40000.0;
    private const float StatusLogSeconds = 10f;
    private const float WarnRepeatSeconds = 5f;
}
