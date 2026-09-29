using System.Text;

namespace AICopilotCore.Domain;

// Hasta donde ha habido que tocar lo que se pidio.
public enum AdaptationLevel
{
    // Sale tal cual: el avion esta donde la maniobra esperaba encontrarlo.
    AsRequested,
    // Sale, pero distinta: menos alabeo, tiron mas suave, arco mas abierto,
    // otra gestion de gases. El texto dice que y por que.
    Adapted,
    // No existe ninguna version segura de esta maniobra aqui y ahora (un
    // Split-S con menos altura de la que se pierde tirando a tope). Es el unico
    // caso en el que se dice que no, y es raro.
    Impossible,
}

// Lo que el planificador decide para ESTE frame: la maniobra del catalogo ya
// traducida al estado real del avion. El ejecutor (ManeuverSequence) no vuelve
// a decidir nada -- coge estos numeros y los persigue.
//
// La diferencia con el catalogo: ManeuverDefinition dice lo que se PIDE
// ("picado pronunciado, gas alto"), ManeuverPlan dice lo que se HACE aqui y
// ahora ("-22 deg porque a 3.200 ft AGL no hay altura para salir de -40, y el
// morro baja a 4 deg/s como mucho para no pasar de 0.3 g"). Cuando el estado
// cambia -- se gana altura, se gana velocidad -- el plan del frame siguiente
// vuelve a acercarse solo a lo pedido: la adaptacion no es una decision que se
// toma al pulsar el boton, se recalcula sesenta veces por segundo.
public readonly record struct ManeuverPlan(
    // Objetivo de MORRO ya resuelto (trayectoria pedida + angulo de ataque que
    // hace falta a esta velocidad, peso y alabeo).
    float PitchTargetDeg,
    float BankTargetDeg,
    // Ritmo maximo del objetivo de alabeo. El del cabeceo no viene de aqui:
    // sale del presupuesto de G en CommandLimits, que es donde se puede mirar
    // la G real del frame.
    float BankRateDegPerSec,
    // Mando de motor. Si SpeedTargetKt no es NaN manda el autothrottle; si lo
    // es, se manda ThrottleTarget tal cual. ThrottleFeedForward es la posicion
    // de palanca que el modelo dice que hace falta para sostener esto, y sirve
    // para enganchar el autothrottle sin que el PID tenga que integrarlo todo.
    float ThrottleTarget,
    float SpeedTargetKt,
    float ThrottleFeedForward,
    float SpeedbrakeTarget,
    // Trayectoria y V/S objetivo, para la pantalla y el CSV.
    float FlightPathTargetDeg,
    float VsTargetFpm,
    // Presupuesto de G de este frame: lo usa la proteccion continua para
    // limitar el ritmo del objetivo antes de que la G suba.
    float GBudgetMax,
    float GBudgetMin,
    // G que la maniobra quiere estar dando (acrobacias y virajes coordinados).
    float LoadFactorCmd,
    AdaptationLevel Level,
    // Que se ha cambiado respecto a lo pedido, en texto corto para el log, la
    // UI y el CSV. Vacio = sale tal cual se pidio.
    string Adaptation)
{
    public bool IsAdapted => Level != AdaptationLevel.AsRequested;
    public bool HoldsSpeed => !float.IsNaN(SpeedTargetKt);
}

// Acumulador de motivos de adaptacion. Existe para que el planificador vaya
// anotando ("alabeo 75->63 deg por sustentacion", "gas a ralenti: VNE cerca")
// sin construir cadenas a mano, y para que el ejecutor pueda comparar el texto
// de un frame con el del anterior y loguear solo cuando CAMBIA -- si no, un
// motivo permanente llenaria el log sesenta veces por segundo.
public sealed class AdaptationNotes
{
    private readonly StringBuilder _sb = new();

    public void Add(string note)
    {
        if (note.Length == 0) return;
        if (_sb.Length > 0) _sb.Append("; ");
        _sb.Append(note);
    }

    // Solo anota si hubo un cambio apreciable: sin esto, el ruido de un dataref
    // dejaria el texto parpadeando entre "60->59.9" y "60->60.0" cada frame.
    public void AddIfChanged(string what, float requested, float applied, string unit, string why,
                             float tolerance = 1f)
    {
        if (MathF.Abs(requested - applied) <= tolerance) return;
        Add($"{what} {requested:0.#}->{applied:0.#} {unit} ({why})");
    }

    public override string ToString() => _sb.ToString();
    public bool IsEmpty => _sb.Length == 0;
    public void Clear() => _sb.Clear();
}
