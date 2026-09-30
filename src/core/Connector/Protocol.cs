namespace AICopilotCore.Connector;

// Gemelo exacto de src/connector/Protocol.h. Si tocas uno, toca el otro en
// el mismo commit: el connector compara la version en el HELLO y avisa,
// pero un layout cambiado a medias escribe valores sin sentido en datarefs
// reales antes de que nadie se entere.
//
// Transporte: named pipe duplex en modo BYTE. Cada mensaje va como
//     u32 longitud (bytes que siguen) + u8 opcode + payload
// todo little-endian. Ver el comentario de cabecera de Protocol.h para el
// porque de las dos decisiones que explican el resto: los nombres de
// dataref viajan una sola vez (luego solo ids) y todo valor escalar viaja
// como double sea cual sea el tipo real del dataref.
public static class Protocol
{
    public const ushort Version = 9;

    // El lado C# usa el nombre corto: NamedPipeClientStream le pone el
    // \.\pipe\ delante solo.
    public const string PipeName = "AICopilot.v1";

    // Cada cuanto manda el core un PING. Tiene que ser holgadamente menor
    // que el kHeartbeatTimeoutMs del connector (1000 ms) o el SafetyGuard
    // nos soltaria los overrides en pleno vuelo por creernos muertos.
    public const int PingIntervalMs = 250;
}

public enum Op : byte
{
    // core -> connector
    Hello = 0x01,
    Define = 0x02,
    Subscribe = 0x03,
    Unsubscribe = 0x04,
    Set = 0x05,
    Hold = 0x06,
    Toggle = 0x07,
    Pulse = 0x08,
    Command = 0x09,
    ReleaseAll = 0x0A,
    Ping = 0x0B,
    // Overlays 2D: ver Protocol.h (flags, font, rgb, scale, text).
    GraphicsConfig = 0x0C,
    // Escenario fijo usuario+IA: ver Protocol.h (lat/lon/elev/hdg/spd + path).
    PlaceScenario = 0x0D,
    // Exclusive access a un avion IA: ver Protocol.h (action + planeIndex).
    AiControl = 0x0E,
    // Camara: ver Protocol.h (0=off, 1..19=chase IA, 255=vista aerea).
    CameraFollow = 0x0F,

    // connector -> core
    HelloAck = 0x81,
    DefineAck = 0x82,
    Telemetry = 0x83,
    Event = 0x84,
    Pong = 0x85,
    // Ver Protocol.h: indice, nombre de fichero .acf y ruta completa de
    // cada avion activo. El 0 es el del usuario.
    Planes = 0x86,
}

public enum EntryKind : byte { Dataref = 0, Command = 1 }

public enum DataKind : byte
{
    Unknown = 0, Int = 1, Float = 2, Double = 3,
    IntArray = 4, FloatArray = 5, ByteArray = 6,
}

public enum HoldMode : byte
{
    Off = 0,    // suelta el hold; el connector RESTAURA el valor previo
    Latch = 1,  // reescribe el valor cada frame hasta que se suelte
}

public enum CmdAction : byte { Once = 0, Begin = 1, End = 2 }

public enum EventKind : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
    OverridesReleased = 3,
    AircraftReloaded = 4,
    ScenarioReady = 5,
}
