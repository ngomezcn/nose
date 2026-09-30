// plugin.cpp — AI Copilot Connector
//
// El puente, y nada mas que el puente. Este plugin no sabe que es un
// despegue, ni que es un PID, ni que datarefs importan: sabe leer y
// escribir cualquier dataref que le pidan, disparar cualquier comando, y
// mantener valores puestos (holds) para que el otro lado no tenga que
// mandar trafico por frame para cosas que son constantes.
//
// Toda la logica y la UI viven en el core (src/core -> AICopilotCore.exe),
// un proceso de Windows aparte que habla con esto por un named pipe. La
// idea es que añadir una fase de vuelo, un PID nuevo o un panel nuevo se
// haga tocando solo C#, sin recompilar el plugin ni recargarlo en X-Plane.
//
// Lo que SI es responsabilidad de este lado, y no puede irse al core:
// la seguridad. Ver SafetyGuard.h — con el que decide viviendo en otro
// proceso, alguien tiene que soltar los overrides cuando ese proceso
// desaparece, y solo puede ser este.
//
// XPLM200..XPLM400 se definen como target_compile_definitions en
// CMakeLists.txt para todo el target.

#include <cstdint>
#include <cstring>
#include <string>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include "XPLMDefs.h"
#include "XPLMPlanes.h"
#include "XPLMPlugin.h"
#include "XPLMProcessing.h"
#include "XPLMUtilities.h"

#include "AiControl.h"
#include "AiLabel.h"
#include "StationMarker.h"
#include "CameraFollow.h"
#include "DatarefRegistry.h"
#include "Holds.h"
#include "Logger.h"
#include "OriginWatch.h"
#include "PipeServer.h"
#include "Protocol.h"
#include "SafetyGuard.h"
#include "ScenarioPlace.h"
#include "Subscriptions.h"

// v4.0: el plugin deja de ser la aplicacion y pasa a ser el connector.
// El contador de build vive solo en la UI (tools\build_and_deploy_ui.ps1);
// el addon no lo necesita.
static const char* kPluginVersion = "v4.0";
static const std::string kPluginVersionFull = kPluginVersion;

namespace {

PipeServer* g_pipe = nullptr;
DatarefRegistry* g_registry = nullptr;
Holds* g_holds = nullptr;
Subscriptions* g_subs = nullptr;
SafetyGuard* g_guard = nullptr;
AiLabel* g_label = nullptr;
StationMarker* g_marker = nullptr;

uint32_t g_frame = 0;
uint32_t g_lastSession = 0;
OriginWatch g_origin;
std::string g_pluginDir;

// Ruta (con barra final) de la carpeta donde vive este .xpl. Se usa para
// el log propio y para encontrar AICopilotCore.exe, que se despliega justo
// al lado (ver tools/build_and_deploy_ui.ps1).
std::string ComputePluginDir() {
    char path[512] = {0};
    XPLMGetPluginInfo(XPLMGetMyID(), nullptr, path, nullptr, nullptr);
    std::string p(path);
    size_t sep = p.find_last_of("/\\");
    return (sep != std::string::npos) ? p.substr(0, sep + 1) : std::string();
}

void SendEvent(proto::EventKind kind, const std::string& text) {
    if (!g_pipe || !g_pipe->IsConnected()) return;
    proto::Writer w(proto::Op::Event);
    w.U8(static_cast<uint8_t>(kind));
    w.Str(text);
    g_pipe->Send(w.Take(), false);
}

void NotifyOverviewFocus(int xplmIndex) {
    SendEvent(proto::EventKind::OverviewFocus, std::to_string(xplmIndex));
}

// Suelta todo y avisa al core. Se usa desde los mensajes de X-Plane
// (choque, aeropuerto nuevo, avion recargado) y desde XPluginDisable.
void ReleaseEverything(const char* reason, bool notifyCore) {
    if (g_marker) g_marker->Set(false, 0, 0, 0, 0, 0, 0, 1.0f, 0, 0, 0);
    camera_follow::Stop();
    ai_control::Release();
    // OJO: no cancela la colocacion pendiente de PlaceScenario. AIRPORT_LOADED
    // y PLANE_LOADED (que llegan justo despues de teletransportar al usuario)
    // llaman aqui, y cancelarla dejaba a la IA sin colocar. Se cancela solo al
    // desactivar, en un crash y al empezar otro PlaceScenario.
    if (!g_guard || !g_holds || !g_registry) return;
    g_guard->ReleaseAll(reason, *g_holds, *g_registry);
    if (notifyCore) {
        SendEvent(proto::EventKind::OverridesReleased, reason);
    }
}

// --- Despacho de mensajes del core -----------------------------------------
// Se llama desde PipeServer::Poll(), o sea desde el flight loop: aqui si se
// puede tocar el SDK.

void HandleMessage(const uint8_t* data, size_t size) {
    proto::Reader r(data, size);
    const proto::Op op = static_cast<proto::Op>(r.U8());
    if (!r.Ok()) return;

    switch (op) {
        case proto::Op::Hello: {
            uint16_t version = r.U16();
            std::string clientName = r.Str();
            if (!r.Ok()) return;

            int xplaneVer = 0, xplmVer = 0;
            XPLMHostApplicationID host;
            XPLMGetVersions(&xplaneVer, &xplmVer, &host);

            proto::Writer w(proto::Op::HelloAck);
            w.U16(proto::kVersion);
            w.Str(kPluginVersionFull);
            w.I32(xplaneVer);
            w.I32(xplmVer);
            g_pipe->Send(w.Take(), false);

            LogInfo("HELLO de '%s' (protocolo v%u).", clientName.c_str(), version);
            if (version != proto::kVersion) {
                // No intentamos interpretar los mensajes de una version que
                // no conocemos: los layouts podrian haber cambiado y
                // escribiriamos valores sin sentido en datarefs reales.
                char msg[160];
                std::snprintf(msg, sizeof(msg),
                              "Version de protocolo incompatible: el core habla v%u "
                              "y el connector v%u. Recompila los dos.",
                              version, proto::kVersion);
                LogWarn("%s", msg);
                SendEvent(proto::EventKind::Error, msg);
            }
            break;
        }

        case proto::Op::Define: {
            uint16_t id = r.U16();
            uint8_t kind = r.U8();
            int32_t index = r.I32();
            int32_t count = r.I32();
            uint8_t frame = r.U8();
            std::string name = r.Str();
            if (!r.Ok()) return;

            DatarefRegistry::Entry* e = g_registry->Define(
                id, static_cast<proto::EntryKind>(kind), name, index, count,
                static_cast<proto::FrameAxis>(frame));

            proto::Writer w(proto::Op::DefineAck);
            w.U16(id);
            w.U8(e && e->resolved ? 1 : 0);
            w.U8(e ? static_cast<uint8_t>(e->dataKind) : 0);
            w.I32(e ? e->arrayLength : 0);
            w.U8(e && e->writable ? 1 : 0);
            g_pipe->Send(w.Take(), false);
            break;
        }

        case proto::Op::Subscribe: {
            uint16_t count = r.U16();
            for (uint16_t i = 0; i < count; ++i) {
                uint16_t id = r.U16();
                uint8_t divisor = r.U8();
                if (!r.Ok()) return;
                g_subs->Set(id, divisor);
            }
            break;
        }

        case proto::Op::Unsubscribe: {
            uint16_t count = r.U16();
            for (uint16_t i = 0; i < count; ++i) {
                uint16_t id = r.U16();
                if (!r.Ok()) return;
                g_subs->Remove(id);
            }
            break;
        }

        case proto::Op::Set: {
            // Escritura puntual. Si ese dataref esta bajo hold, el hold
            // volvera a imponer su valor en el Apply de este mismo frame:
            // para cambiar algo mantenido hay que mandar otro HOLD, no un
            // SET. SET es para lo que el sim anima y el usuario deberia
            // poder seguir tocando (flaps, tren...).
            uint16_t count = r.U16();
            for (uint16_t i = 0; i < count; ++i) {
                uint16_t id = r.U16();
                double value = r.F64();
                if (!r.Ok()) return;
                DatarefRegistry::Entry* e = g_registry->Get(id);
                if (e) g_registry->Write(*e, value);
            }
            break;
        }

        case proto::Op::Hold: {
            uint16_t id = r.U16();
            uint8_t mode = r.U8();
            double value = r.F64();
            float rate = r.F32();
            if (!r.Ok()) return;
            g_holds->Set(id, static_cast<proto::HoldMode>(mode), value, rate, *g_registry);
            break;
        }

        case proto::Op::Toggle: {
            uint16_t id = r.U16();
            if (!r.Ok()) return;
            g_holds->Toggle(id, *g_registry);
            break;
        }

        case proto::Op::Pulse: {
            uint16_t id = r.U16();
            double value = r.F64();
            uint16_t ms = r.U16();
            if (!r.Ok()) return;
            g_holds->Pulse(id, value, ms, *g_registry);
            break;
        }

        case proto::Op::Command: {
            uint16_t id = r.U16();
            uint8_t action = r.U8();
            if (!r.Ok()) return;

            DatarefRegistry::Entry* e = g_registry->Get(id);
            if (!e || e->kind != proto::EntryKind::Command || !e->resolved) break;

            switch (static_cast<proto::CmdAction>(action)) {
                case proto::CmdAction::Once:
                    XPLMCommandOnce(e->command);
                    break;
                case proto::CmdAction::Begin:
                    // El guardia de commandHeld evita dos Begin seguidos sin
                    // End, que dejaria el comando pulsado aunque luego
                    // llegue un solo End.
                    if (!e->commandHeld) {
                        XPLMCommandBegin(e->command);
                        e->commandHeld = true;
                    }
                    break;
                case proto::CmdAction::End:
                    if (e->commandHeld) {
                        XPLMCommandEnd(e->command);
                        e->commandHeld = false;
                    }
                    break;
            }
            break;
        }

        case proto::Op::ReleaseAll:
            ReleaseEverything("el core lo pidio", true);
            break;

        case proto::Op::Ping: {
            uint32_t seq = r.U32();
            if (!r.Ok()) return;
            proto::Writer w(proto::Op::Pong);
            w.U32(seq);
            g_pipe->Send(w.Take(), false);
            break;
        }

        case proto::Op::GraphicsConfig: {
            uint8_t flags = r.U8();
            uint8_t font = r.U8();
            float cr = r.F32();
            float cg = r.F32();
            float cb = r.F32();
            float scale = r.F32();
            std::string labels[AiLabel::kMaxPlanes];
            for (int i = 0; i < AiLabel::kMaxPlanes; ++i)
                labels[i] = r.Str();
            if (!r.Ok()) return;
            if (g_label) g_label->ApplyConfig(flags, font, cr, cg, cb, scale, labels);
            break;
        }

        case proto::Op::PlaceScenario: {
            double userLat = r.F64();
            double userLon = r.F64();
            float userElev = static_cast<float>(r.F64());
            float userHdg = static_cast<float>(r.F64());
            float userSpd = static_cast<float>(r.F64());
            double aiLat = r.F64();
            double aiLon = r.F64();
            float aiElev = static_cast<float>(r.F64());
            float aiHdg = static_cast<float>(r.F64());
            float aiSpd = static_cast<float>(r.F64());
            std::string aiPath = r.Str();
            bool aiOnGround = r.U8() != 0;
            if (!r.Ok()) return;

            // Suelta overrides propios antes de teletransportar: si no, el
            // avion llega a la pista con holds viejos pegados.
            scenario::CancelPending();
            ReleaseEverything("PlaceScenario", false);
            scenario::Begin(userLat, userLon, userElev, userHdg, userSpd,
                            aiLat, aiLon, aiElev, aiHdg, aiSpd, aiPath, aiOnGround);
            SendEvent(proto::EventKind::Info,
                      "Inicio de simulacion: usuario colocado; IA en cuanto cargue el escenario.");
            break;
        }

        case proto::Op::AiControl: {
            uint8_t action = r.U8();
            uint8_t planeIndex = r.U8();
            if (!r.Ok()) return;

            if (action == 0) {
                // planeIndex 0 = soltar todo; 1..19 = solo ese avion.
                if (planeIndex != 0 && !ai_control::IsValid(planeIndex)) {
                    SendEvent(proto::EventKind::Warning,
                              "AiControl: Release rechazo (planeIndex fuera de 0..19).");
                    break;
                }
                ai_control::Release(planeIndex);
                SendEvent(proto::EventKind::Info, "AiControl: Release ok.");
                break;
            }
            if (action == 1) {
                if (planeIndex < 1 || planeIndex > 19) {
                    LogWarn("AiControl: Take con planeIndex=%u invalido (1..19).",
                            static_cast<unsigned>(planeIndex));
                    SendEvent(proto::EventKind::Warning,
                              "AiControl: Take rechazo (planeIndex fuera de 1..19).");
                    break;
                }
                if (ai_control::Take(planeIndex)) {
                    SendEvent(proto::EventKind::Info, "AiControl: Take ok.");
                } else {
                    SendEvent(proto::EventKind::Warning,
                              "AiControl: Take fallo (AcquirePlanes).");
                }
                break;
            }
            LogWarn("AiControl: action=%u desconocida.",
                    static_cast<unsigned>(action));
            SendEvent(proto::EventKind::Warning, "AiControl: action desconocida.");
            break;
        }

        case proto::Op::StationMarker: {
            uint8_t enable = r.U8();
            double x = r.F64();
            double y = r.F64();
            double z = r.F64();
            float vx = r.F32();
            float vy = r.F32();
            float vz = r.F32();
            float radius = r.F32();
            float cr = r.F32();
            float cg = r.F32();
            float cb = r.F32();
            if (!r.Ok()) return;
            // Marco estable del core -> marco local real de X-Plane.
            if (g_marker)
                g_marker->Set(enable != 0, x - g_registry->OriginOffsetX(), y,
                              z - g_registry->OriginOffsetZ(), vx, vy, vz, radius,
                              cr, cg, cb);
            break;
        }

        case proto::Op::CameraFollow: {
            uint8_t planeIndex = r.U8();
            if (!r.Ok()) return;

            if (planeIndex == 0) {
                camera_follow::Stop();
                SendEvent(proto::EventKind::Info, "CameraFollow: Stop ok.");
                break;
            }
            if (planeIndex == camera_follow::kOverviewIndex) {
                if (camera_follow::StartOverview()) {
                    SendEvent(proto::EventKind::Info,
                              "CameraFollow: Overview ok.");
                } else {
                    SendEvent(proto::EventKind::Warning,
                              "CameraFollow: Overview fallo (datarefs).");
                }
                break;
            }
            if (planeIndex > 19) {
                LogWarn("CameraFollow: planeIndex=%u invalido (0..19|255).",
                        static_cast<unsigned>(planeIndex));
                SendEvent(proto::EventKind::Warning,
                          "CameraFollow: rechazo (planeIndex fuera de rango).");
                break;
            }
            if (camera_follow::Start(planeIndex)) {
                SendEvent(proto::EventKind::Info, "CameraFollow: Start ok.");
            } else {
                SendEvent(proto::EventKind::Warning,
                          "CameraFollow: Start fallo (datarefs).");
            }
            break;
        }

        default:
            break;
    }
}

// Lista de aviones de la partida. XPLMCountAircraft cuenta el del usuario
// (indice 0) mas las IAs que estan realmente activas; XPLMGetNthAircraftModel
// devuelve el .acf de cada uno. No es un dataref, por eso sale por su
// propio mensaje y no por el DEFINE del core. Solo desde el flight loop:
// el header marca estas dos llamadas como no seguras fuera del hilo
// principal.
//
// Cada 30 frames (~2 Hz a 60, ~1 Hz a 30). El modelo no cambia a ritmo de
// frame; la posicion de las IAs va por telemetria, aparte.
void SendAircraftRoster() {
    if (!g_pipe || !g_pipe->IsConnected()) return;
    if (g_frame % 30 != 0) return;

    int total = 0;
    int active = 0;
    XPLMCountAircraft(&total, &active, nullptr);
    if (active < 0) active = 0;
    if (total > 0 && active > total) active = total;
    if (active > 64) active = 64;

    proto::Writer w(proto::Op::Planes);
    w.U8(static_cast<uint8_t>(active));
    for (int i = 0; i < active; ++i) {
        char fileName[256] = {};
        char path[512] = {};
        XPLMGetNthAircraftModel(i, fileName, path);
        // Por si el sim no termina en 0: el buffer es de tamano fijo.
        fileName[255] = '\0';
        path[511] = '\0';
        w.U8(static_cast<uint8_t>(i));
        w.Str(fileName);
        w.Str(path);
    }
    g_pipe->Send(w.Take(), true);
}

// --- Flight loop -----------------------------------------------------------

// Tiempo de SIMULACION desde el frame anterior. El dt que da X-Plane al flight
// loop es de pared: en pausa (o con sim_speed 0) sigue corriendo y el core, que
// integra las IAs con ese dt, las movia con el mundo congelado. Con 0 todas las
// secuencias del core se quedan quietas (todas exigen dt > 0).
static float SimDt(float wallDt) {
    static XPLMDataRef paused = XPLMFindDataRef("sim/time/paused");
    static XPLMDataRef speed = XPLMFindDataRef("sim/time/sim_speed");
    if (paused && XPLMGetDatai(paused) != 0) return 0.0f;
    if (speed && XPLMGetDatai(speed) == 0) return 0.0f;
    return wallDt;
}

float FlightLoopCallback(float wallDt, float, int, void*) {
    const float dt = SimDt(wallDt);
    // 1. ¿Sesion nueva? El core que se acaba de conectar no sabe nada de los
    //    ids ni de los holds del anterior, asi que se empieza de cero. Va
    //    ANTES de Poll() para que el HELLO de la sesion nueva no se
    //    encuentre con el estado de la vieja a medio limpiar.
    uint32_t session = g_pipe->SessionId();
    if (session != g_lastSession) {
        if (g_lastSession != 0) {
            g_holds->ReleaseAll(*g_registry);
            g_registry->EndAllHeldCommands();
        }
        g_lastSession = session;
        g_holds->Clear();
        g_subs->Clear();
        g_registry->Clear();
        g_frame = 0;
        g_guard->NoteConnected();
        // El marco estable del core nace aqui: offset 0 y ancla nueva.
        g_origin.Reset(*g_registry);
    }

    // 1b. ¿X-Plane movio el origen local? Antes de aplicar holds y leer
    //     telemetria, para que este mismo frame ya salga en el marco estable.
    {
        std::string originText;
        if (g_origin.Update(*g_registry, originText)) {
            SendEvent(proto::EventKind::OriginShift, originText);
        }
        if (g_label)
            g_label->SetOriginOffset(g_registry->OriginOffsetX(),
                                     g_registry->OriginOffsetZ());
    }

    // 2. Seguridad primero: si el core murio o se quedo mudo, soltar antes
    //    de hacer nada mas.
    g_guard->Update(*g_pipe, *g_holds, *g_registry);

    // 3. Todo lo que llego por el pipe desde el frame anterior.
    g_pipe->Poll();

    // 4. Reescribir los holds. Va despues del paso 3 para que un objetivo
    //    recien llegado tenga efecto en ESTE frame, y despues de que el sim
    //    ya haya hecho lo suyo, que es lo que hace que el override gane la
    //    pelea por el dataref.
    g_holds->Apply(dt, *g_registry);

    // 5. Telemetria: se lee al final, asi que lo que reporta es el estado
    //    de verdad tras aplicar los holds, no el de antes.
    if (g_pipe->IsConnected() && !g_subs->Empty()) {
        proto::Writer w(proto::Op::Telemetry);
        if (g_subs->BuildTelemetry(g_frame, dt, *g_registry, w)) {
            g_pipe->Send(w.Take(), true);  // droppable: si se atasca, manda el siguiente
        }
    }

    // 6. Que aviones hay (el nuestro y las IAs). Independiente de que haya
    //    suscripciones: el listado de la UI tiene que salir igual.
    SendAircraftRoster();

    // 7. IA pendiente de un PlaceScenario (espera AIRPORT_LOADED + un poco).
    if (scenario::TickPending()) {
        SendEvent(proto::EventKind::ScenarioReady,
                  "Escenario colocado: aplica config idle de suelo.");
    }

    ++g_frame;
    return -1.0f;  // cada frame
}

}  // namespace

PLUGIN_API int XPluginStart(char* outName, char* outSig, char* outDesc) {
    std::string nameWithVersion =
        std::string("AI Copilot Connector ") + kPluginVersionFull;
    std::strcpy(outName, nameWithVersion.c_str());
    std::strcpy(outSig, "com.naim.aicopilot");
    std::strcpy(outDesc,
                "Puente de datarefs y comandos para X-Plane 12. Expone lectura, "
                "escritura, holds y comandos por named pipe; toda la logica y la "
                "UI viven en AICopilotCore.exe, un proceso aparte.");

    g_pluginDir = ComputePluginDir();
    Logger::Instance().Open(g_pluginDir + "AICopilot_log.txt");
    LogInfo("AICopilot Connector %s cargado (protocolo v%u).",
            kPluginVersionFull.c_str(), proto::kVersion);

    g_registry = new DatarefRegistry();
    g_holds = new Holds();
    g_subs = new Subscriptions();
    g_guard = new SafetyGuard();
    g_pipe = new PipeServer();
    g_pipe->SetHandler(&HandleMessage);

    g_label = new AiLabel();
    g_label->Init();
    g_marker = new StationMarker();
    g_marker->Init();

    camera_follow::SetOverviewFocusNotify(&NotifyOverviewFocus);

    XPLMRegisterFlightLoopCallback(FlightLoopCallback, -1.0f, nullptr);
    return 1;
}

PLUGIN_API void XPluginStop() {
    XPLMUnregisterFlightLoopCallback(FlightLoopCallback, nullptr);
    camera_follow::Shutdown();

    delete g_label;  g_label = nullptr;
    delete g_marker; g_marker = nullptr;
    delete g_pipe;   g_pipe = nullptr;
    delete g_guard;  g_guard = nullptr;
    delete g_subs;   g_subs = nullptr;
    delete g_holds;  g_holds = nullptr;
    delete g_registry; g_registry = nullptr;

    LogInfo("AICopilot Connector %s descargado.", kPluginVersionFull.c_str());
    Logger::Instance().Close();
}

PLUGIN_API int XPluginEnable() {
    // El plugin abre el pipe y se queda esperando, pero NO lanza el core: ese
    // lo abre el usuario a mano cuando quiere, y arrancar X-Plane no tiene por
    // que llenarle el escritorio de ventanas. Da igual el orden -- el core
    // reintenta conectar en bucle, asi que se puede abrir y cerrar tantas
    // veces como haga falta con X-Plane corriendo.
    g_pipe->Start(proto::kPipeName);
    g_label->Enable();
    g_marker->Enable();
    return 1;
}

PLUGIN_API void XPluginDisable() {
    // Soltar ANTES de cerrar el pipe: una vez cerrado no hay forma de
    // avisar al core, y sobre todo hay que dejar el avion pilotable pase lo
    // que pase.
    scenario::CancelPending();
    ReleaseEverything("el plugin se esta desactivando", true);
    g_pipe->Stop();
    g_label->Disable();
    g_marker->Disable();
    g_lastSession = 0;
}

PLUGIN_API void XPluginReceiveMessage(XPLMPluginID, int inMessage, void* inParam) {
    // X-Plane no llama a XPluginDisable/Enable al reiniciar la situacion
    // (Ctrl+; / "reset flight" / reposicionar en un aeropuerto): solo manda
    // uno de estos mensajes. Si habia holds con overrides puestos, ese
    // override se queda enganchado para siempre y ni el core ni el usuario
    // pueden mover el avion, porque X-Plane sigue pensando que un plugin
    // tiene el control exclusivo de ese eje.
    if (inMessage == XPLM_MSG_PLANE_CRASHED || inMessage == XPLM_MSG_AIRPORT_LOADED) {
        if (inMessage == XPLM_MSG_PLANE_CRASHED) scenario::CancelPending();
        ReleaseEverything("reinicio de situacion en X-Plane", true);
        if (inMessage == XPLM_MSG_AIRPORT_LOADED) scenario::OnAirportLoaded();
        return;
    }

    // Avion del usuario recargado (indice 0). Ademas de soltar, hay que
    // re-resolver los handles: los datarefs que publica el avion vienen de
    // su propio plugin, que se descarga con el, y los XPLMDataRef cacheados
    // dejan de valer. El core no se entera — sigue usando los mismos ids.
    if (inMessage == XPLM_MSG_PLANE_LOADED &&
        reinterpret_cast<intptr_t>(inParam) == 0) {
        ReleaseEverything("el avion del usuario se recargo", false);
        g_registry->ReresolveAll();
        SendEvent(proto::EventKind::AircraftReloaded,
                  "Avion recargado: datarefs re-resueltos y overrides soltados.");
    }
}
