// Subscriptions.h
//
// Que datarefs se emiten al core y cada cuantos frames. El core suscribe
// una vez y a partir de ahi recibe telemetria sola -- no hay que pedirla,
// que es la diferencia entre un puente y un servidor de peticiones: con
// peticion-respuesta cada lectura costaria un frame de ida y otro de
// vuelta, y con los PID viviendo en el core eso seria retraso metido
// directamente en el lazo de control.
//
// El divisor es por dataref a proposito: la actitud y la velocidad
// interesan cada frame porque alimentan los PID, pero el QNH o el estado
// del tren cambian cada varios segundos y no hay razon para mandarlos 60
// veces por segundo.
#pragma once

#include <algorithm>
#include <cstdint>
#include <vector>

#include "DatarefRegistry.h"
#include "Protocol.h"

class Subscriptions {
public:
    void Set(uint16_t id, uint8_t divisor) {
        if (divisor == 0) divisor = 1;
        for (auto& s : subs_) {
            if (s.id == id) { s.divisor = divisor; return; }
        }
        subs_.push_back(Sub{id, divisor});
    }

    void Remove(uint16_t id) {
        subs_.erase(std::remove_if(subs_.begin(), subs_.end(),
                                   [id](const Sub& s) { return s.id == id; }),
                    subs_.end());
    }

    void Clear() { subs_.clear(); }
    bool Empty() const { return subs_.empty(); }
    size_t Count() const { return subs_.size(); }

    // Arma el payload de TELEMETRY con los datarefs que tocan en este
    // frame. Devuelve false si no toca ninguno, para no gastar un mensaje
    // vacio.
    bool BuildTelemetry(uint32_t frame, float dt, DatarefRegistry& registry,
                        proto::Writer& w) const {
        // Se recogen primero los valores y luego se escriben porque el
        // Writer no sabe volver atras a rellenar el contador.
        std::vector<std::pair<uint16_t, double>> values;
        values.reserve(subs_.size());

        for (const Sub& s : subs_) {
            if (frame % s.divisor != 0) continue;
            DatarefRegistry::Entry* e = registry.Get(s.id);
            if (!e || !e->resolved) continue;
            values.emplace_back(s.id, registry.Read(*e));
        }

        if (values.empty()) return false;

        w.U32(frame);
        w.F32(dt);
        w.U16(static_cast<uint16_t>(values.size()));
        for (const auto& [id, v] : values) {
            w.U16(id);
            w.F64(v);
        }
        return true;
    }

private:
    struct Sub {
        uint16_t id;
        uint8_t divisor;
    };

    std::vector<Sub> subs_;
};
