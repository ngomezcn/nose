using System.Linq;
using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

// Todo lo que la interceptacion necesita SABER, separado de lo que hace:
// donde esta el blanco, si vuela o sigue en tierra, y en que punto del
// espacio hay que ponerse respecto a el. El lazo de control que lleva el
// avion hasta ahi vive en InterceptSequence.cs.
//
// -- El marco de referencia, que es la decision que lo ordena todo --------
//
// Se trabaja en coordenadas locales de X-Plane (las "OpenGL local coords"):
// +X al este, +Y arriba, +Z al SUR, en metros. El avion del usuario
// (sim/flightmodel/position/local_*) y las IAs
// (sim/multiplayer/position/planeN_x|y|z) estan en el MISMO marco, asi que
// restar dos posiciones da metros directos sin pasar por lat/lon: ni
// trigonometria esferica, ni convergencia de meridianos, ni dos altimetros
// con subescalas distintas. El marco se mueve con el mundo (X-Plane
// recoloca el origen cuando te alejas), pero eso da igual: lo que se usa
// siempre es la DIFERENCIA entre dos posiciones leidas en el mismo frame.
//
// Ojo con el eje Z: apunta al SUR, asi que la componente norte de un vector
// es -Z. Es el error facil de cometer aqui, y sale como una interceptacion
// que se coloca al otro lado del blanco.

// Donde ponerse respecto al blanco. "Cola alta" es la de por defecto: es la
// posicion de interceptacion real -- detras y algo por encima se ve al otro
// avion entero recortado contra el suelo, la separacion se controla con el
// gas, y no se vuela dentro de su estela.
public enum InterceptStation
{
    TailHigh,
    ParallelRight,
    ParallelLeft,
    Above,
    Below,
}

// Una posicion del catalogo. Los factores son solo el SIGNO / aplicabilidad
// (lado derecho = +1, izquierdo = -1, por encima = +1, por debajo = -1, 0 =
// no aplica): los modulos de las distancias son por puesto y se editan en
// ControlTuning (GetStationOffsets / SetStationOffsets).
//
// Los ejes son los del BLANCO, no los nuestros: Aft = metros por detras de
// su cola, Right = metros a su derecha, Up = metros por encima.
public sealed record InterceptStationDef(
    InterceptStation Id,
    string Label,
    string Description,
    float AftFactor,
    float RightFactor,
    float UpFactor)
{
    public bool HasLateral => RightFactor != 0f;
    public bool HasVertical => UpFactor != 0f;

    // "200 m detras / 40 m por encima": lo que se pinta debajo de los botones
    // para que el numero y lo que va a hacer el avion se vean juntos.
    public static string Summary(float aft, float right, float up)
    {
        var parts = new List<string>();
        if (MathF.Abs(aft) >= 1f) parts.Add($"{aft:0} m detras");
        if (MathF.Abs(right) >= 1f)
            parts.Add($"{MathF.Abs(right):0} m a la {(right > 0f ? "derecha" : "izquierda")}");
        if (MathF.Abs(up) >= 1f)
            parts.Add($"{MathF.Abs(up):0} m por {(up > 0f ? "encima" : "debajo")}");
        return parts.Count == 0 ? "pegado al blanco" : string.Join(" / ", parts);
    }
}

public static class InterceptCatalog
{
    // Los factores estan elegidos para que NINGUNA posicion se quede mas
    // atras que la distancia pedida (ese era el requisito: "no mas de 200 m
    // desde atras") y para que las de al lado queden lo bastante adelantadas
    // como para verse desde la cabina del otro -- que es de lo que va una
    // interceptacion militar: que te vean.
    public static readonly IReadOnlyList<InterceptStationDef> All = new[]
    {
        new InterceptStationDef(InterceptStation.TailHigh, "Cola alta",
            "Detras y un poco por encima: la posicion de interceptacion clasica. " +
            "Se ve al blanco entero, la separacion se controla con el gas y no se " +
            "vuela dentro de su estela.",
            AftFactor: 1f, RightFactor: 0f, UpFactor: 1f),

        new InterceptStationDef(InterceptStation.ParallelRight, "Paralelo derecha",
            "Al costado derecho del blanco, ligeramente atrasado: formacion de ala, " +
            "para que su piloto nos vea sin tener que girar del todo la cabeza.",
            AftFactor: 1f, RightFactor: 1f, UpFactor: 0f),

        new InterceptStationDef(InterceptStation.ParallelLeft, "Paralelo izquierda",
            "Al costado izquierdo del blanco, ligeramente atrasado: la formacion de " +
            "interceptacion estandar (al interceptado se le señala por su izquierda).",
            AftFactor: 1f, RightFactor: -1f, UpFactor: 0f),

        new InterceptStationDef(InterceptStation.Above, "Arriba",
            "Por encima y algo atras: domina la geometria y deja sitio para picar " +
            "si el blanco maniobra.",
            AftFactor: 1f, RightFactor: 0f, UpFactor: 1f),

        new InterceptStationDef(InterceptStation.Below, "Abajo",
            "Por debajo y algo atras: la posicion desde la que se inspecciona el " +
            "vientre del otro avion (cargas externas, tren, daños).",
            AftFactor: 1f, RightFactor: 0f, UpFactor: -1f),
    };

    private static readonly Dictionary<InterceptStation, InterceptStationDef> ByKind =
        All.ToDictionary(s => s.Id);

    public static InterceptStationDef Get(InterceptStation id) => ByKind[id];
}

// Si el blanco esta volando o no. Unknown existe a proposito: mientras no
// haya telemetria suficiente, "no lo se" y "esta en tierra" no son lo mismo
// -- el primero no debe disparar una persecucion, pero tampoco darla por
// imposible.
public enum TargetAirState
{
    Unknown,
    OnGround,
    Airborne,
}

// Foto de un avion de la partida en un instante, ya en unidades utiles.
// Inmutable y capturada de una vez por frame, por el mismo motivo que
// FlightState: que el lazo de control no pueda leer la posicion a mitad de
// un calculo y encontrarsela cambiada.
public readonly record struct TargetSnapshot(
    bool Valid,
    int XplmIndex,
    double X, double Y, double Z,        // posicion local OGL (m)
    double Vx, double Vy, double Vz,     // velocidad local OGL (m/s)
    float HeadingDeg,                    // psi verdadero
    float GearRatio,                     // 0 = recogido, 1 = fuera
    double ElevationM)                   // MSL, solo para pintar
{
    public const double MpsToKnots = 1.943844;
    public const double MpsToFpm = 196.8504;
    public const double MetersToFeet = 3.28084;
    public const double MetersToNm = 1.0 / 1852.0;

    // XPLM numera al usuario como 0 y las IAs desde 1; los arrays de
    // Datarefs.Other* empiezan en plane1, o sea en el indice 0.
    // Kinematics (ownship o cualquier body) -> foto del blanco con su indice.
    public static TargetSnapshot FromKinematics(int xplmIndex, in Agents.Kinematics k) =>
        new(Valid: true, XplmIndex: xplmIndex,
            X: k.X, Y: k.Y, Z: k.Z, Vx: k.Vx, Vy: k.Vy, Vz: k.Vz,
            HeadingDeg: Pid.NormalizeAngleDeg360(k.HeadingDeg),
            GearRatio: k.GearRatio, ElevationM: k.ElevationM);

    // Cualquier indice XPLM 0..19: 0 = ownship (local_*), 1..19 = IA.
    public static TargetSnapshot Capture(Datarefs d, int xplmIndex)
    {
        if (xplmIndex == 0)
        {
            if (!d.LocalX.HasValue || !d.LocalY.HasValue || !d.LocalZ.HasValue) return default;
            FlightState own = FlightState.Capture(d);
            return FromKinematics(0, new Agents.Kinematics(
                d.LocalX.Value, d.LocalY.Value, d.LocalZ.Value,
                d.LocalVx.Value, own.VsFpm / MpsToFpm, d.LocalVz.Value,
                own.HeadingDeg, d.GearHandleDown.Float, own.AltFt / MetersToFeet));
        }

        int slot = xplmIndex - 1;
        if (slot < 0 || slot >= Datarefs.OtherPlaneSlots) return default;

        DataHandle x = d.OtherLocalX[slot], y = d.OtherLocalY[slot], z = d.OtherLocalZ[slot];
        DataHandle psi = d.OtherHeadingDeg[slot];
        if (!x.HasValue || !y.HasValue || !z.HasValue || !psi.HasValue) return default;

        return new TargetSnapshot(
            Valid: true,
            XplmIndex: xplmIndex,
            X: x.Value, Y: y.Value, Z: z.Value,
            Vx: d.OtherVelX[slot].Value,
            Vy: d.OtherVelY[slot].Value,
            Vz: d.OtherVelZ[slot].Value,
            HeadingDeg: Pid.NormalizeAngleDeg360((float)psi.Value),
            GearRatio: d.OtherGearRatio[slot].Float,
            ElevationM: d.OtherElevMeters[slot].Value);
    }

    public double GroundSpeedMps => Math.Sqrt(Vx * Vx + Vz * Vz);
    public double GroundSpeedKt => GroundSpeedMps * MpsToKnots;
    public double VerticalSpeedFpm => Vy * MpsToFpm;

    // Rumbo del VECTOR VELOCIDAD sobre el suelo. Con viento no coincide con
    // HeadingDeg (el morro va cruzado): para moverse CON el blanco manda
    // este, para colocarse a su lado manda el morro. Se usan los dos, cada
    // uno donde toca (ver InterceptSequence).
    public float TrackDeg => GroundSpeedMps < 2.0
        ? HeadingDeg
        : Pid.NormalizeAngleDeg360((float)(Math.Atan2(Vx, -Vz) * 180.0 / Math.PI));

    // Volando o en tierra. No hay un dataref "onground" por IA, asi que se
    // decide con lo que si hay:
    //
    //   - Velocidad sobre el suelo muy baja y sin ritmo vertical: parado o
    //     rodando. Ningun avion se sostiene a 40 kt de suelo con aire en calma.
    //   - Casi pegado al terreno, sin ritmo vertical y por debajo de la
    //     velocidad de rotacion: carrera de despegue o de aterrizaje, que a
    //     efectos de "todavia no lo puedo interceptar" cuenta como tierra.
    //
    // terrainYM es la Y local del terreno, que el llamante estima con su
    // propia posicion (Y propia menos su AGL). Esa estimacion vale mientras
    // el blanco este razonablemente cerca -- a 200 km puede haber una sierra
    // en medio -- asi que la regla del terreno solo se aplica cuando lo esta;
    // de lejos manda la velocidad, que no depende del relieve.
    public TargetAirState Classify(double terrainYM, bool terrainUsable)
    {
        if (!Valid) return TargetAirState.Unknown;

        double gsKt = GroundSpeedKt;
        double absVy = Math.Abs(Vy);
        double heightM = Y - terrainYM;

        if (gsKt < TaxiSpeedKt && absVy < StillVerticalMps) return TargetAirState.OnGround;
        if (terrainUsable && heightM < GroundHeightM && absVy < RollVerticalMps &&
            gsKt < RotateSpeedKt)
            return TargetAirState.OnGround;
        if (gsKt > FlyingSpeedKt || (terrainUsable && heightM > AirborneHeightM))
            return TargetAirState.Airborne;
        return TargetAirState.Unknown;
    }

    public static string StateName(TargetAirState s) => s switch
    {
        TargetAirState.OnGround => "en tierra",
        TargetAirState.Airborne => "en el aire",
        _ => "sin determinar",
    };

    // Velocidad sobre el suelo por debajo de la cual esta rodando o parado.
    // Deliberadamente baja: un ultraligero con viento en contra puede volar a
    // 45 kt de suelo.
    private const double TaxiSpeedKt = 40.0;
    // Ritmo vertical por debajo del cual no esta subiendo ni bajando.
    private const double StillVerticalMps = 1.5;
    private const double RollVerticalMps = 2.5;
    // Altura sobre el terreno que todavia cuenta como "en la pista".
    private const double GroundHeightM = 25.0;
    // A partir de aqui esta volando seguro, aunque vaya despacio.
    private const double AirborneHeightM = 60.0;
    private const double RotateSpeedKt = 160.0;
    private const double FlyingSpeedKt = 130.0;
}

// La geometria del problema, resuelta una vez por frame y en funciones
// puras: donde esta el punto de formacion, a que distancia estamos de el, y
// como se descompone ese error en "voy retrasado", "voy descentrado" y "voy
// bajo". Separado del lazo de control para poder comprobarlo con numeros a
// mano, sin simulador.
public readonly record struct InterceptGeometry(
    // Punto de formacion en coordenadas locales.
    double Px, double Py, double Pz,
    // Vector de nosotros al punto, en metros: este / norte / arriba.
    double EastM, double NorthM, double UpM,
    // Descompuesto en los ejes del blanco: >0 = el punto esta por delante /
    // a nuestra derecha.
    double AlongM, double CrossM,
    // Rumbo verdadero al que hay que volar para ir directo al punto.
    float BearingDeg,
    // Distancia horizontal y total al punto, y distancia real al AVION
    // (separacion, que es lo que importa para no chocar).
    double HorizontalRangeM, double RangeM, double SeparationM)
{
    public static InterceptGeometry Solve(
        double ownX, double ownY, double ownZ,
        in TargetSnapshot t, float aftM, float rightM, float upM, double leadSeconds)
    {
        // Ejes del blanco en el plano horizontal. Adelante = su morro;
        // derecha = su morro girado 90 deg a estribor. Recordar que el norte
        // es -Z.
        double psi = t.HeadingDeg * Math.PI / 180.0;
        double fwdE = Math.Sin(psi), fwdN = Math.Cos(psi);
        double rgtE = Math.Cos(psi), rgtN = -Math.Sin(psi);

        // Punto de formacion: desde el blanco, hacia atras y a un lado.
        double offE = -aftM * fwdE + rightM * rgtE;
        double offN = -aftM * fwdN + rightM * rgtN;

        // Adonde estara el blanco dentro de leadSeconds. En persecucion esto
        // es lo que convierte una curva de persecucion pura (que siempre
        // llega tarde y por detras) en un rumbo de colision.
        double px = t.X + t.Vx * leadSeconds + offE;
        double py = t.Y + t.Vy * leadSeconds + upM;
        // offN es una componente NORTE y Z apunta al sur: se resta.
        double pz = t.Z + t.Vz * leadSeconds - offN;

        double eE = px - ownX;
        double eU = py - ownY;
        double eN = -(pz - ownZ);

        double along = eE * fwdE + eN * fwdN;
        double cross = eE * rgtE + eN * rgtN;

        double horiz = Math.Sqrt(eE * eE + eN * eN);
        double range = Math.Sqrt(horiz * horiz + eU * eU);

        double sepE = t.X - ownX, sepU = t.Y - ownY, sepZ = t.Z - ownZ;
        double separation = Math.Sqrt(sepE * sepE + sepU * sepU + sepZ * sepZ);

        float bearing = horiz < 1e-6
            ? 0f
            : Pid.NormalizeAngleDeg360((float)(Math.Atan2(eE, eN) * 180.0 / Math.PI));

        return new InterceptGeometry(px, py, pz, eE, eN, eU, along, cross,
                                     bearing, horiz, range, separation);
    }

    // Tiempo aproximado hasta el punto de formacion volando a ownSpeedMps.
    // Se usa para el lead: se resuelve por iteracion porque el punto se mueve
    // mientras vamos hacia el (tres pasadas bastan de sobra; converge rapido
    // salvo persecuciones de cola en las que no hay solucion, y ahi el tope
    // de MaxLeadSeconds es lo que la deja acotada).
    public static double LeadSeconds(double ownX, double ownY, double ownZ,
                                     in TargetSnapshot t, float aftM, float rightM, float upM,
                                     double ownSpeedMps, double maxLeadSeconds)
    {
        double lead = 0.0;
        for (int i = 0; i < 3; i++)
        {
            InterceptGeometry g = Solve(ownX, ownY, ownZ, t, aftM, rightM, upM, lead);
            lead = g.RangeM / Math.Max(ownSpeedMps, 40.0);
            if (lead > maxLeadSeconds) { lead = maxLeadSeconds; break; }
        }
        return lead;
    }
}
