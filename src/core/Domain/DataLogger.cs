using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace AICopilotCore.Domain;

// Caja negra de vuelo: registro continuo de muestras para diagnosticar
// temblores / oscilaciones y para pintar graficos (ver BlackBoxView).
// No es logica de control -- no lee ni escribe datarefs por su cuenta, ni
// decide nada del vuelo -- solo bufferiza lo que ShellWindow.Refresh() ya
// calcula a ~10Hz y lo deja en tres sitios:
//
//   1. Buffer tipado (TypedSamples) para la pestana de graficos del viewport.
//   2. Buffer de lineas CSV (Samples) para la pestana "CAJA NEGRA" del panel
//      izquierdo.
//   3. Fichero en disco junto al .exe (LogFilePath), para sesiones futuras.
//
// Formato CSV: ';' y numeros en InvariantCulture (punto decimal fijo).
public sealed class DataLogger
{
    // ~5 minutos a 10Hz. Suficiente para acorralar un temblor / revisar una
    // maniobra sin comerse memoria si el core se deja abierto horas.
    public const int MaxSamples = 3000;

    private const long MaxFileBytes = 8 * 1024 * 1024;

    private const string Header =
        "Hora;Fase;Accion;Nota;IAS_kt;ALT_ft;AGL_ft;VS_fpm;" +
        "PitchObj_deg;PitchReal_deg;BankObj_deg;BankReal_deg;" +
        "RumboObj_deg;RumboReal_deg;G;" +
        "ThrottleObj_pct;ThrottleReal_pct;FlapsObj_pct;FlapsReal_pct;" +
        "MandoPitch;MandoRoll;MandoYaw;" +
        "G_obj;G_pred;AoA_deg;Aerofrenos_pct;Peso_lb;Mach;" +
        "PitchRate_dps;RollRate_dps;Adaptacion;Proteccion";

    public static readonly string LogFilePath =
        Path.Combine(AppContext.BaseDirectory, "DataLog.csv");

    private static readonly string PreviousLogFilePath =
        Path.Combine(AppContext.BaseDirectory, "DataLog.previous.csv");

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<BlackBoxSample> _typed = new(MaxSamples);
    private readonly List<BlackBoxMarker> _markers = new(256);

    public ObservableCollection<string> Samples { get; } = new();
    public IReadOnlyList<BlackBoxSample> TypedSamples => _typed;
    public IReadOnlyList<BlackBoxMarker> Markers => _markers;

    private StreamWriter? _writer;

    public DataLogger()
    {
        try
        {
            var info = new FileInfo(LogFilePath);
            if (info.Exists && info.Length > Header.Length + 4)
                File.Copy(LogFilePath, PreviousLogFilePath, overwrite: true);
        }
        catch (IOException)
        {
        }
        OpenWriter();
    }

    public int Count => Samples.Count;

    public void Record(
        string phase,
        string action,
        float iasKt, float altFt, float aglFt, float vsFpm,
        float pitchTargetDeg, float pitchRealDeg,
        float bankTargetDeg, float bankRealDeg,
        float headingTargetDeg, float headingRealDeg,
        float gNormal,
        float throttleTarget01, float throttleReal01,
        float flapsTarget01, float flapsReal01,
        float pitchCmd, float rollCmd, float yawCmd,
        float gCommand, float gPredicted, float aoaDeg, float speedbrake01,
        float weightLb, float mach,
        float pitchRateDps, float rollRateDps,
        string adaptation, string protection)
    {
        double tSec = _clock.Elapsed.TotalSeconds;
        _typed.Add(new BlackBoxSample(
            tSec,
            iasKt, altFt, aglFt, vsFpm,
            pitchTargetDeg, pitchRealDeg,
            bankTargetDeg, bankRealDeg,
            headingTargetDeg, headingRealDeg,
            gNormal, gCommand, gPredicted, aoaDeg,
            pitchCmd, rollCmd, yawCmd,
            throttleTarget01, throttleReal01,
            flapsTarget01, flapsReal01,
            speedbrake01, weightLb, mach,
            pitchRateDps, rollRateDps));
        while (_typed.Count > MaxSamples) _typed.RemoveAt(0);
        PruneMarkers();

        string line = string.Join(';', new[]
        {
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Safe(phase), Safe(action), "",
            F(iasKt, "0.0"), F(altFt, "0"), F(aglFt, "0"), F(vsFpm, "0"),
            F(pitchTargetDeg, "0.0"), F(pitchRealDeg, "0.0"),
            F(bankTargetDeg, "0.0"), F(bankRealDeg, "0.0"),
            F(headingTargetDeg, "0.0"), F(headingRealDeg, "0.0"),
            F(gNormal, "0.00"),
            F(throttleTarget01 * 100f, "0.0"), F(throttleReal01 * 100f, "0.0"),
            F(flapsTarget01 * 100f, "0.0"), F(flapsReal01 * 100f, "0.0"),
            F(pitchCmd, "0.000"), F(rollCmd, "0.000"), F(yawCmd, "0.000"),
            F(gCommand, "0.00"), F(gPredicted, "0.00"), F(aoaDeg, "0.0"),
            F(speedbrake01 * 100f, "0"), F(weightLb, "0"), F(mach, "0.000"),
            F(pitchRateDps, "0.0"), F(rollRateDps, "0.0"),
            Safe(adaptation), Safe(protection),
        });

        AddLine(line);
    }

    // Nota en el CSV (columna Nota). Si chartMarker, ademas queda en Markers
    // para pintar lineas verticales en la pestana de graficos. Las acciones
    // de vuelo (maniobra / intercept / despegue) pasan chartMarker=true;
    // mensajes del docker o de config solo van al CSV/log de texto.
    public void RecordEvent(string note, bool chartMarker = false)
    {
        if (chartMarker && note.Length > 0)
        {
            _markers.Add(new BlackBoxMarker(_clock.Elapsed.TotalSeconds, note));
            PruneMarkers();
        }

        string line = string.Join(';', new[]
        {
            DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            "", "", Safe(note),
            "", "", "", "",
            "", "", "", "",
            "", "", "",
            "", "", "", "",
            "", "", "",
            "", "", "", "", "", "", "", "", "", "",
        });
        AddLine(line);
    }

    private void AddLine(string line)
    {
        Samples.Add(line);
        while (Samples.Count > MaxSamples) Samples.RemoveAt(0);
        WriteToFile(line);
    }

    public void Clear()
    {
        Samples.Clear();
        _typed.Clear();
        _markers.Clear();
        CloseWriter();
        TryDelete(LogFilePath);
        TryDelete(PreviousLogFilePath);
        OpenWriter();
    }

    // Las marcas que quedan detras del buffer tipado ya no se ven en el
    // grafico: se tiran para no crecer sin limite en sesiones largas.
    private void PruneMarkers()
    {
        if (_typed.Count == 0)
        {
            while (_markers.Count > 200) _markers.RemoveAt(0);
            return;
        }
        double tMin = _typed[0].TSec;
        int i = 0;
        while (i < _markers.Count && _markers[i].TSec < tMin) i++;
        if (i > 0) _markers.RemoveRange(0, i);
    }

    public void Close() => CloseWriter();

    private void WriteToFile(string line)
    {
        if (_writer is null) return;
        try
        {
            if (_writer.BaseStream.Length > MaxFileBytes) RotateFile();
            _writer.WriteLine(line);
        }
        catch (IOException)
        {
        }
    }

    private void RotateFile()
    {
        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            File.Copy(LogFilePath, PreviousLogFilePath, overwrite: true);
        }
        catch (IOException)
        {
        }
        OpenWriter();
    }

    private void OpenWriter()
    {
        try
        {
            _writer = new StreamWriter(LogFilePath, append: false)
            {
                AutoFlush = true,
            };
            _writer.WriteLine(Header);
        }
        catch (IOException)
        {
            _writer = null;
        }
    }

    private void CloseWriter()
    {
        try { _writer?.Flush(); _writer?.Dispose(); }
        catch (IOException) { }
        _writer = null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private static string Safe(string s) => s.Replace(';', ',');

    private static string F(float v, string format) =>
        float.IsNaN(v) ? "" : v.ToString(format, CultureInfo.InvariantCulture);
}
