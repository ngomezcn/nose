// AiControl.h
//
// Aplica Op.AiControl: otorga o suelta acceso exclusivo a aviones IA via
// XPLMAcquirePlanes / XPLMDisableAIForPlane / XPLMReleasePlanes. El core
// escribe posiciones con Holds existentes; aqui solo se gestiona el acceso.
//
// Se controla un CONJUNTO de indices (bitmask, bit N = plane N, 1..19).
// Semantica del SDK: AcquirePlanes da acceso exclusivo al plugin (una sola
// vez, no por avion); DisableAIForPlane apaga la IA de un avion concreto y
// no hay API para reactivarla individualmente: solo XPLMReleasePlanes
// devuelve todo. Por eso Release(idx) quita del conjunto y solo cuando
// queda vacio se llama a XPLMReleasePlanes (las IAs quitadas antes siguen
// sin IA propia hasta ese momento).
#pragma once

#include <cstdint>

#include "XPLMPlanes.h"

#include "Logger.h"

namespace ai_control {

inline bool g_acquired = false;
inline uint32_t g_mask = 0;  // bit N = plane N controlado (N = 1..19)

inline bool IsValid(uint8_t idx) { return idx >= 1 && idx <= 19; }

// Suelta todo (ReleaseEverything, XPluginDisable, action=0 con idx 0).
inline void Release() {
    if (!g_acquired) {
        g_mask = 0;
        return;
    }
    XPLMReleasePlanes();
    LogInfo("AiControl: ReleasePlanes (mascara=0x%X).",
            static_cast<unsigned>(g_mask));
    g_acquired = false;
    g_mask = 0;
}

// Quita solo ese indice; ReleasePlanes unicamente si el conjunto queda vacio.
inline void Release(uint8_t planeIndex) {
    if (planeIndex == 0) {
        Release();
        return;
    }
    if (!IsValid(planeIndex)) return;
    const uint32_t bit = 1u << planeIndex;
    if (!(g_mask & bit)) return;
    g_mask &= ~bit;
    LogInfo("AiControl: Release planeIndex=%u (quedan mascara=0x%X).",
            static_cast<unsigned>(planeIndex), static_cast<unsigned>(g_mask));
    if (g_mask == 0) {
        Release();
    }
}

// AcquirePlanes(NULL) (solo la primera vez) + DisableAIForPlane(planeIndex).
// planeIndex debe ser 1..19. Anade al conjunto sin liberar los demas.
inline bool Take(uint8_t planeIndex) {
    if (!IsValid(planeIndex)) return false;
    const uint32_t bit = 1u << planeIndex;
    if (g_acquired && (g_mask & bit)) {
        LogInfo("AiControl: Take planeIndex=%u (ya adquirido).",
                static_cast<unsigned>(planeIndex));
        return true;
    }

    if (!g_acquired) {
        if (!XPLMAcquirePlanes(nullptr, nullptr, nullptr)) {
            LogWarn("AiControl: XPLMAcquirePlanes fallo (planeIndex=%u).",
                    static_cast<unsigned>(planeIndex));
            return false;
        }
        g_acquired = true;
    }

    XPLMDisableAIForPlane(static_cast<int>(planeIndex));
    g_mask |= bit;
    LogInfo("AiControl: Take ok planeIndex=%u (mascara=0x%X).",
            static_cast<unsigned>(planeIndex), static_cast<unsigned>(g_mask));
    return true;
}

// Da por adquiridos los aviones ya obtenidos con XPLMAcquirePlanes fuera de
// este modulo (ScenarioPlace) y apaga la IA nativa de ese avion, sin soltar:
// asi X-Plane no lo mueve mientras el core no lo ha tomado.
inline void Adopt(uint8_t planeIndex) {
    if (!IsValid(planeIndex)) return;
    XPLMDisableAIForPlane(static_cast<int>(planeIndex));
    g_acquired = true;
    g_mask |= 1u << planeIndex;
    LogInfo("AiControl: Adopt planeIndex=%u (mascara=0x%X).",
            static_cast<unsigned>(planeIndex), static_cast<unsigned>(g_mask));
}

}  // namespace ai_control
