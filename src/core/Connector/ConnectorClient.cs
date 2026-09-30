using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace AICopilotCore.Connector;

// Un dataref (o comando) ya registrado en el connector. El core trabaja
// siempre con estos objetos, nunca con rutas: la ruta solo se usa en el
// DEFINE inicial.
//
// Value es el ultimo valor recibido por telemetria, asi que leer un dataref
// desde la logica es `handle.Value` -- el equivalente del FloatRef::Get()
// del C++ viejo, pero sin coste: el dato ya esta aqui, no hay que ir a
// buscarlo al simulador.
public sealed class DataHandle
{
    public ushort Id { get; init; }
    public string Path { get; init; } = "";
    public EntryKind Kind { get; init; }
    public int Index { get; init; }
    public int Count { get; init; }

    // Suscripcion pedida: 0 = no suscrito, N = emitir uno de cada N frames.
    public byte Divisor { get; internal set; }

    // Rellenado por el DEFINE_ACK del connector.
    public bool Resolved { get; internal set; }
    public DataKind DataKind { get; internal set; }
    public int ArrayLength { get; internal set; }
    public bool Writable { get; internal set; }

    // Escrito desde el hilo de lectura del pipe, leido desde la logica y la
    // UI. En x64 un double alineado no se parte a medias, asi que no hace
    // falta candado para un valor suelto; y aqui no hay ninguna invariante
    // entre varios datarefs que proteger, cada uno vale por si mismo.
    public volatile bool HasValue;
    private double _value;
    public double Value
    {
        get => _value;
        internal set { _value = value; HasValue = true; }
    }

    public float Float => (float)_value;
    public bool Bool => _value != 0.0;

    public override string ToString() => $"{Path} (#{Id})";
}

public readonly record struct TelemetryFrame(uint Frame, float Dt);

// Un avion de la partida, tal cual lo enumera X-Plane. Index 0 es el del
// usuario; 1.. son las IAs. FileName es el .acf (p.ej. "Cirrus SR22.acf")
// y Path la ruta completa. El array publicado es inmutable: el hilo del
// pipe lo sustituye entero, la UI solo lo lee.
public readonly record struct SimPlane(byte Index, string FileName, string Path);

// Cliente del named pipe del connector.
//
// Lo importante de esta clase, mas alla de mover bytes: la reconexion es
// transparente. Guarda todo lo que se ha definido y suscrito, y cuando el
// pipe vuelve (X-Plane reiniciado, plugin recargado, core reabierto) lo
// replica entero sin que la logica de arriba se entere. Por eso Define() se
// puede llamar antes incluso de que haya conexion: se apunta y se manda
// cuando toque.
public sealed class ConnectorClient : IDisposable
{
    // Cada cuanto se reintenta conectar cuando no hay plugin al otro lado.
    private const int ReconnectDelayMs = 500;
    private const uint MaxMessageBytes = 1 << 20;

    private readonly List<DataHandle> _handles = new();
    private readonly object _handlesLock = new();
    private ushort _nextId;

    private readonly Channel<ReadOnlyMemory<byte>> _outbox =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
            new UnboundedChannelOptions { SingleReader = true });

    private CancellationTokenSource? _cts;
    private volatile bool _connected;

    public bool IsConnected => _connected;
    public string PluginVersion { get; private set; } = "";

    // Ultimo listado recibido (Op.Planes). Vacio hasta el primer mensaje
    // y otra vez vacio al caer la sesion.
    private volatile SimPlane[] _planes = Array.Empty<SimPlane>();
    public SimPlane[] Planes => _planes;

    // Conectado Y con HELLO_ACK: a partir de aqui el connector ya sabe
    // quienes somos y las definiciones estan en vuelo.
    public event Action? SessionReady;
    public event Action? Disconnected;

    // Se dispara en el hilo de lectura del pipe, ya con los valores
    // aplicados a los handles. La logica se engancha aqui para correr al
    // ritmo del simulador en vez de al de un timer de Windows.
    public event Action<TelemetryFrame>? TelemetryReceived;
    public event Action<EventKind, string>? ConnectorEvent;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    public void Dispose() => _cts?.Cancel();

    // --- registro de datarefs ---------------------------------------------

    // index/count solo se usan en datarefs de tipo array: al LEER se
    // devuelve el elemento `index`, y al ESCRIBIR se escribe el mismo valor
    // en `count` elementos desde `index`. Con eso se cubre el caso real de
    // "el mismo throttle en los N motores".
    public DataHandle Define(string path, EntryKind kind = EntryKind.Dataref,
                             int index = 0, int count = 1)
    {
        DataHandle handle;
        lock (_handlesLock)
        {
            handle = new DataHandle
            {
                Id = _nextId++,
                Path = path,
                Kind = kind,
                Index = index,
                Count = count,
            };
            _handles.Add(handle);
        }
        if (_connected) SendDefine(handle);
        return handle;
    }

    public DataHandle DefineCommand(string path) => Define(path, EntryKind.Command);

    // divisor: 1 = cada frame (actitud, velocidad: alimentan los PID),
    // N = uno de cada N (QNH, tren: cambian cada varios segundos).
    public void Subscribe(DataHandle handle, byte divisor = 1)
    {
        handle.Divisor = divisor == 0 ? (byte)1 : divisor;
        if (!_connected) return;
        var w = new MessageWriter(Op.Subscribe);
        w.U16(1);
        w.U16(handle.Id);
        w.U8(handle.Divisor);
        Send(w);
    }

    // --- ordenes ----------------------------------------------------------

    // Escritura puntual. Para algo que tenga que quedarse puesto usa Hold:
    // si el dataref esta bajo hold, el hold impone su valor en el mismo
    // frame y este Set no se nota.
    public void Set(DataHandle handle, double value)
    {
        var w = new MessageWriter(Op.Set);
        w.U16(1);
        w.U16(handle.Id);
        w.F64(value);
        Send(w);
    }

    // Varias escrituras puntuales en un solo mensaje. El protocolo lo
    // soporta de serie (SET lleva un contador), y agrupar sirve para cosas
    // que son un mismo gesto -- las dos subescalas del altimetro, por
    // ejemplo: asi no pueden quedar descuadradas si la conexion se corta
    // justo entre una y otra.
    public void SetMany(params (DataHandle Handle, double Value)[] writes)
    {
        if (writes.Length == 0) return;
        var w = new MessageWriter(Op.Set);
        w.U16((ushort)writes.Length);
        foreach ((DataHandle h, double v) in writes) { w.U16(h.Id); w.F64(v); }
        Send(w);
    }

    // Mantiene el valor escrito cada frame hasta Release. Es lo que hay que
    // usar para los overrides y para los ejes de mando: si el core muere,
    // el SafetyGuard del connector restaura el valor previo de todos los
    // holds y el avion vuelve a ser pilotable. Un Set no tiene esa red.
    //
    // ratePerSec > 0 limita cuanto puede moverse el valor escrito por
    // segundo (rampa); 0 = instantaneo.
    public void Hold(DataHandle handle, double value, float ratePerSec = 0f)
    {
        var w = new MessageWriter(Op.Hold);
        w.U16(handle.Id);
        w.U8((byte)HoldMode.Latch);
        w.F64(value);
        w.F32(ratePerSec);
        Send(w);
    }

    // Suelta el hold RESTAURANDO el valor que el dataref tenia antes de que
    // empezara. Ver el comentario de Holds.h: por eso soltar el hold de un
    // override deja el override en 0 sin que nadie escriba ese 0 a mano.
    public void Release(DataHandle handle)
    {
        var w = new MessageWriter(Op.Hold);
        w.U16(handle.Id);
        w.U8((byte)HoldMode.Off);
        w.F64(0);
        w.F32(0);
        Send(w);
    }

    public void Toggle(DataHandle handle)
    {
        var w = new MessageWriter(Op.Toggle);
        w.U16(handle.Id);
        Send(w);
    }

    public void Pulse(DataHandle handle, double value, ushort ms)
    {
        var w = new MessageWriter(Op.Pulse);
        w.U16(handle.Id);
        w.F64(value);
        w.U16(ms);
        Send(w);
    }

    public void Command(DataHandle handle, CmdAction action = CmdAction.Once)
    {
        var w = new MessageWriter(Op.Command);
        w.U16(handle.Id);
        w.U8((byte)action);
        Send(w);
    }

    // Boton de panico: suelta todos los holds y comandos mantenidos.
    public void ReleaseAll() => Send(new MessageWriter(Op.ReleaseAll));

    // Overlays 2D del connector (etiqueta / lineas / marcadores). Hay que
    // reenviarlo tras cada SessionReady: el plugin arranca con defaults
    // y no guarda lo que mando el core anterior.
    public void SetGraphicsConfig(byte flags, byte font,
                                  float r, float g, float b, float scale,
                                  string text)
    {
        var w = new MessageWriter(Op.GraphicsConfig);
        w.U8(flags);
        w.U8(font);
        w.F32(r);
        w.F32(g);
        w.F32(b);
        w.F32(scale);
        w.Str(text ?? string.Empty);
        Send(w);
    }

    // Ruta de interceptacion (coords locales OGL absolutas). Vacia/null la
    // borra en el connector. Maximo 64 puntos (se submuestrea si hay mas).
    public void SetInterceptPath(IReadOnlyList<(double X, double Y, double Z)>? pts)
    {
        int n = pts?.Count ?? 0;
        if (n > 64) n = 64;
        var w = new MessageWriter(Op.InterceptPath);
        w.U8((byte)n);
        for (int i = 0; i < n; i++)
        {
            var p = pts![n == pts.Count ? i : (int)((long)i * (pts.Count - 1) / (n - 1))];
            w.F64(p.X);
            w.F64(p.Y);
            w.F64(p.Z);
        }
        Send(w);
    }

    // Teleporta usuario + IA a las coordenadas que decide el dominio
    // (SimScenario). El connector aplica PlaceUserAtLocation y, tras cargar
    // el aeropuerto, coloca la IA sin quedarsela (X-Plane la vuela).
    public void PlaceScenario(
        double userLat, double userLon, double userElevMsl, double userHdgTrue, double userSpeedMps,
        double aiLat, double aiLon, double aiElevMsl, double aiHdgTrue, double aiSpeedMps,
        string aiAircraftRelPath)
    {
        var w = new MessageWriter(Op.PlaceScenario);
        w.F64(userLat);
        w.F64(userLon);
        w.F64(userElevMsl);
        w.F64(userHdgTrue);
        w.F64(userSpeedMps);
        w.F64(aiLat);
        w.F64(aiLon);
        w.F64(aiElevMsl);
        w.F64(aiHdgTrue);
        w.F64(aiSpeedMps);
        w.Str(aiAircraftRelPath ?? string.Empty);
        Send(w);
    }

    // --- transporte -------------------------------------------------------

    // Sin conexion no se encola nada: lo que haya que decir se dira al
    // reconectar, cuando se replique el estado entero. Encolar ordenes de
    // control viejas y soltarlas de golpe al volver el pipe seria bastante
    // peor que perderlas.
    private void Send(MessageWriter w)
    {
        if (!_connected) return;
        _outbox.Writer.TryWrite(w.Finish());
    }

    private void SendDefine(DataHandle h)
    {
        var w = new MessageWriter(Op.Define);
        w.U16(h.Id);
        w.U8((byte)h.Kind);
        w.I32(h.Index);
        w.I32(h.Count);
        w.Str(h.Path);
        Send(w);
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", Protocol.PipeName,
                                                 PipeDirection.InOut,
                                                 PipeOptions.Asynchronous);
                await pipe.ConnectAsync(ct).ConfigureAwait(false);

                _connected = true;
                await RunSessionAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[connector] sesion terminada: {ex.Message}");
            }
            finally
            {
                bool wasConnected = _connected;
                _connected = false;
                _planes = Array.Empty<SimPlane>();
                pipe?.Dispose();
                // Vaciar la cola: lo que quedara sin mandar pertenece a la
                // sesion que acaba de morir.
                while (_outbox.Reader.TryRead(out _)) { }
                if (wasConnected) Disconnected?.Invoke();
            }

            if (ct.IsCancellationRequested) break;
            try { await Task.Delay(ReconnectDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunSessionAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = sessionCts.Token;

        Task writer = WriteLoopAsync(pipe, token);
        Task pinger = PingLoopAsync(token);

        // Saludo + replica del estado. Va en este orden porque el connector
        // procesa los mensajes tal y como llegan: primero tiene que saber
        // quienes somos, luego que ids existen, y solo entonces puede
        // suscribir.
        var hello = new MessageWriter(Op.Hello);
        hello.U16(Protocol.Version);
        hello.Str("AICopilotCore");
        Send(hello);

        DataHandle[] snapshot;
        lock (_handlesLock) snapshot = _handles.ToArray();
        foreach (DataHandle h in snapshot) SendDefine(h);

        DataHandle[] subs = snapshot.Where(h => h.Divisor > 0).ToArray();
        if (subs.Length > 0)
        {
            var w = new MessageWriter(Op.Subscribe);
            w.U16((ushort)subs.Length);
            foreach (DataHandle h in subs) { w.U16(h.Id); w.U8(h.Divisor); }
            Send(w);
        }

        try
        {
            await ReadLoopAsync(pipe, token).ConfigureAwait(false);
        }
        finally
        {
            sessionCts.Cancel();
            await Task.WhenAny(Task.WhenAll(writer, pinger), Task.Delay(500)).ConfigureAwait(false);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        byte[] header = new byte[4];
        byte[] body = new byte[4096];

        while (!ct.IsCancellationRequested)
        {
            await pipe.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (len == 0 || len > MaxMessageBytes)
                throw new InvalidDataException($"longitud de mensaje invalida: {len}");

            if (body.Length < len) body = new byte[len];
            await pipe.ReadExactlyAsync(body.AsMemory(0, (int)len), ct).ConfigureAwait(false);

            Dispatch(body.AsSpan(0, (int)len));
        }
    }

    private void Dispatch(ReadOnlySpan<byte> payload)
    {
        var r = new MessageReader(payload);
        var op = (Op)r.U8();
        if (!r.Ok) return;

        switch (op)
        {
            case Op.HelloAck:
            {
                ushort version = r.U16();
                PluginVersion = r.Str();
                int xplaneVer = r.I32();
                int xplmVer = r.I32();
                if (!r.Ok) return;

                if (version != Protocol.Version)
                {
                    ConnectorEvent?.Invoke(EventKind.Error,
                        $"Protocolo incompatible: connector v{version}, core v{Protocol.Version}. " +
                        "Recompila los dos con tools/build_and_deploy.ps1 (o ui/addon por separado).");
                }
                ConnectorEvent?.Invoke(EventKind.Info,
                    $"Conectado a {PluginVersion} (X-Plane {xplaneVer}, XPLM {xplmVer}).");
                SessionReady?.Invoke();
                break;
            }

            case Op.DefineAck:
            {
                ushort id = r.U16();
                bool ok = r.U8() != 0;
                var kind = (DataKind)r.U8();
                int arrayLength = r.I32();
                bool writable = r.U8() != 0;
                if (!r.Ok) return;

                DataHandle? h = FindById(id);
                if (h is null) return;
                h.Resolved = ok;
                h.DataKind = kind;
                h.ArrayLength = arrayLength;
                h.Writable = writable;
                if (!ok)
                {
                    ConnectorEvent?.Invoke(EventKind.Warning,
                        $"Dataref no encontrado en este avion: {h.Path}");
                }
                break;
            }

            case Op.Telemetry:
            {
                uint frame = r.U32();
                float dt = r.F32();
                ushort count = r.U16();
                for (int i = 0; i < count; i++)
                {
                    ushort id = r.U16();
                    double value = r.F64();
                    if (!r.Ok) return;
                    DataHandle? h = FindById(id);
                    if (h is not null) h.Value = value;
                }
                // Se dispara con los valores ya aplicados: quien escuche
                // solo tiene que leer los handles.
                TelemetryReceived?.Invoke(new TelemetryFrame(frame, dt));
                break;
            }

            case Op.Event:
            {
                var kind = (EventKind)r.U8();
                string text = r.Str();
                if (!r.Ok) return;
                ConnectorEvent?.Invoke(kind, text);
                break;
            }

            case Op.Pong:
                break;

            case Op.Planes:
            {
                byte count = r.U8();
                var list = new SimPlane[count];
                for (int i = 0; i < count; i++)
                {
                    byte index = r.U8();
                    string fileName = r.Str();
                    string path = r.Str();
                    if (!r.Ok) return;
                    list[i] = new SimPlane(index, fileName, path);
                }
                _planes = list;
                break;
            }
        }
    }

    // Los ids se asignan consecutivos desde 0, asi que el indice en la lista
    // ES el id. La busqueda lineal de reserva esta por si algun dia dejan de
    // serlo.
    private DataHandle? FindById(ushort id)
    {
        lock (_handlesLock)
        {
            if (id < _handles.Count && _handles[id].Id == id) return _handles[id];
            return _handles.FirstOrDefault(h => h.Id == id);
        }
    }

    private async Task WriteLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        try
        {
            while (await _outbox.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_outbox.Reader.TryRead(out ReadOnlyMemory<byte> msg))
                {
                    await pipe.WriteAsync(msg, ct).ConfigureAwait(false);
                }
                await pipe.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[connector] escritura: {ex.Message}"); }
    }

    // El latido. No es decorativo: si el connector deja de oirnos durante
    // kHeartbeatTimeoutMs (1 s) teniendo holds puestos, da por muerto al core
    // y suelta los overrides en pleno vuelo. Un core vivo pero callado -- por
    // ejemplo, con la secuencia corriendo pero sin nada nuevo que decir ese
    // frame -- tiene que seguir dando señales de vida.
    private async Task PingLoopAsync(CancellationToken ct)
    {
        uint seq = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Protocol.PingIntervalMs, ct).ConfigureAwait(false);
                var w = new MessageWriter(Op.Ping);
                w.U32(seq++);
                Send(w);
            }
        }
        catch (OperationCanceledException) { }
    }
}
