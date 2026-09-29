using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AICopilotCore.Connector;
using AICopilotCore.Domain;

namespace AICopilotCore.Driver;

/// <summary>
/// HTTP REST minimo: TcpListener 127.0.0.1:17890, una peticion a la vez.
/// ReceiveTimeout NO funciona en este host: todo el I/O usa Poll + Receive.
/// Marker: TCPLISTENER_SIMPLE_V1
/// </summary>
public sealed class ControlApi : IDisposable
{
    public const int DefaultPort = 17890;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly FlightDirector _director;
    private readonly ConnectorClient _client;
    private readonly int _port;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;

    public string BaseUrl => $"http://127.0.0.1:{_port}/";

    public ControlApi(FlightDirector director, ConnectorClient client, int port = DefaultPort)
    {
        _director = director;
        _client = client;
        _port = port > 0 ? port : DefaultPort;
        _listener = new TcpListener(IPAddress.Loopback, _port);
    }

    public void Start()
    {
        _listener.Start();
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "AICopilot-ControlApi" };
        _thread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _thread?.Join(500); } catch { }
        _cts.Dispose();
    }

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = _listener.AcceptTcpClient();
                tcp.NoDelay = true;
                Serve(tcp.Client);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (_cts.IsCancellationRequested) { break; }
            catch { }
            finally
            {
                try
                {
                    if (tcp?.Client != null)
                    {
                        try { tcp.Client.Shutdown(SocketShutdown.Send); } catch { }
                    }
                }
                catch { }
                try { tcp?.Close(); } catch { }
            }
        }
    }

    private void Serve(Socket sock)
    {
        try
        {
            if (!ReadRequest(sock, out string method, out string path, out JsonObject? json))
                return; // zombie / timeout / EOF: cerrar sin respuesta

            (int status, object payload) = Dispatch(method, path, json);
            WriteResponse(sock, status, payload);
        }
        catch { }
    }

    // Poll microsegundos; NUNCA Read/Receive bloqueante sin Poll previo.
    private static bool PollRead(Socket sock, int timeoutMs)
    {
        try { return sock.Poll(checked(timeoutMs * 1000), SelectMode.SelectRead); }
        catch { return false; }
    }

    private static int Recv(Socket sock, byte[] buf, int timeoutMs)
    {
        if (!PollRead(sock, timeoutMs))
            throw new IOException("recv timeout");
        return sock.Receive(buf, 0, buf.Length, SocketFlags.None);
    }

    private static bool ReadRequest(Socket sock, out string method, out string path, out JsonObject? json)
    {
        method = ""; path = ""; json = null;
        var ms = new MemoryStream();
        byte[] buf = new byte[4096];
        int headerEnd = -1;
        int contentLength = 0;

        // Primer byte: hasta 2s. Sin datos = zombie, salir.
        try
        {
            int n0 = Recv(sock, buf, 2000);
            if (n0 <= 0) return false;
            ms.Write(buf, 0, n0);
        }
        catch { return false; }

        while (ms.Length < 65536)
        {
            if (headerEnd < 0)
            {
                byte[] all = ms.ToArray();
                for (int i = 0; i + 3 < all.Length; i++)
                {
                    if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10)
                    { headerEnd = i; break; }
                }
                if (headerEnd >= 0)
                {
                    string[] lines = Encoding.UTF8.GetString(all, 0, headerEnd).Split("\r\n");
                    if (lines.Length == 0) return false;
                    string[] rl = lines[0].Split(' ');
                    if (rl.Length < 2) return false;
                    method = rl[0].ToUpperInvariant();
                    path = rl[1];
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path[..q];
                    foreach (string line in lines.Skip(1))
                    {
                        int c = line.IndexOf(':');
                        if (c <= 0) continue;
                        if (line[..c].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(line[(c + 1)..].Trim(), out int cl))
                            contentLength = Math.Clamp(cl, 0, 65536);
                    }
                }
            }

            if (headerEnd >= 0 && ms.Length >= headerEnd + 4 + contentLength)
                break;

            try
            {
                int n = Recv(sock, buf, 1000);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            catch { break; }
        }

        if (headerEnd < 0) return false;
        if (contentLength > 0)
        {
            byte[] all = ms.ToArray();
            if (all.Length < headerEnd + 4 + contentLength) return false;
            string body = Encoding.UTF8.GetString(all, headerEnd + 4, contentLength);
            if (body.Length > 0)
            {
                try { json = JsonNode.Parse(body) as JsonObject; }
                catch { return false; }
            }
        }
        return method.Length > 0 && path.Length > 0;
    }

    private static void WriteResponse(Socket sock, int status, object payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            409 => "Conflict",
            500 => "Internal Server Error",
            _ => "Error",
        };
        string head =
            "HTTP/1.1 " + status + " " + reason + "\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            "Content-Length: " + body.Length + "\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        byte[] packet = new byte[headBytes.Length + body.Length];
        Buffer.BlockCopy(headBytes, 0, packet, 0, headBytes.Length);
        Buffer.BlockCopy(body, 0, packet, headBytes.Length, body.Length);

        int off = 0;
        while (off < packet.Length)
        {
            if (!sock.Poll(1000 * 1000, SelectMode.SelectWrite))
                return;
            int n = sock.Send(packet, off, packet.Length - off, SocketFlags.None);
            if (n <= 0) return;
            off += n;
        }
    }

    private static object Fail(string message) => new { ok = false, error = message };

    private (int, object) Dispatch(string method, string path, JsonObject? body)
    {
        try
        {
            return (method, path) switch
            {
                ("GET", "/health") => (200, new { ok = true }),
                ("GET", "/status") => (200, BuildStatus()),
                ("POST", "/intercept") => DoIntercept(body),
                ("POST", "/intercept/abort") => (200, DoInterceptAbort()),
                ("POST", "/abort") => (200, DoAbort()),
                _ => (404, Fail("ruta desconocida: " + method + " " + path)),
            };
        }
        catch (Exception ex)
        {
            return (500, Fail(ex.Message));
        }
    }

    private object BuildStatus()
    {
        var t = _director.Takeoff;
        var m = _director.Maneuvers;
        var i = _director.Intercept;
        string mode = t.IsRunning ? "takeoff"
            : t.Phase == TakeoffPhase.Done ? "takeoff-done"
            : m.IsRunning ? "maneuver"
            : i.IsRunning ? "intercept"
            : "idle";
        return new
        {
            ok = true,
            connected = _client.IsConnected,
            mode,
            intercept = new
            {
                running = i.IsRunning,
                phase = i.Phase.ToString(),
                targetIndex = i.TargetIndex,
                station = i.Station.ToString(),
            },
        };
    }

    private (int, object) DoIntercept(JsonObject? body)
    {
        if (body?["index"] is null)
            return (400, Fail("falta index (1..19)"));
        int index = body["index"]!.GetValue<int>();
        string stationName = body["station"]?.GetValue<string>() ?? "TailHigh";
        if (!Enum.TryParse(stationName, ignoreCase: true, out InterceptStation station))
            return (400, Fail("station desconocida: " + stationName));
        string label = body["label"]?.GetValue<string>() ?? ("IA " + index);
        if (!_director.StartIntercept(index, station, label, out string error))
            return (409, Fail(error));
        return (200, new { ok = true, started = true, index, station = station.ToString(), label });
    }

    private object DoInterceptAbort()
    {
        if (!_director.Intercept.IsRunning)
            return new { ok = true, aborted = false, reason = "no hay interceptacion en marcha" };
        _director.Intercept.Abort();
        return new { ok = true, aborted = true };
    }

    private object DoAbort()
    {
        _director.AbortAll();
        return new { ok = true, aborted = true };
    }
}