// CameraFollow.h
//
// Aplica Op.CameraFollow via XPLMControlCamera:
//   0     = soltar
//   1..19 = chase detras de una IA (lee planeN_x/y/z/psi)
//   255   = vista aerea que enmarca ownship + IAs activas
//
// Sin logica de dominio: el core decide cuándo; aquí solo se leen datarefs
// y se rellena XPLMCameraPosition_t cada frame.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>

#include "XPLMCamera.h"
#include "XPLMDataAccess.h"
#include "XPLMPlanes.h"

#include "Logger.h"

namespace camera_follow {

inline constexpr uint8_t kOverviewIndex = 255;
inline constexpr int kMaxAi = 19;

enum class Mode : uint8_t { Off = 0, Chase = 1, Overview = 2 };

inline Mode g_mode = Mode::Off;
inline uint8_t g_followingIndex = 0;  // 1..19 en Chase; 0 si no

inline XPLMDataRef g_refX = nullptr;
inline XPLMDataRef g_refY = nullptr;
inline XPLMDataRef g_refZ = nullptr;
inline XPLMDataRef g_refPsi = nullptr;

inline XPLMDataRef g_userX = nullptr;
inline XPLMDataRef g_userY = nullptr;
inline XPLMDataRef g_userZ = nullptr;
inline XPLMDataRef g_aiX[kMaxAi] = {};
inline XPLMDataRef g_aiY[kMaxAi] = {};
inline XPLMDataRef g_aiZ[kMaxAi] = {};
inline bool g_overviewRefsReady = false;

constexpr float kChaseDistM = 50.0f;
constexpr float kChaseHeightM = 12.0f;
constexpr float kChasePitchDeg = -8.0f;
constexpr float kDeg2Rad = 0.01745329252f;

// Vista aerea: pitch casi nadir; altura = margen * radio horizontal / tan(fov/2).
constexpr float kOverviewPitchDeg = -88.0f;
constexpr float kOverviewFovHalfTan = 0.57735026919f;  // tan(30°) ≈ FOV 60°
constexpr float kOverviewMargin = 1.45f;
constexpr float kOverviewMinHeightM = 400.0f;
constexpr float kOverviewMaxHeightM = 80000.0f;
constexpr float kOverviewSoloRadiusM = 350.0f;

inline void ClearChaseRefs() {
    g_followingIndex = 0;
    g_refX = nullptr;
    g_refY = nullptr;
    g_refZ = nullptr;
    g_refPsi = nullptr;
}

inline void ClearState() {
    g_mode = Mode::Off;
    ClearChaseRefs();
}

inline void EnsureOverviewRefs() {
    if (g_overviewRefsReady) return;
    g_userX = XPLMFindDataRef("sim/flightmodel/position/local_x");
    g_userY = XPLMFindDataRef("sim/flightmodel/position/local_y");
    g_userZ = XPLMFindDataRef("sim/flightmodel/position/local_z");
    for (int i = 0; i < kMaxAi; ++i) {
        char path[64];
        std::snprintf(path, sizeof(path),
                      "sim/multiplayer/position/plane%d_x", i + 1);
        g_aiX[i] = XPLMFindDataRef(path);
        std::snprintf(path, sizeof(path),
                      "sim/multiplayer/position/plane%d_y", i + 1);
        g_aiY[i] = XPLMFindDataRef(path);
        std::snprintf(path, sizeof(path),
                      "sim/multiplayer/position/plane%d_z", i + 1);
        g_aiZ[i] = XPLMFindDataRef(path);
    }
    g_overviewRefsReady = true;
}

inline int ChaseCameraCallback(XPLMCameraPosition_t* outCameraPosition,
                               int inIsLosingControl,
                               void* /*inRefcon*/) {
    if (inIsLosingControl) {
        ClearState();
        return 0;
    }
    if (!outCameraPosition || !g_refX || !g_refY || !g_refZ || !g_refPsi) {
        ClearState();
        return 0;
    }

    const double px = XPLMGetDatad(g_refX);
    const double py = XPLMGetDatad(g_refY);
    const double pz = XPLMGetDatad(g_refZ);
    const float hdg = XPLMGetDataf(g_refPsi);
    const float rad = hdg * kDeg2Rad;

    // OpenGL local: +X este, +Y up, +Z sur. Rumbo 0 = norte (-Z).
    outCameraPosition->x = static_cast<float>(px - std::sin(rad) * kChaseDistM);
    outCameraPosition->y = static_cast<float>(py + kChaseHeightM);
    outCameraPosition->z = static_cast<float>(pz + std::cos(rad) * kChaseDistM);
    outCameraPosition->pitch = kChasePitchDeg;
    outCameraPosition->heading = hdg;
    outCameraPosition->roll = 0.0f;
    outCameraPosition->zoom = 1.0f;
    return 1;
}

inline int OverviewCameraCallback(XPLMCameraPosition_t* outCameraPosition,
                                  int inIsLosingControl,
                                  void* /*inRefcon*/) {
    if (inIsLosingControl) {
        ClearState();
        return 0;
    }
    if (!outCameraPosition || !g_userX || !g_userY || !g_userZ) {
        ClearState();
        return 0;
    }

    double xs[1 + kMaxAi];
    double ys[1 + kMaxAi];
    double zs[1 + kMaxAi];
    int n = 0;

    xs[n] = XPLMGetDatad(g_userX);
    ys[n] = XPLMGetDatad(g_userY);
    zs[n] = XPLMGetDatad(g_userZ);
    ++n;

    int total = 0, active = 0;
    XPLMCountAircraft(&total, &active, nullptr);
    if (active < 1) active = 1;
    if (active > 1 + kMaxAi) active = 1 + kMaxAi;

    for (int i = 1; i < active; ++i) {
        const int slot = i - 1;
        if (!g_aiX[slot] || !g_aiY[slot] || !g_aiZ[slot]) continue;
        xs[n] = XPLMGetDatad(g_aiX[slot]);
        ys[n] = XPLMGetDatad(g_aiY[slot]);
        zs[n] = XPLMGetDatad(g_aiZ[slot]);
        ++n;
    }

    double minX = xs[0], maxX = xs[0];
    double minY = ys[0], maxY = ys[0];
    double minZ = zs[0], maxZ = zs[0];
    for (int i = 1; i < n; ++i) {
        minX = std::min(minX, xs[i]);
        maxX = std::max(maxX, xs[i]);
        minY = std::min(minY, ys[i]);
        maxY = std::max(maxY, ys[i]);
        minZ = std::min(minZ, zs[i]);
        maxZ = std::max(maxZ, zs[i]);
    }

    const double cx = 0.5 * (minX + maxX);
    const double cy = 0.5 * (minY + maxY);
    const double cz = 0.5 * (minZ + maxZ);

    // Radio horizontal desde el centroide de la caja (XZ); un solo avion
    // usa un radio por defecto para no pegar la camara al CG.
    double radius = 0.0;
    for (int i = 0; i < n; ++i) {
        const double dx = xs[i] - cx;
        const double dz = zs[i] - cz;
        radius = std::max(radius, std::sqrt(dx * dx + dz * dz));
    }
    if (n <= 1 || radius < 1.0)
        radius = kOverviewSoloRadiusM;

    // Altura para que el diametro quepa en FOV ~60°, mas margen y mitad
    // del span vertical (aviones a cotas muy distintas).
    const double spanY = maxY - minY;
    float height = static_cast<float>(
        kOverviewMargin * (radius / kOverviewFovHalfTan) + 0.5 * spanY);
    height = std::clamp(height, kOverviewMinHeightM, kOverviewMaxHeightM);

    outCameraPosition->x = static_cast<float>(cx);
    outCameraPosition->y = static_cast<float>(maxY + height);
    outCameraPosition->z = static_cast<float>(cz);
    outCameraPosition->pitch = kOverviewPitchDeg;
    outCameraPosition->heading = 0.0f;  // norte
    outCameraPosition->roll = 0.0f;
    outCameraPosition->zoom = 1.0f;
    (void)cy;
    return 1;
}

inline void Stop() {
    if (g_mode == Mode::Off) return;
    XPLMDontControlCamera();
    LogInfo("CameraFollow: Stop (mode=%u planeIndex=%u).",
            static_cast<unsigned>(g_mode),
            static_cast<unsigned>(g_followingIndex));
    ClearState();
}

// planeIndex 1..19. Si ya seguimos otro (o overview), reinicia el chase.
inline bool Start(uint8_t planeIndex) {
    if (planeIndex < 1 || planeIndex > 19) {
        LogWarn("CameraFollow: Start planeIndex=%u invalido (1..19).",
                static_cast<unsigned>(planeIndex));
        return false;
    }

    char path[64];
    std::snprintf(path, sizeof(path), "sim/multiplayer/position/plane%u_x",
                  static_cast<unsigned>(planeIndex));
    XPLMDataRef rx = XPLMFindDataRef(path);
    std::snprintf(path, sizeof(path), "sim/multiplayer/position/plane%u_y",
                  static_cast<unsigned>(planeIndex));
    XPLMDataRef ry = XPLMFindDataRef(path);
    std::snprintf(path, sizeof(path), "sim/multiplayer/position/plane%u_z",
                  static_cast<unsigned>(planeIndex));
    XPLMDataRef rz = XPLMFindDataRef(path);
    std::snprintf(path, sizeof(path), "sim/multiplayer/position/plane%u_psi",
                  static_cast<unsigned>(planeIndex));
    XPLMDataRef rpsi = XPLMFindDataRef(path);

    if (!rx || !ry || !rz || !rpsi) {
        LogWarn("CameraFollow: datarefs plane%u_* no encontrados.",
                static_cast<unsigned>(planeIndex));
        return false;
    }

    if (g_mode != Mode::Off) {
        XPLMDontControlCamera();
        ClearState();
    }

    g_mode = Mode::Chase;
    g_followingIndex = planeIndex;
    g_refX = rx;
    g_refY = ry;
    g_refZ = rz;
    g_refPsi = rpsi;

    XPLMControlCamera(xplm_ControlCameraUntilViewChanges, ChaseCameraCallback,
                      nullptr);
    LogInfo("CameraFollow: Start chase planeIndex=%u.",
            static_cast<unsigned>(planeIndex));
    return true;
}

inline bool StartOverview() {
    EnsureOverviewRefs();
    if (!g_userX || !g_userY || !g_userZ) {
        LogWarn("CameraFollow: StartOverview fallo (datarefs usuario).");
        return false;
    }

    if (g_mode != Mode::Off) {
        XPLMDontControlCamera();
        ClearState();
    }

    g_mode = Mode::Overview;
    ClearChaseRefs();

    XPLMControlCamera(xplm_ControlCameraUntilViewChanges, OverviewCameraCallback,
                      nullptr);
    LogInfo("CameraFollow: Start overview.");
    return true;
}

}  // namespace camera_follow
