// Holds.h
//
// Las primitivas de actuacion del connector: hold (latch), toggle y pulse.
// Son la unica "inteligencia" propia que tiene el puente, y existen para
// que el core no tenga que mandar trafico por frame para cosas que son
// constantes durante minutos: "el override de cabeceo vale 1 hasta que yo
// diga", "el tren va arriba", "manten el freno de parking suelto".
//
// Esto NO es control: aqui no se decide ningun valor, solo se repite el
// que mando el core. Los PID y las etapas viven en el core.
//
// -- LA DECISION IMPORTANTE: soltar un hold RESTAURA el valor anterior ----
//
// Cuando se activa un hold se guarda el valor que tenia el dataref justo
// antes, y al soltarlo se vuelve a escribir. Podria parecer mas simple
// "soltar = dejar de escribir", pero seria un fallo de seguridad grave:
// el core pone override_joystick_pitch a 1 con un hold, y si soltar
// significara solo dejar de reescribirlo, el dataref se quedaria en 1 para
// siempre -- X-Plane seguiria creyendo que un plugin tiene el control
// exclusivo del cabeceo y el avion no se podria pilotar a mano nunca mas,
// ni con el core muerto. Con la semantica de restaurar, "suelta todo" del
// SafetyGuard devuelve el avion a su estado anterior sin que nadie tenga
// que acordarse de escribir los ceros uno a uno.
//
// -- rampa opcional ------------------------------------------------------
//
// ratePerSec limita cuanto puede cambiar el valor escrito por segundo. No
// decide el objetivo (eso es del core), solo a que ritmo se llega; con 0
// la escritura es instantanea, que es el comportamiento por defecto. Sirve
// para que un objetivo que pega un salto no se traduzca en un tiron de
// mandos, y para que perder un frame de IPC no se note.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <map>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "DatarefRegistry.h"
#include "Protocol.h"

class Holds {
public:
    void Clear() { holds_.clear(); pulses_.clear(); nextSeq_ = 0; }

    bool AnyActive() const { return !holds_.empty() || !pulses_.empty(); }
    size_t ActiveCount() const { return holds_.size(); }

    // mode Off suelta el hold (restaurando); Latch lo activa o actualiza
    // el objetivo de uno ya activo sin volver a capturar el valor previo.
    void Set(uint16_t id, proto::HoldMode mode, double value, float ratePerSec,
             DatarefRegistry& registry) {
        DatarefRegistry::Entry* e = registry.Get(id);
        if (!e || e->kind != proto::EntryKind::Dataref) return;

        if (mode == proto::HoldMode::Off) {
            Release(id, registry);
            return;
        }

        auto it = holds_.find(id);
        if (it == holds_.end()) {
            Hold h;
            h.previous = registry.Read(*e);
            h.current = h.previous;  // la rampa arranca desde donde esta
            h.seq = nextSeq_++;
            it = holds_.emplace(id, h).first;
        }
        it->second.target = value;
        it->second.ratePerSec = std::max(0.0f, ratePerSec);
    }

    // Invierte un dataref booleano. Si ese id esta bajo hold, lo que se
    // invierte es el OBJETIVO del hold -- si no, el propio hold volveria a
    // escribir el valor viejo en el mismo frame y el toggle no haria nada.
    void Toggle(uint16_t id, DatarefRegistry& registry) {
        DatarefRegistry::Entry* e = registry.Get(id);
        if (!e || e->kind != proto::EntryKind::Dataref) return;

        auto it = holds_.find(id);
        if (it != holds_.end()) {
            it->second.target = (it->second.target != 0.0) ? 0.0 : 1.0;
            return;
        }
        double now = registry.Read(*e);
        registry.Write(*e, (now != 0.0) ? 0.0 : 1.0);
    }

    // Escribe un valor durante ms milisegundos y luego devuelve el dataref
    // a donde estaba. Pensado para pulsadores de cabina y para cosas que
    // el sim lee por flanco.
    void Pulse(uint16_t id, double value, uint16_t ms, DatarefRegistry& registry) {
        DatarefRegistry::Entry* e = registry.Get(id);
        if (!e || e->kind != proto::EntryKind::Dataref) return;

        auto it = pulses_.find(id);
        if (it == pulses_.end()) {
            PulseState p;
            p.previous = registry.Read(*e);
            it = pulses_.emplace(id, p).first;
        }
        it->second.value = value;
        it->second.expiresAtMs = GetTickCount64() + ms;
    }

    // Una vez por frame, despues de procesar los mensajes entrantes y
    // antes de leer la telemetria, para que lo que se reporta sea lo que
    // de verdad hay escrito.
    void Apply(float dt, DatarefRegistry& registry) {
        uint64_t now = GetTickCount64();

        for (auto& [id, h] : holds_) {
            DatarefRegistry::Entry* e = registry.Get(id);
            if (!e) continue;
            // Un pulse en curso manda sobre el hold; el hold vuelve a
            // mandar solo cuando el pulse expira.
            if (pulses_.count(id)) continue;

            if (h.ratePerSec > 0.0f && dt > 0.0f) {
                double maxDelta = static_cast<double>(h.ratePerSec) * dt;
                double delta = std::clamp(h.target - h.current, -maxDelta, maxDelta);
                h.current += delta;
            } else {
                h.current = h.target;
            }
            registry.Write(*e, h.current);
        }

        for (auto it = pulses_.begin(); it != pulses_.end();) {
            DatarefRegistry::Entry* e = registry.Get(it->first);
            if (!e) { it = pulses_.erase(it); continue; }

            if (now < it->second.expiresAtMs) {
                registry.Write(*e, it->second.value);
                ++it;
                continue;
            }

            // Expirado: solo restauramos si nadie mas manda sobre este id.
            // Si hay un hold activo, el Apply del proximo frame ya pondra
            // su valor y restaurar aqui seria un parpadeo inutil.
            if (!holds_.count(it->first)) {
                registry.Write(*e, it->second.previous);
            }
            it = pulses_.erase(it);
        }
    }

    void Release(uint16_t id, DatarefRegistry& registry) {
        auto it = holds_.find(id);
        if (it == holds_.end()) return;
        DatarefRegistry::Entry* e = registry.Get(id);
        if (e) registry.Write(*e, it->second.previous);
        holds_.erase(it);
    }

    // Suelta todo restaurando en orden INVERSO al de activacion. Importa:
    // el core activa primero el override (override_joystick_pitch = 1) y
    // luego empieza a mover el eje (yoke_pitch_ratio). Deshaciendo al
    // reves, el eje vuelve a su sitio mientras el override todavia esta
    // activo, y solo despues se suelta el override -- asi el avion no pasa
    // ni un frame con el mando en una posicion rara ya bajo control del
    // usuario.
    void ReleaseAll(DatarefRegistry& registry) {
        std::vector<std::pair<uint32_t, uint16_t>> order;
        order.reserve(holds_.size());
        for (const auto& [id, h] : holds_) order.emplace_back(h.seq, id);
        std::sort(order.begin(), order.end(),
                  [](const auto& a, const auto& b) { return a.first > b.first; });

        for (const auto& [seq, id] : order) {
            (void)seq;
            auto it = holds_.find(id);
            DatarefRegistry::Entry* e = registry.Get(id);
            if (e) registry.Write(*e, it->second.previous);
        }
        holds_.clear();

        for (auto& [id, p] : pulses_) {
            DatarefRegistry::Entry* e = registry.Get(id);
            if (e) registry.Write(*e, p.previous);
        }
        pulses_.clear();
    }

private:
    struct Hold {
        double target = 0.0;
        double current = 0.0;
        double previous = 0.0;  // lo que habia antes: a esto se vuelve al soltar
        float ratePerSec = 0.0f;
        uint32_t seq = 0;
    };

    struct PulseState {
        double value = 0.0;
        double previous = 0.0;
        uint64_t expiresAtMs = 0;
    };

    std::map<uint16_t, Hold> holds_;
    std::map<uint16_t, PulseState> pulses_;
    uint32_t nextSeq_ = 0;
};
