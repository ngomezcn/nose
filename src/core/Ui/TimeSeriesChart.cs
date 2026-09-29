using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AICopilotCore.Domain;

namespace AICopilotCore.Ui;

// Un panel de series temporales dibujado a mano (sin NuGet). Recibe el
// buffer tipado de la caja negra y pinta varias series encima con el mismo
// eje X (tiempo). Autoescala el eje Y con un margen del 8 %.
public sealed class TimeSeriesChart : FrameworkElement
{
    private IReadOnlyList<BlackBoxSample> _samples = Array.Empty<BlackBoxSample>();
    private IReadOnlyList<BlackBoxMarker> _markers = Array.Empty<BlackBoxMarker>();
    private SeriesDef[] _series = Array.Empty<SeriesDef>();
    private string _title = "";
    private string _unit = "";
    private bool _showMarkerLabels;

    private static readonly Typeface LabelFace =
        new(new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Pen GridPen = MakePen(Color.FromRgb(30, 38, 51), 1);
    private static readonly Pen AxisPen = MakePen(Color.FromRgb(60, 72, 88), 1);
    private static readonly Pen MarkerPen = MakeDashedPen(Color.FromRgb(255, 45, 130), 1.2);
    private static readonly Brush TitleBrush = new SolidColorBrush(Color.FromRgb(140, 165, 175));
    private static readonly Brush ValueBrush = new SolidColorBrush(Color.FromRgb(225, 240, 245));
    private static readonly Brush MarkerBrush = new SolidColorBrush(Color.FromRgb(255, 140, 180));

    public void SetTitle(string title, string unit)
    {
        _title = title;
        _unit = unit;
    }

    public void SetSeries(params SeriesDef[] series) => _series = series;

    // Solo el grafico de arriba pinta el texto de la marca; el resto solo
    // dibuja la linea vertical para no repetir la etiqueta diez veces.
    public void SetShowMarkerLabels(bool show) => _showMarkerLabels = show;

    public void Update(IReadOnlyList<BlackBoxSample> samples, IReadOnlyList<BlackBoxMarker> markers)
    {
        _samples = samples;
        _markers = markers;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 40 || h < 40) return;

        const double padL = 52, padR = 12, padT = 22, padB = 18;
        double plotW = w - padL - padR;
        double plotH = h - padT - padB;
        if (plotW < 10 || plotH < 10) return;

        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 10, 15)), null,
                         new Rect(0, 0, w, h));

        var title = new FormattedText(
            string.IsNullOrEmpty(_unit) ? _title : $"{_title} ({_unit})",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelFace, 11, TitleBrush, 1.25);
        dc.DrawText(title, new Point(padL, 4));

        if (_samples.Count < 2 || _series.Length == 0)
        {
            var empty = new FormattedText("Sin datos todavia — vuela o lanza una maniobra.",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, 12, TitleBrush, 1.25);
            dc.DrawText(empty, new Point(padL, padT + plotH / 2 - 8));
            return;
        }

        double t0 = _samples[0].TSec;
        double t1 = _samples[^1].TSec;
        if (t1 <= t0) t1 = t0 + 1;

        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;
        foreach (var s in _samples)
        {
            foreach (var ser in _series)
            {
                float v = ser.Pick(s);
                if (float.IsNaN(v)) continue;
                if (v < yMin) yMin = v;
                if (v > yMax) yMax = v;
            }
        }
        if (double.IsInfinity(yMin) || double.IsInfinity(yMax))
        {
            yMin = -1; yMax = 1;
        }
        if (Math.Abs(yMax - yMin) < 1e-6)
        {
            yMin -= 1;
            yMax += 1;
        }
        double pad = (yMax - yMin) * 0.08;
        yMin -= pad;
        yMax += pad;

        // Rejilla horizontal (4 lineas).
        for (int i = 0; i <= 4; i++)
        {
            double y = padT + plotH * i / 4.0;
            dc.DrawLine(GridPen, new Point(padL, y), new Point(padL + plotW, y));
            double val = yMax - (yMax - yMin) * i / 4.0;
            var lbl = new FormattedText(val.ToString("0.##", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, 10, TitleBrush, 1.25);
            dc.DrawText(lbl, new Point(4, y - 7));
        }
        dc.DrawRectangle(null, AxisPen, new Rect(padL, padT, plotW, plotH));

        // Marcas de inicio/fin/fase: linea vertical alineada en tiempo.
        // La etiqueta solo en el grafico que lo pide (el de arriba).
        if (_markers.Count > 0)
        {
            double labelY = padT + 14;
            foreach (var m in _markers)
            {
                if (m.TSec < t0 || m.TSec > t1) continue;
                double x = padL + (m.TSec - t0) / (t1 - t0) * plotW;
                dc.DrawLine(MarkerPen, new Point(x, padT), new Point(x, padT + plotH));
                if (!_showMarkerLabels || string.IsNullOrEmpty(m.Label)) continue;

                string text = m.Label.Length > 42 ? m.Label[..39] + "…" : m.Label;
                var lbl = new FormattedText(text,
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelFace, 9.5, MarkerBrush, 1.25);
                double lx = Math.Clamp(x + 3, padL, padL + plotW - lbl.Width);
                // Fondo minimo para que se lea sobre la serie.
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 7, 10, 15)), null,
                                 new Rect(lx - 1, labelY - 1, lbl.Width + 2, lbl.Height + 1));
                dc.DrawText(lbl, new Point(lx, labelY));
                labelY += lbl.Height + 2;
                if (labelY > padT + plotH * 0.45) labelY = padT + 14;
            }
        }

        // Leyenda + polilineas.
        double legendX = padL + 4;
        foreach (var ser in _series)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                bool started = false;
                for (int i = 0; i < _samples.Count; i++)
                {
                    float v = ser.Pick(_samples[i]);
                    if (float.IsNaN(v))
                    {
                        started = false;
                        continue;
                    }
                    double x = padL + (_samples[i].TSec - t0) / (t1 - t0) * plotW;
                    double y = padT + (1.0 - (v - yMin) / (yMax - yMin)) * plotH;
                    if (!started)
                    {
                        ctx.BeginFigure(new Point(x, y), false, false);
                        started = true;
                    }
                    else ctx.LineTo(new Point(x, y), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, ser.Pen, geo);

            var swatch = new FormattedText("— " + ser.Name,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, 10, ser.Brush, 1.25);
            dc.DrawText(swatch, new Point(legendX, padT + 2));
            legendX += swatch.Width + 14;
        }

        // Ultimo valor de la primera serie, util al vuelo.
        float last = _series[0].Pick(_samples[^1]);
        if (!float.IsNaN(last))
        {
            var live = new FormattedText(last.ToString("0.##", CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                LabelFace, 11, ValueBrush, 1.25);
            dc.DrawText(live, new Point(w - padR - live.Width, 4));
        }

        // Etiquetas de tiempo en los extremos.
        var tLeft = new FormattedText(FormatSpan(0),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelFace, 10, TitleBrush, 1.25);
        var tRight = new FormattedText(FormatSpan(t1 - t0),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelFace, 10, TitleBrush, 1.25);
        dc.DrawText(tLeft, new Point(padL, h - padB + 2));
        dc.DrawText(tRight, new Point(padL + plotW - tRight.Width, h - padB + 2));
    }

    private static string FormatSpan(double sec)
    {
        if (sec < 60) return $"{sec:0}s";
        int m = (int)(sec / 60);
        int s = (int)(sec % 60);
        return $"{m}:{s:00}";
    }

    private static Pen MakePen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness) { LineJoin = PenLineJoin.Round };
        p.Freeze();
        return p;
    }

    private static Pen MakeDashedPen(Color c, double thickness)
    {
        var p = new Pen(new SolidColorBrush(c), thickness)
        {
            LineJoin = PenLineJoin.Round,
            DashStyle = new DashStyle(new double[] { 3, 2 }, 0),
        };
        p.Freeze();
        return p;
    }

    public readonly struct SeriesDef
    {
        public readonly string Name;
        public readonly Func<BlackBoxSample, float> Pick;
        public readonly Brush Brush;
        public readonly Pen Pen;

        public SeriesDef(string name, Color color, Func<BlackBoxSample, float> pick)
        {
            Name = name;
            Pick = pick;
            Brush = new SolidColorBrush(color);
            Brush.Freeze();
            Pen = new Pen(Brush, 1.4) { LineJoin = PenLineJoin.Round };
            Pen.Freeze();
        }
    }
}
