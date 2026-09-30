using System.IO;
using System.Globalization;
using System.Text;

namespace AICopilotCore.Domain;

// Traza de diagnostico del intercept: una fila por frame con las posiciones y
// velocidades de propio y blanco (marco local, m y m/s), la decision del
// planificador y las ordenes emitidas. No decide nada; se abre al empezar un
// intercept y se sobrescribe en el siguiente (InterceptTrace.csv junto al
// exe). Sirve para reproducir y analizar el vuelo real sin simulador aparte.
public sealed class InterceptTrace : IDisposable
{
    public static readonly string PathCsv =
        System.IO.Path.Combine(AppContext.BaseDirectory, "InterceptTrace.csv");

    private StreamWriter? _w;
    private double _t;

    public void Begin()
    {
        Dispose();
        try
        {
            _w = new StreamWriter(PathCsv, false, new UTF8Encoding(false)) { AutoFlush = true };
            _w.WriteLine("t;stage;regime;phase;detour;side;sideLocked;corridor;ox;oy;oz;ovx;ovy;ovz;tx;ty;tz;tvx;tvy;tvz;" +
                         "thdg;tturn;range;rpRange;along;cross;up;ownIas;ownBank;ownG;" +
                         "desTrk;filtTrk;desGs;desIas;iasCmd;vsCmd;bankCmd;minSep;eta");
        }
        catch { _w = null; }
        _t = 0;
    }

    public void Row(float dt, params object[] cols)
    {
        if (_w == null) return;
        _t += dt;
        var sb = new StringBuilder();
        sb.Append(_t.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (object c in cols)
        {
            sb.Append(';');
            sb.Append(c is double d ? d.ToString("0.###", CultureInfo.InvariantCulture)
                    : c is float f ? f.ToString("0.###", CultureInfo.InvariantCulture)
                    : Convert.ToString(c, CultureInfo.InvariantCulture));
        }
        try { _w.WriteLine(sb.ToString()); } catch { }
    }

    public void Dispose() { try { _w?.Dispose(); } catch { } _w = null; }
}
