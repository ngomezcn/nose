// Protocol.h
//
// Contrato de cable entre el connector (este plugin) y el core
// (AICopilotCore.exe). Tiene un gemelo exacto en C# en
// src/core/Connector/Protocol.cs: si tocas uno, toca el otro en el mismo
// commit o la sesion se rompe en el HELLO.
//
// Transporte: named pipe duplex \\.\pipe\AICopilot.v1, en modo BYTE (no
// MESSAGE). El modo mensaje de Windows daria fronteras de mensaje gratis,
// pero leerlas desde .NET obliga a andar con IsMessageComplete y a
// reensamblar a mano; con un prefijo de longitud explicito el lado C# es
// un ReadExactly y ya esta. Cada mensaje va asi:
//
//     u32 longitud   (bytes que siguen, sin contar estos 4)
//     u8  opcode
//     ... payload segun el opcode
//
// Todo little-endian (x86 nativo en los dos lados, asi que los enteros y
// floats se copian tal cual, sin conversion).
//
// Decision de diseño que explica casi todo lo demas: los NOMBRES de
// dataref viajan UNA sola vez. El core manda DEFINE con la ruta completa
// ("sim/joystick/yoke_pitch_ratio") y se queda con un u16 id; a partir de
// ahi todo el trafico por frame usa solo ese id. Asi el connector nunca
// resuelve un string en el camino caliente, y el protocolo no tiene que
// saber nada de que datarefs existen -- eso es cosa del core, que es justo
// el reparto de responsabilidades que buscamos.
//
// Segunda decision: TODO valor escalar viaja como f64 (double),
// independientemente del tipo real del dataref. El connector convierte al
// tipo de verdad usando el DataKind que cacheo en el DEFINE. Cuesta unos
// bytes de mas (8 en vez de 4) pero quita del cable toda la logica de
// despacho por tipo. A 60 Hz con 50 datarefs suscritos son ~30 KB/s: nada.
#pragma once

#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

namespace proto {

// Sube esto en cuanto cambie cualquier layout de abajo. El connector
// rechaza (y loguea) un HELLO con una version distinta en vez de
// interpretar bytes con el layout equivocado, que es la clase de bug que
// se manifiesta como "el avion hace cosas raras" en vez de como un error.
constexpr uint16_t kVersion = 9;

constexpr const char* kPipeName = "\\\\.\\pipe\\AICopilot.v1";

// Si el core no da señales de vida en este tiempo teniendo holds activos,
// el SafetyGuard suelta todo. Ver SafetyGuard.h para por que esto no es
// opcional.
constexpr uint32_t kHeartbeatTimeoutMs = 1000;

enum class Op : uint8_t {
    // --- core -> connector ---
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
    // Overlays 2D del connector (etiquetas / lineas / marcadores / trayectoria).
    // Payload:
    //   u8  flags   bit0=ownLabel bit1=otherLabels bit2=lines
    //               bit3=markers bit4=path
    //   u8  font    0=Basic 1=Proportional
    //   f32 r,g,b   color 0..1
    //   f32 scale   tamano relativo (marcador / grosor de linea)
    //   str text    etiqueta (UTF-8, tipicamente ASCII corto)
    GraphicsConfig = 0x0C,
    // Coloca al usuario y una IA en posiciones fijas (escenario de prueba).
    // Los numeros los decide el core; aqui solo se aplican via SDK/datarefs.
    // Payload:
    //   f64 userLat, userLon, userElevMsl, userHdgTrue, userSpeedMps
    //   f64 aiLat, aiLon, aiElevMsl, aiHdgTrue, aiSpeedMps
    //   str aiAircraftRelPath  (relativo a la raiz de X-Plane; vacio = C172)
    PlaceScenario = 0x0D,
    // Exclusive access a un avion IA (AcquirePlanes + DisableAIForPlane).
    // El core escribe posiciones via Hold; el connector solo otorga/suelta.
    // Payload:
    //   u8 action      0 = Release (XPLMReleasePlanes si teniamos acquire)
    //                  1 = Take    (AcquirePlanes NULL + DisableAIForPlane)
    //   u8 planeIndex  indice XPLM 1..19 (nunca 0)
    AiControl = 0x0E,
    // Camara via XPLMControlCamera. Sin logica de dominio: el core decide.
    // Payload:
    //   u8 planeIndex  0   = soltar camara (DontControlCamera)
    //                  1..19 = chase detras de esa IA (indice XPLM)
    //                  255 = vista aerea: enmarca ownship + IAs activas
    CameraFollow = 0x0F,

    // --- connector -> core ---
    HelloAck = 0x81,
    DefineAck = 0x82,
    Telemetry = 0x83,
    Event = 0x84,
    Pong = 0x85,
    // Aviones activos de la partida (el del usuario en el indice 0 y las
    // IAs a continuacion). Lo arma el connector con XPLMCountAircraft /
    // XPLMGetNthAircraftModel: el modelo no es un dataref, asi que no puede
    // pedirlo el core por DEFINE. La posicion y la velocidad de las IAs si
    // lo son (sim/multiplayer/position/planeN_*) y viajan por telemetria.
    Planes = 0x86,
};

// Que se esta definiendo: un dataref (XPLMFindDataRef) o un comando
// (XPLMFindCommand). Comparten espacio de ids para que el core lleve una
// sola tabla.
enum class EntryKind : uint8_t {
    Dataref = 0,
    Command = 1,
};

// Espejo de xplmType_* de XPLMDataAccess.h, pero compactado a valores
// consecutivos (los del SDK son un campo de bits: 1, 2, 4, 8, 16, 32).
enum class DataKind : uint8_t {
    Unknown = 0,
    Int = 1,
    Float = 2,
    Double = 3,
    IntArray = 4,
    FloatArray = 5,
    ByteArray = 6,
};

enum class HoldMode : uint8_t {
    Off = 0,    // suelta el hold; el dataref vuelve a ser del sim/usuario
    Latch = 1,  // reescribe el valor CADA frame hasta que se suelte
};

enum class CmdAction : uint8_t {
    Once = 0,   // XPLMCommandOnce
    Begin = 1,  // XPLMCommandBegin (queda "pulsado")
    End = 2,    // XPLMCommandEnd
};

enum class EventKind : uint8_t {
    Info = 0,
    Warning = 1,
    Error = 2,
    OverridesReleased = 3,  // el SafetyGuard solto todo; dice por que
    AircraftReloaded = 4,   // reinicio de situacion / avion recargado
    ScenarioReady = 5,      // PlaceScenario termino (usuario+IA colocados)
};

// ---------------------------------------------------------------------------
// Lectura/escritura de los payloads.
//
// Reader es deliberadamente paranoico: cualquier lectura que se pase del
// final marca el reader como invalido (Ok() == false) y devuelve ceros, en
// vez de leer memoria de al lado. Un paquete corrupto o de una version
// vieja tiene que dar un mensaje descartado, no un crash dentro del
// proceso de X-Plane.
// ---------------------------------------------------------------------------

class Reader {
public:
    Reader(const uint8_t* data, size_t size) : data_(data), size_(size) {}

    bool Ok() const { return ok_; }
    size_t Remaining() const { return ok_ ? size_ - pos_ : 0; }

    uint8_t U8() { return Read<uint8_t>(); }
    uint16_t U16() { return Read<uint16_t>(); }
    uint32_t U32() { return Read<uint32_t>(); }
    int32_t I32() { return Read<int32_t>(); }
    float F32() { return Read<float>(); }
    double F64() { return Read<double>(); }

    // Strings: u16 con la longitud en bytes + los bytes (UTF-8, sin
    // terminador). Las rutas de dataref son ASCII puro, pero UTF-8 sale
    // gratis y evita sorpresas si algun dia hay texto de usuario.
    std::string Str() {
        uint16_t len = U16();
        if (!ok_ || pos_ + len > size_) {
            ok_ = false;
            return std::string();
        }
        std::string out(reinterpret_cast<const char*>(data_ + pos_), len);
        pos_ += len;
        return out;
    }

private:
    template <typename T>
    T Read() {
        if (!ok_ || pos_ + sizeof(T) > size_) {
            ok_ = false;
            return T{};
        }
        T value;
        std::memcpy(&value, data_ + pos_, sizeof(T));
        pos_ += sizeof(T);
        return value;
    }

    const uint8_t* data_;
    size_t size_;
    size_t pos_ = 0;
    bool ok_ = true;
};

// Escribe un mensaje completo, prefijo de longitud incluido: se construye
// con el opcode, se le van metiendo campos, y Take() devuelve el buffer
// listo para mandar por el pipe tal cual.
class Writer {
public:
    explicit Writer(Op op) {
        buf_.resize(4);  // hueco para el u32 de longitud, se rellena en Take()
        U8(static_cast<uint8_t>(op));
    }

    void U8(uint8_t v) { Raw(&v, 1); }
    void U16(uint16_t v) { Raw(&v, 2); }
    void U32(uint32_t v) { Raw(&v, 4); }
    void I32(int32_t v) { Raw(&v, 4); }
    void F32(float v) { Raw(&v, 4); }
    void F64(double v) { Raw(&v, 8); }

    void Str(const std::string& s) {
        uint16_t len = static_cast<uint16_t>(s.size() > 0xFFFF ? 0xFFFF : s.size());
        U16(len);
        Raw(s.data(), len);
    }

    // Deja el buffer con el prefijo de longitud ya puesto. A partir de
    // aqui el Writer no se vuelve a usar.
    std::vector<uint8_t> Take() {
        uint32_t payload = static_cast<uint32_t>(buf_.size() - 4);
        std::memcpy(buf_.data(), &payload, 4);
        return std::move(buf_);
    }

private:
    void Raw(const void* src, size_t n) {
        const uint8_t* p = static_cast<const uint8_t*>(src);
        buf_.insert(buf_.end(), p, p + n);
    }

    std::vector<uint8_t> buf_;
};

}  // namespace proto
