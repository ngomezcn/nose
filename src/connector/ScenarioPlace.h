// ScenarioPlace.h
//
// Aplica Op.PlaceScenario: teleporta al usuario (XPLMPlaceUserAtLocation) y,
// tras cargar el aeropuerto, coloca una IA en lat/lon fijos con velocidad y
// rumbo, con su IA nativa apagada (el core la toma tras ScenarioReady).
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

#include "AiControl.h"
#include "Logger.h"

namespace scenario {

inline constexpr const char* kDefaultAiRelPath =
    "Aircraft/Laminar Research/Airbus A330-300/A330_AI.acf";

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
    bool onGround = false;   // parada en tierra: tren fuera, gas 0, sin cabeceo
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

// Escribe la pose completa de la IA (slot 1, plane1_*). Convierte lat/lon a
// local en cada llamada: si X-Plane recentra su origen local entre medias, la
// pose se recalcula bien.
inline void WriteAiPose(const PendingAi& p) {
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
    WriteFloat("sim/multiplayer/position/plane1_the", p.onGround ? 0.0f : 2.0f);
    WriteFloat("sim/multiplayer/position/plane1_phi", 0.0f);
    WriteFloat("sim/multiplayer/position/plane1_v_x", vx);
    WriteFloat("sim/multiplayer/position/plane1_v_y", 0.0f);
    WriteFloat("sim/multiplayer/position/plane1_v_z", vz);
    WriteFloatArray0("sim/multiplayer/position/plane1_gear_deploy", p.onGround ? 1.0f : 0.0f);
    WriteFloatArray0("sim/multiplayer/position/plane1_throttle", p.onGround ? 0.0f : 0.75f);
}

// Lee donde esta ahora la IA (slot 1) en lat/lon/elev MSL. false si no hay
// dataref.
inline bool ReadAiWorld(double& lat, double& lon, double& elev) {
    XPLMDataRef rx = XPLMFindDataRef("sim/multiplayer/position/plane1_x");
    XPLMDataRef ry = XPLMFindDataRef("sim/multiplayer/position/plane1_y");
    XPLMDataRef rz = XPLMFindDataRef("sim/multiplayer/position/plane1_z");
    if (!rx || !ry || !rz) return false;
    XPLMLocalToWorld(XPLMGetDatad(rx), XPLMGetDatad(ry), XPLMGetDatad(rz), &lat, &lon, &elev);
    return true;
}

// Adquiere los aviones, asegura que el slot 1 existe con el modelo pedido y
// apaga su IA nativa SIN soltarla (si no, X-Plane la mueve por su cuenta).
// Se puede llamar cada frame: solo actua si se perdio la adquisicion.
inline bool EnsureAiOwned(const PendingAi& p) {
    if (ai_control::g_acquired && (ai_control::g_mask & (1u << 1))) return true;

    std::string fullPath = ResolveAircraftPath(p.acfRelPath);
    if (!ai_control::g_acquired) {
        const char* want[] = { fullPath.c_str(), nullptr };
        if (!XPLMAcquirePlanes(want, nullptr, nullptr)) {
            // Sin modelo concreto: quiza otro plugin tiene el control, o ya hay
            // IAs cargadas. Reintentamos sin forzar path.
            if (!XPLMAcquirePlanes(nullptr, nullptr, nullptr)) {
                LogWarn("PlaceScenario: no se pudo XPLMAcquirePlanes para la IA.");
                return false;
            }
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

    ai_control::Adopt(1);
    return true;
}

// Fase de asentamiento: X-Plane carga objetos/IA de forma asincrona y puede
// recolocar su propia IA (plane1) DESPUES de que la hayamos puesto. Por eso no
// vale escribir una vez: cada frame se lee donde esta y, si no esta donde toca,
// se reescribe. Solo se da por terminado (ScenarioReady) tras kStableFrames
// seguidos en su sitio.
struct Settle {
    bool active = false;
    PendingAi p;
    int stable = 0;
    int frames = 0;
    int corrections = 0;
};
inline Settle g_settle;

inline constexpr int kStableFrames = 90;     // ~1.5 s a 60 Hz sin que se mueva
inline constexpr int kSettleTimeoutFrames = 1200;
inline constexpr double kMaxHorizM = 25.0;
inline constexpr double kMaxVertM = 25.0;

inline bool AiAtTarget(const PendingAi& p) {
    double lat = 0, lon = 0, el = 0;
    if (!ReadAiWorld(lat, lon, el)) return false;
    const double mPerDegLat = 111320.0;
    const double mPerDegLon = 111320.0 * std::cos(p.lat * 3.14159265358979323846 / 180.0);
    const double dN = (lat - p.lat) * mPerDegLat;
    const double dE = (lon - p.lon) * mPerDegLon;
    return std::sqrt(dN * dN + dE * dE) <= kMaxHorizM && std::fabs(el - p.elevMsl) <= kMaxVertM;
}

inline void CancelPending() {
    g_pendingAi = PendingAi{};
    g_settle = Settle{};
}

inline void OnAirportLoaded() {
    // Un AIRPORT_LOADED durante el asentamiento (X-Plane recargo la escena):
    // se vuelve a empezar a contar.
    if (g_settle.active) g_settle.stable = 0;
    if (!g_pendingAi.active || !g_pendingAi.waitAirport) return;
    g_pendingAi.waitAirport = false;
    g_pendingAi.framesLeft = 60;  // ~1 s a 60 Hz para asentar el origen local
}

// true = el escenario esta listo (IA estable en su sitio) -> ScenarioReady.
inline bool TickPending() {
    if (g_settle.active) {
        ++g_settle.frames;
        if (EnsureAiOwned(g_settle.p)) {
            if (AiAtTarget(g_settle.p)) {
                ++g_settle.stable;
            } else {
                g_settle.stable = 0;
                ++g_settle.corrections;
                if (g_settle.corrections <= 5 || g_settle.corrections % 60 == 0) {
                    double lat = 0, lon = 0, el = 0;
                    ReadAiWorld(lat, lon, el);
                    LogInfo("PlaceScenario: IA fuera de sitio (lat=%.5f lon=%.5f elev=%.0fm); reescribiendo (#%d).",
                            lat, lon, el, g_settle.corrections);
                }
                WriteAiPose(g_settle.p);
            }
        }
        const bool ready = g_settle.stable >= kStableFrames;
        if (ready || g_settle.frames >= kSettleTimeoutFrames) {
            if (!ready)
                LogWarn("PlaceScenario: la IA no se estabilizo en %d frames; se continua igualmente.",
                        g_settle.frames);
            else
                LogInfo("PlaceScenario: IA estable tras %d frames (%d correcciones).",
                        g_settle.frames, g_settle.corrections);
            g_settle = Settle{};
            return true;
        }
        return false;
    }

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

    g_settle = Settle{};
    g_settle.active = true;
    g_settle.p = g_pendingAi;
    g_pendingAi = PendingAi{};
    if (EnsureAiOwned(g_settle.p)) WriteAiPose(g_settle.p);
    LogInfo("PlaceScenario: IA en lat=%.5f lon=%.5f elev=%.0fm hdg=%.1f spd=%.0fm/s; asentando.",
            g_settle.p.lat, g_settle.p.lon, g_settle.p.elevMsl, g_settle.p.hdgTrue,
            g_settle.p.speedMps);
    return false;
}

inline void Begin(double userLat, double userLon, float userElevMsl,
                  float userHdgTrue, float userSpeedMps,
                  double aiLat, double aiLon, float aiElevMsl,
                  float aiHdgTrue, float aiSpeedMps,
                  const std::string& aiAcfRelPath, bool aiOnGround) {
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
    g_pendingAi.onGround = aiOnGround;

    XPLMPlaceUserAtLocation(userLat, userLon, userElevMsl, userHdgTrue, userSpeedMps);
    LogInfo("PlaceScenario: usuario en lat=%.5f lon=%.5f elev=%.1fm hdg=%.1f.",
            userLat, userLon, userElevMsl, userHdgTrue);
}

}  // namespace scenario
