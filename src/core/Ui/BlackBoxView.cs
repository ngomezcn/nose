using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AICopilotCore.Domain;

namespace AICopilotCore.Ui;

// Vista "Caja negra" del viewport central: checkboxes de series + graficos
// apilados. Se construye en codigo para no anadir otra Page XAML al .csproj
// (el shell ya es monolito) y porque las series se definen juntas al chart.
public sealed class BlackBoxView : DockPanel
{
    private readonly TimeSeriesChart _altChart = NewChart();
    private readonly TimeSeriesChart _vsChart = NewChart();
    private readonly TimeSeriesChart _iasChart = NewChart();
    private readonly TimeSeriesChart _pitchChart = NewChart();
    private readonly TimeSeriesChart _bankChart = NewChart();
    private readonly TimeSeriesChart _gChart = NewChart();
    private readonly TimeSeriesChart _aoaChart = NewChart();
    private readonly TimeSeriesChart _stickChart = NewChart();
    private readonly TimeSeriesChart _powerChart = NewChart();
    private readonly TimeSeriesChart _ratesChart = NewChart();

    private readonly CheckBox _altCb, _vsCb, _iasCb, _pitchCb, _bankCb;
    private readonly CheckBox _gCb, _aoaCb, _stickCb, _powerCb, _ratesCb;
    private readonly Border _altHost, _vsHost, _iasHost, _pitchHost, _bankHost;
    private readonly Border _gHost, _aoaHost, _stickHost, _powerHost, _ratesHost;
    private readonly TextBlock _status;

    private static readonly Color Cyan = Color.FromRgb(0, 225, 255);
    private static readonly Color Magenta = Color.FromRgb(255, 45, 130);
    private static readonly Color Lime = Color.FromRgb(120, 220, 100);
    private static readonly Color Orange = Color.FromRgb(255, 170, 60);
    private static readonly Color Yellow = Color.FromRgb(240, 220, 90);
    private static readonly Color White = Color.FromRgb(220, 230, 240);

    public BlackBoxView()
    {
        Background = new SolidColorBrush(Color.FromRgb(7, 10, 15));

        var toolbar = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(10, 8, 10, 6) };
        Children.Add(toolbar);
        SetDock(toolbar, Dock.Top);

        _status = new TextBlock
        {
            FontFamily = new FontFamily("Rajdhani SemiBold, Segoe UI"),
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(140, 165, 175)),
            Margin = new Thickness(0, 0, 0, 6),
        };
        toolbar.Children.Add(_status);

        var toggles = new WrapPanel();
        toolbar.Children.Add(toggles);

        _altCb = MkCb("Altitud / AGL", true, toggles);
        _vsCb = MkCb("V/S", true, toggles);
        _iasCb = MkCb("IAS / Mach", true, toggles);
        _pitchCb = MkCb("Pitch", true, toggles);
        _bankCb = MkCb("Bank", true, toggles);
        _gCb = MkCb("G", true, toggles);
        _aoaCb = MkCb("AoA", false, toggles);
        _stickCb = MkCb("Mandos", true, toggles);
        _powerCb = MkCb("Gas / Flaps", false, toggles);
        _ratesCb = MkCb("Rates Q/P", false, toggles);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Children.Add(scroll);

        var stack = new StackPanel { Margin = new Thickness(6, 0, 6, 8) };
        scroll.Content = stack;

        _altHost = Host(_altChart, stack);
        _vsHost = Host(_vsChart, stack);
        _iasHost = Host(_iasChart, stack);
        _pitchHost = Host(_pitchChart, stack);
        _bankHost = Host(_bankChart, stack);
        _gHost = Host(_gChart, stack);
        _aoaHost = Host(_aoaChart, stack);
        _stickHost = Host(_stickChart, stack);
        _powerHost = Host(_powerChart, stack);
        _ratesHost = Host(_ratesChart, stack);

        _altChart.SetTitle("Altitud", "ft");
        _altChart.SetSeries(
            new TimeSeriesChart.SeriesDef("ALT", Cyan, s => s.AltFt),
            new TimeSeriesChart.SeriesDef("AGL", Lime, s => s.AglFt));

        _vsChart.SetTitle("Velocidad vertical", "fpm");
        _vsChart.SetSeries(new TimeSeriesChart.SeriesDef("V/S", Orange, s => s.VsFpm));

        _iasChart.SetTitle("Velocidad", "kt / Mach×100");
        _iasChart.SetSeries(
            new TimeSeriesChart.SeriesDef("IAS", Cyan, s => s.IasKt),
            new TimeSeriesChart.SeriesDef("Mach×100", Magenta, s => s.Mach * 100f));

        _pitchChart.SetTitle("Pitch", "deg");
        _pitchChart.SetSeries(
            new TimeSeriesChart.SeriesDef("obj", Magenta, s => s.PitchObjDeg),
            new TimeSeriesChart.SeriesDef("real", Cyan, s => s.PitchRealDeg));

        _bankChart.SetTitle("Bank", "deg");
        _bankChart.SetSeries(
            new TimeSeriesChart.SeriesDef("obj", Magenta, s => s.BankObjDeg),
            new TimeSeriesChart.SeriesDef("real", Cyan, s => s.BankRealDeg));

        _gChart.SetTitle("Factor de carga", "g");
        _gChart.SetSeries(
            new TimeSeriesChart.SeriesDef("G", Cyan, s => s.GNormal),
            new TimeSeriesChart.SeriesDef("G obj", Magenta, s => s.GCommand),
            new TimeSeriesChart.SeriesDef("G pred", Yellow, s => s.GPredicted));

        _aoaChart.SetTitle("Angulo de ataque", "deg");
        _aoaChart.SetSeries(new TimeSeriesChart.SeriesDef("AoA", Orange, s => s.AoaDeg));

        _stickChart.SetTitle("Mandos yoke", "−1..+1");
        _stickChart.SetSeries(
            new TimeSeriesChart.SeriesDef("pitch", Cyan, s => s.PitchCmd),
            new TimeSeriesChart.SeriesDef("roll", Magenta, s => s.RollCmd),
            new TimeSeriesChart.SeriesDef("yaw", Lime, s => s.YawCmd));

        _powerChart.SetTitle("Gas / flaps / aerofrenos", "%");
        _powerChart.SetSeries(
            new TimeSeriesChart.SeriesDef("gas obj", Magenta, s => s.ThrottleObj01 * 100f),
            new TimeSeriesChart.SeriesDef("gas", Cyan, s => s.ThrottleReal01 * 100f),
            new TimeSeriesChart.SeriesDef("flaps", Lime, s => s.FlapsReal01 * 100f),
            new TimeSeriesChart.SeriesDef("SB", Orange, s => s.Speedbrake01 * 100f));

        _ratesChart.SetTitle("Rates", "deg/s");
        _ratesChart.SetSeries(
            new TimeSeriesChart.SeriesDef("Q pitch", Cyan, s => s.PitchRateDps),
            new TimeSeriesChart.SeriesDef("P roll", Magenta, s => s.RollRateDps));

        ApplyVisibility();
        foreach (var cb in new[] { _altCb, _vsCb, _iasCb, _pitchCb, _bankCb,
                                   _gCb, _aoaCb, _stickCb, _powerCb, _ratesCb })
        {
            cb.Checked += (_, _) => ApplyVisibility();
            cb.Unchecked += (_, _) => ApplyVisibility();
        }
    }

    public void Refresh(IReadOnlyList<BlackBoxSample> samples,
                        IReadOnlyList<BlackBoxMarker> markers,
                        string statusText)
    {
        _status.Text = statusText;
        // Etiquetas solo en el primer grafico visible: el resto lleva la
        // misma linea vertical sin repetir el texto.
        bool labelsPlaced = false;
        void Paint(Border host, TimeSeriesChart chart)
        {
            if (host.Visibility != Visibility.Visible) return;
            chart.SetShowMarkerLabels(!labelsPlaced);
            labelsPlaced = true;
            chart.Update(samples, markers);
        }
        Paint(_altHost, _altChart);
        Paint(_vsHost, _vsChart);
        Paint(_iasHost, _iasChart);
        Paint(_pitchHost, _pitchChart);
        Paint(_bankHost, _bankChart);
        Paint(_gHost, _gChart);
        Paint(_aoaHost, _aoaChart);
        Paint(_stickHost, _stickChart);
        Paint(_powerHost, _powerChart);
        Paint(_ratesHost, _ratesChart);
    }

    private void ApplyVisibility()
    {
        _altHost.Visibility = Vis(_altCb);
        _vsHost.Visibility = Vis(_vsCb);
        _iasHost.Visibility = Vis(_iasCb);
        _pitchHost.Visibility = Vis(_pitchCb);
        _bankHost.Visibility = Vis(_bankCb);
        _gHost.Visibility = Vis(_gCb);
        _aoaHost.Visibility = Vis(_aoaCb);
        _stickHost.Visibility = Vis(_stickCb);
        _powerHost.Visibility = Vis(_powerCb);
        _ratesHost.Visibility = Vis(_ratesCb);
    }

    private static Visibility Vis(CheckBox cb) =>
        cb.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private static TimeSeriesChart NewChart() => new() { Height = 150, Margin = new Thickness(0, 0, 0, 8) };

    private static Border Host(TimeSeriesChart chart, Panel parent)
    {
        var b = new Border
        {
            Child = chart,
            BorderBrush = new SolidColorBrush(Color.FromRgb(30, 38, 51)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Margin = new Thickness(0, 0, 0, 6),
        };
        parent.Children.Add(b);
        return b;
    }

    private static CheckBox MkCb(string label, bool on, Panel parent)
    {
        var cb = new CheckBox
        {
            Content = label,
            IsChecked = on,
            Margin = new Thickness(0, 0, 14, 4),
            Foreground = new SolidColorBrush(Color.FromRgb(225, 240, 245)),
            FontFamily = new FontFamily("Rajdhani SemiBold, Segoe UI"),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        parent.Children.Add(cb);
        return cb;
    }
}
