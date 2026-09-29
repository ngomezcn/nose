// SafetyGuard.h
//
// La pieza que la arquitectura nueva hace obligatoria.
//
// Mientras la maquina de estados vivia dentro del plugin, "el que decide"
// y "el que escribe en los datarefs" eran el mismo proceso: si moria uno
// moria el otro, y X-Plane llamaba a XPluginDisable y ahi se soltaba todo.
// Ahora quien decide es otro proceso, que se puede cerrar, colgar, quedarse
// en un breakpoint o petar a mitad de la rotacion. Si eso pasa con los
// overrides de cabeceo/alabeo/guiñada/motores puestos, X-Plane sigue
// creyendo que un plugin tiene el control exclusivo de esos ejes y el
// avion queda IMPOSIBLE de pilotar a mano -- no es que se pilote mal: los
// mandos del usuario dejan de tener efecto.
//
// Por eso el connector no confia en que el core se despida bien. Suelta
// todo por su cuenta ante cualquiera de estas cuatro cosas:
//
//   1. El pipe se rompe (el core cerro o murio). Es el caso limpio.
//   2. El core sigue conectado pero lleva kHeartbeatTimeoutMs sin decir
//      nada. Cubre el core colgado o en un breakpoint, donde el socket
//      sigue abierto y el caso 1 no salta.
//   3. X-Plane nos desactiva (XPluginDisable).
//   4. Cambia la situacion: choque, aeropuerto nuevo, avion recargado.
//      Esto ya estaba cubierto en el plugin viejo y se conserva.
#pragma once

#include <cstdint>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "DatarefRegistry.h"
#include "Holds.h"
#include "Logger.h"
#include "PipeServer.h"
#include "Protocol.h"

class SafetyGuard {
public:
    // Devuelve el motivo por el que solto, o nullptr si no hizo falta.
    // Se llama una vez por frame, lo primero de todo.
    const char* Update(const PipeServer& pipe, Holds& holds, DatarefRegistry& registry) {
        bool connected = pipe.IsConnected();

        if (!connected) {
            if (wasConnected_) {
                wasConnected_ = false;
                if (holds.AnyActive()) {
                    return ReleaseAll("el core se desconecto", holds, registry);
                }
            }
            return nullptr;
        }

        wasConnected_ = true;

        // Solo miramos el latido si hay algo que soltar. Un core conectado
        // y callado que no esta tocando nada no es un problema, y avisar
        // de ello solo llenaria el log de ruido.
        if (!holds.AnyActive()) return nullptr;

        uint64_t silentMs = GetTickCount64() - pipe.LastInboundTickMs();
        if (silentMs >= proto::kHeartbeatTimeoutMs) {
            return ReleaseAll("el core lleva demasiado tiempo sin responder", holds,
                              registry);
        }
        return nullptr;
    }

    // Suelta todo aqui y ahora. Publico porque el plugin lo llama tambien
    // desde XPluginDisable y desde XPluginReceiveMessage.
    const char* ReleaseAll(const char* reason, Holds& holds, DatarefRegistry& registry) {
        size_t heldCount = holds.ActiveCount();
        holds.ReleaseAll(registry);
        int commands = registry.EndAllHeldCommands();
        LogInfo("SUELTO TODO (%s): %zu holds restaurados, %d comandos soltados.",
                reason, heldCount, commands);
        return reason;
    }

    void NoteConnected() { wasConnected_ = true; }

private:
    bool wasConnected_ = false;
};
