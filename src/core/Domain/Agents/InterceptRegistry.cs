namespace AICopilotCore.Domain.Agents;

// Quien intercepta a quien (interceptor -> objetivo), compartido por todas las
// InterceptSequence de la partida. Un objetivo activo por interceptor.
// Rechaza autointercepcion, el cruce directo (A->B con B->A ya registrado,
// tambien si B->A es solo una intencion pendiente tras despegue) y ciclos
// mas largos (A->B->C->A). Thread-safe.
public sealed class InterceptRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _targetOf = new();

    public static string Name(int idx) => idx == 0 ? "Local (idx 0)" : $"IA {idx}";

    // Registra (o sustituye) el objetivo del interceptor. Idempotente si ya
    // esta registrado el mismo par. Si se rechaza, el registro no cambia.
    public bool TryRegister(int interceptorIdx, int targetIdx, out string refusal)
    {
        lock (_gate)
        {
            if (interceptorIdx == targetIdx)
            {
                refusal = $"{Name(interceptorIdx)} no puede interceptarse a si mismo.";
                return false;
            }

            // B->A cuando A->B ya esta: cruce directo.
            if (_targetOf.TryGetValue(targetIdx, out int theirTarget) && theirTarget == interceptorIdx)
            {
                refusal = $"{Name(targetIdx)} ya esta interceptando a {Name(interceptorIdx)}; " +
                          "dos aviones no pueden interceptarse a la vez. " +
                          $"Aborta la de {Name(targetIdx)} primero.";
                return false;
            }

            // Ciclo largo: seguir la cadena desde el objetivo; si vuelve al
            // interceptor, cerraria un anillo.
            var chain = new List<int> { interceptorIdx, targetIdx };
            int cur = targetIdx;
            for (int guard = 0; guard <= _targetOf.Count && _targetOf.TryGetValue(cur, out int next); guard++)
            {
                chain.Add(next);
                if (next == interceptorIdx)
                {
                    refusal = "Ciclo de intercepciones (" +
                              string.Join(" -> ", chain.Select(Name)) +
                              "); aborta alguna de las intercepciones de la cadena primero.";
                    return false;
                }
                cur = next;
            }

            _targetOf[interceptorIdx] = targetIdx;
            refusal = "";
            return true;
        }
    }

    public void Unregister(int interceptorIdx)
    {
        lock (_gate) _targetOf.Remove(interceptorIdx);
    }

    // Objetivo actual del interceptor, o null si no intercepta a nadie.
    public int? TargetOf(int interceptorIdx)
    {
        lock (_gate) return _targetOf.TryGetValue(interceptorIdx, out int t) ? t : null;
    }

    public IReadOnlyDictionary<int, int> Snapshot()
    {
        lock (_gate) return new Dictionary<int, int>(_targetOf);
    }
}
