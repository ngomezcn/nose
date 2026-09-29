#include "DatarefRegistry.h"

#include <algorithm>
#include <cmath>

#include "Logger.h"

namespace {

// Tope de elementos que escribimos de una tacada en un dataref de array.
// El uso real es "el mismo throttle en los N motores" (8 de sobra para
// cualquier avion civil); el tope existe para poder usar un buffer de pila
// y no tener que reservar memoria en mitad del flight loop.
constexpr int kMaxArrayWrite = 64;

}  // namespace

proto::DataKind DatarefRegistry::PickKind(XPLMDataTypeID types) {
    // Los arrays van primero porque son excluyentes con los escalares: un
    // dataref de array nunca se anuncia tambien como Float suelto, asi que
    // si vemos el bit de array, ese es el tipo y punto.
    if (types & xplmType_FloatArray) return proto::DataKind::FloatArray;
    if (types & xplmType_IntArray) return proto::DataKind::IntArray;
    if (types & xplmType_Data) return proto::DataKind::ByteArray;

    // Entre los escalares si hay solapamiento: muchisimos datarefs de
    // X-Plane se anuncian como Float Y Double a la vez (lo dice el propio
    // XPLMDataAccess.h). Preferimos Float porque es lo que devuelve el
    // simulador de verdad -- leerlo como double solo añade una conversion
    // que no aporta precision que no estuviera ya perdida.
    if (types & xplmType_Float) return proto::DataKind::Float;
    if (types & xplmType_Double) return proto::DataKind::Double;
    if (types & xplmType_Int) return proto::DataKind::Int;
    return proto::DataKind::Unknown;
}

void DatarefRegistry::Resolve(Entry& e) {
    e.resolved = false;
    e.dataref = nullptr;
    e.command = nullptr;
    e.dataKind = proto::DataKind::Unknown;
    e.arrayLength = 0;
    e.writable = false;

    if (e.kind == proto::EntryKind::Command) {
        e.command = XPLMFindCommand(e.name.c_str());
        e.resolved = (e.command != nullptr);
        return;
    }

    e.dataref = XPLMFindDataRef(e.name.c_str());
    if (!e.dataref) return;

    e.dataKind = PickKind(XPLMGetDataRefTypes(e.dataref));
    if (e.dataKind == proto::DataKind::Unknown) return;

    e.writable = XPLMCanWriteDataRef(e.dataref) != 0;

    // Pasando nullptr como buffer, las funciones de array devuelven el
    // tamaño del array e ignoran offset/max. Se lo mandamos al core en el
    // DEFINE_ACK para que sepa cuantos motores/ruedas/lo que sea hay de
    // verdad en este avion en vez de asumir un numero.
    switch (e.dataKind) {
        case proto::DataKind::FloatArray:
            e.arrayLength = XPLMGetDatavf(e.dataref, nullptr, 0, 0);
            break;
        case proto::DataKind::IntArray:
            e.arrayLength = XPLMGetDatavi(e.dataref, nullptr, 0, 0);
            break;
        case proto::DataKind::ByteArray:
            e.arrayLength = XPLMGetDatab(e.dataref, nullptr, 0, 0);
            break;
        default:
            break;
    }

    e.resolved = true;
}

DatarefRegistry::Entry* DatarefRegistry::Define(uint16_t id, proto::EntryKind kind,
                                                const std::string& name, int index,
                                                int count) {
    if (id >= kMaxEntries) return nullptr;
    if (entries_.size() <= id) entries_.resize(id + 1);

    Entry& e = entries_[id];

    // Redefinir un id que estaba con un comando pulsado lo soltaria sin
    // que nadie lo suelte. Es un caso raro (el core no reusa ids), pero
    // dejarlo pulsado para siempre es justo el fallo que este connector
    // existe para no tener.
    if (e.commandHeld && e.command) {
        XPLMCommandEnd(e.command);
        e.commandHeld = false;
    }

    e.defined = true;
    e.kind = kind;
    e.name = name;
    e.index = std::max(0, index);
    e.count = std::clamp(count, 1, kMaxArrayWrite);
    Resolve(e);

    if (!e.resolved) {
        LogInfo("DEFINE id=%u '%s' NO resuelto (no existe en este avion?)", id,
                name.c_str());
    }
    return &e;
}

DatarefRegistry::Entry* DatarefRegistry::Get(uint16_t id) {
    if (id >= entries_.size()) return nullptr;
    Entry& e = entries_[id];
    return e.defined ? &e : nullptr;
}

void DatarefRegistry::ReresolveAll() {
    int ok = 0, failed = 0;
    for (Entry& e : entries_) {
        if (!e.defined) continue;
        e.commandHeld = false;  // el comando de antes ya no existe
        Resolve(e);
        if (e.resolved) ++ok; else ++failed;
    }
    LogInfo("Datarefs re-resueltos tras cambio de avion: %d ok, %d fallidos.", ok, failed);
}

int DatarefRegistry::EndAllHeldCommands() {
    int n = 0;
    for (Entry& e : entries_) {
        if (!e.commandHeld) continue;
        if (e.command) XPLMCommandEnd(e.command);
        e.commandHeld = false;
        ++n;
    }
    return n;
}

void DatarefRegistry::Clear() {
    for (Entry& e : entries_) {
        if (e.commandHeld && e.command) XPLMCommandEnd(e.command);
    }
    entries_.clear();
}

double DatarefRegistry::Read(const Entry& e) const {
    if (!e.resolved || !e.dataref) return 0.0;

    switch (e.dataKind) {
        case proto::DataKind::Int:
            return static_cast<double>(XPLMGetDatai(e.dataref));
        case proto::DataKind::Float:
            return static_cast<double>(XPLMGetDataf(e.dataref));
        case proto::DataKind::Double:
            return XPLMGetDatad(e.dataref);
        case proto::DataKind::FloatArray: {
            float v = 0.0f;
            XPLMGetDatavf(e.dataref, &v, e.index, 1);
            return static_cast<double>(v);
        }
        case proto::DataKind::IntArray: {
            int v = 0;
            XPLMGetDatavi(e.dataref, &v, e.index, 1);
            return static_cast<double>(v);
        }
        case proto::DataKind::ByteArray: {
            unsigned char v = 0;
            XPLMGetDatab(e.dataref, &v, e.index, 1);
            return static_cast<double>(v);
        }
        default:
            return 0.0;
    }
}

void DatarefRegistry::Write(const Entry& e, double value) {
    if (!e.resolved || !e.dataref || !e.writable) return;

    switch (e.dataKind) {
        case proto::DataKind::Int:
            // lround y no un cast: un cast trunca, asi que un PID del core
            // que mande 0.9999 para "tren abajo = 1" acabaria escribiendo 0.
            XPLMSetDatai(e.dataref, static_cast<int>(std::lround(value)));
            break;
        case proto::DataKind::Float:
            XPLMSetDataf(e.dataref, static_cast<float>(value));
            break;
        case proto::DataKind::Double:
            XPLMSetDatad(e.dataref, value);
            break;
        case proto::DataKind::FloatArray: {
            float buf[kMaxArrayWrite];
            float v = static_cast<float>(value);
            for (int i = 0; i < e.count; ++i) buf[i] = v;
            XPLMSetDatavf(e.dataref, buf, e.index, e.count);
            break;
        }
        case proto::DataKind::IntArray: {
            int buf[kMaxArrayWrite];
            int v = static_cast<int>(std::lround(value));
            for (int i = 0; i < e.count; ++i) buf[i] = v;
            XPLMSetDatavi(e.dataref, buf, e.index, e.count);
            break;
        }
        case proto::DataKind::ByteArray: {
            unsigned char buf[kMaxArrayWrite];
            unsigned char v = static_cast<unsigned char>(
                std::clamp(std::lround(value), 0L, 255L));
            for (int i = 0; i < e.count; ++i) buf[i] = v;
            XPLMSetDatab(e.dataref, buf, e.index, e.count);
            break;
        }
        default:
            break;
    }
}
