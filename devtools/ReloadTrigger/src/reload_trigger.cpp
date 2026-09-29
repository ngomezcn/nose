// reload_trigger.cpp
//
// Plugin auxiliar, independiente de AICopilot: no hace nada relacionado
// con el vuelo. Su unico trabajo es escuchar un datagrama UDP en
// localhost (puerto kReloadPort) y, al recibirlo, llamar a
// XPLMReloadPlugins(). Existe para que tools\build_and_deploy.ps1 (en el
// repo de AICopilot) pueda disparar un reload de plugins desde fuera,
// sin que el usuario tenga que ir al menu a mano cada vez que se
// compila un build nuevo. Ver README.md en esta carpeta.
//
// Se instala una sola vez (ver tools\build_and_deploy.ps1 de esta misma
// carpeta) y normalmente no hace falta volver a tocarlo: no forma parte
// del ciclo de desarrollo de AICopilot, es infraestructura del propio
// pipeline.
//
// Por que UDP en localhost y no otra cosa: es el mismo patron que ya usa
// AICopilot para hablar con su core (ver src/connector/PipeServer.h en el
// repo principal) -- simple, sin dependencias extra, y sobrevive bien a
// que el proceso al otro lado (aqui, el script de PowerShell) ni siquiera
// este vivo cuando no hay un build en marcha.

#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <string>

#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>

#include "XPLMDefs.h"
#include "XPLMPlugin.h"
#include "XPLMProcessing.h"
#include "XPLMUtilities.h"

namespace {

// Puerto en el que se escucha el "RELOAD". Distinto de los que ya usa
// AICopilot, que ya no usa UDP sino un named pipe (ver
// src/connector/PipeServer.h), asi que no hay forma de pisarse.
constexpr unsigned short kReloadPort = 34570;
constexpr const char* kReloadCommand = "RELOAD";

bool g_wsaStarted = false;
SOCKET g_recvSocket = INVALID_SOCKET;
FILE* g_logFile = nullptr;

std::string ComputePluginDir() {
    char path[256] = {0};
    XPLMGetPluginInfo(XPLMGetMyID(), nullptr, path, nullptr, nullptr);
    std::string p(path);
    size_t sep = p.find_last_of("/\\");
    if (sep != std::string::npos) return p.substr(0, sep + 1);
    return {};
}

void LogLine(const char* fmt, ...) {
    char msg[256];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(msg, sizeof(msg), fmt, args);
    va_end(args);

    char full[300];
    std::snprintf(full, sizeof(full), "ReloadTrigger: %s\n", msg);
    XPLMDebugString(full);

    if (g_logFile) {
        std::time_t now = std::time(nullptr);
        std::tm tmBuf;
        localtime_s(&tmBuf, &now);
        char timeBuf[16];
        std::strftime(timeBuf, sizeof(timeBuf), "%H:%M:%S", &tmBuf);
        std::fprintf(g_logFile, "[%s] %s\n", timeBuf, msg);
        std::fflush(g_logFile);
    }
}

// Callback de flight loop: no bloqueante, drena todos los datagramas que
// hayan llegado desde el ultimo frame. Si alguno es "RELOAD" exacto,
// dispara XPLMReloadPlugins() -- que descarga y vuelve a cargar todos los
// plugins, incluido este mismo, asi que no hace falta ningun estado
// despues de la llamada.
float FlightLoopCallback(float, float, int, void*) {
    if (g_recvSocket == INVALID_SOCKET) return -1.0f;

    char buf[64];
    for (;;) {
        int n = recvfrom(g_recvSocket, buf, sizeof(buf) - 1, 0, nullptr, nullptr);
        if (n <= 0) break;
        buf[n] = '\0';
        if (std::strncmp(buf, kReloadCommand, std::strlen(kReloadCommand)) == 0) {
            LogLine("RELOAD recibido, llamando a XPLMReloadPlugins().");
            XPLMReloadPlugins();
            return -1.0f;  // el plugin se va a descargar ya mismo
        }
    }
    return -1.0f;
}

}  // namespace

PLUGIN_API int XPluginStart(char* outName, char* outSig, char* outDesc) {
    std::strcpy(outName, "ReloadTrigger");
    std::strcpy(outSig, "com.naim.reloadtrigger");
    std::strcpy(outDesc,
                "Herramienta de desarrollo: escucha un UDP en localhost y "
                "llama a XPLMReloadPlugins() para automatizar el pipeline "
                "de build+deploy de otros plugins.");

    std::string logPath = ComputePluginDir() + "ReloadTrigger_log.txt";
    g_logFile = std::fopen(logPath.c_str(), "a");

    WSADATA wsaData;
    if (WSAStartup(MAKEWORD(2, 2), &wsaData) == 0) {
        g_wsaStarted = true;
        g_recvSocket = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (g_recvSocket != INVALID_SOCKET) {
            sockaddr_in addr{};
            addr.sin_family = AF_INET;
            addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
            addr.sin_port = htons(kReloadPort);
            bind(g_recvSocket, reinterpret_cast<sockaddr*>(&addr), sizeof(addr));

            u_long nonBlocking = 1;
            ioctlsocket(g_recvSocket, FIONBIO, &nonBlocking);
        }
    }

    LogLine("cargado, escuchando UDP en 127.0.0.1:%d", kReloadPort);

    XPLMRegisterFlightLoopCallback(FlightLoopCallback, -1.0f, nullptr);
    return 1;
}

PLUGIN_API void XPluginStop() {
    XPLMUnregisterFlightLoopCallback(FlightLoopCallback, nullptr);

    if (g_recvSocket != INVALID_SOCKET) {
        closesocket(g_recvSocket);
        g_recvSocket = INVALID_SOCKET;
    }
    if (g_wsaStarted) {
        WSACleanup();
        g_wsaStarted = false;
    }

    LogLine("descargado.");
    if (g_logFile) {
        std::fclose(g_logFile);
        g_logFile = nullptr;
    }
}

PLUGIN_API int XPluginEnable() { return 1; }
PLUGIN_API void XPluginDisable() {}
PLUGIN_API void XPluginReceiveMessage(XPLMPluginID, int, void*) {}
