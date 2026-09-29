// Logger.h
//
// Sistema de log del plugin: cada línea va a la vez a Log.txt de X-Plane
// (vía XPLMDebugString, lo que ya sabes mirar) y a un fichero propio,
// `AICopilot_log.txt`, guardado junto al `.xpl`. Tenerlo en un fichero
// aparte hace mucho más fácil encontrar "qué ha pasado" después de una
// sesión larga, sin tener que buscarlo entre miles de líneas de otros
// plugins y del propio X-Plane en Log.txt.
//
// Uso: LogInfo("mensaje %d", valor); LogWarn("algo raro: %s", texto);
// No hace falta inicializar nada a mano salvo Logger::Instance().Open(...)
// una vez al arrancar el plugin (ver plugin.cpp) y Close() al pararlo.
#pragma once

#include <cstdarg>
#include <cstdio>
#include <ctime>
#include <string>

#include "XPLMUtilities.h"

class Logger {
public:
    static Logger& Instance() {
        static Logger instance;
        return instance;
    }

    void Open(const std::string& path) {
        file_ = std::fopen(path.c_str(), "a");
        if (file_) {
            std::fprintf(file_, "\n===== AICopilot: log iniciado =====\n");
            std::fflush(file_);
        } else {
            // Si no se pudo abrir el fichero propio, seguimos escribiendo
            // solo en Log.txt (mejor eso que perder el log entero).
            XPLMDebugString("AICopilot: no se pudo abrir AICopilot_log.txt, "
                             "se usara solo Log.txt\n");
        }
    }

    void Close() {
        if (file_) {
            std::fprintf(file_, "===== AICopilot: log cerrado =====\n");
            std::fclose(file_);
            file_ = nullptr;
        }
    }

    void Write(const char* level, const char* fmt, va_list args) {
        char msg[400];
        std::vsnprintf(msg, sizeof(msg), fmt, args);

        std::time_t now = std::time(nullptr);
        std::tm tmBuf;
#if defined(_WIN32)
        localtime_s(&tmBuf, &now);
#else
        localtime_r(&now, &tmBuf);
#endif
        char timeBuf[16];
        std::strftime(timeBuf, sizeof(timeBuf), "%H:%M:%S", &tmBuf);

        char line[480];
        std::snprintf(line, sizeof(line), "[%s][AICopilot][%s] %s\n",
                       timeBuf, level, msg);

        XPLMDebugString(line);
        if (file_) {
            std::fputs(line, file_);
            std::fflush(file_);
        }
    }

private:
    std::FILE* file_ = nullptr;
};

inline void LogInfo(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    Logger::Instance().Write("INFO", fmt, args);
    va_end(args);
}

inline void LogWarn(const char* fmt, ...) {
    va_list args;
    va_start(args, fmt);
    Logger::Instance().Write("AVISO", fmt, args);
    va_end(args);
}
