using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AICopilotCore.Connector;
using AICopilotCore.Domain;
using AICopilotCore.Domain.Agents;

namespace AICopilotCore.Ui;

// Refresco de telemetria (10 Hz), paneles derechos y caja negra.
//
// Un solo camino para cualquier avion: se lee el AircraftAgent (sus
// secuencias, su AgentView) y el State de su cuerpo. Lo que un avion no
// tiene (NaN) se pinta "—"; los paneles y botones son los mismos para todos.
public partial class ShellWindow {
    private const string Dash = "—";

    // Objetivos de las secuencias de UN avion (NaN = sin objetivo).
    private readonly record struct AgentTargets(
        float Pitch, float Bank, float Ias, float Vs, float Alt, float Heading,
        float Throttle01, float FlapPct, float GCommand, bool GearDownCommanded, bool GearTargetKnown);

    private static AgentTargets ReadTargets(AircraftAgent a) {
        bool intercept = a.Intercept.IsRunning;
        bool maneuver = a.Maneuvers.IsRunning;
        bool takeoff = a.Takeoff.IsRunning;
        const float N = float.NaN;
        return new AgentTargets(
            Pitch: intercept ? a.Intercept.LastPitchTarget : maneuver ? a.Maneuvers.LastPitchTarget
                 : takeoff ? a.Takeoff.LastPitchTarget : N,
            Bank: intercept ? a.Intercept.LastBankTarget : maneuver ? a.Maneuvers.LastBankTarget
                : takeoff ? a.Takeoff.LastBankTarget : N,
            Ias: intercept ? a.Intercept.LastIasTarget : maneuver ? a.Maneuvers.LastIasTarget
               : takeoff ? a.Takeoff.LastIasTarget : N,
            Vs: intercept ? a.Intercept.LastVsTarget : maneuver ? a.Maneuvers.LastVsTarget
              : takeoff ? a.Takeoff.LastVsTarget : N,
            Alt: takeoff ? a.Takeoff.LastAltTarget : N,
            Heading: takeoff ? a.Takeoff.HeadingTargetDeg : intercept ? a.Intercept.LastDesiredTrack : N,
            Throttle01: intercept ? a.Intercept.LastThrottleCmd : maneuver ? a.Maneuvers.LastThrottleCmd
                      : takeoff ? a.Takeoff.LastThrottleCmd : N,
            FlapPct: intercept ? a.Intercept.LastFlapCmd * 100f : takeoff ? a.Takeoff.LastFlapCmd * 100f : N,
            GCommand: maneuver ? a.Maneuvers.LastGCommand : N,
            GearDownCommanded: a.Takeoff.GearDownCommanded,
            GearTargetKnown: takeoff);
    }

    // --- Refresco -------------------------------------------------------------

    private void Refresh() {
        bool connected = _client.IsConnected;
        ConnectionStatus = connected
            ? $"Conectado a {_client.PluginVersion}"
            : "Sin conexion con el plugin";
        ConnectionDotColor = connected ? Brushes.LimeGreen : Brushes.Gray;

        AircraftAgent? focused = _director.Focused;
        if (focused is null) {
            FillGlobalTelemetry(connected);
        } else {
            FillAgentTelemetry(focused, focused.View());
        }

        // Caja negra por avion: alimenta TODOS los loggers con IsRecording,
        // por el mismo camino (State reducido = NaN para una IA observada).
        // Modo GLOBAL: si Empezar esta activo, incluye naves que aparezcan
        // en el roster a mitad de grabacion.
        if (connected && _globalDataLogRecording)
            EnsureGlobalRosterRecording();
        if (connected) {
            foreach ((int xplmIndex, DataLogger log) in _dataLogs.Recording)
                RecordAgentSample(log, _director.World.Get(xplmIndex));
        }

        DataLogger focusedLog = FocusedDataLog;
        DataLogStatusText = FormatDataLogStatus(focusedLog, connected);
        RefreshFocusedBlackBoxCharts();

        RefreshManeuverButtons();
        // Listado + IsFocused siempre: la franja EN FOCO es global y el roster
        // puede cambiar con el A330 del reset aunque no estes en Aviones.
        RefreshAircraftList();
        if (InterceptView.Visibility == Visibility.Visible) {
            RefreshInterceptTargets();
            RefreshInterceptStatus();
        }
    }

    // Panel derecho con foco GLOBAL: se deja montado (misma anchura) pero
    // vacio — no hay avion en foco. Evita resize del hueco de X-Plane.
    private void FillGlobalTelemetry(bool connected)
    {
        _ = connected;
        ModeText = "Vista global";
        FlightText = "";
        AttitudeText = "";
        ControlsText = "";
    }

    // Panel derecho de CUALQUIER avion: objetivos de sus secuencias a la
    // izquierda, lo real (State de su cuerpo) a la derecha. Sin ramas por rol.
    private void FillAgentTelemetry(AircraftAgent agent, AgentView view)
    {
        IAircraftBody body = agent.Body;
        FlightState st = body.State;
        AgentTargets t = ReadTargets(agent);

        // Mandos reales: solo si el cuerpo los tiene (local, o IA simulada).
        bool hasControls = body.Caps.HasAoa;
        float throttleReal = hasControls ? body.ThrottleReadback * 100f : float.NaN;
        float flapsReal = hasControls ? body.FlapRatio * 100f : float.NaN;
        float speedbrakeReal = hasControls ? body.SpeedbrakeRatio * 100f : float.NaN;
        float qnh = body.Caps.HasBarometer ? body.QnhInHg : float.NaN;
        float aglFt = float.IsNaN(body.AglMeters) ? float.NaN : body.AglMeters * MetersToFeet;

        string gearReal = Dash;
        if (hasControls && body.TryGetKinematics(out Kinematics k))
            gearReal = k.GearRatio > 0.5f ? "abajo" : "arriba";
        string gearObj = t.GearTargetKnown ? (t.GearDownCommanded ? "abajo" : "arriba") : Dash;

        ModeText = view.ModeText;

        float thrShown = float.IsNaN(t.Throttle01) ? float.NaN : t.Throttle01 * 100f;
        FlightText =
            Head("obj") + "\n" +
            Row("IAS", t.Ias, st.IasKt, "0", "kt") + "\n" +
            Row("V/S", t.Vs, st.VsFpm, "0", "fpm") + "\n" +
            Row("ALT", t.Alt, st.AltFt, "0", "ft") + "\n" +
            Row("AGL", float.NaN, aglFt, "0", "ft");
        AttitudeText =
            Head("obj") + "\n" +
            Row("Pitch", t.Pitch, st.PitchDeg, "0.0", "deg") + "\n" +
            Row("Bank", t.Bank, st.BankDeg, "0.0", "deg") + "\n" +
            Row("Rumbo", t.Heading, st.HeadingDeg, "0", "deg") + "\n" +
            Row("G", t.GCommand, st.GNormal, "0.0", "g") + "\n" +
            Row("AoA", float.NaN, st.AoaDeg, "0.0", "deg");
        ControlsText =
            Head("mando") + "\n" +
            Row("Gas", thrShown, throttleReal, "0", "%") + "\n" +
            Row("Flaps", t.FlapPct, flapsReal, "0", "%") + "\n" +
            RowText("Tren", gearObj, gearReal, "") + "\n" +
            Row("Aerofr", float.NaN, speedbrakeReal, "0", "%") + "\n" +
            Row("QNH", float.NaN, qnh, "0.00", "inHg");
    }

    // Una muestra de caja negra de un avion (mismo camino para todos). Sin
    // prefijo de avion: cada avion tiene su propio fichero.
    private void RecordAgentSample(DataLogger log, AircraftAgent agent)
    {
        IAircraftBody body = agent.Body;
        FlightState st = body.State;
        AgentView view = agent.View();
        AgentTargets t = ReadTargets(agent);
        bool hasControls = body.Caps.HasAoa;
        bool maneuver = view.ManeuverRunning;
        bool intercept = view.InterceptRunning;
        const float N = float.NaN;

        string phase = view.TakeoffRunning ? TakeoffSequence.PhaseName(view.TakeoffPhase) : "-";
        string action = intercept ? view.InterceptPhaseText
                      : view.InterceptPending ? "Interceptar · Despegue combate"
                      : view.TakeoffRunning ? "Ruta · Despegue"
                      : view.CruiseRunning ? $"Ruta · {view.CruiseModeName}"
                      : maneuver ? view.ManeuverPhaseText
                      : "-";

        float speedbrake = maneuver ? agent.Maneuvers.LastSpeedbrakeCmd
                         : intercept ? agent.Intercept.LastSpeedbrakeCmd
                         : hasControls ? body.SpeedbrakeRatio : N;

        log.Record(
            phase: phase,
            action: action,
            iasKt: st.IasKt, altFt: st.AltFt,
            aglFt: float.IsNaN(body.AglMeters) ? N : body.AglMeters * MetersToFeet,
            vsFpm: st.VsFpm,
            pitchTargetDeg: t.Pitch, pitchRealDeg: st.PitchDeg,
            bankTargetDeg: t.Bank, bankRealDeg: st.BankDeg,
            headingTargetDeg: t.Heading, headingRealDeg: st.HeadingDeg,
            gNormal: st.GNormal,
            throttleTarget01: t.Throttle01,
            throttleReal01: hasControls ? body.ThrottleReadback : N,
            flapsTarget01: float.IsNaN(t.FlapPct) ? N : t.FlapPct / 100f,
            flapsReal01: hasControls ? body.FlapRatio : N,
            pitchCmd: hasControls ? body.PitchInputCmd : N,
            rollCmd: hasControls ? body.RollInputCmd : N,
            yawCmd: hasControls ? body.YawInputCmd : N,
            gCommand: t.GCommand,
            gPredicted: maneuver ? agent.Maneuvers.PredictedG : N,
            aoaDeg: st.AoaDeg,
            speedbrake01: speedbrake,
            weightLb: hasControls ? st.WeightLb : N, mach: st.MachNo,
            pitchRateDps: hasControls ? st.PitchRateDegPerSec : N,
            rollRateDps: hasControls ? st.RollRateDegPerSec : N,
            adaptation: maneuver ? agent.Maneuvers.AdaptationText
                      : intercept ? agent.Intercept.TelemetryText : "",
            protection: maneuver ? agent.Maneuvers.ProtectionText : "");
    }

    // Marca cada boton de accion con lo que va a pasar si se pulsa: normal si
    // la maniobra sale tal cual, en cursiva y con el motivo en el tooltip si va
    // a salir adaptada, y deshabilitado solo cuando no existe ninguna version
    // segura de ella aqui y ahora. Se evalua contra el avion EN FOCO.
    private void RefreshManeuverButtons() {
        AircraftAgent? agent = _director.Focused;
        if (agent is null) return;

        foreach (Button btn in ActionsStack.Children.OfType<Button>()) {
            if (btn.Tag is not string tag || !Enum.TryParse(tag, out ManeuverKind kind)) continue;

            var (level, text) = agent.Maneuvers.Preview(kind);
            btn.IsEnabled = level != AdaptationLevel.Impossible;
            btn.FontStyle = level == AdaptationLevel.Adapted ? FontStyles.Italic : FontStyles.Normal;

            // El tooltip se reasigna solo si cambio: a 10 Hz, reescribirlo
            // siempre cerraria y reabriria el popup mientras se lee.
            string tip = level switch {
                AdaptationLevel.Impossible => text.Length > 0 ? $"No es posible: {text}." : "",
                AdaptationLevel.Adapted => text.Length > 0 ? $"Se ejecutara adaptada: {text}." : "",
                _ => "",
            };
            string? current = btn.ToolTip as string;
            if (tip.Length == 0) { if (current is not null) btn.ToolTip = null; }
            else if (current != tip) btn.ToolTip = tip;
        }
    }

    private static string Fmt(float v, string fmt) => float.IsNaN(v) ? Dash : v.ToString(fmt);

    private static string Head(string left) => $"{"",-6}{left,7}  {"real",7}";

    private static string Row(string name, float obj, float real, string fmt, string unit)
    {
        string o = float.IsNaN(obj) ? Dash : obj.ToString(fmt);
        string r = float.IsNaN(real) ? Dash : real.ToString(fmt);
        return $"{name,-6}{o,7}  {r,7}  {unit}";
    }

    private static string RowText(string name, string obj, string real, string unit) =>
        $"{name,-6}{obj,7}  {real,7}  {unit}";

    private const float MetersToFeet = 3.28084f;
    private const double MpsToKnots = 1.943844;
}
