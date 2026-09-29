// ScenarioPlace.h
//
// Aplica Op.PlaceScenario: teleporta al usuario (XPLMPlaceUserAtLocation) y,
// tras cargar el aeropuerto, coloca una IA en lat/lon fijos con velocidad y
// rumbo, sin DisableAI — al soltar XPLMReleasePlanes el sim la vuela solo.
// Los numeros llegan del core; aqui no hay "escenario LEBL", solo el puente.
#pragma once

#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>

#include "XPLMDataAccess.h"
#include "XPLMGraphics.h"
#include "XPLMPlanes.h"
#include "XPLMUtilities.h"

#include "Logger.h"

namespace scenario {

inline constexpr const char* kDefaultAiRelPath =
    "Aircraft/Laminar Research/Cessna Citation X/Cessna_CitationX.acf";

struct PendingAi {
    bool active = false;
    bool waitAirport = false;
    int framesLeft = 0;
    int timeoutFrames = 0;
    double lat = 0;
    double lon = 0;
    float elevMsl = 0;
    float hdgTrue = 0;
    float speedMps = 0;
    std::string acfRelPath;
};

inline PendingAi g_pendingAi;

inline void WriteDouble(const char* path, double v) {
    XPLMDataRef r = XPLMFindDataRef(path);
    if (!r) return;
    if (XPLMGetDataRefTypes(r) & xplmType_Double) XPLMSetDatad(r, v);
    else if (XPLMGetDataRefTypes(r) & xplmType_Float) XPLMSetDataf(r, static_cast<float>(v));
}

inline void WriteFloat(const char* path, float v) {
    XPLMDataRef r = XPLMFindDataRef(path);
    if (!r) return;
    if (XPLMGetDataRefTypes(r) & xplmType_Float) XPLMSetDataf(r, v);
    else if (XPLMGetDataRefTypes(r) & xplmType_Double) XPLMSetDatad(r, v);
}

inline void WriteFloatArray0(const char* path, float v) {
    XPLMDataRef r = XPLMFindDataRef(path);
    if (!r) return;
    XPLMSetDatavf(r, &v, 0, 1);
}

inline std::string ResolveAircraftPath(const std::string& relOrEmpty) {
    const char* rel = relOrEmpty.empty() ? kDefaultAiRelPath : relOrEmpty.c_str();
    char sys[512] = {};
    XPLMGetSystemPath(sys);
    std::string full = std::string(sys) + rel;
    for (char& c : full) {
        if (c == '/') c = '\\';
    }
    return full;
}

// Coloca la IA en el slot 1 (plane1_*). Requiere flight loop / hilo principal.
inline bool PlaceAiNow(const PendingAi& p) {
    std::string fullPath = ResolveAircraftPath(p.acfRelPath);
    const char* want[] = { fullPath.c_str(), nullptr };

    if (!XPLMAcquirePlanes(want, nullptr, nullptr)) {
        // Sin modelo concreto: quizá otro plugin tiene el control, o ya hay
        // IAs cargadas. Reintentamos sin forzar path.
        if (!XPLMAcquirePlanes(nullptr, nullptr, nullptr)) {
            LogWarn("PlaceScenario: no se pudo XPLMAcquirePlanes para la IA.");
            return false;
        }
    }

    int total = 0;
    int active = 0;
    XPLMCountAircraft(&total, &active, nullptr);
    if (active < 2) {
        XPLMSetActiveAircraftCount(total >= 2 ? 2 : total);
    }

    // Si Acquire con path no cargo el modelo, forzar slot 1.
    char fileName[256] = {};
    char pathBuf[512] = {};
    XPLMGetNthAircraftModel(1, fileName, pathBuf);
    if (fileName[0] == '\0' && !fullPath.empty()) {
        XPLMSetAircraftModel(1, fullPath.c_str());
    }

    double x = 0, y = 0, z = 0;
    XPLMWorldToLocal(p.lat, p.lon, p.elevMsl, &x, &y, &z);

    // Marco OpenGL de X-Plane: +X este, +Y arriba, +Z sur.
    const double rad = p.hdgTrue * (3.14159265358979323846 / 180.0);
    const float vx = static_cast<float>(p.speedMps * std::sin(rad));
    const float vz = static_cast<float>(-p.speedMps * std::cos(rad));

    WriteDouble("sim/multiplayer/position/plane1_x", x);
    WriteDouble("sim/multiplayer/position/plane1_y", y);
    WriteDouble("sim/multiplayer/position/plane1_z", z);
    WriteFloat("sim/multiplayer/position/plane1_psi", p.hdgTrue);
    WriteFloat("sim/multiplayer/position/plane1_the", 2.0f);
    WriteFloat("sim/multiplayer/position/plane1_phi", 0.0f);
    WriteFloat("sim/multiplayer/position/plane1_v_x", vx);
    WriteFloat("sim/multiplayer/position/plane1_v_y", 0.0f);
    WriteFloat("sim/multiplayer/position/plane1_v_z", vz);
    WriteFloatArray0("sim/multiplayer/position/plane1_gear_deploy", 0.0f);
    WriteFloatArray0("sim/multiplayer/position/plane1_throttle", 0.75f);

    // Sin DisableAI: al liberar, X-Plane retoma el piloto automatico de IA
    // desde la posicion/velocidad que acabamos de escribir.
    XPLMReleasePlanes();

    LogInfo("PlaceScenario: IA en lat=%.5f lon=%.5f elev=%.0fm hdg=%.1f spd=%.0fm/s.",
            p.lat, p.lon, p.elevMsl, p.hdgTrue, p.speedMps);
    return true;
}

inline void CancelPending() {
    g_pendingAi = PendingAi{};
}

inline void OnAirportLoaded() {
    if (!g_pendingAi.active || !g_pendingAi.waitAirport) return;
    g_pendingAi.waitAirport = false;
    g_pendingAi.framesLeft = 60;  // ~1 s a 60 Hz para asentar el origen local
}

inline bool TickPending() {
    if (!g_pendingAi.active) return false;

    ++g_pendingAi.timeoutFrames;
    if (g_pendingAi.waitAirport) {
        // Si AIRPORT_LOADED no llega (mismo aeropuerto), no nos quedamos
        // colgados: a los ~3 s colocamos la IA igual.
        if (g_pendingAi.timeoutFrames < 180) return false;
        g_pendingAi.waitAirport = false;
        g_pendingAi.framesLeft = 10;
    }

    if (g_pendingAi.framesLeft > 0) {
        --g_pendingAi.framesLeft;
        return false;
    }

    PendingAi done = g_pendingAi;
    g_pendingAi = PendingAi{};
    PlaceAiNow(done);
    return true;
}

inline void Begin(double userLat, double userLon, float userElevMsl,
                  float userHdgTrue, float userSpeedMps,
                  double aiLat, double aiLon, float aiElevMsl,
                  float aiHdgTrue, float aiSpeedMps,
                  const std::string& aiAcfRelPath) {
    // Primero agenda la IA (PlaceUser disparara AIRPORT_LOADED).
    g_pendingAi.active = true;
    g_pendingAi.waitAirport = true;
    g_pendingAi.framesLeft = 0;
    g_pendingAi.timeoutFrames = 0;
    g_pendingAi.lat = aiLat;
    g_pendingAi.lon = aiLon;
    g_pendingAi.elevMsl = aiElevMsl;
    g_pendingAi.hdgTrue = aiHdgTrue;
    g_pendingAi.speedMps = aiSpeedMps;
    g_pendingAi.acfRelPath = aiAcfRelPath;

    XPLMPlaceUserAtLocation(userLat, userLon, userElevMsl, userHdgTrue, userSpeedMps);
    LogInfo("PlaceScenario: usuario en lat=%.5f lon=%.5f elev=%.1fm hdg=%.1f.",
            userLat, userLon, userElevMsl, userHdgTrue);
}

}  // namespace scenario
