// OriginWatch.h
//
// Detecta cuando X-Plane recoloca el ORIGEN de su marco local y mantiene el
// desplazamiento acumulado para que el core viva en un marco estable.
//
// -- Por que existe ------------------------------------------------------
//
// Hipotesis (documentada tras el incidente del 2026-09-30, pendiente de
// confirmar en un vuelo con esta deteccion activa):
//
//   Durante un intercept de ~250 s el ownship estaba a ~54 km del origen
//   local. En UN frame su local_x paso de 54 277 a -29 226 (-83,5 km; z
//   +142 m) con IAS/altitud/G continuas, mientras el blanco (A330, IA
//   cinematica) no se movia de 54 556. El core veia range=83 km, cambiaba a
//   "Persecucion" y el avion "se teletransportaba" 80 km. La explicacion
//   que encaja: X-Plane movio lat_ref/lon_ref (el punto de referencia del
//   marco local) y re-expreso las posiciones que el posee (ownship), pero
//   las IAs cinematicas las SOSTENEMOS nosotros escribiendo planeN_x/z cada
//   frame desde un modelo que integra en el marco viejo. Resultado: el
//   ownship en el marco nuevo, la IA en el viejo.
//
//   Lo respaldan los docs locales: movingtheplane.md ("the local coordinate
//   system moves to keep the aircraft near the origin"; "lat_ref/lon_ref:
//   the sim will pick this value for you as it processes scenery") y
//   pluginsandobjects.md ("watch lat_ref/lon_ref to determine when scenery
//   has shifted"). Lo que NO se ha podido confirmar: el Log.txt de X-Plane
//   de esa hora ya no existia y DataLog.planeN.csv no graba posicion.
//
//   Si vuelve a pasar: buscar en AICopilot_log.txt / Log.txt la linea
//   "OriginWatch: desplazamiento de origen" y en la caja negra la marca
//   "AVISO: origen local desplazado". Si el salto ocurre SIN esa linea, la
//   hipotesis es falsa (el ownship se movio por otra causa).
//
// -- Como se detecta -------------------------------------------------------
//
// Un punto FIJO del mundo (lat/lon/elev) convertido con XPLMWorldToLocal da
// siempre el mismo local_x/z MIENTRAS el origen no cambie; si cambia, el
// resultado salta exactamente lo que se desplazo el marco. Es exacto y no
// depende de la velocidad del avion ni de teletransportes del usuario.
// lat_ref/lon_ref se leen solo para dejarlos en el log.
//
// -- Que se hace con ello ----------------------------------------------------
//
// El desplazamiento se acumula en DatarefRegistry::SetOriginOffset: las
// entradas de posicion local se leen/escriben trasladadas (ver
// proto::FrameAxis), asi que el core ve un marco que no salta nunca y los
// holds de planeN_x/z siguen apuntando al sitio correcto en el mismo frame.
//
// Limite conocido: solo se traslada (x, z). La rotacion del marco (la
// convergencia de meridianos, ~0,75 grados por 83 km) y la Y local no se
// corrigen; para un salto de origen ocasional es despreciable frente al
// error que evita.
#pragma once

#include <cmath>
#include <cstdio>
#include <string>

#include "XPLMDataAccess.h"
#include "XPLMGraphics.h"

#include "DatarefRegistry.h"
#include "Logger.h"

class OriginWatch {
public:
    // Fija el punto de referencia al usuario AHORA y pone el offset a cero.
    // Se llama al empezar una sesion de core (su marco estable es el marco
    // real de este instante).
    void Reset(DatarefRegistry& registry) {
        registry.SetOriginOffset(0.0, 0.0);
        totalX_ = totalZ_ = 0.0;
        armed_ = Arm();
    }

    // Una vez por frame, ANTES de aplicar holds y leer telemetria. Devuelve
    // true si el origen cambio en este frame; `text` lleva el detalle para el
    // evento al core.
    bool Update(DatarefRegistry& registry, std::string& text) {
        if (!armed_) {
            armed_ = Arm();
            return false;
        }

        double x = 0, y = 0, z = 0;
        XPLMWorldToLocal(lat_, lon_, elev_, &x, &y, &z);
        const double dx = x - ax_, dy = y - ay_, dz = z - az_;
        if (std::hypot(dx, dz) < kThresholdM) return false;

        // estable = real + offset. El ancla real paso de a -> a+d, y en el
        // marco estable tiene que seguir en `a`: offset -= d.
        totalX_ -= dx;
        totalZ_ -= dz;
        registry.SetOriginOffset(totalX_, totalZ_);
        ax_ = x; ay_ = y; az_ = z;

        const double latRef = ReadRef("sim/flightmodel/position/lat_ref");
        const double lonRef = ReadRef("sim/flightmodel/position/lon_ref");
        LogWarn("OriginWatch: desplazamiento de origen local dx=%.1f dy=%.1f dz=%.1f m "
                "(lat_ref=%.6f lon_ref=%.6f). Compensado: offset total x=%.1f z=%.1f.",
                dx, dy, dz, latRef, lonRef, totalX_, totalZ_);

        char buf[200];
        std::snprintf(buf, sizeof(buf),
                      "dx=%.1f;dy=%.1f;dz=%.1f;latRef=%.6f;lonRef=%.6f;total=%.1f,%.1f",
                      dx, dy, dz, latRef, lonRef, totalX_, totalZ_);
        text = buf;
        return true;
    }

private:
    // Cualquier cambio real de origen es de cientos de metros como minimo;
    // esto solo absorbe ruido numerico.
    static constexpr double kThresholdM = 0.5;

    bool Arm() {
        XPLMDataRef rx = XPLMFindDataRef("sim/flightmodel/position/local_x");
        XPLMDataRef ry = XPLMFindDataRef("sim/flightmodel/position/local_y");
        XPLMDataRef rz = XPLMFindDataRef("sim/flightmodel/position/local_z");
        if (!rx || !ry || !rz) return false;
        XPLMLocalToWorld(XPLMGetDatad(rx), XPLMGetDatad(ry), XPLMGetDatad(rz),
                         &lat_, &lon_, &elev_);
        XPLMWorldToLocal(lat_, lon_, elev_, &ax_, &ay_, &az_);
        return true;
    }

    static double ReadRef(const char* name) {
        XPLMDataRef r = XPLMFindDataRef(name);
        if (!r) return 0.0;
        XPLMDataTypeID t = XPLMGetDataRefTypes(r);
        if (t & xplmType_Double) return XPLMGetDatad(r);
        if (t & xplmType_Float) return static_cast<double>(XPLMGetDataf(r));
        return 0.0;
    }

    bool armed_ = false;
    double lat_ = 0, lon_ = 0, elev_ = 0;   // punto fijo del mundo
    double ax_ = 0, ay_ = 0, az_ = 0;       // donde cae en el marco real
    double totalX_ = 0, totalZ_ = 0;        // offset acumulado estable-real
};
