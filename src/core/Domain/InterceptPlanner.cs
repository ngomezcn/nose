namespace AICopilotCore.Domain;

// Planificador de aproximacion para la interceptacion: funciones PURAS, sin
// X-Plane ni estado global, que dicen POR DONDE y A QUE VELOCIDAD hay que
// llegar a la estacion pedida respecto a un blanco que se mueve (incluso
// girando o trepando). Separado de InterceptSequence (el lazo de control) para
// poder comprobarlo con numeros a mano y con un arnes, sin simulador.
//
// -- Marco --------------------------------------------------------------------
// Entradas y salidas en el marco local OGL de Intercept.cs (+X este, +Y arriba,
// +Z SUR, metros, m/s). Por dentro se trabaja en ENU (E = X, N = -Z, U = Y).
//
// -- Por que se rehizo (velocidad + lado) ------------------------------------
// Side lock / corridor evita cruzar el eje, pero el bug dominante era
// VELOCIDAD: Transit pedia Vmax (~500 kt) con blanco a ~200 kt y rng<5 km
// (cierre racionado sobre dRp + FormationCap suelto). El director clasifica
// situacion (HeadOn / SternFar / SternNear / BeamFar / Overtaking) y CapDesiredGs
// impone techo = Vtgt + cierre acotado por rango: cerca NUNCA Vmax.
//
// -- Dos etapas, con logicas completamente distintas -----------------------------
//  1. TRANSITO (InterceptRegime.Transit). El objetivo NO es el avion, sino el
//     PUNTO DE REUNION RP: un punto virtual 3 km directamente detras del blanco,
//     sobre su vector de cola. Se apunta al RP PREDICHO (persecucion con
//     adelanto). Head-on/beam: pasillo lateral. Velocidad por situacion
//     (HeadOnMatch / SternCapture), no sprint ciego.
//  2. FORMACION (Formation / Station). Ya detras y alineados: se COPIA la
//     velocidad del blanco mas un cierre suave y limitado hacia la estacion
//     (perfil de frenado, tope MaxClosure), con correccion lateral/vertical y
//     feed-forward del giro del blanco. Si se pierde la formacion se vuelve
//     a Transito. SternNear en Transit se entrega a esta ley sin esperar al RP.
public enum InterceptRegime
{
    // Volando al punto de reunion detras del blanco (o rodeandolo para llegar).
    Transit,
    // Detras y alineados: cerrando despacio sobre la estacion.
    Formation,
    // Encima del puesto: copiar velocidad y corregir.
    Station,
}

// Limites del avion propio. Todo en SI y velocidad de SUELO (GS): el llamante
// convierte desde IAS con el cociente IAS/GS medido (ver IasPerGs en la
// entrada), igual que hacia SolveSpeedCommand.
public sealed record OwnLimits
{
    // Velocidad minima SEGURA de suelo (perdida * margen; con flaps sacados
    // si el llamante los va a usar). Por debajo el planificador no baja nunca.
    public double VminGsMps { get; init; } = 75.0;
    // Maxima (VNE * margen, y el tope del usuario).
    public double VmaxGsMps { get; init; } = 440.0;
    // Velocidad de esquina: la mas lenta a la que se alcanza toda la G
    // utilizable. Por debajo n = nmax*(v/Vesquina)^2 (limite de sustentacion).
    public double CornerSpeedMps { get; init; } = 160.0;
    // G utilizable sostenible (ya con margen: la del ManeuverPlanner /
    // GSoftHigh * GBudgetFraction).
    public double MaxG { get; init; } = 5.5;
    public double MaxBankDeg { get; init; } = 72.0;
    // Aceleracion a Vmin y a Vmax con gases a tope (m/s^2); interpolada.
    public double AccelLowMps2 { get; init; } = 4.0;
    public double AccelHighMps2 { get; init; } = 1.5;
    // Deceleracion a Vmin y a Vmax con gases al ralenti Y aerofrenos (m/s^2).
    // Ojo: es un valor CONSERVADOR (lo que se puede garantizar), no el pico.
    // Medido en vuelo real (F-14, ~7.7 km): 0.9-1.9 m/s2 a 200-230 m/s de
    // suelo y 3.4-4.6 a 250-450 m/s. El modelo anterior (2..5) sobreestimaba
    // el frenado del tramo final y se llegaba a la estacion con 50 m/s de cierre.
    public double DecelLowMps2 { get; init; } = 0.6;
    public double DecelHighMps2 { get; init; } = 4.6;
    public double MaxClimbMps { get; init; } = 30.0;
    public double MaxDescentMps { get; init; } = 30.0;
    // Retardo motor/aerofrenos: cuanto tarda la orden de velocidad en
    // notarse. Se descuenta del recorrido de frenado.
    public double SpeedLagSec { get; init; } = 2.0;

    public static OwnLimits F14 { get; } = new();
}

// Memoria entre frames: las decisiones discretas (etapa, lado del rodeo) no
// deben saltar de un frame al siguiente. Inmutable: el planificador devuelve
// una nueva en InterceptPlan.Memory.
public sealed record PlannerMemory
{
    // 0 = Transito, 1 = Formacion.
    public int Stage { get; init; }
    // Rodeo activo (con histeresis) y lado (+1 derecha del blanco).
    public bool Detour { get; init; }
    public int Side { get; init; } = 1;
    // Lado de engagement comprometido: una vez SideLocked, ApproachSide no
    // cambia hasta Abort/Lost. Side se mantiene alineado con ApproachSide.
    public bool SideLocked { get; init; }
    public int ApproachSide { get; init; } = 1;
    // Ultimo comando EMITIDO (rumbo absoluto en rad, su tasa en rad/s y la
    // velocidad de suelo): el comando de salida se suaviza contra el anterior.
    public bool HasOut { get; init; }
    public double OutTrackRad { get; init; }
    public double OutRateRadPerS { get; init; }
    public double OutGsMps { get; init; }
}

public sealed record InterceptPlanInput
{
    // Propio (marco local OGL, m y m/s).
    public double OwnX { get; init; }
    public double OwnY { get; init; }
    public double OwnZ { get; init; }
    public double OwnVx { get; init; }
    public double OwnVy { get; init; }
    public double OwnVz { get; init; }
    // Blanco.
    public double TgtX { get; init; }
    public double TgtY { get; init; }
    public double TgtZ { get; init; }
    public double TgtVx { get; init; }
    public double TgtVy { get; init; }
    public double TgtVz { get; init; }
    // Morro (psi) del blanco: fija los ejes de la estacion. La direccion del
    // MOVIMIENTO sale de la velocidad, asi que con viento no se confunden.
    public double TgtHeadingDeg { get; init; }
    // Tasa de giro del blanco (deg/s, horario positivo). TurnRateEstimator
    // la da a partir de diferencias de TrackDeg.
    public double TgtTurnRateDegPerS { get; init; }
    // Estacion pedida: metros por detras / a la derecha / por encima (ya
    // multiplicados por los factores del catalogo).
    public double AftM { get; init; } = 200.0;
    public double RightM { get; init; }
    public double UpM { get; init; } = 40.0;
    // Punto de reunion: metros detras del blanco sobre su vector de cola.
    public double RendezvousBehindM { get; init; } = 3000.0;
    public OwnLimits Limits { get; init; } = OwnLimits.F14;
    // IAS/GS medido ahora mismo (mete altura y viento sin modelarlos).
    public double IasPerGs { get; init; } = 1.0;
    // Tiempo desde la llamada anterior (para el suavizado de la salida).
    public double DtSec { get; init; } = 0.1;
    public PlannerMemory? Memory { get; init; }
}

public sealed record InterceptPlan
{
    public bool Valid { get; init; }
    public InterceptRegime Regime { get; init; }
    // Fase sugerida en el vocabulario de InterceptSequence.
    public InterceptPhase SuggestedPhase { get; init; }
    // Punto de mira INMEDIATO (donde apunta el guiado en este frame), local.
    public double AimX { get; init; }
    public double AimY { get; init; }
    public double AimZ { get; init; }
    // Rumbo de TRAYECTORIA deseado (deg verdadero) y velocidad de suelo /
    // indicada deseadas ESTE frame.
    public double DesiredTrackDeg { get; init; }
    public double DesiredGsMps { get; init; }
    public double DesiredIasMps { get; init; }
    // Alabeo sugerido (deg, + = derecha) para dar la tasa de giro pedida, y
    // V/S deseada (m/s).
    public double SuggestedBankDeg { get; init; }
    public double DesiredVsMps { get; init; }
    // Tiempo estimado hasta la estacion; NaN si no se puede estimar.
    public double EtaSec { get; init; } = double.NaN;
    public bool Reaches { get; init; }
    // Lo que se va a volar: polilinea en coordenadas locales ABSOLUTAS. Primer
    // punto = posicion propia, ultimo = punto de estacion. Maximo MaxPathPoints.
    public IReadOnlyList<(double X, double Y, double Z)> Path { get; init; } =
        Array.Empty<(double, double, double)>();
    // Estacion respecto a NOSOTROS en ejes del blanco (mismo convenio que
    // InterceptGeometry: >0 = la estacion esta por delante / a la derecha /
    // arriba) y distancia horizontal a ella.
    public double AlongM { get; init; }
    public double CrossM { get; init; }
    public double UpM { get; init; }
    public double RangeM { get; init; }
    // Diagnostico.
    public double PredictedOvertakeM { get; init; }   // >0: estamos por delante de la estacion
    public double PredictedMinSepM { get; init; }     // separacion minima prevista en el transito
    public double PredictedPeakG { get; init; }
    public double PredictedFrontPassM { get; init; } = double.NaN;
    // Distancia al punto de reunion (RP) ahora mismo y etapa activa.
    public double RendezvousRangeM { get; init; }
    public bool Detouring { get; init; }
    // Pasillo de engagement: +1 derecha del blanco, -1 izquierda. SideLocked
    // indica que ya no se puede cambiar (ver InterceptEngagement).
    public int ApproachSide { get; init; } = 1;
    public bool SideLocked { get; init; }
    // Corredor lateral activo (Cola alta / estacion en eje).
    public bool Corridor { get; init; }
    // Situacion del director (elige modulo de ley).
    public InterceptSituation Situation { get; init; }
    // El blanco va mas lento que nuestro minimo seguro: no se puede acompanar
    // a su velocidad; el llamante debe serpentear (WeaveDeg = acos(vt/Vmin)).
    public bool TooSlow { get; init; }
    public double WeaveDeg { get; init; }
    public PlannerMemory Memory { get; init; } = new();
}

// Estima la tasa de giro del blanco por diferencias de TrackDeg entre frames
// (paso bajo de constante TauSec y banda muerta: el rumbo llega cuantizado y
// a 10 Hz, y un blanco recto no debe parecer que gira).
public sealed class TurnRateEstimator
{
    private const double TauSec = 2.5;
    private const double MaxRateDegPerS = 15.0;
    private const double DeadbandDegPerS = 0.25;
    private double _prev = double.NaN;
    private double _rate;

    public double RateDegPerS => Math.Abs(_rate) < DeadbandDegPerS ? 0.0 : _rate;

    public void Reset() { _prev = double.NaN; _rate = 0.0; }

    public double Update(double trackDeg, double dtSec, double speedMps)
    {
        if (dtSec <= 1e-4) return RateDegPerS;
        if (double.IsNaN(_prev) || speedMps < 15.0) { _prev = trackDeg; _rate *= 0.9; return RateDegPerS; }
        double d = trackDeg - _prev;
        d -= 360.0 * Math.Round(d / 360.0);
        _prev = trackDeg;
        double inst = Math.Clamp(d / dtSec, -MaxRateDegPerS, MaxRateDegPerS);
        double alpha = 1.0 - Math.Exp(-dtSec / TauSec);
        _rate += alpha * (inst - _rate);
        return RateDegPerS;
    }
}

public static class InterceptPlanner
{
    public const int MaxPathPoints = 40;

    private const double G = 9.80665;
    private const double Deg = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    // --- Transito ---------------------------------------------------------------
    // Se pasa a Formacion al llegar a esta distancia del RP.
    private const double TransitDoneM = 800.0;
    // Tambien se entra directo a Formacion si ya se esta detras, dentro de este
    // embudo, a menos de DirectEntryM de la estacion y con un cierre que se
    // puede frenar (no se retrocede hasta el RP para volver a avanzar).
    private static readonly double TanDirect = Math.Tan(20.0 * Deg);
    // Alineacion exigida para entregar a Formacion por llegar al RP.
    private static readonly double TanHandoff = Math.Tan(30.0 * Deg);
    // Mientras se este descentrado del eje, el punto de mira se retrasa
    // ExtraPerCross metros por metro de desvio (tope ExtraMaxM): la ruta
    // desemboca en el eje de cola en vez de cruzarlo de lado.
    private const double ExtraPerCross = 0.9;
    private const double ExtraMaxM = 3500.0;
    private const double DirectEntryM = 3500.0;
    private const double DirectEntryMaxClosureMps = 100.0;
    // Velocidad relativa TOTAL maxima para entrar en Formacion (no solo el cierre
    // sobre el eje: cruzar de lado a 300 m/s tambien atraviesa la estacion).
    private const double HandoffMaxRelMps = 110.0;
    // Rodeo: se activa si la ruta directa pasa a menos de EnterM del blanco y
    // se mantiene hasta que quede a mas de ExitM (histeresis).
    private const double DetourEnterM = 1000.0;
    private const double DetourExitM = 1500.0;
    private const double DetourSideM = 1800.0;
    private const double DetourBackM = 1500.0;
    // Adelanto maximo de la puntería y giro maximo que se extrapola del blanco.
    private const double MaxLeadSec = 90.0;
    private const double MaxPredTurnRad = 150.0 * Deg;
    // Blanco que gira: el radio de giro propio a la velocidad de vuelo tiene que
    // caber (con este margen) en el del blanco, o no se le puede seguir.
    private const double TurnFitMargin = 0.85;
    // Prudencia sobre la deceleracion real (<1).
    private const double BrakeF = 0.75;
    // Adelantados a la estacion: rumbo del blanco, frenar y dejarse adelantar.
    private const double DropBackMaxM = 2500.0;
    private const double DropBackBaseMps = 8.0;
    private const double DropBackGain = 0.3;
    private const double DropBackCapMps = 35.0;
    private const double DropBackLatDegPerM = 0.05;
    private const double DropBackMaxOffRad = 25.0 * Deg;

    // --- Formacion --------------------------------------------------------------
    // Tope del cierre relativo mientras se recorren los ultimos km (~136 kt).
    private const double FormationCapMps = 70.0;
    private const double StationTrimMps = 22.0;
    private const double AlongGain = 0.30;
    // Ganancia del cierre sobre la distancia total en la guia inercial: a la
    // entrada de la zona de estacion (~160 m) deja ~24 m/s de cierre.
    private const double FormationCloseGain = 0.15;
    private const double CrossGain = 0.25;
    // Se pierde la formacion si se queda por delante de la estacion mas de esto.
    private const double FormationAheadM = 2000.0;
    // ... pero solo si ademas queda casi alineado con el blanco (riesgo de pasar
    // por delante/encima): descentrado se deja caer hacia atras con el cierre
    // negativo de la propia ley, sin abandonar la formacion.
    private const double FormationAheadLatM = 1200.0;
    private const double FormationFarAheadM = 3000.0;
    private const double VerticalTauSec = 6.0;

    // --- Suavizado de la salida ---------------------------------------------
    private const double ForceSideRad = 100.0 * Deg;
    private const double OutAccelRadPerS2 = 8.0 * Deg;   // aceleracion angular maxima del comando de rumbo
    private const double OutRateFactor = 1.2;            // tasa maxima del comando / tasa de giro posible
    private const double OutLeadMaxRad = 60.0 * Deg;     // cuanto puede adelantarse el comando al rumbo real
    private const double OutSpeedSlewMps2 = 9.0;         // pendiente maxima del comando de velocidad (~17 kt/s)
    private const double OutSpeedSlewNearMps2 = 22.0;    // bajada rapida cerca (<8 km) para matar Vmax
    private const double TurnGain = 0.7;                 // 1/s: tasa de giro por rad de error

    private const int ModeRp = 0, ModeWaypoint = 1, ModeStation = 2, ModeCorridor = 3;

    private struct OwnS { public double E, N, U, Psi, V, Vy; }
    private struct TgtS { public double E, N, U, Track, V, Vy, Omega, HeadOff; }

    private struct Ctx
    {
        public OwnLimits L;
        public double Aft, Right, Up, Behind, Zone;
        // Ancho del pasillo lateral (0 = RP en eje). Se reduce al quedar detras.
        public double CorridorW;
    }

    private struct Cmd
    {
        public double HeadingCmd, TurnErr, VCmd, VyCmd, PsiDotFF;
        public double AimE, AimN, AimU;
        public InterceptRegime Regime;
        public bool Detour;
        public bool Corridor;
        public double TInt, MinSep;
        public (double E, double N, double U)? Waypoint;
    }

    // ==========================================================================
    // API
    // ==========================================================================
    public static InterceptPlan Plan(InterceptPlanInput inp)
    {
        OwnLimits L = inp.Limits;
        OwnS own = new()
        {
            E = inp.OwnX, N = -inp.OwnZ, U = inp.OwnY,
            V = Math.Sqrt(inp.OwnVx * inp.OwnVx + inp.OwnVz * inp.OwnVz),
            Vy = inp.OwnVy,
        };
        own.Psi = own.V < 3.0 ? 0.0 : Math.Atan2(inp.OwnVx, -inp.OwnVz);
        own.V = Math.Max(own.V, 1.0);

        double tv = Math.Sqrt(inp.TgtVx * inp.TgtVx + inp.TgtVz * inp.TgtVz);
        double hdg = inp.TgtHeadingDeg * Deg;
        TgtS tgt = new()
        {
            E = inp.TgtX, N = -inp.TgtZ, U = inp.TgtY,
            V = tv, Vy = inp.TgtVy,
            Track = tv < 2.0 ? hdg : Math.Atan2(inp.TgtVx, -inp.TgtVz),
            Omega = inp.TgtTurnRateDegPerS * Deg,
        };
        tgt.HeadOff = tv < 2.0 ? 0.0 : WrapPi(hdg - tgt.Track);
        // Un cabeceo de crab enorme (viento) no debe girar los ejes de la estacion.
        tgt.HeadOff = Math.Clamp(tgt.HeadOff, -0.6, 0.6);

        Ctx x = new()
        {
            L = L, Aft = inp.AftM, Right = inp.RightM, Up = inp.UpM,
            Behind = Math.Max(inp.RendezvousBehindM, Math.Abs(inp.AftM) + 600.0),
            Zone = Math.Clamp(0.6 * Math.Abs(inp.AftM) + 40.0, 80.0, 250.0),
        };

        PlannerMemory mem = inp.Memory ?? new PlannerMemory();

        // Geometria de ahora respecto a la estacion (marco del blanco).
        GetRel(own, tgt, x, out double a, out double c, out double dh, out double dU);
        double h = tgt.Track + tgt.HeadOff;
        double fE = Math.Sin(h), fN = Math.Cos(h);
        double rE = Math.Cos(h), rN = -Math.Sin(h);
        // Misma proyeccion pero respecto al AVION blanco (engagement): el lado
        // se decide aqui, no respecto a la estacion (que en Cola alta esta en
        // el eje y hace que sign(c) bascule al cruzar).
        double alongT = (own.E - tgt.E) * fE + (own.N - tgt.N) * fN;
        double crossT = (own.E - tgt.E) * rE + (own.N - tgt.N) * rN;
        double rangeT = Math.Sqrt((own.E - tgt.E) * (own.E - tgt.E) + (own.N - tgt.N) * (own.N - tgt.N));
        bool sideLocked = mem.SideLocked;
        int approachSide = InterceptEngagement.ChooseApproachSide(
            crossT, sideLocked, mem.ApproachSide, x.Right);
        if (!sideLocked && (rangeT <= InterceptEngagement.SideLockRangeM ||
                            Math.Abs(crossT) >= InterceptEngagement.SidePreferM ||
                            mem.Detour))
            sideLocked = true;
        InterceptSituation sit = InterceptEngagement.ClassifySituation(alongT, crossT, rangeT);
        bool useCorridor = InterceptEngagement.NeedsCorridor(x.Right, alongT, crossT)
                           || InterceptEngagement.ForceCorridor(sit, alongT);
        // Al quedar detras, el pasillo se estrecha hacia el eje para poder
        // entregar a Formacion sin quedarse orbitando a CorridorSideM.
        double sternBlend = Math.Clamp((-alongT - 400.0) / 2600.0, 0.0, 1.0);
        x.CorridorW = useCorridor
            ? InterceptEngagement.CorridorSideM * (1.0 - sternBlend)
            : 0.0;

        double vtE = tgt.V * Math.Sin(tgt.Track), vtN = tgt.V * Math.Cos(tgt.Track);
        // Cierre a lo largo del eje del blanco (>0: nos acercamos desde atras).
        double vRelAxis = (own.V * Math.Sin(own.Psi) - vtE) * fE + (own.V * Math.Cos(own.Psi) - vtN) * fN;
        int rpMode = x.CorridorW > 50.0 ? ModeCorridor : ModeRp;
        AimPoint(tgt, x, rpMode, approachSide, 0.0, 0.0, out double rpE, out double rpN, out _);
        double rpTrk = tgt.Track - tgt.Omega * x.Behind / Math.Max(tgt.V, 20.0);
        double dRp = Math.Sqrt((own.E - rpE) * (own.E - rpE) + (own.N - rpN) * (own.N - rpN));

        // --- Maquina de etapas (con histeresis por construccion) ---------------
        bool inFunnel = a < 0.0 && Math.Abs(c) <= -a * TanDirect;
        double wBrakeNow = Math.Sqrt(2.0 * BrakeF * Decel(L, 0.5 * (own.V + tgt.V)) *
                                     Math.Max(dh - Math.Max(vRelAxis, 0.0) * L.SpeedLagSec, 0.0));
        double relE = own.V * Math.Sin(own.Psi) - vtE, relN = own.V * Math.Cos(own.Psi) - vtN;
        double relSpeed = Math.Sqrt(relE * relE + relN * relN);
        bool slowEnough = relSpeed <= HandoffMaxRelMps;
        bool directOk = inFunnel && dh < DirectEntryM && slowEnough &&
                        vRelAxis <= Math.Min(1.1 * wBrakeNow + 10.0, DirectEntryMaxClosureMps);
        int stage = mem.Stage;
        if (stage == 0)
        {
            double aRp = (own.E - rpE) * Math.Sin(rpTrk) + (own.N - rpN) * Math.Cos(rpTrk);
            double cRp = (own.E - rpE) * Math.Cos(rpTrk) - (own.N - rpN) * Math.Sin(rpTrk);
            bool alignedAtRp = dRp < TransitDoneM && Math.Abs(cRp) <= Math.Abs(aRp) * TanHandoff + 300.0;
            if ((alignedAtRp && slowEnough) || directOk) stage = 1;
        }
        else if (dh > x.Behind + 2000.0 || a > FormationFarAheadM ||
                 (a > FormationAheadM && Math.Abs(c) < FormationAheadLatM))
        {
            stage = 0;
        }

        // En Transit, si ya estamos en SternNear con cierre manejable, preferir
        // la ley de formacion (mata el sprint a Vmax puesto detras a mano).
        bool sternCapture = stage == 0 && sit == InterceptSituation.SternNear &&
                            alongT < -400.0 && rangeT < InterceptEngagement.SternNearRangeM;
        Cmd c0 = (stage == 0 && !sternCapture)
            ? Transit(own, tgt, x, mem, approachSide, useCorridor, dRp, rangeT, sit)
            : Formation(own, tgt, x, a, c, dh, dU, fE, fN, vtE, vtN, vRelAxis, mem, rangeT, sit);

        // Tope duro de velocidad: cerca del blanco NUNCA Vmax.
        c0.VCmd = InterceptEngagement.CapDesiredGs(c0.VCmd, tgt.V, rangeT, L, sit);

        double wMax = TurnRateMax(L, own.V);

        // --- Suavizado del comando de salida ---------------------------------
        // El guiado cambia de etapa/rodeo y su comando bruto da saltos de
        // decenas de grados o de m/s entre dos frames. Al lazo se le entrega un
        // comando continuo: rumbo con tasa y aceleracion angular acotadas
        // (perfil sqrt(2*A*e), sin sobreoscilar) y velocidad con pendiente
        // acotada.
        double dtc = Math.Clamp(inp.DtSec, 0.02, 0.5);
        double lead = mem.HasOut ? WrapPi(mem.OutTrackRad - own.Psi) : 0.0;
        double rate = mem.HasOut ? mem.OutRateRadPerS : 0.0;
        double e = c0.TurnErr - lead;   // sin envolver: conserva el lado forzado
        double rMax = Math.Max(OutRateFactor * wMax, 3.0 * Deg);
        double rWant = Math.Sign(e) * Math.Min(rMax, Math.Sqrt(2.0 * OutAccelRadPerS2 * Math.Abs(e)));
        rate += Math.Clamp(rWant - rate, -OutAccelRadPerS2 * dtc, OutAccelRadPerS2 * dtc);
        double newLeadAbs = own.Psi + lead + rate * dtc;
        double leadNew = Math.Clamp(newLeadAbs - own.Psi, -OutLeadMaxRad, OutLeadMaxRad);
        double outTrack = own.Psi + leadNew;
        double vPrev = mem.HasOut ? mem.OutGsMps : own.V;
        // Cerca: bajar de Vmax a Vtgt+cierre tiene que ser rapido; si no, el
        // slewing deja DesiredGs alto varios segundos y se pasa igual.
        double speedSlew = OutSpeedSlewMps2;
        if (c0.VCmd < vPrev - 1.0 && rangeT < InterceptEngagement.SpeedMatchRangeM)
            speedSlew = OutSpeedSlewNearMps2;
        double vGs = vPrev + Math.Clamp(c0.VCmd - vPrev, -speedSlew * dtc, OutSpeedSlewMps2 * dtc);
        double psiDot = Math.Clamp(TurnGain * leadNew + c0.PsiDotFF, -wMax, wMax);
        double bank = Math.Atan(own.V * psiDot / G) * Rad2Deg;
        double peakG = Math.Sqrt(1.0 + Math.Pow(own.V * psiDot / G, 2.0));

        bool tooSlow = tgt.V < L.VminGsMps + 2.0;
        double weave = tooSlow && c0.Regime == InterceptRegime.Station
            ? Math.Acos(Math.Clamp(tgt.V / L.VminGsMps, 0.05, 1.0)) * Rad2Deg
            : 0.0;

        InterceptPhase phase = c0.Regime switch
        {
            InterceptRegime.Station => InterceptPhase.Station,
            InterceptRegime.Formation => InterceptPhase.Closing,
            _ => dRp > 3500.0 ? InterceptPhase.Pursuit : InterceptPhase.Closing,
        };

        // ETA y ruta.
        double formSpeed = 0.6 * FormationCapMps;
        double etaForm = dh / formSpeed;
        double eta;
        var pts = new List<(double E, double N, double U)>(4) { (own.E, own.N, own.U) };
        if (c0.Regime == InterceptRegime.Transit)
        {
            double toS = Math.Max(x.Behind - Math.Abs(x.Aft), 0.0) / formSpeed;
            eta = c0.TInt + toS;
            if (c0.Waypoint is { } wp) pts.Add(wp);
            AimPoint(tgt, x, ModeRp, 1, c0.TInt, 0.0, out double pe, out double pn, out double pu);
            pts.Add((pe, pn, pu));
            StationAt(tgt, x, eta, out double sE, out double sN, out double sU);
            pts.Add((sE, sN, sU));
        }
        else
        {
            eta = etaForm;
            StationAt(tgt, x, eta, out double sE, out double sN, out double sU);
            pts.Add((sE, sN, sU));
        }
        var path = new List<(double, double, double)>(pts.Count);
        foreach (var p in pts) path.Add((p.E, p.U, -p.N));   // ENU -> OGL

        return new InterceptPlan
        {
            Valid = true,
            Regime = c0.Regime,
            SuggestedPhase = phase,
            AimX = c0.AimE, AimY = c0.AimU, AimZ = -c0.AimN,
            DesiredTrackDeg = NormDeg(outTrack * Rad2Deg),
            DesiredGsMps = vGs,
            DesiredIasMps = vGs * inp.IasPerGs,
            SuggestedBankDeg = bank,
            DesiredVsMps = c0.VyCmd,
            EtaSec = eta,
            Reaches = true,
            Path = path,
            AlongM = -a,
            CrossM = -c,
            UpM = dU,
            RangeM = dh,
            PredictedOvertakeM = Math.Max(a, 0.0),
            PredictedMinSepM = double.IsNaN(c0.MinSep) ? Math.Sqrt(dh * dh + dU * dU) : c0.MinSep,
            PredictedPeakG = peakG,
            RendezvousRangeM = dRp,
            Detouring = c0.Detour,
            ApproachSide = approachSide,
            SideLocked = sideLocked,
            Corridor = c0.Corridor,
            Situation = sit,
            TooSlow = tooSlow,
            WeaveDeg = weave,
            Memory = new PlannerMemory
            {
                Stage = stage,
                Detour = c0.Detour,
                Side = approachSide,
                SideLocked = sideLocked,
                ApproachSide = approachSide,
                HasOut = true, OutTrackRad = outTrack, OutRateRadPerS = rate, OutGsMps = vGs,
            },
        };
    }

    // ==========================================================================
    // Envolvente del avion propio
    // ==========================================================================
    private static double Lerp01(OwnLimits L, double v) =>
        Math.Clamp((v - L.VminGsMps) / Math.Max(L.VmaxGsMps - L.VminGsMps, 1.0), 0.0, 1.0);

    public static double Decel(OwnLimits L, double v) =>
        L.DecelLowMps2 + (L.DecelHighMps2 - L.DecelLowMps2) * Lerp01(L, v);

    public static double Accel(OwnLimits L, double v) =>
        L.AccelLowMps2 + (L.AccelHighMps2 - L.AccelLowMps2) * Lerp01(L, v);

    // Factor de carga utilizable a la velocidad v: la G utilizable por
    // encima de la velocidad de esquina y n = nmax*(v/Vesquina)^2 (sustentacion)
    // por debajo.
    public static double UsableN(OwnLimits L, double v)
    {
        double r = v / Math.Max(L.CornerSpeedMps, 1.0);
        return Math.Max(Math.Min(L.MaxG, L.MaxG * r * r), 1.0);
    }

    // Tasa de giro maxima (rad/s) en viraje coordinado nivelado:
    // psi' = g*sqrt(n^2-1)/v, con n acotado tambien por el alabeo maximo.
    public static double TurnRateMax(OwnLimits L, double v)
    {
        v = Math.Max(v, 20.0);
        double n = UsableN(L, v);
        double byN = G * Math.Sqrt(Math.Max(n * n - 1.0, 0.0)) / v;
        double byBank = G * Math.Tan(L.MaxBankDeg * Deg) / v;
        return Math.Min(byN, byBank);
    }

    // Radio de giro minimo (m) a la velocidad v: r = v / psi'max.
    public static double Radius(OwnLimits L, double v) =>
        Math.Max(v, 20.0) / Math.Max(TurnRateMax(L, v), 1e-3);

    // Velocidad maxima de vuelo con la que el radio de giro propio cabe en la
    // circunferencia del blanco (tv/omega). Sin giro apreciable, vmax.
    private static double TurnFitVmax(OwnLimits L, in TgtS t)
    {
        double om = Math.Abs(t.Omega);
        if (om < 0.5 * Deg || t.V < 1.0) return L.VmaxGsMps;
        double rt = t.V / om * TurnFitMargin;
        double v = L.VmaxGsMps;
        while (v > L.VminGsMps && Radius(L, v) > rt) v -= 5.0;
        return Math.Max(v, Math.Min(L.VminGsMps * 1.1, L.VmaxGsMps));
    }

    // ==========================================================================
    // Etapa 1: transito al punto de reunion
    // ==========================================================================
    private static Cmd Transit(OwnS o, TgtS t, Ctx x, PlannerMemory mem, int approachSide,
                               bool useCorridor, double dRp,
                               double rangeT, InterceptSituation sit)
    {
        OwnLimits L = x.L;
        var cmd = new Cmd { Regime = InterceptRegime.Transit, MinSep = double.NaN };

        // Cierre contra el RP REAL, que se mueve (con el blanco girando barre
        // Behind*omega m/s): velocidad del RP por diferencias finitas.
        AimPoint(t, x, ModeRp, approachSide, 0.0, 0.0, out double rp0E, out double rp0N, out _);
        AimPoint(t, x, ModeRp, approachSide, 1.0, 0.0, out double rp1E, out double rp1N, out _);
        double vrE = rp1E - rp0E, vrN = rp1N - rp0N;
        double lx = rp0E - o.E, ly = rp0N - o.N;
        double ld = Math.Max(Math.Sqrt(lx * lx + ly * ly), 1.0);
        double w0 = ((o.V * Math.Sin(o.Psi) - vrE) * lx + (o.V * Math.Cos(o.Psi) - vrN) * ly) / ld;
        double vRpMag = Math.Sqrt(vrE * vrE + vrN * vrN);
        // Velocidad por situacion: HeadOn iguala; Stern/Beam = tgt + cierre
        // racionado. El tope duro CapDesiredGs (sobre rangeT, no dRp) evita
        // el sprint a Vmax a <5-8 km con blanco lento.
        double dBrake = Math.Max(dRp - TransitDoneM - Math.Max(w0, 0.0) * L.SpeedLagSec, 0.0);
        double vTop = TurnFitVmax(L, t);
        double vCmd = sit switch
        {
            InterceptSituation.HeadOn =>
                InterceptEngagement.HeadOnMatchGs(t.V, rangeT, L),
            InterceptSituation.Overtaking =>
                InterceptEngagement.CapDesiredGs(t.V - 20.0, t.V, rangeT, L, sit),
            _ => InterceptEngagement.SternCaptureGs(t.V, rangeT, dBrake, o.V, L, sit),
        };
        // Lejos y no head-on: se puede usar el margen hasta vTop si el techo
        // de situacion lo permite (CapDesiredGs ya lo corta cerca).
        if ((sit is InterceptSituation.SternFar or InterceptSituation.BeamFar) &&
            rangeT >= InterceptEngagement.SpeedMatchRangeM)
        {
            double aDec = BrakeF * Decel(L, 0.5 * (o.V + t.V));
            double wFar = Math.Min(Math.Sqrt(2.0 * aDec * dBrake),
                                   InterceptEngagement.MaxClosureMps(rangeT, sit));
            vCmd = Math.Clamp(vRpMag + Math.Max(wFar, 0.0), L.VminGsMps, vTop);
            vCmd = InterceptEngagement.CapDesiredGs(vCmd, t.V, rangeT, L, sit);
        }
        double vSolve = 0.5 * (o.V + vCmd);

        // Descentrado respecto al eje de cola del RP: se apunta mas atras.
        double rpTrk = t.Track - t.Omega * x.Behind / Math.Max(t.V, 20.0);
        double tE = Math.Sin(rpTrk), tN = Math.Cos(rpTrk);
        double aR = (o.E - rp0E) * tE + (o.N - rp0N) * tN;
        double cR = (o.E - rp0E) * tN - (o.N - rp0N) * tE;
        double extra = Math.Clamp(ExtraPerCross * Math.Abs(cR), 0.0, ExtraMaxM);
        if (aR < -extra) extra *= Math.Clamp(-aR / Math.Max(extra, 1.0) - 1.0, 0.0, 1.0);   // ya detras del punto: sin retraso

        // Ruta directa al RP predicho; si pasa cerca del blanco, se rodea.
        // Cola alta / estacion en eje: se fuerza el pasillo lateral (ModeCorridor)
        // con el lado bloqueado para no cruzar el eje del blanco.
        double tInt = SolveT(o, t, x, ModeRp, approachSide, extra, vSolve, out double aE, out double aN, out double aU);
        double minSep = MinSepStraight(o, t, aE, aN, aU, vSolve, tInt);
        bool detour = mem.Detour ? minSep < DetourExitM : minSep < DetourEnterM;
        cmd.MinSep = minSep;
        if (useCorridor)
        {
            tInt = SolveT(o, t, x, ModeCorridor, approachSide, extra, vSolve, out aE, out aN, out aU);
            cmd.Waypoint = (aE, aN, aU);
            cmd.Corridor = true;
            // El corredor ya es un rodeo suave: no hace falta el waypoint de
            // emergencia salvo que la recta al corredor tambien roce al blanco.
            double corridorSep = MinSepStraight(o, t, aE, aN, aU, vSolve, tInt);
            cmd.MinSep = corridorSep;
            if (corridorSep < DetourEnterM)
            {
                tInt = SolveT(o, t, x, ModeWaypoint, approachSide, 0.0, vSolve, out aE, out aN, out aU);
                cmd.Waypoint = (aE, aN, aU);
                detour = true;
            }
        }
        else if (detour)
        {
            tInt = SolveT(o, t, x, ModeWaypoint, approachSide, 0.0, vSolve, out aE, out aN, out aU);
            cmd.Waypoint = (aE, aN, aU);
        }
        cmd.Detour = detour;
        cmd.TInt = tInt;

        double heading = Math.Atan2(aE - o.E, aN - o.N);
        cmd.HeadingCmd = heading;
        cmd.TurnErr = ForcedSideErr(WrapPi(heading - o.Psi), mem);
        cmd.VCmd = vCmd;
        // Vertical: la que hace falta para estar a la altura del RP al llegar.
        cmd.VyCmd = Math.Clamp((aU - o.U) / Math.Max(tInt, 5.0), -L.MaxDescentMps, L.MaxClimbMps);
        cmd.AimE = aE; cmd.AimN = aN; cmd.AimU = aU;
        cmd.PsiDotFF = 0.0;
        return cmd;
    }

    // Punto al que se apunta en el instante tau: el RP (3 km detras sobre la
    // cola del blanco predicho), el RP con offset de corredor, o, en rodeo,
    // un punto de paso lateral y atrasado.
    private static void AimPoint(TgtS t, Ctx x, int mode, int side, double tau, double extra,
                                 out double e, out double n, out double u)
    {
        u = t.U + t.Vy * tau + x.Up;
        if (mode == ModeStation)
        {
            StationAt(t, x, tau, out e, out n, out u);
            return;
        }
        if (mode == ModeRp || mode == ModeCorridor)
        {
            // RP: punto de la TRAYECTORIA del blanco (Behind + extra) metros por
            // detras. Con el blanco recto es su vector de cola; girando, es el
            // punto que el blanco ya recorrio, que se mueve a SU velocidad (la
            // tangente a 3 km barreria hacia fuera a Behind*omega m/s mas).
            Predict(t, tau - (x.Behind + extra) / Math.Max(t.V, 20.0), out e, out n, out double trk);
            if (mode == ModeCorridor)
            {
                double hh = trk + t.HeadOff;
                double rrE = Math.Cos(hh), rrN = -Math.Sin(hh);
                e += side * x.CorridorW * rrE;
                n += side * x.CorridorW * rrN;
            }
            return;
        }
        Predict(t, tau, out double te, out double tn, out double trkWp);
        double h = trkWp + t.HeadOff;
        e = te - DetourBackM * Math.Sin(h) + side * DetourSideM * Math.Cos(h);
        n = tn - DetourBackM * Math.Cos(h) - side * DetourSideM * Math.Sin(h);
    }

    // Persecucion con adelanto contra un punto que se mueve: punto fijo
    // tau = |P(tau) - propio| / v (converge rapido porque v > velocidad del
    // punto). Devuelve el tiempo y el punto en ese instante.
    private static double SolveT(OwnS o, TgtS t, Ctx x, int wp, int side, double extra, double v,
                                 out double aE, out double aN, out double aU)
    {
        v = Math.Max(v, 20.0);
        AimPoint(t, x, wp, side, 0.0, extra, out aE, out aN, out aU);
        double tau = Math.Sqrt((aE - o.E) * (aE - o.E) + (aN - o.N) * (aN - o.N)) / v;
        for (int i = 0; i < 8; i++)
        {
            tau = Math.Min(tau, MaxLeadSec);
            AimPoint(t, x, wp, side, tau, extra, out aE, out aN, out aU);
            double nt = Math.Sqrt((aE - o.E) * (aE - o.E) + (aN - o.N) * (aN - o.N)) / v;
            bool done = Math.Abs(nt - tau) < 0.05;
            tau = nt;
            if (done) break;
        }
        tau = Math.Min(tau, MaxLeadSec);
        AimPoint(t, x, wp, side, tau, extra, out aE, out aN, out aU);
        return tau;
    }

    // Separacion minima con el blanco predicho si se vuela recto al punto de
    // mira a velocidad v durante tMax.
    private static double MinSepStraight(OwnS o, TgtS t, double aE, double aN, double aU,
                                         double v, double tMax)
    {
        double dx = aE - o.E, dy = aN - o.N, dz = aU - o.U;
        double dn = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1.0);
        dx /= dn; dy /= dn; dz /= dn;
        double min = double.MaxValue;
        double end = Math.Min(Math.Max(tMax, 2.0), MaxLeadSec);
        for (double tau = 0.0; tau <= end + 1e-9; tau += 2.0)
        {
            Predict(t, tau, out double te, out double tn, out _);
            double tu = t.U + t.Vy * tau;
            double ex = o.E + dx * v * tau - te, ey = o.N + dy * v * tau - tn, ez = o.U + dz * v * tau - tu;
            double sep = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            if (sep < min) min = sep;
        }
        return min;
    }

    // ==========================================================================
    // Etapa 2: formacion
    // ==========================================================================
    private static Cmd Formation(OwnS o, TgtS t, Ctx x, double a, double c, double dh, double dU,
                                 double fE, double fN, double vtE, double vtN, double vRel,
                                 PlannerMemory mem, double rangeT, InterceptSituation sit)
    {
        OwnLimits L = x.L;
        double rE = fN, rN = -fE;
        GetStationPoint(t, x, out double sE, out double sN, out double sU);
        var cmd = new Cmd
        {
            Regime = dh < x.Zone ? InterceptRegime.Station : InterceptRegime.Formation,
            MinSep = double.NaN,
        };

        // Vertical: V/S del blanco + error de altura.
        double vyRel = Math.Clamp(dU / VerticalTauSec, -L.MaxDescentMps, L.MaxClimbMps);
        cmd.VyCmd = Math.Clamp(t.Vy + vyRel, -L.MaxDescentMps, L.MaxClimbMps);

        // Nos hemos pasado (la estacion queda ATRAS): no se da la vuelta hacia
        // ella, que es el bucle de 360 grados. Se mantiene el rumbo del blanco
        // (con una correccion lateral acotada) y se frena por debajo de su
        // velocidad hasta que nos adelante. Sobre la zona de estacion lo
        // resuelve la ley de marco de abajo.
        if (dh >= x.Zone && a > 0.0 && dh < DropBackMaxM)
        {
            double off = Math.Clamp(-c * DropBackLatDegPerM * Deg, -DropBackMaxOffRad, DropBackMaxOffRad);
            double wBack = Math.Min(DropBackBaseMps + DropBackGain * a, DropBackCapMps);
            cmd.HeadingCmd = t.Track + off;
            cmd.TurnErr = WrapPi(cmd.HeadingCmd - o.Psi);
            cmd.VCmd = InterceptEngagement.CapDesiredGs(
                t.V - wBack, t.V, rangeT, L, InterceptSituation.Overtaking);
            cmd.TInt = 0.0;
            cmd.AimE = sE; cmd.AimN = sN; cmd.AimU = sU;
            cmd.PsiDotFF = 0.0;
            return cmd;
        }

        if (dh >= x.Zone)
        {
            // Fuera de la zona de estacion se guia en INERCIAL: persecucion con
            // adelanto hacia la estacion PREDICHA, con el cierre racionado sobre
            // la distancia total. Separar eje/lateral en el marco del blanco
            // falla si este gira: un interceptor a 1.8 km de lado ve su "a" barrer
            // omega*c (94 m/s a 3 deg/s) y se pasa de largo por el eje.
            AimPoint(t, x, ModeStation, 1, 0.0, 0.0, out double s0E, out double s0N, out _);
            double lx = s0E - o.E, ly = s0N - o.N;
            double ld = Math.Max(Math.Sqrt(lx * lx + ly * ly), 1.0);
            double w0 = ((o.V * Math.Sin(o.Psi) - vtE) * lx + (o.V * Math.Cos(o.Psi) - vtN) * ly) / ld;
            double dBrk = Math.Max(ld - Math.Max(w0, 0.0) * L.SpeedLagSec, 0.0);
            // CaptureLaw: cierre = min(freno, cap por rango). NUNCA FormationCap
            // suelto + Vmax: el techo es Vtgt + MaxClosure(range).
            InterceptSituation formSit = sit == InterceptSituation.SternFar
                ? InterceptSituation.SternNear : sit;
            if (formSit is not (InterceptSituation.SternNear or InterceptSituation.SternFar))
                formSit = InterceptSituation.SternNear;
            double wF = Math.Min(Math.Sqrt(2.0 * BrakeF * Decel(L, 0.5 * (o.V + t.V)) * dBrk),
                                 Math.Min(ld * FormationCloseGain,
                                          InterceptEngagement.MaxClosureMps(rangeT, formSit)));
            double vCmdF = InterceptEngagement.CapDesiredGs(
                t.V + wF, t.V, rangeT, L, formSit);
            double tI = SolveT(o, t, x, ModeStation, 1, 0.0, 0.5 * (o.V + vCmdF),
                               out double aE, out double aN, out double aU);
            cmd.HeadingCmd = Math.Atan2(aE - o.E, aN - o.N);
            cmd.TurnErr = ForcedSideErr(WrapPi(cmd.HeadingCmd - o.Psi), mem);
            cmd.VCmd = vCmdF;
            cmd.TInt = tI;
            cmd.AimE = aE; cmd.AimN = aN; cmd.AimU = aU;
            cmd.PsiDotFF = 0.0;
            return cmd;
        }

        // Cierre a lo largo del eje: proporcional a la distancia, sin pedir mas
        // de lo que la deceleracion real (y el retardo) permiten frenar en lo que
        // queda, y con tope bajo (StationTrim / MaxClosure).
        double dA = -a;
        double moving = dA >= 0.0 ? Math.Max(vRel, 0.0) : Math.Max(-vRel, 0.0);
        double dEff = Math.Max(Math.Abs(dA) - moving * L.SpeedLagSec, 0.0);
        double wBrake = Math.Sqrt(2.0 * BrakeF * Decel(L, 0.5 * (o.V + t.V)) * dEff);
        double cap = cmd.Regime == InterceptRegime.Station
            ? StationTrimMps
            : Math.Min(FormationCapMps, InterceptEngagement.MaxClosureMps(rangeT, InterceptSituation.SternNear));
        double wa = Math.Clamp(Math.Sign(dA) * Math.Min(Math.Abs(dA) * AlongGain, wBrake), -cap, cap);
        double wc = Math.Clamp(-c * CrossGain, -cap * 0.7, cap * 0.7);

        double wE = wa * fE + wc * rE, wN = wa * fN + wc * rN;
        // Techo de |v| = Vtgt + cierre de situacion (no Vmax del avion).
        double vCeil = Math.Min(L.VmaxGsMps,
            t.V + InterceptEngagement.MaxClosureMps(rangeT, InterceptSituation.SternNear));
        RelToOwn(vtE, vtN, wE, wN, L.VminGsMps, vCeil, fE, fN, out double ownE, out double ownN);
        cmd.VCmd = InterceptEngagement.CapDesiredGs(
            Math.Sqrt(ownE * ownE + ownN * ownN), t.V, rangeT, L, InterceptSituation.SternNear);
        cmd.HeadingCmd = cmd.VCmd < 1e-3 ? o.Psi : Math.Atan2(ownE, ownN);
        cmd.TurnErr = ForcedSideErr(WrapPi(cmd.HeadingCmd - o.Psi), mem);
        cmd.AimE = sE; cmd.AimN = sN; cmd.AimU = sU;
        // Feed-forward: termino de blanco maniobrante (a poca distancia).
        cmd.PsiDotFF = dh < 4000.0 ? t.Omega : 0.0;
        return cmd;
    }

    // Rumbo muy opuesto al actual: se gira por el lado que ya se estaba
    // girando, no por el ruido del signo del error.
    private static double ForcedSideErr(double err, PlannerMemory mem)
    {
        if (Math.Abs(err) <= ForceSideRad) return err;
        int dir = Math.Abs(mem.OutRateRadPerS) > 0.005 ? Math.Sign(mem.OutRateRadPerS) : Math.Sign(err);
        return dir * Math.Abs(err);
    }

    // ==========================================================================
    // Geometria
    // ==========================================================================
    // Posicion y rumbo del blanco dentro de tau segundos: recto, o en arco si
    // gira (con el giro acumulado acotado: a partir de ahi se supone recto).
    private static void Predict(in TgtS t, double tau, out double e, out double n, out double trk)
    {
        double om = t.Omega;
        if (Math.Abs(om) < 0.2 * Deg || t.V < 1.0)
        {
            trk = t.Track;
            e = t.E + t.V * Math.Sin(trk) * tau;
            n = t.N + t.V * Math.Cos(trk) * tau;
            return;
        }
        double tArc = Math.Clamp(tau, -MaxPredTurnRad / Math.Abs(om), MaxPredTurnRad / Math.Abs(om));
        double tEnd = t.Track + om * tArc;
        e = t.E + t.V / om * (Math.Cos(t.Track) - Math.Cos(tEnd)) + t.V * Math.Sin(tEnd) * (tau - tArc);
        n = t.N + t.V / om * (Math.Sin(tEnd) - Math.Sin(t.Track)) + t.V * Math.Cos(tEnd) * (tau - tArc);
        trk = tEnd;
    }

    private static void StationAt(in TgtS t, in Ctx x, double tau,
                                  out double sE, out double sN, out double sU)
    {
        Predict(t, tau, out double te, out double tn, out double trk);
        double h = trk + t.HeadOff;
        double fE = Math.Sin(h), fN = Math.Cos(h), rE = Math.Cos(h), rN = -Math.Sin(h);
        sE = te - x.Aft * fE + x.Right * rE;
        sN = tn - x.Aft * fN + x.Right * rN;
        sU = t.U + t.Vy * Math.Max(tau, 0.0) + x.Up;
    }

    private static void GetStationPoint(in TgtS t, in Ctx x, out double sE, out double sN, out double sU)
    {
        double h = t.Track + t.HeadOff;
        double fE = Math.Sin(h), fN = Math.Cos(h), rE = Math.Cos(h), rN = -Math.Sin(h);
        sE = t.E - x.Aft * fE + x.Right * rE;
        sN = t.N - x.Aft * fN + x.Right * rN;
        sU = t.U + x.Up;
    }

    private static void GetRel(in OwnS o, in TgtS t, in Ctx x,
                               out double a, out double c, out double dh, out double dU)
    {
        GetStationPoint(t, x, out double sE, out double sN, out double sU);
        double h = t.Track + t.HeadOff;
        double fE = Math.Sin(h), fN = Math.Cos(h), rE = Math.Cos(h), rN = -Math.Sin(h);
        double relE = o.E - sE, relN = o.N - sN;
        a = relE * fE + relN * fN;
        c = relE * rE + relN * rN;
        dh = Math.Sqrt(relE * relE + relN * relN);
        dU = sU - o.U;
    }

    // Velocidad propia (ENU) que da la relativa deseada w sobre el blanco, con
    // |v| recortada a [vmin, vmax]: si hay que recortar, se conserva la DIRECCION
    // relativa y se resuelve la cuadratica |vt + lam*u| = v (triangulo de
    // colision).
    private static void RelToOwn(double vtE, double vtN, double wE, double wN,
                                 double vmin, double vmax, double fbE, double fbN,
                                 out double oE, out double oN)
    {
        oE = vtE + wE; oN = vtN + wN;
        double sp = Math.Sqrt(oE * oE + oN * oN);
        if (sp >= vmin && sp <= vmax) return;
        double target = sp > vmax ? vmax : vmin;
        double wm = Math.Sqrt(wE * wE + wN * wN);
        double ux = wm < 1e-6 ? fbE : wE / wm, uy = wm < 1e-6 ? fbN : wN / wm;
        double b = vtE * ux + vtN * uy;
        double disc = b * b + target * target - (vtE * vtE + vtN * vtN);
        double lam = double.NaN;
        if (disc >= 0.0)
        {
            // Dos raices posibles cuando se recorta a vmin (< |vt|) con w hacia
            // atras: se toma la MENOR no negativa (la velocidad propia mas
            // cercana a la del blanco); la otra da un vuelo hacia atras.
            double sq = Math.Sqrt(disc);
            lam = -b - sq >= 0.0 ? -b - sq : -b + sq;
        }
        if (double.IsNaN(lam) || (lam < 0.0 && sp > 1e-6))
        {
            // Sin solucion en esa direccion: se conserva el rumbo y se escala.
            double s = sp < 1e-6 ? 1.0 : target / sp;
            oE = sp < 1e-6 ? fbE * target : oE * s;
            oN = sp < 1e-6 ? fbN * target : oN * s;
            return;
        }
        oE = vtE + lam * ux; oN = vtN + lam * uy;
    }

    private static double WrapPi(double r)
    {
        r %= 2.0 * Math.PI;
        if (r > Math.PI) r -= 2.0 * Math.PI;
        else if (r < -Math.PI) r += 2.0 * Math.PI;
        return r;
    }

    private static double NormDeg(double d)
    {
        d %= 360.0;
        return d < 0.0 ? d + 360.0 : d;
    }
}
