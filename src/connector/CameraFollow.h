// CameraFollow.h
//
// Aplica Op.CameraFollow via XPLMControlCamera:
//   0     = soltar
//   1..19 = chase orbitable sobre una IA (lee planeN_x/y/z/psi)
//   255   = vista aerea adaptativa:
//           1 nave estable → la sigue; 2+ → centroide
//           ignora naves que teletransportan entre frames
//
// Controles chase (mismo gesto que la camara libre de X-Plane):
//   clic derecho + arrastre = orbitar alrededor del avion
//   rueda del raton         = zoom (factor focal XPLMCameraPosition_t.zoom)
//   teclas , y .            = alejar / acercar (radio de orbita / dolly)
//
// Controles vista aerea:
//   clic izquierdo sobre avion   = aviso OverviewFocus (foco UI; la camara no se mueve)
//   clic izquierdo + arrastre    = paneo relativo al centroide (sigue a las naves);
//                                  si habia seguimiento, vuelve a libre
//   doble clic sobre avion       = zoom-in rapido a la vista del avion
//                                  (chase IA / soltar en local)
//   doble clic en vacio          = reencuadrar todas las naves
//   clic derecho + arrastre      = orbitar (incluye por debajo del plano)
//   rueda / teclas , y .         = alejar / acercar (distancia)
//
// Sin logica de dominio: el core decide el foco UI al recibir OverviewFocus;
// aqui solo se leen datarefs y se rellena XPLMCameraPosition_t cada frame.
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>

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
inline XPLMDataRef g_userPsi = nullptr;

inline XPLMDataRef g_userX = nullptr;
inline XPLMDataRef g_userY = nullptr;
inline XPLMDataRef g_userZ = nullptr;
inline XPLMDataRef g_aiX[kMaxAi] = {};
inline XPLMDataRef g_aiY[kMaxAi] = {};
inline XPLMDataRef g_aiZ[kMaxAi] = {};
inline XPLMDataRef g_frameDt = nullptr;
inline XPLMDataRef g_worldMatrix = nullptr;
inline XPLMDataRef g_projMatrix = nullptr;
inline XPLMDataRef g_viewport = nullptr;
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
// Zoom-in overview → chase / local: corto para no cortar el ritmo.
constexpr float kEnterZoomSec = 0.26f;

inline float g_azimDeg = 0.0f;   // rumbo camara (0 = norte)
inline float g_elevDeg = kChaseElevDeg;
inline float g_distM = kChaseDistM;
inline float g_zoom = 1.0f;      // 1.0 = normal; 2.0 = 2x tele (SDK)
inline bool g_orbitDragging = false;
inline int g_lastMouseX = 0;
inline int g_lastMouseY = 0;
inline int g_pendingWheelClicks = 0;  // >0 = zoom in (tele)

// Transicion de entrada (doble clic en vista aerea).
struct CamPose {
    float x = 0, y = 0, z = 0;
    float pitch = 0, heading = 0, zoom = 1.0f;
};
inline bool g_ziActive = false;
inline bool g_ziReleaseAtEnd = false;  // local: al acabar, soltar camara
inline CamPose g_ziFrom{};
inline float g_ziT = 0.0f;  // 0..1

inline XPLMWindowID g_inputWindow = nullptr;

// Vista aerea: pitch casi nadir; altura = margen * radio horizontal / tan(fov/2).
constexpr float kOverviewElevDeg = 88.0f;
constexpr float kOverviewFovHalfTan = 0.57735026919f;  // tan(30°) ≈ FOV 60°
constexpr float kOverviewMargin = 1.45f;
constexpr float kOverviewMinHeightM = 400.0f;
constexpr float kOverviewMaxHeightM = 500000.0f;
constexpr float kOverviewMinManualDistM = 40.0f;
constexpr float kOverviewMinElevDeg = -85.0f;  // permite orbitar por debajo
constexpr float kOverviewMaxElevDeg = 89.0f;
constexpr float kOverviewSoloRadiusM = 350.0f;
constexpr ULONGLONG kOverviewReframeMs = 350;
constexpr float kOverviewPickRadiusPx = 40.0f;
// Salto maximo entre frames para aceptar una nave en el encuadre (~Mach 7
// a 60 Hz cabria; por encima es teletransporte / dataref corrupto).
constexpr double kOverviewMaxJumpM = 500.0;
// Suavizado del look-at / altura en modo auto (evita latigazos).
constexpr float kOverviewLookSmoothPerSec = 6.0f;
constexpr float kOverviewDistSmoothPerSec = 4.0f;

inline bool g_ovManual = false;
inline bool g_ovHasFrame = false;
inline int g_ovTrackIndex = -1;  // -1 libre; 0..19 seguimiento desde arriba
inline double g_ovLookX = 0.0;
inline double g_ovLookY = 0.0;
inline double g_ovLookZ = 0.0;
// Paneo relativo al centroide de las naves (modo libre). El look-at sigue el
// centroide + este offset; asi el usuario panea sin perder el seguimiento.
inline double g_ovPanX = 0.0;
inline double g_ovPanZ = 0.0;
inline float g_ovDist = kOverviewMinHeightM;
inline float g_ovElevDeg = kOverviewElevDeg;
inline float g_ovAzimDeg = 0.0f;
inline int g_ovPressX = 0;
inline int g_ovPressY = 0;
inline bool g_ovPressMoved = false;
inline ULONGLONG g_ovLastLeftClickMs = 0;
// Ultima pose aceptada por indice XPLM (filtro de teletransporte).
inline double g_ovPrevX[1 + kMaxAi] = {};
inline double g_ovPrevY[1 + kMaxAi] = {};
inline double g_ovPrevZ[1 + kMaxAi] = {};
inline bool g_ovPrevValid[1 + kMaxAi] = {};
inline int g_ovLastFramedCount = -1;

// plugin.cpp registra SendEvent(OverviewFocus, idx).
using OverviewFocusNotifyFn = void (*)(int xplmIndex);
inline OverviewFocusNotifyFn g_overviewFocusNotify = nullptr;

inline void SetOverviewFocusNotify(OverviewFocusNotifyFn fn) {
    g_overviewFocusNotify = fn;
}

// Indice XPLM del avion en seguimiento aereo (-1 = libre / sin highlight).
inline int OverviewTrackIndex() {
    return (g_mode == Mode::Overview) ? g_ovTrackIndex : -1;
}

inline void NotifyOverviewFocus(int xplmIndex) {
    if (g_overviewFocusNotify) g_overviewFocusNotify(xplmIndex);
}

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
    g_ziActive = false;
    g_ziReleaseAtEnd = false;
    g_ziT = 0.0f;
    g_ovManual = false;
    g_ovHasFrame = false;
    g_ovTrackIndex = -1;
    g_ovPanX = 0.0;
    g_ovPanZ = 0.0;
    g_ovElevDeg = kOverviewElevDeg;
    g_ovAzimDeg = 0.0f;
    g_ovPressMoved = false;
    g_ovLastLeftClickMs = 0;
    g_ovLastFramedCount = -1;
    for (int i = 0; i < 1 + kMaxAi; ++i) g_ovPrevValid[i] = false;
}

inline float Wrap360(float deg) {
    deg = std::fmod(deg, 360.0f);
    if (deg < 0.0f) deg += 360.0f;
    return deg;
}

inline float Lerp(float a, float b, float t) { return a + (b - a) * t; }

inline float LerpAngleDeg(float a, float b, float t) {
    float d = b - a;
    while (d > 180.0f) d -= 360.0f;
    while (d < -180.0f) d += 360.0f;
    return Wrap360(a + d * t);
}

// Ease-out cubico: arranca con energia y frena al llegar (zoom-in snappy).
inline float EaseOutCubic(float t) {
    const float u = 1.0f - t;
    return 1.0f - u * u * u;
}

inline CamPose CaptureOverviewEye() {
    const float az = g_ovAzimDeg * kDeg2Rad;
    const float el = g_ovElevDeg * kDeg2Rad;
    const float cosEl = std::cos(el);
    CamPose p;
    p.x = static_cast<float>(g_ovLookX - g_ovDist * cosEl * std::sin(az));
    p.y = static_cast<float>(g_ovLookY + g_ovDist * std::sin(el));
    p.z = static_cast<float>(g_ovLookZ + g_ovDist * cosEl * std::cos(az));
    p.pitch = -g_ovElevDeg;
    p.heading = g_ovAzimDeg;
    p.zoom = 1.0f;
    return p;
}

inline CamPose OrbitPoseAt(double px, double py, double pz,
                           float azimDeg, float elevDeg, float distM,
                           float zoom) {
    const float az = azimDeg * kDeg2Rad;
    const float el = elevDeg * kDeg2Rad;
    const float cosEl = std::cos(el);
    CamPose p;
    p.x = static_cast<float>(px - distM * cosEl * std::sin(az));
    p.y = static_cast<float>(py + distM * std::sin(el));
    p.z = static_cast<float>(pz + distM * cosEl * std::cos(az));
    p.pitch = -elevDeg;
    p.heading = azimDeg;
    p.zoom = zoom;
    return p;
}

inline void WritePose(XPLMCameraPosition_t* out, const CamPose& p) {
    out->x = p.x;
    out->y = p.y;
    out->z = p.z;
    out->pitch = p.pitch;
    out->heading = p.heading;
    out->roll = 0.0f;
    out->zoom = p.zoom;
}

inline CamPose LerpPose(const CamPose& a, const CamPose& b, float t) {
    CamPose p;
    p.x = Lerp(a.x, b.x, t);
    p.y = Lerp(a.y, b.y, t);
    p.z = Lerp(a.z, b.z, t);
    p.pitch = Lerp(a.pitch, b.pitch, t);
    p.heading = LerpAngleDeg(a.heading, b.heading, t);
    p.zoom = Lerp(a.zoom, b.zoom, t);
    return p;
}

inline void ResetChaseOrbit(float planeHdgDeg) {
    g_azimDeg = Wrap360(planeHdgDeg);
    g_elevDeg = kChaseElevDeg;
    g_distM = kChaseDistM;
    g_zoom = 1.0f;
    g_orbitDragging = false;
    g_pendingWheelClicks = 0;
}

inline void BeginEnterZoom(const CamPose& from, bool releaseAtEnd) {
    g_ziActive = true;
    g_ziReleaseAtEnd = releaseAtEnd;
    g_ziFrom = from;
    g_ziT = 0.0f;
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
        // Encima de otros overlays del mismo layer para recibir rueda/clicks.
        XPLMBringWindowToFront(g_inputWindow);
    } else {
        XPLMSetWindowIsVisible(g_inputWindow, 0);
    }
}

inline void DrawInputWindow(XPLMWindowID /*inWindowID*/, void* /*inRefcon*/) {
    // Invisible: captura LMB/RMB y rueda.
}

inline void BeginOverviewManual() {
    if (g_mode != Mode::Overview || g_ovManual || !g_ovHasFrame) return;
    g_ovManual = true;
}

inline void ClearOverviewTrack(bool notify) {
    if (g_ovTrackIndex < 0) return;
    g_ovTrackIndex = -1;
    if (notify) NotifyOverviewFocus(-1);
}

inline void ReframeOverview() {
    ClearOverviewTrack(true);
    g_ovManual = false;
    g_ovPanX = 0.0;
    g_ovPanZ = 0.0;
    g_ovElevDeg = kOverviewElevDeg;
    g_ovAzimDeg = 0.0f;
    g_orbitDragging = false;
}

// Metros de suelo por pixel en el centro, a la distancia actual de la camara.
inline float OverviewMetersPerPixel() {
    int left = 0, top = 0, right = 0, bottom = 0;
    XPLMGetScreenBoundsGlobal(&left, &top, &right, &bottom);
    int screenH = top - bottom;
    if (screenH < 2) screenH = 1080;
    const float dist = std::max(g_ovDist, 1.0f);
    return (2.0f * dist * kOverviewFovHalfTan) /
           static_cast<float>(screenH);
}

inline void PanOverview(int dx, int dy) {
    const float hdg = g_ovAzimDeg * kDeg2Rad;
    const float rightX = std::cos(hdg);
    const float rightZ = std::sin(hdg);
    const float fwdX = std::sin(hdg);
    const float fwdZ = -std::cos(hdg);
    const float mpp = OverviewMetersPerPixel();
    const float fdx = static_cast<float>(dx);
    const float fdy = static_cast<float>(dy);
    // Offset relativo al centroide: arrastrar a la derecha mueve el encuadre
    // a la izquierda; las naves siguen arrastrando el look-at con ellas.
    g_ovPanX -= (fdx * rightX + fdy * fwdX) * mpp;
    g_ovPanZ -= (fdx * rightZ + fdy * fwdZ) * mpp;
}

inline void OrbitOverview(int dx, int dy) {
    g_ovAzimDeg = Wrap360(g_ovAzimDeg - dx * kOrbitSensDegPerBoxel);
    g_ovElevDeg = std::clamp(
        g_ovElevDeg + dy * kOrbitSensDegPerBoxel,
        kOverviewMinElevDeg, kOverviewMaxElevDeg);
}

inline bool ReadPlaneLocal(int xplmIndex, double& x, double& y, double& z) {
    if (xplmIndex == 0) {
        if (!g_userX || !g_userY || !g_userZ) return false;
        x = XPLMGetDatad(g_userX);
        y = XPLMGetDatad(g_userY);
        z = XPLMGetDatad(g_userZ);
        return true;
    }
    if (xplmIndex < 1 || xplmIndex > kMaxAi) return false;
    const int slot = xplmIndex - 1;
    if (!g_aiX[slot] || !g_aiY[slot] || !g_aiZ[slot]) return false;
    x = XPLMGetDatad(g_aiX[slot]);
    y = XPLMGetDatad(g_aiY[slot]);
    z = XPLMGetDatad(g_aiZ[slot]);
    return true;
}

// Centroide horizontal (y altura de mira) de ownship + IAs activas.
// 1 nave → su CG; 2+ → centro de caja y maxY.
inline bool ComputeActiveBounds(double& cx, double& cy, double& cz,
                                double& lookY) {
    if (!g_userX || !g_userY || !g_userZ) return false;

    double minX = XPLMGetDatad(g_userX);
    double maxX = minX;
    double minY = XPLMGetDatad(g_userY);
    double maxY = minY;
    double minZ = XPLMGetDatad(g_userZ);
    double maxZ = minZ;
    int n = 1;

    int total = 0, active = 0;
    XPLMCountAircraft(&total, &active, nullptr);
    if (active < 1) active = 1;
    if (active > 1 + kMaxAi) active = 1 + kMaxAi;

    for (int i = 1; i < active; ++i) {
        const int slot = i - 1;
        if (!g_aiX[slot] || !g_aiY[slot] || !g_aiZ[slot]) continue;
        const double x = XPLMGetDatad(g_aiX[slot]);
        const double y = XPLMGetDatad(g_aiY[slot]);
        const double z = XPLMGetDatad(g_aiZ[slot]);
        minX = std::min(minX, x);
        maxX = std::max(maxX, x);
        minY = std::min(minY, y);
        maxY = std::max(maxY, y);
        minZ = std::min(minZ, z);
        maxZ = std::max(maxZ, z);
        ++n;
    }

    if (n <= 1) {
        cx = minX;
        cy = minY;
        cz = minZ;
        lookY = minY;
    } else {
        cx = 0.5 * (minX + maxX);
        cy = 0.5 * (minY + maxY);
        cz = 0.5 * (minZ + maxZ);
        lookY = maxY;
    }
    return true;
}

// Fija el offset de paneo para que el look-at actual no salte al soltar track.
inline void SyncPanFromLook() {
    double cx = 0, cy = 0, cz = 0, lookY = 0;
    if (!ComputeActiveBounds(cx, cy, cz, lookY)) {
        g_ovPanX = 0.0;
        g_ovPanZ = 0.0;
        return;
    }
    g_ovPanX = g_ovLookX - cx;
    g_ovPanZ = g_ovLookZ - cz;
    (void)cy;
    (void)lookY;
}

inline void MulMatVec(const float* m, float x, float y, float z, float w,
                      float& ox, float& oy, float& oz, float& ow) {
    ox = m[0] * x + m[4] * y + m[8] * z + m[12] * w;
    oy = m[1] * x + m[5] * y + m[9] * z + m[13] * w;
    oz = m[2] * x + m[6] * y + m[10] * z + m[14] * w;
    ow = m[3] * x + m[7] * y + m[11] * z + m[15] * w;
}

// Misma proyeccion que AiLabel: false solo si esta detras de la camara.
inline bool ProjectSoft(double x, double y, double z,
                        const float* world, const float* proj,
                        const int* viewport, float& outSx, float& outSy) {
    float ex, ey, ez, ew;
    MulMatVec(world, static_cast<float>(x), static_cast<float>(y),
              static_cast<float>(z), 1.0f, ex, ey, ez, ew);
    float cx, cy, cz, cw;
    MulMatVec(proj, ex, ey, ez, ew, cx, cy, cz, cw);
    if (cw <= 1e-4f) return false;
    const float ndcX = cx / cw;
    const float ndcY = cy / cw;
    outSx = viewport[0] + (ndcX * 0.5f + 0.5f) * viewport[2];
    outSy = viewport[1] + (ndcY * 0.5f + 0.5f) * viewport[3];
    return true;
}

// Avion mas cercano al clic en pantalla; -1 si ninguno dentro del radio.
inline int PickOverviewPlane(int mouseX, int mouseY) {
    if (!g_worldMatrix || !g_projMatrix || !g_viewport) return -1;

    float world[16] = {};
    float proj[16] = {};
    int viewport[4] = {};
    XPLMGetDatavf(g_worldMatrix, world, 0, 16);
    XPLMGetDatavf(g_projMatrix, proj, 0, 16);
    XPLMGetDatavi(g_viewport, viewport, 0, 4);

    int total = 0, active = 0;
    XPLMCountAircraft(&total, &active, nullptr);
    if (active < 1) active = 1;
    if (active > 1 + kMaxAi) active = 1 + kMaxAi;

    int best = -1;
    float bestDist2 = kOverviewPickRadiusPx * kOverviewPickRadiusPx;

    for (int idx = 0; idx < active; ++idx) {
        double px = 0, py = 0, pz = 0;
        if (!ReadPlaneLocal(idx, px, py, pz)) continue;
        float sx = 0, sy = 0;
        if (!ProjectSoft(px, py, pz, world, proj, viewport, sx, sy)) continue;
        const float dx = sx - static_cast<float>(mouseX);
        const float dy = sy - static_cast<float>(mouseY);
        const float d2 = dx * dx + dy * dy;
        if (d2 <= bestDist2) {
            bestDist2 = d2;
            best = idx;
        }
    }
    return best;
}

// Clic sobre un avion: solo selecciona en la UI. No mueve la camara (ya se ve
// en la vista aerea); el usuario panea/orbita/zoom a mano si quiere acercarse.
inline void SelectOverviewPlane(int xplmIndex) {
    if (xplmIndex < 0 || xplmIndex > kMaxAi) return;
    double x = 0, y = 0, z = 0;
    if (!ReadPlaneLocal(xplmIndex, x, y, z)) return;
    NotifyOverviewFocus(xplmIndex);
}

// Declaraciones adelantadas: EnterPlaneView llama a Start/Stop definidos abajo.
inline bool Start(uint8_t planeIndex, bool zoomFromOverview = false);
inline void Stop();
inline bool ZoomInThenReleaseLocal();

// Doble clic sobre un avion en vista aerea: foco UI + zoom-in a su vista
// (chase en IA, soltar a la vista nativa en el local).
inline void EnterPlaneView(int xplmIndex) {
    if (xplmIndex < 0 || xplmIndex > kMaxAi) return;
    double x = 0, y = 0, z = 0;
    if (!ReadPlaneLocal(xplmIndex, x, y, z)) return;
    NotifyOverviewFocus(xplmIndex);
    if (xplmIndex >= 1) {
        Start(static_cast<uint8_t>(xplmIndex), /*zoomFromOverview=*/true);
    } else {
        if (!ZoomInThenReleaseLocal())
            Stop();
    }
}

// Clic izquierdo (overview): pick / paneo; doble clic = vista avion o reencuadre.
inline int HandleLeftClick(XPLMWindowID /*inWindowID*/, int x, int y,
                           XPLMMouseStatus status, void* /*inRefcon*/) {
    if (g_mode != Mode::Overview || !g_ovHasFrame) return 0;

    switch (status) {
    case xplm_MouseDown: {
        const ULONGLONG now = GetTickCount64();
        if (g_ovLastLeftClickMs != 0 &&
            now - g_ovLastLeftClickMs < kOverviewReframeMs) {
            g_ovLastLeftClickMs = 0;
            const int hit = PickOverviewPlane(x, y);
            if (hit >= 0)
                EnterPlaneView(hit);
            else
                ReframeOverview();
            return 1;
        }
        BeginOverviewManual();
        g_orbitDragging = true;
        g_ovPressMoved = false;
        g_ovPressX = x;
        g_ovPressY = y;
        g_lastMouseX = x;
        g_lastMouseY = y;
        return 1;
    }
    case xplm_MouseDrag:
        if (g_orbitDragging) {
            const int dx = x - g_lastMouseX;
            const int dy = y - g_lastMouseY;
            if (std::abs(x - g_ovPressX) + std::abs(y - g_ovPressY) > 4) {
                if (!g_ovPressMoved) {
                    g_ovPressMoved = true;
                    // Primer movimiento: salir del seguimiento a vista libre
                    // conservando el look-at como paneo relativo al centroide.
                    if (g_ovTrackIndex >= 0) {
                        ClearOverviewTrack(true);
                        SyncPanFromLook();
                    }
                }
            }
            PanOverview(dx, dy);
            g_lastMouseX = x;
            g_lastMouseY = y;
        }
        return 1;
    case xplm_MouseUp:
        g_orbitDragging = false;
        if (!g_ovPressMoved) {
            const int hit = PickOverviewPlane(g_ovPressX, g_ovPressY);
            if (hit >= 0) SelectOverviewPlane(hit);
            g_ovLastLeftClickMs = GetTickCount64();
        } else {
            g_ovLastLeftClickMs = 0;
        }
        return 1;
    default:
        return 1;
    }
}

// Clic derecho: chase orbita; overview solo orbita.
inline int HandleRightClick(XPLMWindowID /*inWindowID*/, int x, int y,
                            XPLMMouseStatus status, void* /*inRefcon*/) {
    if (g_mode == Mode::Overview) {
        if (!g_ovHasFrame) return 0;
        switch (status) {
        case xplm_MouseDown:
            BeginOverviewManual();
            g_orbitDragging = true;
            g_ovPressMoved = false;
            g_ovPressX = x;
            g_ovPressY = y;
            g_lastMouseX = x;
            g_lastMouseY = y;
            return 1;
        case xplm_MouseDrag:
            if (g_orbitDragging) {
                const int dx = x - g_lastMouseX;
                const int dy = y - g_lastMouseY;
                if (std::abs(x - g_ovPressX) + std::abs(y - g_ovPressY) > 4)
                    g_ovPressMoved = true;
                OrbitOverview(dx, dy);
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
    if (g_mode != Mode::Chase && g_mode != Mode::Overview) return 0;
    if (g_mode == Mode::Overview && !g_ovHasFrame) return 0;
    if (wheel != 0) return 0;
    if (g_mode == Mode::Overview) BeginOverviewManual();
    g_pendingWheelClicks += clicks;
    return 1;
}

inline XPLMCursorStatus HandleCursor(XPLMWindowID /*inWindowID*/, int /*x*/,
                                     int /*y*/, void* /*inRefcon*/) {
    if (g_mode != Mode::Chase && g_mode != Mode::Overview)
        return xplm_CursorDefault;
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
    params.handleMouseClickFunc = HandleLeftClick;
    params.handleKeyFunc = nullptr;
    params.handleCursorFunc = HandleCursor;
    params.handleMouseWheelFunc = HandleMouseWheel;
    params.refcon = nullptr;
    params.decorateAsFloatingWindow = xplm_WindowDecorationNone;
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

// , = alejar; . = acercar. Solo con foco en XP.
inline void PollDistanceKeys(float dt) {
    if ((g_mode != Mode::Chase && g_mode != Mode::Overview) || !XPlaneHasFocus())
        return;
    if (g_mode == Mode::Overview && !g_ovHasFrame) return;
    const bool farther = (GetAsyncKeyState(VK_OEM_COMMA) & 0x8000) != 0;
    const bool closer = (GetAsyncKeyState(VK_OEM_PERIOD) & 0x8000) != 0;
    if (farther == closer) return;
    const float factor = std::exp(kDistRatePerSec * dt);
    if (g_mode == Mode::Overview) {
        BeginOverviewManual();
        if (farther)
            g_ovDist = std::min(g_ovDist * factor, kOverviewMaxHeightM);
        else
            g_ovDist = std::max(g_ovDist / factor, kOverviewMinManualDistM);
        return;
    }
    if (farther)
        g_distM = std::min(g_distM * factor, kMaxDistM);
    else
        g_distM = std::max(g_distM / factor, kMinDistM);
}

inline void ApplyPendingZoom() {
    if (g_pendingWheelClicks == 0) return;
    const int clicks = g_pendingWheelClicks;
    g_pendingWheelClicks = 0;
    if (g_mode == Mode::Overview) {
        // clicks > 0 = rueda hacia delante = acercar (menos altura).
        g_ovDist /= std::pow(kZoomStep, static_cast<float>(clicks));
        g_ovDist = std::clamp(g_ovDist, kOverviewMinManualDistM,
                              kOverviewMaxHeightM);
        return;
    }
    // clicks > 0 = rueda hacia delante = mas tele (zoom up), NO dolly.
    g_zoom *= std::pow(kZoomStep, static_cast<float>(clicks));
    g_zoom = std::clamp(g_zoom, kMinZoom, kMaxZoom);
}

inline void EnsureOverviewRefs() {
    if (g_overviewRefsReady) return;
    g_userX = XPLMFindDataRef("sim/flightmodel/position/local_x");
    g_userY = XPLMFindDataRef("sim/flightmodel/position/local_y");
    g_userZ = XPLMFindDataRef("sim/flightmodel/position/local_z");
    g_userPsi = XPLMFindDataRef("sim/flightmodel/position/psi");
    g_frameDt = XPLMFindDataRef("sim/operation/misc/frame_rate_period");
    g_worldMatrix = XPLMFindDataRef("sim/graphics/view/world_matrix");
    g_projMatrix = XPLMFindDataRef("sim/graphics/view/projection_matrix_3d");
    g_viewport = XPLMFindDataRef("sim/graphics/view/viewport");
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
    const float dt = FrameDt();

    const double px = XPLMGetDatad(g_refX);
    const double py = XPLMGetDatad(g_refY);
    const double pz = XPLMGetDatad(g_refZ);

    if (g_ziActive) {
        // Durante el zoom no orbitamos ni dolly: el destino es la orbita chase
        // actual (sigue al avion si se mueve).
        const CamPose to = OrbitPoseAt(px, py, pz, g_azimDeg, g_elevDeg,
                                       g_distM, g_zoom);
        g_ziT += dt / kEnterZoomSec;
        if (g_ziT >= 1.0f) {
            g_ziT = 1.0f;
            g_ziActive = false;
            WritePose(outCameraPosition, to);
            if (g_ziReleaseAtEnd) {
                // Camino local: soltar tras el zoom (vista nativa de XP).
                SetInputWindowVisible(false);
                ClearState();
                return 0;
            }
            return 1;
        }
        const float u = EaseOutCubic(g_ziT);
        WritePose(outCameraPosition, LerpPose(g_ziFrom, to, u));
        return 1;
    }

    PollDistanceKeys(dt);
    ApplyPendingZoom();

    // OpenGL local: +X este, +Y up, +Z sur. Rumbo 0 = norte (-Z).
    // Camara en orbita esferica; mira al CG (heading=azim, pitch=-elev).
    WritePose(outCameraPosition,
              OrbitPoseAt(px, py, pz, g_azimDeg, g_elevDeg, g_distM, g_zoom));
    return 1;
}

inline int OverviewCameraCallback(XPLMCameraPosition_t* outCameraPosition,
                                  int inIsLosingControl,
                                  void* /*inRefcon*/) {
    if (inIsLosingControl) {
        SetInputWindowVisible(false);
        ClearState();
        return 0;
    }
    if (!outCameraPosition || !g_userX || !g_userY || !g_userZ) {
        SetInputWindowVisible(false);
        ClearState();
        return 0;
    }

    double xs[1 + kMaxAi];
    double ys[1 + kMaxAi];
    double zs[1 + kMaxAi];
    int n = 0;

    auto tryAdd = [&](int idx, double x, double y, double z) {
        if (idx < 0 || idx > kMaxAi) return;
        if (g_ovPrevValid[idx]) {
            const double dx = x - g_ovPrevX[idx];
            const double dy = y - g_ovPrevY[idx];
            const double dz = z - g_ovPrevZ[idx];
            const double jump = std::sqrt(dx * dx + dy * dy + dz * dz);
            // Teletransporte: actualiza baseline pero no enmarca este frame
            // (si se estabiliza alli, el siguiente frame ya entra).
            if (jump > kOverviewMaxJumpM) {
                g_ovPrevX[idx] = x;
                g_ovPrevY[idx] = y;
                g_ovPrevZ[idx] = z;
                return;
            }
        }
        g_ovPrevX[idx] = x;
        g_ovPrevY[idx] = y;
        g_ovPrevZ[idx] = z;
        g_ovPrevValid[idx] = true;
        xs[n] = x;
        ys[n] = y;
        zs[n] = z;
        ++n;
    };

    tryAdd(0, XPLMGetDatad(g_userX), XPLMGetDatad(g_userY),
           XPLMGetDatad(g_userZ));

    int total = 0, active = 0;
    XPLMCountAircraft(&total, &active, nullptr);
    if (active < 1) active = 1;
    if (active > 1 + kMaxAi) active = 1 + kMaxAi;

    for (int i = 1; i < active; ++i) {
        const int slot = i - 1;
        if (!g_aiX[slot] || !g_aiY[slot] || !g_aiZ[slot]) continue;
        tryAdd(i, XPLMGetDatad(g_aiX[slot]), XPLMGetDatad(g_aiY[slot]),
               XPLMGetDatad(g_aiZ[slot]));
    }

    // Sin ninguna nave estable: mantiene el look-at actual.
    if (n <= 0) {
        g_ovHasFrame = true;
        SyncInputWindowBounds();
        PollDistanceKeys(FrameDt());
        ApplyPendingZoom();
        const float az = g_ovAzimDeg * kDeg2Rad;
        const float el = g_ovElevDeg * kDeg2Rad;
        const float cosEl = std::cos(el);
        outCameraPosition->x = static_cast<float>(
            g_ovLookX - g_ovDist * cosEl * std::sin(az));
        outCameraPosition->y = static_cast<float>(
            g_ovLookY + g_ovDist * std::sin(el));
        outCameraPosition->z = static_cast<float>(
            g_ovLookZ + g_ovDist * cosEl * std::cos(az));
        outCameraPosition->pitch = -g_ovElevDeg;
        outCameraPosition->heading = g_ovAzimDeg;
        outCameraPosition->roll = 0.0f;
        outCameraPosition->zoom = 1.0f;
        return 1;
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

    // Adaptativo: 1 nave estable → la sigue; 2+ → centroide de la caja.
    const double cx = (n <= 1) ? xs[0] : 0.5 * (minX + maxX);
    const double cz = (n <= 1) ? zs[0] : 0.5 * (minZ + maxZ);
    const double lookY = (n <= 1) ? ys[0] : maxY;

    double radius = 0.0;
    for (int i = 0; i < n; ++i) {
        const double dx = xs[i] - cx;
        const double dz = zs[i] - cz;
        radius = std::max(radius, std::sqrt(dx * dx + dz * dz));
    }
    if (n <= 1 || radius < 1.0)
        radius = kOverviewSoloRadiusM;

    const double spanY = (n <= 1) ? 0.0 : (maxY - minY);
    float height = static_cast<float>(
        kOverviewMargin * (radius / kOverviewFovHalfTan) + 0.5 * spanY);
    height = std::clamp(height, kOverviewMinHeightM, kOverviewMaxHeightM);

    // Al pasar de 1↔varios solo resetea el paneo (no la orbita manual).
    if (g_ovLastFramedCount >= 0 && g_ovLastFramedCount != n &&
        !g_orbitDragging && g_ovTrackIndex < 0) {
        g_ovPanX = 0.0;
        g_ovPanZ = 0.0;
        if (!g_ovManual) {
            g_ovElevDeg = kOverviewElevDeg;
            g_ovAzimDeg = 0.0f;
        }
    }
    g_ovLastFramedCount = n;

    const float dt = FrameDt();

    if (g_ovTrackIndex >= 0) {
        double tx = 0, ty = 0, tz = 0;
        if (ReadPlaneLocal(g_ovTrackIndex, tx, ty, tz)) {
            g_ovLookX = tx;
            g_ovLookY = ty;
            g_ovLookZ = tz;
            g_ovManual = true;
        } else {
            ClearOverviewTrack(true);
            SyncPanFromLook();
        }
    } else if (!g_ovManual) {
        g_ovPanX = 0.0;
        g_ovPanZ = 0.0;
        const double targetX = cx;
        const double targetZ = cz;
        const double targetY = lookY;
        if (!g_ovHasFrame) {
            g_ovLookX = targetX;
            g_ovLookZ = targetZ;
            g_ovLookY = targetY;
            g_ovDist = height;
        } else {
            const float aLook =
                1.0f - std::exp(-kOverviewLookSmoothPerSec * dt);
            const float aDist =
                1.0f - std::exp(-kOverviewDistSmoothPerSec * dt);
            g_ovLookX += (targetX - g_ovLookX) * aLook;
            g_ovLookZ += (targetZ - g_ovLookZ) * aLook;
            g_ovLookY += (targetY - g_ovLookY) * aLook;
            g_ovDist += (height - g_ovDist) * aDist;
        }
        g_ovElevDeg = kOverviewElevDeg;
        g_ovAzimDeg = 0.0f;
    } else {
        g_ovLookX = cx + g_ovPanX;
        g_ovLookZ = cz + g_ovPanZ;
        g_ovLookY = lookY;
    }
    g_ovHasFrame = true;

    SyncInputWindowBounds();
    PollDistanceKeys(dt);
    ApplyPendingZoom();

    const float az = g_ovAzimDeg * kDeg2Rad;
    const float el = g_ovElevDeg * kDeg2Rad;
    const float cosEl = std::cos(el);
    const float dx = -g_ovDist * cosEl * std::sin(az);
    const float dy = g_ovDist * std::sin(el);
    const float dz = g_ovDist * cosEl * std::cos(az);

    outCameraPosition->x = static_cast<float>(g_ovLookX + dx);
    outCameraPosition->y = static_cast<float>(g_ovLookY + dy);
    outCameraPosition->z = static_cast<float>(g_ovLookZ + dz);
    outCameraPosition->pitch = -g_ovElevDeg;
    outCameraPosition->heading = g_ovAzimDeg;
    outCameraPosition->roll = 0.0f;
    outCameraPosition->zoom = 1.0f;
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
// zoomFromOverview: interpola desde la ojo actual de la vista aerea.
inline bool Start(uint8_t planeIndex, bool zoomFromOverview) {
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

    const bool doZoom = zoomFromOverview && g_mode == Mode::Overview &&
                        g_ovHasFrame;
    CamPose from{};
    if (doZoom) from = CaptureOverviewEye();

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
    if (doZoom) BeginEnterZoom(from, /*releaseAtEnd=*/false);
    SetInputWindowVisible(true);

    XPLMControlCamera(xplm_ControlCameraUntilViewChanges, ChaseCameraCallback,
                      nullptr);
    LogInfo("CameraFollow: Start chase planeIndex=%u%s.",
            static_cast<unsigned>(planeIndex),
            doZoom ? " (zoom-in)" : "");
    return true;
}

// Zoom-in hacia una orbita chase del local y, al acabar, suelta la camara
// (vista nativa de X-Plane). Reusa ChaseCameraCallback con g_ziReleaseAtEnd.
inline bool ZoomInThenReleaseLocal() {
    EnsureOverviewRefs();
    if (!g_userX || !g_userY || !g_userZ) return false;
    if (!g_userPsi) g_userPsi = XPLMFindDataRef("sim/flightmodel/position/psi");
    if (!g_userPsi) return false;
    if (g_mode != Mode::Overview || !g_ovHasFrame) return false;

    const CamPose from = CaptureOverviewEye();
    const float hdg = XPLMGetDataf(g_userPsi);

    if (g_mode != Mode::Off) {
        SetInputWindowVisible(false);
        XPLMDontControlCamera();
        ClearState();
    }

    EnsureInputWindow();

    g_mode = Mode::Chase;
    g_followingIndex = 0;
    g_refX = g_userX;
    g_refY = g_userY;
    g_refZ = g_userZ;
    g_refPsi = g_userPsi;
    ResetChaseOrbit(hdg);
    BeginEnterZoom(from, /*releaseAtEnd=*/true);
    SetInputWindowVisible(true);

    XPLMControlCamera(xplm_ControlCameraUntilViewChanges, ChaseCameraCallback,
                      nullptr);
    LogInfo("CameraFollow: Zoom-in to local then release.");
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

    EnsureInputWindow();

    g_mode = Mode::Overview;
    ClearChaseRefs();
    g_ovManual = false;
    g_ovHasFrame = false;
    g_ovTrackIndex = -1;
    g_ovPanX = 0.0;
    g_ovPanZ = 0.0;
    g_ovElevDeg = kOverviewElevDeg;
    g_ovAzimDeg = 0.0f;
    g_ovPressMoved = false;
    g_ovLastLeftClickMs = 0;
    g_ovLastFramedCount = -1;
    for (int i = 0; i < 1 + kMaxAi; ++i) g_ovPrevValid[i] = false;
    SetInputWindowVisible(true);

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
