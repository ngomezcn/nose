// CameraFollow.h
//
// Aplica Op.CameraFollow via XPLMControlCamera:
//   0     = soltar
//   1..19 = chase orbitable sobre una IA (lee planeN_x/y/z/psi)
//   255   = vista aerea que enmarca ownship + IAs activas
//
// Controles chase (mismo gesto que la camara libre de X-Plane):
//   clic derecho + arrastre = orbitar alrededor del avion
//   rueda del raton         = zoom (factor focal XPLMCameraPosition_t.zoom)
//   teclas , y .            = alejar / acercar (radio de orbita / dolly)
//
// Sin logica de dominio: el core decide cuándo; aquí solo se leen datarefs
// y se rellena XPLMCameraPosition_t cada frame.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "XPLMCamera.h"
#include "XPLMDataAccess.h"
#include "XPLMDisplay.h"
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
inline XPLMDataRef g_frameDt = nullptr;
inline bool g_overviewRefsReady = false;

// Chase: orbita esferica alrededor del CG de la IA (apuntando siempre a el).
constexpr float kChaseDistM = 50.0f;
constexpr float kChaseElevDeg = 8.0f;
constexpr float kDeg2Rad = 0.01745329252f;
constexpr float kOrbitSensDegPerBoxel = 0.25f;
constexpr float kDistRatePerSec = 1.8f;   // e^(±rate*dt) con ,/.
constexpr float kZoomStep = 1.12f;       // por click de rueda
constexpr float kMinDistM = 3.0f;
constexpr float kMaxDistM = 5000.0f;
constexpr float kMinZoom = 0.25f;
constexpr float kMaxZoom = 16.0f;
constexpr float kMinElevDeg = -85.0f;
constexpr float kMaxElevDeg = 85.0f;

inline float g_azimDeg = 0.0f;   // rumbo camara (0 = norte)
inline float g_elevDeg = kChaseElevDeg;
inline float g_distM = kChaseDistM;
inline float g_zoom = 1.0f;      // 1.0 = normal; 2.0 = 2x tele (SDK)
inline bool g_orbitDragging = false;
inline int g_lastMouseX = 0;
inline int g_lastMouseY = 0;
inline int g_pendingWheelClicks = 0;  // >0 = zoom in (tele)

inline XPLMWindowID g_inputWindow = nullptr;

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
    g_orbitDragging = false;
    g_pendingWheelClicks = 0;
}

inline float Wrap360(float deg) {
    deg = std::fmod(deg, 360.0f);
    if (deg < 0.0f) deg += 360.0f;
    return deg;
}

inline void ResetChaseOrbit(float planeHdgDeg) {
    g_azimDeg = Wrap360(planeHdgDeg);
    g_elevDeg = kChaseElevDeg;
    g_distM = kChaseDistM;
    g_zoom = 1.0f;
    g_orbitDragging = false;
    g_pendingWheelClicks = 0;
}

// Solo orbitar/dolly cuando el foco de SO esta en X-Plane (no en la UI core).
inline bool XPlaneHasFocus() {
    HWND fg = GetForegroundWindow();
    if (!fg) return false;
    DWORD pid = 0;
    GetWindowThreadProcessId(fg, &pid);
    return pid == GetCurrentProcessId();
}

inline void SyncInputWindowBounds() {
    if (!g_inputWindow) return;
    int left = 0, top = 0, right = 0, bottom = 0;
    XPLMGetScreenBoundsGlobal(&left, &top, &right, &bottom);
    XPLMSetWindowGeometry(g_inputWindow, left, top, right, bottom);
}

inline void SetInputWindowVisible(bool visible) {
    if (!g_inputWindow) return;
    if (visible) {
        SyncInputWindowBounds();
        XPLMSetWindowIsVisible(g_inputWindow, 1);
        // Encima de otros overlays del mismo layer para recibir rueda/RMB.
        XPLMBringWindowToFront(g_inputWindow);
    } else {
        XPLMSetWindowIsVisible(g_inputWindow, 0);
    }
}

inline void DrawInputWindow(XPLMWindowID /*inWindowID*/, void* /*inRefcon*/) {
    // Invisible: captura RMB (orbita) y rueda (zoom focal).
}

// Clic derecho: consumir para que XP no cambie de vista
// (xplm_ControlCameraUntilViewChanges) ni entre en free-look.
inline int HandleRightClick(XPLMWindowID /*inWindowID*/, int x, int y,
                            XPLMMouseStatus status, void* /*inRefcon*/) {
    if (g_mode != Mode::Chase) return 0;

    switch (status) {
    case xplm_MouseDown:
        g_orbitDragging = true;
        g_lastMouseX = x;
        g_lastMouseY = y;
        return 1;
    case xplm_MouseDrag:
        if (g_orbitDragging) {
            g_azimDeg = Wrap360(
                g_azimDeg - (x - g_lastMouseX) * kOrbitSensDegPerBoxel);
            // y global sube hacia arriba; arrastrar arriba = mas elevacion.
            g_elevDeg = std::clamp(
                g_elevDeg + (y - g_lastMouseY) * kOrbitSensDegPerBoxel,
                kMinElevDeg, kMaxElevDeg);
            g_lastMouseX = x;
            g_lastMouseY = y;
        }
        return 1;
    case xplm_MouseUp:
        g_orbitDragging = false;
        return 1;
    default:
        return 1;
    }
}

// wheel 0 = eje vertical; clicks > 0 = rueda hacia delante.
inline int HandleMouseWheel(XPLMWindowID /*inWindowID*/, int /*x*/, int /*y*/,
                            int wheel, int clicks, void* /*inRefcon*/) {
    if (g_mode != Mode::Chase) return 0;
    if (wheel != 0) return 0;
    g_pendingWheelClicks += clicks;
    return 1;
}

// Reclamar el cursor sobre el 3D para que la ventana reciba rueda/RMB;
// clic izquierdo sigue pasando (sin handleMouseClickFunc).
inline XPLMCursorStatus HandleCursor(XPLMWindowID /*inWindowID*/, int /*x*/,
                                     int /*y*/, void* /*inRefcon*/) {
    if (g_mode != Mode::Chase) return xplm_CursorDefault;
    return xplm_CursorArrow;
}

inline void EnsureInputWindow() {
    if (g_inputWindow) return;

    int left = 0, top = 0, right = 1024, bottom = 0;
    XPLMGetScreenBoundsGlobal(&left, &top, &right, &bottom);

    XPLMCreateWindow_t params = {};
    params.structSize = sizeof(params);
    params.left = left;
    params.top = top;
    params.right = right;
    params.bottom = bottom;
    params.visible = 0;
    params.drawWindowFunc = DrawInputWindow;
    params.handleMouseClickFunc = nullptr;  // LMB al sim / cockpit
    params.handleKeyFunc = nullptr;
    params.handleCursorFunc = HandleCursor;
    params.handleMouseWheelFunc = HandleMouseWheel;
    params.refcon = nullptr;
    params.decorateAsFloatingWindow = xplm_WindowDecorationNone;
    // Floating: encima del 3D; decoration None deja pasar LMB sin handler.
    params.layer = xplm_WindowLayerFloatingWindows;
    params.handleRightClickFunc = HandleRightClick;

    g_inputWindow = XPLMCreateWindowEx(&params);
    if (!g_inputWindow) {
        LogWarn("CameraFollow: no se pudo crear ventana de input.");
        return;
    }
    XPLMSetWindowPositioningMode(g_inputWindow, xplm_WindowPositionFree, -1);
}

inline void DestroyInputWindow() {
    if (!g_inputWindow) return;
    XPLMDestroyWindow(g_inputWindow);
    g_inputWindow = nullptr;
}

// , = alejar (mas radio); . = acercar (menos radio). Solo con foco en XP.
inline void PollDistanceKeys(float dt) {
    if (g_mode != Mode::Chase || !XPlaneHasFocus()) return;
    const bool farther = (GetAsyncKeyState(VK_OEM_COMMA) & 0x8000) != 0;
    const bool closer = (GetAsyncKeyState(VK_OEM_PERIOD) & 0x8000) != 0;
    if (farther == closer) return;
    const float factor = std::exp(kDistRatePerSec * dt);
    if (farther)
        g_distM = std::min(g_distM * factor, kMaxDistM);
    else
        g_distM = std::max(g_distM / factor, kMinDistM);
}

inline void ApplyPendingZoom() {
    if (g_pendingWheelClicks == 0) return;
    const int clicks = g_pendingWheelClicks;
    g_pendingWheelClicks = 0;
    // clicks > 0 = rueda hacia delante = mas tele (zoom up), NO dolly.
    g_zoom *= std::pow(kZoomStep, static_cast<float>(clicks));
    g_zoom = std::clamp(g_zoom, kMinZoom, kMaxZoom);
}

inline void EnsureOverviewRefs() {
    if (g_overviewRefsReady) return;
    g_userX = XPLMFindDataRef("sim/flightmodel/position/local_x");
    g_userY = XPLMFindDataRef("sim/flightmodel/position/local_y");
    g_userZ = XPLMFindDataRef("sim/flightmodel/position/local_z");
    g_frameDt = XPLMFindDataRef("sim/operation/misc/frame_rate_period");
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

inline float FrameDt() {
    if (!g_frameDt)
        g_frameDt = XPLMFindDataRef("sim/operation/misc/frame_rate_period");
    float dt = g_frameDt ? XPLMGetDataf(g_frameDt) : (1.0f / 60.0f);
    if (dt < 0.001f || dt > 0.1f) dt = 1.0f / 60.0f;
    return dt;
}

inline int ChaseCameraCallback(XPLMCameraPosition_t* outCameraPosition,
                               int inIsLosingControl,
                               void* /*inRefcon*/) {
    if (inIsLosingControl) {
        SetInputWindowVisible(false);
        ClearState();
        return 0;
    }
    if (!outCameraPosition || !g_refX || !g_refY || !g_refZ || !g_refPsi) {
        SetInputWindowVisible(false);
        ClearState();
        return 0;
    }

    SyncInputWindowBounds();
    PollDistanceKeys(FrameDt());
    ApplyPendingZoom();

    const double px = XPLMGetDatad(g_refX);
    const double py = XPLMGetDatad(g_refY);
    const double pz = XPLMGetDatad(g_refZ);

    // OpenGL local: +X este, +Y up, +Z sur. Rumbo 0 = norte (-Z).
    // Camara en orbita esferica; mira al CG (heading=azim, pitch=-elev).
    const float az = g_azimDeg * kDeg2Rad;
    const float el = g_elevDeg * kDeg2Rad;
    const float cosEl = std::cos(el);
    const float dx = -g_distM * cosEl * std::sin(az);
    const float dy = g_distM * std::sin(el);
    const float dz = g_distM * cosEl * std::cos(az);

    outCameraPosition->x = static_cast<float>(px + dx);
    outCameraPosition->y = static_cast<float>(py + dy);
    outCameraPosition->z = static_cast<float>(pz + dz);
    outCameraPosition->pitch = -g_elevDeg;
    outCameraPosition->heading = g_azimDeg;
    outCameraPosition->roll = 0.0f;
    outCameraPosition->zoom = g_zoom;
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
    SetInputWindowVisible(false);
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
        SetInputWindowVisible(false);
        XPLMDontControlCamera();
        ClearState();
    }

    EnsureInputWindow();

    g_mode = Mode::Chase;
    g_followingIndex = planeIndex;
    g_refX = rx;
    g_refY = ry;
    g_refZ = rz;
    g_refPsi = rpsi;
    ResetChaseOrbit(XPLMGetDataf(rpsi));
    SetInputWindowVisible(true);

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
        SetInputWindowVisible(false);
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

inline void Shutdown() {
    Stop();
    DestroyInputWindow();
}

}  // namespace camera_follow
