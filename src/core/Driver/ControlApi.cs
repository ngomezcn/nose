using System.Windows;
using AICopilotCore.Connector;
using AICopilotCore.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AICopilotCore.Driver;

// API REST en http://127.0.0.1:17890 para que un script / LLM pulse los
// mismos botones que la UI. ASP.NET Core minimal API (Kestrel): nada de
// HTTP hecho a mano. Cada endpoint se ejecuta en el hilo de la UI, igual
// que si se hubiera hecho clic en el boton.
//
//   GET  /status
//   POST /takeoff?style=Relaxed|Combat|Emergency
//   POST /intercept?index=1&station=TailHigh|ParallelRight|ParallelLeft|Above|Below
//   POST /abort
public sealed class ControlApi
{
    public const string Url = "http://127.0.0.1:17890";

    private readonly WebApplication _app;

    public ControlApi(FlightDirector director, ConnectorClient client)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(Url);
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _app.MapGet("/status", () => OnUi(() => Results.Ok(new
        {
            connected = client.IsConnected,
            takeoff = new { running = director.Takeoff.IsRunning, phase = director.Takeoff.Phase.ToString() },
            maneuver = new { running = director.Maneuvers.IsRunning },
            intercept = new
            {
                running = director.Intercept.IsRunning || director.IsInterceptPending,
                pendingTakeoff = director.IsInterceptPending,
                phase = director.IsInterceptPending
                    ? "OwnTakeoff"
                    : director.Intercept.Phase.ToString(),
                index = director.IsInterceptPending
                    ? director.PendingInterceptIndex
                    : director.Intercept.TargetIndex,
                station = director.IsInterceptPending
                    ? "" // estación se aplica tras el handoff
                    : director.Intercept.Station.ToString(),
                label = director.IsInterceptPending
                    ? director.PendingInterceptLabel
                    : director.Intercept.TargetLabel,
            },
            log = director.RecentLog.TakeLast(10),
        })));

        _app.MapPost("/takeoff", (string style = "Relaxed") =>
        {
            if (!Enum.TryParse(style, ignoreCase: true, out TakeoffStyleId id))
                return BadValue("style", style, Enum.GetNames<TakeoffStyleId>());
            return OnUi(() => director.StartTakeoff(TakeoffStyles.Get(id), out string error)
                ? Results.Ok(new { ok = true, style = id.ToString() })
                : Results.Conflict(new { ok = false, error }));
        });

        _app.MapPost("/intercept", (int index = 1, string station = "TailHigh") =>
        {
            if (!Enum.TryParse(station, ignoreCase: true, out InterceptStation st))
                return BadValue("station", station, Enum.GetNames<InterceptStation>());
            return OnUi(() => director.StartIntercept(index, st, $"IA {index}", out string error)
                ? Results.Ok(new { ok = true, index, station = st.ToString() })
                : Results.Conflict(new { ok = false, error }));
        });

        _app.MapPost("/abort", () => OnUi(() =>
        {
            director.AbortAll();
            return Results.Ok(new { ok = true });
        }));
    }

    public Task StartAsync() => _app.StartAsync();

    public void Stop() => _app.StopAsync().Wait(TimeSpan.FromSeconds(2));

    private static IResult BadValue(string name, string value, string[] valid) =>
        Results.BadRequest(new { ok = false, error = $"{name} '{value}' no valido; usa: {string.Join(", ", valid)}" });

    private static IResult OnUi(Func<IResult> action) =>
        Application.Current?.Dispatcher is { } d ? d.Invoke(action) : action();
}
