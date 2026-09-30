// AiLabel.h — overlays 2D sobre aviones (etiqueta, lineas, marcador, trayectoria).
//
// Coach marks en fase xplm_Phase_Window: proyecta local_x/y/z (usuario) y
// sim/multiplayer/position/planeN_* (IAs) a pantalla. La config llega por
// Op.GraphicsConfig desde el core; sin mensaje se dibuja el default
// (etiqueta "Jev" amarilla sobre el avion local).
//
// Ver docs/xplane-sdk/guides/plugin-guidance-for-opengl-drawing.md
// ("Use a 2-d Callback for Coach Marks").

#pragma once

#include "XPLMDataAccess.h"
#include "XPLMDisplay.h"
#include "XPLMGraphics.h"
#include "XPLMPlanes.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>

#if IBM
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif
#include <GL/gl.h>

class AiLabel {
public:
    static constexpr int kMaxAi = 19;  // plane1..plane19

    void Init() {
        mLocalX = XPLMFindDataRef("sim/flightmodel/position/local_x");
        mLocalY = XPLMFindDataRef("sim/flightmodel/position/local_y");
        mLocalZ = XPLMFindDataRef("sim/flightmodel/position/local_z");
        mVx = XPLMFindDataRef("sim/flightmodel/position/local_vx");
        mVy = XPLMFindDataRef("sim/flightmodel/position/local_vy");
        mVz = XPLMFindDataRef("sim/flightmodel/position/local_vz");
        mAx = XPLMFindDataRef("sim/flightmodel/position/local_ax");
        mAy = XPLMFindDataRef("sim/flightmodel/position/local_ay");
        mAz = XPLMFindDataRef("sim/flightmodel/position/local_az");
        mWorldMatrix = XPLMFindDataRef("sim/graphics/view/world_matrix");
        mProjMatrix = XPLMFindDataRef("sim/graphics/view/projection_matrix_3d");
        mViewport = XPLMFindDataRef("sim/graphics/view/viewport");

        for (int i = 0; i < kMaxAi; ++i) {
            char path[64];
            std::snprintf(path, sizeof(path),
                          "sim/multiplayer/position/plane%d_x", i + 1);
            mAiX[i] = XPLMFindDataRef(path);
            std::snprintf(path, sizeof(path),
                          "sim/multiplayer/position/plane%d_y", i + 1);
            mAiY[i] = XPLMFindDataRef(path);
            std::snprintf(path, sizeof(path),
                          "sim/multiplayer/position/plane%d_z", i + 1);
            mAiZ[i] = XPLMFindDataRef(path);
            std::snprintf(path, sizeof(path),
                          "sim/multiplayer/position/plane%d_v_x", i + 1);
            mAiVx[i] = XPLMFindDataRef(path);
            std::snprintf(path, sizeof(path),
                          "sim/multiplayer/position/plane%d_v_y", i + 1);
            mAiVy[i] = XPLMFindDataRef(path);
            std::snprintf(path, sizeof(path),
                          "sim/multiplayer/position/plane%d_v_z", i + 1);
            mAiVz[i] = XPLMFindDataRef(path);
        }
    }

    void Enable() {
        XPLMRegisterDrawCallback(&AiLabel::DrawCallback, xplm_Phase_Window,
                                  0 /* after */, this);
    }

    void Disable() {
        XPLMUnregisterDrawCallback(&AiLabel::DrawCallback, xplm_Phase_Window,
                                    0 /* after */, this);
    }

    // flags: bit0 ownLabel, bit1 otherLabels, bit2 lines, bit3 markers,
    //        bit4 path (prediccion de trayectoria), bit5 interceptPath.
    void ApplyConfig(uint8_t flags, uint8_t font,
                     float r, float g, float b, float scale,
                     const std::string& text) {
        mFlags = flags;
        mFont = (font == 0) ? xplmFont_Basic : xplmFont_Proportional;
        mColor[0] = Clamp01(r);
        mColor[1] = Clamp01(g);
        mColor[2] = Clamp01(b);
        mScale = std::clamp(scale, 0.5f, 4.0f);
        if (text.empty()) {
            std::strncpy(mText, "Jev", sizeof(mText) - 1);
        } else {
            std::strncpy(mText, text.c_str(), sizeof(mText) - 1);
        }
        mText[sizeof(mText) - 1] = '\0';
        mTextLen = static_cast<int>(std::strlen(mText));
    }

    // Ruta de interceptacion (Op.InterceptPath): coords locales OGL absolutas.
    // Se llama desde el hilo del pipe/mensajes; Draw lee bajo el mismo mutex.
    // n=0 borra. Caduca sola si no se refresca en kInterceptPathTtlSec.
    void SetInterceptPath(const double* xyz, int n) {
        std::lock_guard<std::mutex> lk(mPathMu);
        if (n < 0) n = 0;
        if (n > kMaxPathPts) n = kMaxPathPts;
        mPathN = n;
        for (int i = 0; i < n * 3; ++i) mPath[i] = xyz[i];
        mPathStamp = std::chrono::steady_clock::now();
    }

private:
    static constexpr int kMaxPathPts = 64;
    static constexpr double kInterceptPathTtlSec = 1.0;
    static constexpr uint8_t kFlagInterceptPath = 1 << 5;
    static constexpr uint8_t kFlagOwnLabel = 1 << 0;
    static constexpr uint8_t kFlagOtherLabels = 1 << 1;
    static constexpr uint8_t kFlagLines = 1 << 2;
    static constexpr uint8_t kFlagMarkers = 1 << 3;
    static constexpr uint8_t kFlagPath = 1 << 4;
    static constexpr double kLabelHeightMeters = 3.5;
    // Horizonte base de la prediccion (segundos). Se alarga/acorta con la
    // aceleracion longitudinal, no con la velocidad.
    static constexpr float kPathHorizonSec = 1.5f;
    static constexpr int kPathSegments = 20;
    // Origen del trazo: CG desplazado hacia adelante (direccion de v) 2.5 m.
    static constexpr float kPathStartForwardMeters = 2.5f;

    static int DrawCallback(XPLMDrawingPhase, int, void* refcon) {
        static_cast<AiLabel*>(refcon)->Draw();
        return 1;
    }

    static float Clamp01(float v) {
        if (v < 0.0f) return 0.0f;
        if (v > 1.0f) return 1.0f;
        return v;
    }

    // Proyecta aunque quede fuera de pantalla (para lineas al borde).
    // false solo si esta detras de la camara.
    bool ProjectSoft(double x, double y, double z,
                     const float* world, const float* proj, const int* viewport,
                     float& outSx, float& outSy) const {
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

    bool ProjectOnScreen(double x, double y, double z,
                         const float* world, const float* proj, const int* viewport,
                         float& outSx, float& outSy) const {
        if (!ProjectSoft(x, y, z, world, proj, viewport, outSx, outSy)) return false;
        const float l = static_cast<float>(viewport[0]);
        const float b = static_cast<float>(viewport[1]);
        const float r = l + viewport[2];
        const float t = b + viewport[3];
        const float margin = 80.0f;
        return outSx >= l - margin && outSx <= r + margin &&
               outSy >= b - margin && outSy <= t + margin;
    }

    static bool ClipLine(float& x0, float& y0, float& x1, float& y1,
                         float xmin, float ymin, float xmax, float ymax) {
        auto code = [&](float x, float y) {
            int c = 0;
            if (x < xmin) c |= 1;
            else if (x > xmax) c |= 2;
            if (y < ymin) c |= 4;
            else if (y > ymax) c |= 8;
            return c;
        };
        int c0 = code(x0, y0), c1 = code(x1, y1);
        for (int iter = 0; iter < 8; ++iter) {
            if (!(c0 | c1)) return true;
            if (c0 & c1) return false;
            const int cOut = c0 ? c0 : c1;
            float x = 0, y = 0;
            if (cOut & 8) {
                x = x0 + (x1 - x0) * (ymax - y0) / (y1 - y0);
                y = ymax;
            } else if (cOut & 4) {
                x = x0 + (x1 - x0) * (ymin - y0) / (y1 - y0);
                y = ymin;
            } else if (cOut & 2) {
                y = y0 + (y1 - y0) * (xmax - x0) / (x1 - x0);
                x = xmax;
            } else {
                y = y0 + (y1 - y0) * (xmin - x0) / (x1 - x0);
                x = xmin;
            }
            if (cOut == c0) {
                x0 = x; y0 = y; c0 = code(x0, y0);
            } else {
                x1 = x; y1 = y; c1 = code(x1, y1);
            }
        }
        return false;
    }

    void Draw() {
        if (!mLocalX || !mLocalY || !mLocalZ || !mWorldMatrix || !mProjMatrix ||
            !mViewport) {
            return;
        }
        const bool any = (mFlags & (kFlagOwnLabel | kFlagOtherLabels |
                                    kFlagLines | kFlagMarkers | kFlagPath |
                                    kFlagInterceptPath)) != 0;
        if (!any) return;

        float world[16];
        float proj[16];
        int viewport[4];
        XPLMGetDatavf(mWorldMatrix, world, 0, 16);
        XPLMGetDatavf(mProjMatrix, proj, 0, 16);
        XPLMGetDatavi(mViewport, viewport, 0, 4);

        const float vpL = static_cast<float>(viewport[0]);
        const float vpB = static_cast<float>(viewport[1]);
        const float vpR = vpL + viewport[2];
        const float vpT = vpB + viewport[3];

        const double ownX = XPLMGetDatad(mLocalX);
        const double ownY = XPLMGetDatad(mLocalY);
        const double ownZ = XPLMGetDatad(mLocalZ);

        float ownCgSx = 0, ownCgSy = 0;
        float ownLblSx = 0, ownLblSy = 0;
        const bool ownCgOk = ProjectSoft(ownX, ownY, ownZ, world, proj, viewport,
                                         ownCgSx, ownCgSy);
        const bool ownLblOk = ProjectOnScreen(ownX, ownY + kLabelHeightMeters, ownZ,
                                              world, proj, viewport, ownLblSx, ownLblSy);

        int total = 0, active = 0;
        XPLMCountAircraft(&total, &active, nullptr);
        if (active < 1) active = 1;
        if (active > 1 + kMaxAi) active = 1 + kMaxAi;

        struct AiPt {
            double x, y, z;
            float vx, vy, vz;
            float cgSx, cgSy, lblSx, lblSy;
            bool cgOk, lblOk, velOk;
        };
        AiPt ai[kMaxAi]{};
        for (int i = 1; i < active; ++i) {
            const int slot = i - 1;
            if (!mAiX[slot] || !mAiY[slot] || !mAiZ[slot]) continue;
            ai[slot].x = XPLMGetDatad(mAiX[slot]);
            ai[slot].y = XPLMGetDatad(mAiY[slot]);
            ai[slot].z = XPLMGetDatad(mAiZ[slot]);
            ai[slot].cgOk = ProjectSoft(ai[slot].x, ai[slot].y, ai[slot].z,
                                         world, proj, viewport,
                                         ai[slot].cgSx, ai[slot].cgSy);
            ai[slot].lblOk = ProjectOnScreen(ai[slot].x, ai[slot].y + kLabelHeightMeters,
                                             ai[slot].z, world, proj, viewport,
                                             ai[slot].lblSx, ai[slot].lblSy);
            if (mAiVx[slot] && mAiVy[slot] && mAiVz[slot]) {
                ai[slot].vx = XPLMGetDataf(mAiVx[slot]);
                ai[slot].vy = XPLMGetDataf(mAiVy[slot]);
                ai[slot].vz = XPLMGetDataf(mAiVz[slot]);
                ai[slot].velOk = true;
            }
        }

        const bool wantGeom = (mFlags & (kFlagLines | kFlagMarkers | kFlagPath |
                                         kFlagInterceptPath)) != 0;
        if (wantGeom) {
            XPLMSetGraphicsState(0 /*fog*/, 0 /*tex*/, 0 /*light*/,
                                 0 /*alpha test*/, 1 /*blend*/,
                                 0 /*depth test*/, 0 /*depth write*/);
            glColor4f(mColor[0], mColor[1], mColor[2], 0.9f);
            glLineWidth(std::clamp(1.0f * mScale, 1.0f, 4.0f));

            if ((mFlags & kFlagLines) && ownCgOk) {
                for (int i = 1; i < active; ++i) {
                    const AiPt& p = ai[i - 1];
                    if (!p.cgOk) continue;
                    float x0 = ownCgSx, y0 = ownCgSy, x1 = p.cgSx, y1 = p.cgSy;
                    if (!ClipLine(x0, y0, x1, y1, vpL, vpB, vpR, vpT)) continue;
                    glBegin(GL_LINES);
                    glVertex2f(x0, y0);
                    glVertex2f(x1, y1);
                    glEnd();

                    // Distancia 3D real; texto en el punto medio del trazo visible.
                    const double dx = p.x - ownX;
                    const double dy = p.y - ownY;
                    const double dz = p.z - ownZ;
                    const float distM = static_cast<float>(
                        std::sqrt(dx * dx + dy * dy + dz * dz));
                    const float midSx = (x0 + x1) * 0.5f;
                    const float midSy = (y0 + y1) * 0.5f;
                    DrawDistanceLabel(midSx, midSy, distM);
                }
            }

            if (mFlags & kFlagMarkers) {
                const float half = 10.0f * mScale;
                if (ownCgOk && ownCgSx >= vpL - 40 && ownCgSx <= vpR + 40 &&
                    ownCgSy >= vpB - 40 && ownCgSy <= vpT + 40) {
                    DrawDiamond(ownCgSx, ownCgSy, half);
                }
                for (int i = 1; i < active; ++i) {
                    const AiPt& p = ai[i - 1];
                    if (!p.cgOk) continue;
                    if (p.cgSx < vpL - 40 || p.cgSx > vpR + 40 ||
                        p.cgSy < vpB - 40 || p.cgSy > vpT + 40) {
                        continue;
                    }
                    DrawDiamond(p.cgSx, p.cgSy, half);
                }
            }

            if (mFlags & kFlagPath) {
                if (mVx && mVy && mVz) {
                    const float vx = XPLMGetDataf(mVx);
                    const float vy = XPLMGetDataf(mVy);
                    const float vz = XPLMGetDataf(mVz);
                    float ax = 0, ay = 0, az = 0;
                    const bool haveA = mAx && mAy && mAz;
                    if (haveA) {
                        ax = XPLMGetDataf(mAx);
                        ay = XPLMGetDataf(mAy);
                        az = XPLMGetDataf(mAz);
                    }
                    DrawPredictedPath(ownX, ownY, ownZ, vx, vy, vz, ax, ay, az,
                                      haveA, world, proj, viewport);
                }
                for (int i = 1; i < active; ++i) {
                    const AiPt& p = ai[i - 1];
                    if (!p.velOk) continue;
                    // IAs: sin aceleracion en datarefs; prediccion a v constante.
                    DrawPredictedPath(p.x, p.y, p.z, p.vx, p.vy, p.vz,
                                      0, 0, 0, /*useAccel=*/false,
                                      world, proj, viewport);
                }
            }

            if (mFlags & kFlagInterceptPath) {
                DrawInterceptPath(world, proj, viewport);
            }

            glLineWidth(1.0f);
        }

        if ((mFlags & kFlagOwnLabel) && ownLblOk) {
            DrawLabel(ownLblSx, ownLblSy);
        }
        if (mFlags & kFlagOtherLabels) {
            for (int i = 1; i < active; ++i) {
                const AiPt& p = ai[i - 1];
                if (p.lblOk) DrawLabel(p.lblSx, p.lblSy);
            }
        }
    }

    // Integra p' = v, v' = a durante un horizonte en segundos y dibuja la
    // polilinea proyectada: es "donde estara la nave" si mantiene la acel
    // actual. El horizonte se alarga/acorta con la acel. longitudinal
    // (proyeccion de a sobre v), no con la velocidad.
    void DrawPredictedPath(double x, double y, double z,
                           float vx, float vy, float vz,
                           float ax, float ay, float az, bool useAccel,
                           const float* world, const float* proj,
                           const int* viewport) const {
        const float speed = std::sqrt(vx * vx + vy * vy + vz * vz);
        if (speed < 0.5f) return;  // parado / casi: no hay trayectoria util

        float horizon = kPathHorizonSec * mScale;
        if (useAccel) {
            // acel. a lo largo de la trayectoria (m/s^2): + acelera, - frena
            const float along = (ax * vx + ay * vy + az * vz) / speed;
            horizon *= std::clamp(1.0f + 0.06f * along, 0.4f, 2.5f);
        }

        const float dt = horizon / static_cast<float>(kPathSegments);
        const float invSpeed = 1.0f / speed;
        float px = static_cast<float>(x) + vx * invSpeed * kPathStartForwardMeters;
        float py = static_cast<float>(y) + vy * invSpeed * kPathStartForwardMeters;
        float pz = static_cast<float>(z) + vz * invSpeed * kPathStartForwardMeters;
        float cvx = vx, cvy = vy, cvz = vz;

        const float vpL = static_cast<float>(viewport[0]);
        const float vpB = static_cast<float>(viewport[1]);
        const float vpR = vpL + viewport[2];
        const float vpT = vpB + viewport[3];

        float prevSx = 0, prevSy = 0;
        bool prevOk = ProjectSoft(px, py, pz, world, proj, viewport, prevSx, prevSy);

        glBegin(GL_LINES);
        for (int i = 0; i < kPathSegments; ++i) {
            if (useAccel) {
                cvx += ax * dt;
                cvy += ay * dt;
                cvz += az * dt;
            }
            px += cvx * dt;
            py += cvy * dt;
            pz += cvz * dt;

            float sx = 0, sy = 0;
            const bool ok = ProjectSoft(px, py, pz, world, proj, viewport, sx, sy);
            if (prevOk && ok) {
                float x0 = prevSx, y0 = prevSy, x1 = sx, y1 = sy;
                if (ClipLine(x0, y0, x1, y1, vpL, vpB, vpR, vpT)) {
                    glVertex2f(x0, y0);
                    glVertex2f(x1, y1);
                }
            }
            prevSx = sx;
            prevSy = sy;
            prevOk = ok;
        }
        glEnd();
    }

    // Polilinea planificada por el core: cian, grosor 3; rombo cian al
    // inicio y rombo magenta (mas grande) al final. No dibuja si esta vacia
    // o caducada (>1 s sin actualizar).
    void DrawInterceptPath(const float* world, const float* proj,
                           const int* viewport) {
        double pts[kMaxPathPts * 3];
        int n = 0;
        {
            std::lock_guard<std::mutex> lk(mPathMu);
            if (mPathN < 2) return;
            const double age = std::chrono::duration<double>(
                std::chrono::steady_clock::now() - mPathStamp).count();
            if (age > kInterceptPathTtlSec) return;
            n = mPathN;
            std::memcpy(pts, mPath, sizeof(double) * 3 * n);
        }
        const float vpL = static_cast<float>(viewport[0]);
        const float vpB = static_cast<float>(viewport[1]);
        const float vpR = vpL + viewport[2];
        const float vpT = vpB + viewport[3];

        float sx[kMaxPathPts], sy[kMaxPathPts];
        bool ok[kMaxPathPts];
        for (int i = 0; i < n; ++i) {
            ok[i] = ProjectSoft(pts[i * 3], pts[i * 3 + 1], pts[i * 3 + 2],
                                world, proj, viewport, sx[i], sy[i]);
        }

        glColor4f(0.0f, 0.9f, 1.0f, 0.95f);
        glLineWidth(3.0f);
        glBegin(GL_LINES);
        for (int i = 1; i < n; ++i) {
            if (!ok[i - 1] || !ok[i]) continue;
            float x0 = sx[i - 1], y0 = sy[i - 1], x1 = sx[i], y1 = sy[i];
            if (ClipLine(x0, y0, x1, y1, vpL, vpB, vpR, vpT)) {
                glVertex2f(x0, y0);
                glVertex2f(x1, y1);
            }
        }
        glEnd();

        auto onScreen = [&](int i) {
            return ok[i] && sx[i] >= vpL - 40 && sx[i] <= vpR + 40 &&
                   sy[i] >= vpB - 40 && sy[i] <= vpT + 40;
        };
        if (onScreen(0)) DrawDiamond(sx[0], sy[0], 8.0f);
        if (onScreen(n - 1)) {
            glColor4f(1.0f, 0.1f, 0.9f, 1.0f);
            DrawDiamond(sx[n - 1], sy[n - 1], 12.0f);
            DrawDiamond(sx[n - 1], sy[n - 1], 6.0f);
        }
    }

    void DrawDistanceLabel(float screenX, float screenY, float distMeters) {
        char buf[32];
        if (distMeters < 10000.0f) {
            std::snprintf(buf, sizeof(buf), "%.0f m", distMeters);
        } else {
            std::snprintf(buf, sizeof(buf), "%.1f km", distMeters * 0.001f);
        }
        const int len = static_cast<int>(std::strlen(buf));
        const float width = XPLMMeasureString(mFont, buf, len);
        const int x0 = static_cast<int>(screenX - width * 0.5f);
        // Un poco por encima del trazo para que no lo tape la linea.
        const int y0 = static_cast<int>(screenY + 4.0f * mScale);
        XPLMDrawString(mColor, x0, y0, buf, nullptr, mFont);
    }

    void DrawDiamond(float cx, float cy, float half) const {
        glBegin(GL_LINE_LOOP);
        glVertex2f(cx, cy + half);
        glVertex2f(cx + half, cy);
        glVertex2f(cx, cy - half);
        glVertex2f(cx - half, cy);
        glEnd();
    }

    void DrawLabel(float screenX, float screenY) {
        const int passes = mScale >= 2.0f ? 3 : (mScale >= 1.4f ? 2 : 1);
        float width = XPLMMeasureString(mFont, mText, mTextLen);
        int x0 = static_cast<int>(screenX - width * 0.5f);
        int y0 = static_cast<int>(screenY);
        for (int p = 0; p < passes; ++p) {
            int dx = (p == 1) ? 1 : 0;
            int dy = (p == 2) ? 1 : 0;
            XPLMDrawString(mColor, x0 + dx, y0 + dy, mText, nullptr, mFont);
        }
    }

    static void MulMatVec(const float* m, float x, float y, float z, float w,
                           float& outX, float& outY, float& outZ, float& outW) {
        outX = m[0] * x + m[4] * y + m[8] * z + m[12] * w;
        outY = m[1] * x + m[5] * y + m[9] * z + m[13] * w;
        outZ = m[2] * x + m[6] * y + m[10] * z + m[14] * w;
        outW = m[3] * x + m[7] * y + m[11] * z + m[15] * w;
    }

    std::mutex mPathMu;
    double mPath[kMaxPathPts * 3]{};
    int mPathN = 0;
    std::chrono::steady_clock::time_point mPathStamp{};

    uint8_t mFlags = kFlagOwnLabel;
    XPLMFontID mFont = xplmFont_Proportional;
    float mColor[3] = {1.0f, 0.85f, 0.1f};
    float mScale = 1.0f;
    char mText[33] = "Jev";
    int mTextLen = 3;

    XPLMDataRef mLocalX = nullptr;
    XPLMDataRef mLocalY = nullptr;
    XPLMDataRef mLocalZ = nullptr;
    XPLMDataRef mVx = nullptr;
    XPLMDataRef mVy = nullptr;
    XPLMDataRef mVz = nullptr;
    XPLMDataRef mAx = nullptr;
    XPLMDataRef mAy = nullptr;
    XPLMDataRef mAz = nullptr;
    XPLMDataRef mWorldMatrix = nullptr;
    XPLMDataRef mProjMatrix = nullptr;
    XPLMDataRef mViewport = nullptr;
    XPLMDataRef mAiX[kMaxAi]{};
    XPLMDataRef mAiY[kMaxAi]{};
    XPLMDataRef mAiZ[kMaxAi]{};
    XPLMDataRef mAiVx[kMaxAi]{};
    XPLMDataRef mAiVy[kMaxAi]{};
    XPLMDataRef mAiVz[kMaxAi]{};
};
