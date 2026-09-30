// AiControl.h
//
// Aplica Op.AiControl: otorga o suelta acceso exclusivo a un avion IA via
// XPLMAcquirePlanes / XPLMDisableAIForPlane / XPLMReleasePlanes. El core
// escribe posiciones con Holds existentes; aqui solo se gestiona el acceso.
#pragma once

#include <cstdint>

#include "XPLMPlanes.h"

#include "Logger.h"

namespace ai_control {

inline bool g_acquired = false;
inline uint8_t g_controlledIndex = 0;  // 0 = ninguno; XPLM 1..19 cuando activo

inline void Release() {
    if (!g_acquired) return;
    XPLMReleasePlanes();
    LogInfo("AiControl: ReleasePlanes (planeIndex=%u).",
            static_cast<unsigned>(g_controlledIndex));
    g_acquired = false;
    g_controlledIndex = 0;
}

// AcquirePlanes(NULL) + DisableAIForPlane(planeIndex). planeIndex debe ser
// 1..19 (el caller valida). Si ya controlamos otro indice, Release primero.
inline bool Take(uint8_t planeIndex) {
    if (g_acquired && g_controlledIndex == planeIndex) {
        LogInfo("AiControl: Take planeIndex=%u (ya adquirido).",
                static_cast<unsigned>(planeIndex));
        return true;
    }
    if (g_acquired) {
        Release();
    }

    if (!XPLMAcquirePlanes(nullptr, nullptr, nullptr)) {
        LogWarn("AiControl: XPLMAcquirePlanes fallo (planeIndex=%u).",
                static_cast<unsigned>(planeIndex));
        return false;
    }

    XPLMDisableAIForPlane(static_cast<int>(planeIndex));
    g_acquired = true;
    g_controlledIndex = planeIndex;
    LogInfo("AiControl: Take ok planeIndex=%u (Acquire+DisableAI).",
            static_cast<unsigned>(planeIndex));
    return true;
}

}  // namespace ai_control
