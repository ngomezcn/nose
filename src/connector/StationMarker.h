// StationMarker.h — bolita 3D en tiempo real del PUESTO objetivo.
//
// El core manda (Op.StationMarker) el punto donde deberia estar el
// interceptor respecto al blanco seleccionado, en su marco estable, junto con
// la velocidad del punto. Aqui solo se dibuja: una esfera sombreada en
// perspectiva (radio real en metros proyectado con la camara de X-Plane,
// con un minimo en pixeles para que se vea de lejos), extrapolada cada frame
// con esa velocidad para que no salte a los ~10 Hz a los que llegan los
// mensajes. Sin logica de dominio: que punto es lo decide el core.
//
// Fase xplm_Phase_Window, mismo metodo de proyeccion que AiLabel.h.

#pragma once

#include "XPLMDataAccess.h"
#include "XPLMDisplay.h"
#include "XPLMGraphics.h"
#include "XPLMUtilities.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>

#if IBM
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif
#include <GL/gl.h>

class StationMarker {
public:
    void Init() {
        mWorldMatrix = XPLMFindDataRef("sim/graphics/view/world_matrix");
        mProjMatrix = XPLMFindDataRef("sim/graphics/view/projection_matrix_3d");
        mViewport = XPLMFindDataRef("sim/graphics/view/viewport");
    }

    void Enable() {
        XPLMRegisterDrawCallback(&StationMarker::DrawCallback, xplm_Phase_Window,
                                 0 /* after */, this);
    }

    void Disable() {
        XPLMUnregisterDrawCallback(&StationMarker::DrawCallback, xplm_Phase_Window,
                                   0 /* after */, this);
    }

    // x/y/z ya en el marco REAL de X-Plane (el caller resta el offset del
    // origen). v*: velocidad del punto en m/s, para extrapolar entre mensajes.
    void Set(bool enabled, double x, double y, double z,
             float vx, float vy, float vz, float radiusM,
             float r, float g, float b) {
        const float now = XPLMGetElapsedTime();
        // Sin salto: el error entre donde se estaba dibujando y donde cae el
        // punto nuevo se guarda y se disipa en unos ms (ver Draw). La
        // velocidad sigue siendo la del mensaje, asi que no hay retraso fijo.
        if (enabled && mEnabled && now - mStamp < 0.5f) {
            double cx, cy, cz;
            CurrentPos(now, cx, cy, cz);
            mErrX = cx - x; mErrY = cy - y; mErrZ = cz - z;
            if (std::fabs(mErrX) + std::fabs(mErrY) + std::fabs(mErrZ) > 300.0)
                mErrX = mErrY = mErrZ = 0.0;   // teletransporte: sin arrastre
        } else {
            mErrX = mErrY = mErrZ = 0.0;
        }
        mEnabled = enabled;
        mX = x; mY = y; mZ = z;
        mVx = vx; mVy = vy; mVz = vz;
        mRadius = std::clamp(radiusM, 0.5f, 200.0f);
        mColor[0] = Clamp01(r); mColor[1] = Clamp01(g); mColor[2] = Clamp01(b);
        mStamp = now;
    }

private:
    static constexpr float kBlendTauSec = 0.06f;

    // Posicion a dibujar en el instante t: extrapolacion lineal + el error del
    // ultimo mensaje decayendo exponencialmente.
    void CurrentPos(float t, double& px, double& py, double& pz) const {
        float dt = std::clamp(t - mStamp, 0.0f, kMaxExtrapolateSec);
        const double k = std::exp(-static_cast<double>(dt) / kBlendTauSec);
        px = mX + mVx * dt + mErrX * k;
        py = mY + mVy * dt + mErrY * k;
        pz = mZ + mVz * dt + mErrZ * k;
    }

    static constexpr float kMinPixelRadius = 9.0f;
    static constexpr int kSegments = 40;
    static constexpr float kMaxExtrapolateSec = 0.5f;

    static float Clamp01(float v) { return std::clamp(v, 0.0f, 1.0f); }

    static int DrawCallback(XPLMDrawingPhase, int, void* refcon) {
        static_cast<StationMarker*>(refcon)->Draw();
        return 1;
    }

    static void MulMatVec(const float* m, float x, float y, float z, float w,
                          float& outX, float& outY, float& outZ, float& outW) {
        outX = m[0] * x + m[4] * y + m[8] * z + m[12] * w;
        outY = m[1] * x + m[5] * y + m[9] * z + m[13] * w;
        outZ = m[2] * x + m[6] * y + m[10] * z + m[14] * w;
        outW = m[3] * x + m[7] * y + m[11] * z + m[15] * w;
    }

    void Draw() {
        if (!mEnabled || !mWorldMatrix || !mProjMatrix || !mViewport) return;

        float world[16];
        float proj[16];
        int vp[4];
        XPLMGetDatavf(mWorldMatrix, world, 0, 16);
        XPLMGetDatavf(mProjMatrix, proj, 0, 16);
        XPLMGetDatavi(mViewport, vp, 0, 4);

        double px, py, pz;
        CurrentPos(XPLMGetElapsedTime(), px, py, pz);

        float ex, ey, ez, ew;
        MulMatVec(world, static_cast<float>(px), static_cast<float>(py),
                  static_cast<float>(pz), 1.0f, ex, ey, ez, ew);
        float cx, cy, cz, cw;
        MulMatVec(proj, ex, ey, ez, ew, cx, cy, cz, cw);
        if (cw <= 1e-4f) return;   // detras de la camara

        const float sx = vp[0] + (cx / cw * 0.5f + 0.5f) * vp[2];
        const float sy = vp[1] + (cy / cw * 0.5f + 0.5f) * vp[3];
        // Radio en pixeles: proj[5] = cot(fov_y / 2); cw = profundidad.
        float rPx = mRadius * proj[5] / cw * vp[3] * 0.5f;
        rPx = std::max(rPx, kMinPixelRadius);

        const float margin = rPx + 40.0f;
        if (sx < vp[0] - margin || sx > vp[0] + vp[2] + margin ||
            sy < vp[1] - margin || sy > vp[1] + vp[3] + margin)
            return;

        XPLMSetGraphicsState(0 /*fog*/, 0 /*tex*/, 0 /*light*/, 0 /*alpha test*/,
                             1 /*blend*/, 0 /*depth test*/, 0 /*depth write*/);

        // Cuerpo: abanico con el punto brillante desplazado hacia arriba a la
        // izquierda y el borde oscuro y algo mas transparente: lee como esfera.
        const float hx = sx - 0.30f * rPx, hy = sy + 0.30f * rPx;
        glBegin(GL_TRIANGLE_FAN);
        glColor4f(Mix(mColor[0], 1.0f, 0.75f), Mix(mColor[1], 1.0f, 0.75f),
                  Mix(mColor[2], 1.0f, 0.75f), 0.85f);
        glVertex2f(hx, hy);
        for (int i = 0; i <= kSegments; ++i) {
            const float a = 6.2831853f * i / kSegments;
            glColor4f(mColor[0] * 0.35f, mColor[1] * 0.35f, mColor[2] * 0.35f, 0.55f);
            glVertex2f(sx + std::cos(a) * rPx, sy + std::sin(a) * rPx);
        }
        glEnd();

        // Contorno + ecuador y meridiano elipticos: refuerzan el volumen.
        glLineWidth(1.6f);
        glColor4f(mColor[0], mColor[1], mColor[2], 0.95f);
        DrawEllipse(sx, sy, rPx, rPx);
        glColor4f(mColor[0], mColor[1], mColor[2], 0.55f);
        DrawEllipse(sx, sy, rPx, rPx * 0.32f);
        DrawEllipse(sx, sy, rPx * 0.32f, rPx);
        glLineWidth(1.0f);

        float col[3] = {mColor[0], mColor[1], mColor[2]};
        const char* txt = "PUESTO";
        const float tw = XPLMMeasureString(xplmFont_Proportional, txt,
                                           static_cast<int>(std::strlen(txt)));
        XPLMDrawString(col, static_cast<int>(sx - tw * 0.5f),
                       static_cast<int>(sy + rPx + 6.0f), const_cast<char*>(txt),
                       nullptr, xplmFont_Proportional);
    }

    static float Mix(float a, float b, float t) { return a + (b - a) * t; }

    static void DrawEllipse(float cx, float cy, float rx, float ry) {
        glBegin(GL_LINE_LOOP);
        for (int i = 0; i < kSegments; ++i) {
            const float a = 6.2831853f * i / kSegments;
            glVertex2f(cx + std::cos(a) * rx, cy + std::sin(a) * ry);
        }
        glEnd();
    }

    XPLMDataRef mWorldMatrix = nullptr;
    XPLMDataRef mProjMatrix = nullptr;
    XPLMDataRef mViewport = nullptr;

    bool mEnabled = false;
    double mX = 0, mY = 0, mZ = 0;
    float mVx = 0, mVy = 0, mVz = 0;
    double mErrX = 0, mErrY = 0, mErrZ = 0;
    float mRadius = 6.0f;
    float mColor[3] = {0.1f, 0.9f, 1.0f};
    float mStamp = 0.0f;
};
