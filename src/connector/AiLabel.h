// AiLabel.h — overlays 2D sobre aviones (nombre, lineas, triangulo, marcador).
//
// Coach marks en fase xplm_Phase_Window: proyecta local_x/y/z (usuario) y
// sim/multiplayer/position/planeN_* (IAs) a pantalla. La config llega por
// Op.GraphicsConfig desde el core; sin mensaje no se dibuja nada.
//
// Etiquetas: un nombre por avion (XPLM 0..19); vacio = no se dibuja.
// Lineas: distancia 3D (cyan). Triangulo: cateto horizontal (naranja) +
// vertical/altitud (verde) respecto al ownship.
//
// Ver docs/xplane-sdk/guides/plugin-guidance-for-opengl-drawing.md
// ("Use a 2-d Callback for Coach Marks").

#pragma once

#include "XPLMDataAccess.h"
#include "XPLMDisplay.h"
#include "XPLMGraphics.h"
#include "XPLMPlanes.h"

#include "CameraFollow.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
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
    static constexpr int kMaxAi = 19;       // plane1..plane19
    static constexpr int kMaxPlanes = 20;   // XPLM 0..19

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

    // flags: bit2 lines, bit3 markers, bit4 path, bit5 triangle.
    // labels[0..19]: nombre por avion; vacio = sin etiqueta.
    void ApplyConfig(uint8_t flags, uint8_t font,
                     float r, float g, float b, float scale,
                     const std::string labels[kMaxPlanes]) {
        mFlags = flags;
        mFont = (font == 0) ? xplmFont_Basic : xplmFont_Proportional;
        mColor[0] = Clamp01(r);
        mColor[1] = Clamp01(g);
        mColor[2] = Clamp01(b);
        mScale = std::clamp(scale, 0.5f, 4.0f);
        for (int i = 0; i < kMaxPlanes; ++i) {
            const std::string& src = labels[i];
            if (src.empty()) {
                mLabels[i][0] = '\0';
                mLabelLens[i] = 0;
                continue;
            }
            std::strncpy(mLabels[i], src.c_str(), sizeof(mLabels[i]) - 1);
            mLabels[i][sizeof(mLabels[i]) - 1] = '\0';
            mLabelLens[i] = static_cast<int>(std::strlen(mLabels[i]));
        }
    }

private:
    static constexpr uint8_t kFlagLines = 1 << 2;
    static constexpr uint8_t kFlagMarkers = 1 << 3;
    static constexpr uint8_t kFlagPath = 1 << 4;
    static constexpr uint8_t kFlagTriangle = 1 << 5;
    static constexpr double kLabelHeightMeters = 3.5;
    // Horizonte base de la prediccion (segundos). Se alarga/acorta con la
    // aceleracion longitudinal, no con la velocidad.
    static constexpr float kPathHorizonSec = 1.5f;
    static constexpr int kPathSegments = 20;
    // Origen del trazo: CG desplazado hacia adelante (direccion de v) 2.5 m.
    static constexpr float kPathStartForwardMeters = 2.5f;

    // Colores fijos de geometria (distintos entre si y del color de UI).
    static constexpr float kColDist[3]  = {0.20f, 0.85f, 1.00f}; // cyan: linea recta
    static constexpr float kColHoriz[3] = {1.00f, 0.60f, 0.15f}; // naranja: Δ horizontal
    static constexpr float kColVert[3]  = {0.40f, 1.00f, 0.45f}; // verde: Δ altitud (Y)

    static int DrawCallback(XPLMDrawingPhase, int, void* refcon) {
        static_cast<AiLabel*>(refcon)->Draw();
        return 1;
    }

    static float Clamp01(float v) {
        if (v < 0.0f) return 0.0f;
        if (v > 1.0f) return 1.0f;
        return v;
    }

    bool AnyLabelSet() const {
        for (int i = 0; i < kMaxPlanes; ++i) {
            if (mLabelLens[i] > 0) return true;
        }
        return false;
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
        const bool anyGeom = (mFlags & (kFlagLines | kFlagMarkers | kFlagPath |
                                        kFlagTriangle)) != 0;
        const bool anyLbl = AnyLabelSet();
        if (!anyGeom && !anyLbl) return;

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
                                         kFlagTriangle)) != 0;
        if (wantGeom) {
            XPLMSetGraphicsState(0 /*fog*/, 0 /*tex*/, 0 /*light*/,
                                 0 /*alpha test*/, 1 /*blend*/,
                                 0 /*depth test*/, 0 /*depth write*/);
            const float lineW = std::clamp(1.0f * mScale, 1.0f, 4.0f);
            glLineWidth(lineW);

            if ((mFlags & (kFlagLines | kFlagTriangle)) && ownCgOk) {
                for (int i = 1; i < active; ++i) {
                    const AiPt& p = ai[i - 1];
                    if (!p.cgOk) continue;

                    const double dx = p.x - ownX;
                    const double dy = p.y - ownY;
                    const double dz = p.z - ownZ;
                    const float dist3d = static_cast<float>(
                        std::sqrt(dx * dx + dy * dy + dz * dz));
                    const float distHoriz = static_cast<float>(
                        std::sqrt(dx * dx + dz * dz));
                    const float distVert = static_cast<float>(dy);

                    // Esquina del rectangulo: misma pos. horizontal que la IA,
                    // misma altitud que el ownship → catetos Δhoriz / ΔY.
                    const double cx = p.x;
                    const double cy = ownY;
                    const double cz = p.z;
                    float cornerSx = 0, cornerSy = 0;
                    const bool cornerOk = ProjectSoft(cx, cy, cz, world, proj,
                                                      viewport, cornerSx, cornerSy);

                    if (mFlags & kFlagLines) {
                        DrawColoredSegment(ownCgSx, ownCgSy, p.cgSx, p.cgSy,
                                           vpL, vpB, vpR, vpT, kColDist, dist3d,
                                           /*signedLabel=*/false);
                    }

                    if ((mFlags & kFlagTriangle) && cornerOk) {
                        DrawColoredSegment(ownCgSx, ownCgSy, cornerSx, cornerSy,
                                           vpL, vpB, vpR, vpT, kColHoriz, distHoriz,
                                           /*signedLabel=*/false,
                                           kTriangleHorizMinMeters);
                        DrawColoredSegment(cornerSx, cornerSy, p.cgSx, p.cgSy,
                                           vpL, vpB, vpR, vpT, kColVert, distVert,
                                           /*signedLabel=*/true,
                                           kTriangleVertMinMeters);
                    }
                }
            }

            if (mFlags & kFlagMarkers) {
                const float half = 10.0f * mScale;
                const int hi = camera_follow::OverviewTrackIndex();
                if (ownCgOk && ownCgSx >= vpL - 40 && ownCgSx <= vpR + 40 &&
                    ownCgSy >= vpB - 40 && ownCgSy <= vpT + 40) {
                    DrawDiamond(ownCgSx, ownCgSy, half, hi == 0, lineW);
                }
                for (int i = 1; i < active; ++i) {
                    const AiPt& p = ai[i - 1];
                    if (!p.cgOk) continue;
                    if (p.cgSx < vpL - 40 || p.cgSx > vpR + 40 ||
                        p.cgSy < vpB - 40 || p.cgSy > vpT + 40) {
                        continue;
                    }
                    DrawDiamond(p.cgSx, p.cgSy, half, hi == i, lineW);
                }
                glLineWidth(lineW);
                glColor4f(mColor[0], mColor[1], mColor[2], 0.9f);
            }

            if (mFlags & kFlagPath) {
                glColor4f(mColor[0], mColor[1], mColor[2], 0.9f);
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

            glLineWidth(1.0f);
        }

        if (mLabelLens[0] > 0 && ownLblOk)
            DrawPlaneLabel(0, ownLblSx, ownLblSy);
        for (int i = 1; i < active; ++i) {
            if (mLabelLens[i] <= 0) continue;
            const AiPt& p = ai[i - 1];
            if (p.lblOk) DrawPlaneLabel(i, p.lblSx, p.lblSy);
        }
    }

    // Por debajo de estos umbrales no se dibuja el cateto (linea + etiqueta).
    static constexpr float kTriangleHorizMinMeters = 1000.0f;
    static constexpr float kTriangleVertMinMeters = 150.0f;

    void DrawColoredSegment(float sx0, float sy0, float sx1, float sy1,
                            float vpL, float vpB, float vpR, float vpT,
                            const float color[3], float meters,
                            bool signedLabel, float minSegmentMeters = 0.0f) {
        if (std::fabs(meters) < minSegmentMeters) return;
        float x0 = sx0, y0 = sy0, x1 = sx1, y1 = sy1;
        if (!ClipLine(x0, y0, x1, y1, vpL, vpB, vpR, vpT)) return;
        glColor4f(color[0], color[1], color[2], 0.95f);
        glBegin(GL_LINES);
        glVertex2f(x0, y0);
        glVertex2f(x1, y1);
        glEnd();
        const float midSx = (x0 + x1) * 0.5f;
        const float midSy = (y0 + y1) * 0.5f;
        DrawDistanceLabel(midSx, midSy, meters, color, signedLabel);
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

    void DrawDistanceLabel(float screenX, float screenY, float meters,
                           const float color[3], bool signedLabel) {
        char buf[40];
        if (signedLabel) {
            const char* sign = meters >= 0.0f ? "+" : "";
            const float absM = std::fabs(meters);
            if (absM < 10000.0f)
                std::snprintf(buf, sizeof(buf), "%s%.0f m", sign, meters);
            else
                std::snprintf(buf, sizeof(buf), "%s%.1f km", sign, meters * 0.001f);
        } else {
            const float absM = std::fabs(meters);
            if (absM < 10000.0f)
                std::snprintf(buf, sizeof(buf), "%.0f m", absM);
            else
                std::snprintf(buf, sizeof(buf), "%.1f km", absM * 0.001f);
        }
        const int len = static_cast<int>(std::strlen(buf));
        const float width = XPLMMeasureString(mFont, buf, len);
        const int x0 = static_cast<int>(screenX - width * 0.5f);
        const int y0 = static_cast<int>(screenY + 4.0f * mScale);
        float drawColor[3] = {color[0], color[1], color[2]};
        XPLMDrawString(drawColor, x0, y0, buf, nullptr, mFont);
    }

    void DrawDiamond(float cx, float cy, float half, bool selected,
                     float baseLineW) const {
        if (selected) {
            glColor4f(0.15f, 0.95f, 0.25f, 1.0f);
            glLineWidth(std::clamp(baseLineW * 1.6f, 2.0f, 5.0f));
        } else {
            glColor4f(mColor[0], mColor[1], mColor[2], 0.9f);
            glLineWidth(baseLineW);
        }
        glBegin(GL_LINE_LOOP);
        glVertex2f(cx, cy + half);
        glVertex2f(cx + half, cy);
        glVertex2f(cx, cy - half);
        glVertex2f(cx - half, cy);
        glEnd();
    }

    void DrawPlaneLabel(int planeIndex, float screenX, float screenY) {
        char* text = mLabels[planeIndex];
        const int len = mLabelLens[planeIndex];
        if (len <= 0) return;
        const int passes = mScale >= 2.0f ? 3 : (mScale >= 1.4f ? 2 : 1);
        float width = XPLMMeasureString(mFont, text, len);
        int x0 = static_cast<int>(screenX - width * 0.5f);
        int y0 = static_cast<int>(screenY);
        for (int p = 0; p < passes; ++p) {
            int dx = (p == 1) ? 1 : 0;
            int dy = (p == 2) ? 1 : 0;
            XPLMDrawString(mColor, x0 + dx, y0 + dy, text, nullptr, mFont);
        }
    }

    static void MulMatVec(const float* m, float x, float y, float z, float w,
                           float& outX, float& outY, float& outZ, float& outW) {
        outX = m[0] * x + m[4] * y + m[8] * z + m[12] * w;
        outY = m[1] * x + m[5] * y + m[9] * z + m[13] * w;
        outZ = m[2] * x + m[6] * y + m[10] * z + m[14] * w;
        outW = m[3] * x + m[7] * y + m[11] * z + m[15] * w;
    }

    uint8_t mFlags = 0;
    XPLMFontID mFont = xplmFont_Proportional;
    float mColor[3] = {1.0f, 0.85f, 0.1f};
    float mScale = 1.0f;
    char mLabels[kMaxPlanes][33]{};
    int mLabelLens[kMaxPlanes]{};

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
