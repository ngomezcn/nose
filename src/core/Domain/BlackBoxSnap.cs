using System.Globalization;
using System.Text;

namespace AICopilotCore.Domain;

// Texto compacto de contexto para las marcas de la caja negra. Va pegado a
// Inicio/Fin/Fase para que, al mirar el grafico o el CSV, se sepa en que
// estado estaba el avion (y, si aplica, el blanco / el plan) en ese instante.
public static class BlackBoxSnap
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Of(in FlightState s)
    {
        return $"IAS={F(s.IasKt, "0")} ALT={F(s.AltFt, "0")} AGL={F(s.AglFt, "0")} " +
               $"VS={F(s.VsFpm, "0")} pitch={F(s.PitchDeg, "0.0")} bank={F(s.BankDeg, "0.0")} " +
               $"hdg={F(s.HeadingDeg, "0")} G={F(s.GNormal, "0.00")} AoA={F(s.AoaDeg, "0.0")} " +
               $"Q={F(s.PitchRateDegPerSec, "0.0")} P={F(s.RollRateDegPerSec, "0.0")} " +
               $"Mach={F(s.MachNo, "0.00")}";
    }

    // Variante corta cuando el mensaje ya lleva otra info y no cabe todo.
    public static string Short(in FlightState s) =>
        $"IAS={F(s.IasKt, "0")} ALT={F(s.AltFt, "0")} AGL={F(s.AglFt, "0")} " +
        $"G={F(s.GNormal, "0.00")} bank={F(s.BankDeg, "0.0")} pitch={F(s.PitchDeg, "0.0")}";

    public static string Join(params string?[] parts)
    {
        var sb = new StringBuilder();
        foreach (string? p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(p);
        }
        return sb.ToString();
    }

    private static string F(float v, string fmt) =>
        float.IsNaN(v) ? "--" : v.ToString(fmt, Inv);

    public static string F(double v, string fmt) =>
        double.IsNaN(v) ? "--" : v.ToString(fmt, Inv);
}
