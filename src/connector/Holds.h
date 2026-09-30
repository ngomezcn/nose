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
#include "Logger.h"
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
        cadRegistry_ = &registry;

        for (auto& [id, h] : holds_) {
            DatarefRegistry::Entry* e = registry.Get(id);
            if (!e) continue;
            // Un pulse en curso manda sobre el hold; el hold vuelve a
            // mandar solo cuando el pulse expira.
            if (pulses_.count(id)) continue;

            // Diagnostico: lo que hay en el dataref ANTES de reescribirlo contra
            // lo que escribimos el frame anterior. Si difieren, alguien (X-Plane)
            // movio el avion entre medias y lo que se dibuja no es lo que el
            // core cree. Solo lee; no cambia lo que se escribe.
            if (h.cad.primed && IsPlanePosition(e->name)) {
                double ext = registry.Read(*e) - h.lastWritten;
                h.cad.extSum += ext;
                double a = std::fabs(ext);
                if (a > 1e-3) ++h.cad.extCount;
                h.cad.extMax = std::max(h.cad.extMax, a);
            }

            if (h.ratePerSec > 0.0f && dt > 0.0f) {
                double maxDelta = static_cast<double>(h.ratePerSec) * dt;
                double delta = std::clamp(h.target - h.current, -maxDelta, maxDelta);
                h.current += delta;
            } else {
                h.current = h.target;
            }
            double written = h.current;
            if (kExtrapolatePlanes && dt > 0.0f) {
                const Hold* v = VelocityHold(id, h, *e, registry);
                if (v) written += v->target * static_cast<double>(dt);
            }
            registry.Write(*e, written);
            h.lastWritten = written;
            Observe(e->name, h);
        }
        ReportCadence(now);

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
    // Diagnostico de cadencia (solo mide, no cambia lo que se escribe): para
    // los holds de POSICION de las IAs cuenta, por frame de X-Plane, si el valor
    // escrito avanzo. Un hold que se mueve cada frame y de pronto repite el
    // mismo valor (stale) o avanza el doble es un tiron visible: el core manda
    // la pose por el pipe y aqui se ve con que regularidad llega de verdad.
    struct Cadence {
        bool primed = false;
        double last = 0.0;
        double meanStep = 0.0;
        uint32_t frames = 0, stale = 0, doubles = 0, staleRun = 0, maxStaleRun = 0;
        double maxStep = 0.0;
        // Readback antes de reescribir: diferencia con lo escrito el frame anterior.
        double extSum = 0.0, extMax = 0.0;
        uint32_t extCount = 0;
    };

    struct Hold {
        double target = 0.0;
        double current = 0.0;
        double previous = 0.0;  // lo que habia antes: a esto se vuelve al soltar
        float ratePerSec = 0.0f;
        uint32_t seq = 0;
        Cadence cad;
        double lastWritten = 0.0;   // lo ultimo escrito (con la extrapolacion)
        // Solo holds de posicion de IA: hold de velocidad asociado (x -> v_x).
        int8_t posKind = -1;        // -1 sin resolver, 0 no es posicion, 1 lo es
        bool velResolved = false;
        uint16_t velId = 0;
        uint32_t velRetry = 0;
    };

    // -- Extrapolacion de la pose de las IAs al dt EXACTO del frame ------------
    //
    // El core integra la pose con el dt del frame N y la manda por el pipe; el
    // connector la escribe en el frame N+1, que dura otro dt. El ownship, que lo
    // mueve X-Plane, avanza en ese frame por su dt real, asi que la IA se queda
    // atras exactamente dt(N+1) * v. Como los frames no duran lo mismo (13-45 ms)
    // ese retraso baila y la IA vista desde otro avion va a tirones. Escribiendo
    // pos + v * dt(N+1) la IA queda en el instante del frame que se dibuja.
    // No decide nada: usa la velocidad que el propio core mando en su hold.
    static constexpr bool kExtrapolatePlanes = true;

    static bool IsPlanePosition(const std::string& n) {
        if (n.find("sim/multiplayer/position/plane") != 0) return false;
        if (n.find("_v_") != std::string::npos) return false;   // velocidades
        size_t k = n.size();
        return k >= 2 && (n.compare(k - 2, 2, "_x") == 0 || n.compare(k - 2, 2, "_y") == 0 ||
                          n.compare(k - 2, 2, "_z") == 0);
    }

    // planeN_x -> planeN_v_x (idem y, z).
    static std::string VelocityName(const std::string& n) {
        size_t k = n.size();
        return n.substr(0, k - 2) + "_v" + n.substr(k - 2);
    }

    // Hold de velocidad que corresponde a este hold de posicion, o nullptr.
    const Hold* VelocityHold(uint16_t id, Hold& h, const DatarefRegistry::Entry& e,
                             DatarefRegistry& registry) {
        if (h.posKind < 0) h.posKind = IsPlanePosition(e.name) ? 1 : 0;
        if (h.posKind == 0) return nullptr;
        if (!h.velResolved) {
            // Reintento cada 30 frames: el hold de velocidad llega en el mismo
            // mensaje que el de posicion, pero no hay garantia de orden.
            if (h.velRetry++ % 30 != 0) return nullptr;
            const std::string want = VelocityName(e.name);
            for (const auto& [vid, vh] : holds_) {
                (void)vh;
                DatarefRegistry::Entry* ve = registry.Get(vid);
                if (ve && vid != id && ve->name == want) {
                    h.velId = vid;
                    h.velResolved = true;
                    break;
                }
            }
            if (!h.velResolved) return nullptr;
        }
        auto it = holds_.find(h.velId);
        if (it == holds_.end()) { h.velResolved = false; return nullptr; }
        return &it->second;
    }

    void Observe(const std::string& name, Hold& h) {
        if (!IsPlanePosition(name)) return;
        Cadence& c = h.cad;
        if (!c.primed) { c.primed = true; c.last = h.current; return; }
        double step = std::fabs(h.current - c.last);
        c.last = h.current;
        ++c.frames;
        if (step == 0.0) {
            // Solo cuenta como stale si el hold ya venia moviendose.
            if (c.meanStep > 0.0) {
                ++c.stale;
                if (++c.staleRun > c.maxStaleRun) c.maxStaleRun = c.staleRun;
            }
            return;
        }
        c.staleRun = 0;
        if (c.meanStep > 0.0 && step > 1.6 * c.meanStep) ++c.doubles;
        c.maxStep = std::max(c.maxStep, step);
        c.meanStep = c.meanStep == 0.0 ? step : c.meanStep * 0.95 + step * 0.05;
    }

    // Cada ~5 s vuelca al log una linea por hold de posicion que se movio y tuvo
    // algun frame sin avanzar o con avance doble; si todo va regular, no dice nada.
    void ReportCadence(uint64_t now) {
        if (now < nextReportMs_) return;
        nextReportMs_ = now + 5000;
        DatarefRegistry* reg = cadRegistry_;
        for (auto& [id, h] : holds_) {
            Cadence& c = h.cad;
            if (c.frames > 0 && (c.stale > 0 || c.doubles > 0 || c.extCount > 0) && reg) {
                DatarefRegistry::Entry* e = reg->Get(id);
                LogInfo("Cadencia %s: %u frames, %u stale (racha max %u), %u dobles, "
                        "paso medio %.3f max %.3f | externo: %u frames, suma %.3f max %.3f",
                        e ? e->name.c_str() : "?", c.frames, c.stale, c.maxStaleRun,
                        c.doubles, c.meanStep, c.maxStep, c.extCount, c.extSum, c.extMax);
            }
            c.frames = c.stale = c.doubles = c.maxStaleRun = c.extCount = 0;
            c.maxStep = c.extSum = c.extMax = 0.0;
        }
    }

    uint64_t nextReportMs_ = 0;
    DatarefRegistry* cadRegistry_ = nullptr;

    struct PulseState {
        double value = 0.0;
        double previous = 0.0;
        uint64_t expiresAtMs = 0;
    };

    std::map<uint16_t, Hold> holds_;
    std::map<uint16_t, PulseState> pulses_;
    uint32_t nextSeq_ = 0;
};
