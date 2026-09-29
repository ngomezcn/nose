// PipeServer.h
//
// Servidor del named pipe \\.\pipe\AICopilot.v1. Su unico trabajo es mover
// bytes entre el core y el flight loop sin bloquear NUNCA el flight loop:
// un frame que se queda esperando a un WriteFile es un tiron visible en el
// simulador, y el core es un proceso que puede estar paginado, en un
// breakpoint o muerto.
//
// Como se consigue: toda la E/S de verdad vive en un hilo propio, y el
// flight loop solo toca dos colas protegidas por un mutex (un lock/unlock
// por frame, decenas de nanosegundos). Poll() vacia la de entrada y Send()
// empuja en la de salida.
//
// Un detalle de diseño que parece menor y no lo es: ese hilo es UNO SOLO y
// es el unico dueño del HANDLE del pipe, aunque eso obligue a mezclar
// lectura y escritura en el mismo bucle con WaitForMultipleObjects. Con un
// hilo lector y otro escritor haria falta coordinar quien cierra el handle
// cuando el core se desconecta, y el escritor puede estar dentro de un
// WriteFile justo en ese momento -- un use-after-close dentro del proceso
// de X-Plane. Con un solo dueño esa carrera no existe.
//
// IMPORTANTE: este hilo no llama a NADA del SDK de X-Plane. Las funciones
// XPLM* no son thread-safe (lo dicen los propios headers) y solo se pueden
// llamar desde el hilo principal, dentro de callbacks. Todo lo que llega
// por el pipe se encola como bytes y se interpreta en Poll(), que corre en
// el flight loop.
//
// Eso incluye el log: XPLMDebugString esta marcado "NOT thread-safe" en
// XPLMUtilities.h, asi que el hilo de E/S no puede llamar a LogInfo. Sus
// mensajes van a una cola de texto que Poll() vacia en el hilo principal.
// Es la razon de que aqui haya una tercera cola que a primera vista
// parece de mas.
#pragma once

#include <atomic>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

class PipeServer {
public:
    // Se llama desde Poll(), o sea desde el hilo principal: dentro se
    // puede tocar el SDK con tranquilidad.
    using Handler = std::function<void(const uint8_t* data, size_t size)>;

    ~PipeServer() { Stop(); }

    bool Start(const std::string& pipeName);
    void Stop();

    void SetHandler(Handler h) { handler_ = std::move(h); }

    // Vacia la cola de entrada llamando al handler una vez por mensaje.
    // Hilo principal, una vez por frame.
    void Poll();

    // Encola un mensaje ya serializado (con su prefijo de longitud, tal y
    // como lo deja proto::Writer::Take()).
    //
    // droppable = true para telemetria: si el core no da abasto leyendo,
    // preferimos tirar el frame viejo y mandar el nuevo -- un dato de
    // telemetria atrasado no le sirve a nadie. false para HELLO_ACK,
    // DEFINE_ACK, EVENT y PONG, que son unicos y perderlos rompe la
    // sesion.
    void Send(std::vector<uint8_t> message, bool droppable);

    bool IsConnected() const { return connected_.load(std::memory_order_relaxed); }

    // Sube en 1 cada vez que se conecta un core. El flight loop lo usa
    // para detectar "sesion nueva" y tirar el estado de la anterior
    // (holds, suscripciones, tabla de datarefs) sin tener que enterarse
    // por otro lado.
    uint32_t SessionId() const { return session_.load(std::memory_order_relaxed); }

    // Milisegundos (GetTickCount64) del ultimo mensaje recibido. El
    // SafetyGuard lo compara contra kHeartbeatTimeoutMs.
    uint64_t LastInboundTickMs() const {
        return lastInbound_.load(std::memory_order_relaxed);
    }

private:
    struct Outgoing {
        std::vector<uint8_t> bytes;
        bool droppable;
    };

    // Tope de mensajes encolados hacia el core. Con telemetria a 60 Hz,
    // 256 son ~4 segundos de retraso: si llegamos ahi, el core no esta
    // leyendo y lo que toca es tirar cosas, no acumular memoria.
    static constexpr size_t kMaxOutbound = 256;

    // Un mensaje mas grande que esto solo puede ser desincronizacion del
    // framing o basura. Se corta la conexion en vez de intentar reservar
    // el tamaño que diga el prefijo.
    static constexpr uint32_t kMaxMessageBytes = 1u << 20;  // 1 MB

    void ThreadMain();
    // Devuelve cuando se pierde la conexion o cuando se pide parar.
    void SessionLoop(HANDLE pipe);
    // Log desde el hilo de E/S: encola el texto para que lo escriba Poll().
    void QueueLog(const char* fmt, ...);
    bool WriteAllPending(HANDLE pipe);
    // false = el framing se desincronizo y hay que cerrar la sesion.
    bool ConsumeBytes(const uint8_t* data, size_t size);
    void DropConnection();

    std::string pipeName_;
    std::thread thread_;

    HANDLE stopEvent_ = nullptr;      // manual-reset: "hay que salir"
    HANDLE outboundEvent_ = nullptr;  // auto-reset: "hay algo que mandar"

    std::atomic<bool> running_{false};
    std::atomic<bool> connected_{false};
    std::atomic<uint32_t> session_{0};
    std::atomic<uint64_t> lastInbound_{0};

    std::mutex mutex_;
    std::vector<std::vector<uint8_t>> inbound_;  // mensajes completos
    std::deque<Outgoing> outbound_;
    std::vector<std::string> pendingLogs_;

    // Buffer de reensamblado: el pipe va en modo BYTE, asi que un ReadFile
    // puede traer medio mensaje o tres y medio. Solo lo toca el hilo de
    // E/S.
    std::vector<uint8_t> rx_;

    Handler handler_;
};
