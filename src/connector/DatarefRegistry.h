// DatarefRegistry.h
//
// Tabla de lo unico que el connector sabe del simulador: "el id 7 es este
// dataref, de este tipo, y se lee/escribe asi". El core manda DEFINE una
// vez con la ruta completa y se queda con el id; a partir de ahi ni el
// cable ni este fichero vuelven a ver un string.
//
// Aqui NO hay ninguna ruta de dataref escrita a mano, y es a proposito:
// que datarefs importan es una decision del core (ver
// src/core/Connector/Datarefs.cs). Este fichero solo sabe resolver
// cualquier nombre que le den y convertir entre el tipo real del dataref
// y el double que viaja por el protocolo.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "XPLMDataAccess.h"
#include "XPLMUtilities.h"

#include "Protocol.h"

class DatarefRegistry {
public:
    // Tope de ids. Es una red de seguridad contra un mensaje corrupto que
    // diga "id 60000" y nos haga reservar memoria a lo tonto; ningun uso
    // real se acerca ni de lejos.
    static constexpr uint16_t kMaxEntries = 4096;

    struct Entry {
        bool defined = false;   // el core lo pidio
        bool resolved = false;  // ...y X-Plane lo encontro
        proto::EntryKind kind = proto::EntryKind::Dataref;
        std::string name;

        XPLMDataRef dataref = nullptr;
        XPLMCommandRef command = nullptr;

        proto::DataKind dataKind = proto::DataKind::Unknown;
        int arrayLength = 0;
        bool writable = false;

        // Solo para datarefs de tipo array: el core dice con que trozo
        // quiere hablar. Al LEER se devuelve el elemento `index`; al
        // ESCRIBIR se escribe el mismo valor en `count` elementos a
        // partir de `index`. Eso cubre de un golpe los dos usos reales
        // que teniamos en el C++ viejo: leer ENGN_thro_use[0] como
        // readback, y escribir el mismo throttle en los 8 motores.
        int index = 0;
        int count = 1;

        // true entre un CommandBegin y su CommandEnd. El SafetyGuard lo
        // necesita: un comando que se quedo "pulsado" porque el core
        // murio a mitad es tan malo como un override enganchado.
        bool commandHeld = false;

        // Eje de posicion local (ver proto::FrameAxis). Lo declara el core.
        proto::FrameAxis frame = proto::FrameAxis::None;
    };

    // Resuelve (o re-resuelve) una entrada. Devuelve nullptr solo si el id
    // esta fuera de rango; si el dataref no existe en este avion, devuelve
    // la entrada con resolved == false para que el connector se lo pueda
    // contar al core en el DEFINE_ACK.
    Entry* Define(uint16_t id, proto::EntryKind kind, const std::string& name,
                  int index, int count,
                  proto::FrameAxis frame = proto::FrameAxis::None);

    // Desplazamiento acumulado entre el marco local REAL de X-Plane y el
    // marco estable que ve el core: estable = real + offset. Read() lo suma
    // y Write() lo resta en las entradas con `frame` (X/Z).
    void SetOriginOffset(double x, double z) { offX_ = x; offZ_ = z; }
    double OriginOffsetX() const { return offX_; }
    double OriginOffsetZ() const { return offZ_; }

    Entry* Get(uint16_t id);

    // Vuelve a pasar por XPLMFindDataRef/XPLMFindCommand todas las
    // entradas ya definidas, sin tocar los ids. Se llama cuando cambia el
    // avion del usuario: los datarefs que publica el avion (no los de
    // sim/) desaparecen con el plugin viejo y los handles cacheados dejan
    // de valer. El core no se entera de nada, que es como tiene que ser.
    void ReresolveAll();

    void Clear();

    // Suelta todos los comandos que quedaron entre un Begin y su End.
    // Devuelve cuantos habia. Un comando pulsado y nunca soltado es tan
    // pegajoso como un override enganchado: por ejemplo, un "flaps arriba"
    // mantenido sigue moviendo el avion despues de que el core muera.
    int EndAllHeldCommands();

    double Read(const Entry& e) const;  // traduce al marco estable (ver SetOriginOffset)
    void Write(const Entry& e, double value);

private:
    // Traduce el campo de bits de XPLMGetDataRefTypes (xplmType_Int=1,
    // _Float=2, _Double=4, _FloatArray=8, _IntArray=16, _Data=32; un
    // dataref puede anunciar varios) a un solo DataKind, que es lo que
    // decide con que par de funciones del SDK lo tocamos.
    static proto::DataKind PickKind(XPLMDataTypeID types);
    void Resolve(Entry& e);
    double ReadRaw(const Entry& e) const;

    std::vector<Entry> entries_;
    double offX_ = 0.0;
    double offZ_ = 0.0;
};
