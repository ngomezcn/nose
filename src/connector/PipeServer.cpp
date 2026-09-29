#include "PipeServer.h"

#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "Logger.h"

bool PipeServer::Start(const std::string& pipeName) {
    if (running_.load()) return true;

    pipeName_ = pipeName;

    // Manual-reset: una vez pedimos parar, TODAS las esperas del hilo
    // tienen que ver la señal, no solo la primera.
    stopEvent_ = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    // Auto-reset: cada Send() despierta una vuelta del bucle, que vacia la
    // cola entera.
    outboundEvent_ = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    if (!stopEvent_ || !outboundEvent_) {
        LogInfo("PipeServer: no se pudieron crear los eventos (err=%lu).", GetLastError());
        Stop();
        return false;
    }

    running_.store(true);
    thread_ = std::thread(&PipeServer::ThreadMain, this);
    LogInfo("PipeServer escuchando en %s", pipeName_.c_str());
    return true;
}

void PipeServer::Stop() {
    if (stopEvent_) SetEvent(stopEvent_);
    if (thread_.joinable()) thread_.join();
    running_.store(false);
    connected_.store(false);

    if (stopEvent_) { CloseHandle(stopEvent_); stopEvent_ = nullptr; }
    if (outboundEvent_) { CloseHandle(outboundEvent_); outboundEvent_ = nullptr; }

    std::lock_guard<std::mutex> lock(mutex_);
    inbound_.clear();
    outbound_.clear();
    pendingLogs_.clear();
}

void PipeServer::Send(std::vector<uint8_t> message, bool droppable) {
    if (!connected_.load(std::memory_order_relaxed)) return;

    {
        std::lock_guard<std::mutex> lock(mutex_);

        if (outbound_.size() >= kMaxOutbound) {
            // Cola llena: el core no lee. Sacrificamos el mensaje
            // descartable MAS VIEJO -- si es telemetria, el hueco lo
            // ocupara el frame nuevo, que es el que de verdad interesa.
            auto it = outbound_.begin();
            while (it != outbound_.end() && !it->droppable) ++it;
            if (it != outbound_.end()) {
                outbound_.erase(it);
            } else if (droppable) {
                return;  // nada que sacrificar y lo nuevo si se puede tirar
            } else {
                // Todo lo encolado es critico y lo nuevo tambien. Tirar el
                // mas viejo es lo menos malo: la sesion ya esta rota de
                // todas formas y el SafetyGuard acabara soltando.
                outbound_.pop_front();
            }
        }

        outbound_.push_back(Outgoing{std::move(message), droppable});
    }

    SetEvent(outboundEvent_);
}

void PipeServer::QueueLog(const char* fmt, ...) {
    char msg[300];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(msg, sizeof(msg), fmt, args);
    va_end(args);

    std::lock_guard<std::mutex> lock(mutex_);
    // Tope por si algo se pone a loguear en bucle con nadie llamando a
    // Poll() (X-Plane cargando una escena, por ejemplo).
    if (pendingLogs_.size() < 200) pendingLogs_.emplace_back(msg);
}

void PipeServer::Poll() {
    std::vector<std::vector<uint8_t>> batch;
    std::vector<std::string> logs;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        batch.swap(inbound_);
        logs.swap(pendingLogs_);
    }
    // Ya estamos en el hilo principal: aqui si se puede tocar XPLMDebugString.
    for (const auto& line : logs) LogInfo("%s", line.c_str());

    if (!handler_) return;
    for (const auto& msg : batch) {
        handler_(msg.data(), msg.size());
    }
}

void PipeServer::DropConnection() {
    connected_.store(false, std::memory_order_relaxed);
    std::lock_guard<std::mutex> lock(mutex_);
    outbound_.clear();
    rx_.clear();
}

void PipeServer::ThreadMain() {
    while (WaitForSingleObject(stopEvent_, 0) != WAIT_OBJECT_0) {
        HANDLE pipe = CreateNamedPipeA(
            pipeName_.c_str(),
            PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1,            // una sola instancia: un core a la vez, a proposito
            64 * 1024,    // buffer de salida
            64 * 1024,    // buffer de entrada
            0,            // timeout por defecto (no lo usamos, todo es overlapped)
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE) {
            QueueLog("PipeServer: CreateNamedPipe fallo (err=%lu); reintento en 1s.",
                     GetLastError());
            if (WaitForSingleObject(stopEvent_, 1000) == WAIT_OBJECT_0) break;
            continue;
        }

        // --- esperar a que el core se conecte ------------------------------
        OVERLAPPED ov{};
        ov.hEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
        bool connected = false;

        if (ConnectNamedPipe(pipe, &ov)) {
            connected = true;
        } else {
            DWORD err = GetLastError();
            if (err == ERROR_PIPE_CONNECTED) {
                // El cliente se colo entre el CreateNamedPipe y el
                // ConnectNamedPipe. No es un error: ya esta conectado.
                connected = true;
            } else if (err == ERROR_IO_PENDING) {
                HANDLE waits[2] = {ov.hEvent, stopEvent_};
                DWORD w = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
                if (w == WAIT_OBJECT_0) {
                    DWORD dummy = 0;
                    connected = GetOverlappedResult(pipe, &ov, &dummy, FALSE) != 0;
                }
            }
        }
        CloseHandle(ov.hEvent);

        if (WaitForSingleObject(stopEvent_, 0) == WAIT_OBJECT_0) {
            CloseHandle(pipe);
            break;
        }

        if (!connected) {
            CloseHandle(pipe);
            continue;
        }

        rx_.clear();
        session_.fetch_add(1, std::memory_order_relaxed);
        lastInbound_.store(GetTickCount64(), std::memory_order_relaxed);
        connected_.store(true, std::memory_order_relaxed);
        QueueLog("Core conectado (sesion %u).", session_.load());

        SessionLoop(pipe);

        DropConnection();
        QueueLog("Core desconectado.");

        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }

    connected_.store(false, std::memory_order_relaxed);
}

void PipeServer::SessionLoop(HANDLE pipe) {
    OVERLAPPED readOv{};
    readOv.hEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    if (!readOv.hEvent) return;

    uint8_t buffer[16 * 1024];
    bool readPending = false;

    for (;;) {
        // --- asegurar que siempre hay una lectura en vuelo ----------------
        if (!readPending) {
            ResetEvent(readOv.hEvent);
            DWORD read = 0;
            if (ReadFile(pipe, buffer, sizeof(buffer), &read, &readOv)) {
                // Completo al instante (habia datos en el buffer del pipe).
                if (read == 0) break;  // EOF: el core cerro
                if (!ConsumeBytes(buffer, read)) break;
                continue;
            }
            DWORD err = GetLastError();
            if (err != ERROR_IO_PENDING) break;  // pipe roto
            readPending = true;
        }

        HANDLE waits[3] = {readOv.hEvent, outboundEvent_, stopEvent_};
        DWORD w = WaitForMultipleObjects(3, waits, FALSE, INFINITE);

        if (w == WAIT_OBJECT_0 + 2) break;  // stop

        if (w == WAIT_OBJECT_0) {
            DWORD read = 0;
            if (!GetOverlappedResult(pipe, &readOv, &read, FALSE)) break;
            readPending = false;
            if (read == 0) break;  // EOF
            if (!ConsumeBytes(buffer, read)) break;
            continue;
        }

        if (w == WAIT_OBJECT_0 + 1) {
            if (!WriteAllPending(pipe)) break;
            continue;
        }

        break;  // WAIT_FAILED / abandonado
    }

    // Hay que cancelar la lectura en vuelo ANTES de que readOv se destruya
    // al salir: si el kernel sigue con una referencia a un OVERLAPPED que
    // vive en esta pila, escribira en memoria que ya no es nuestra.
    if (readPending) {
        CancelIo(pipe);
        DWORD dummy = 0;
        GetOverlappedResult(pipe, &readOv, &dummy, TRUE);  // esperar de verdad
    }
    CloseHandle(readOv.hEvent);
}

bool PipeServer::WriteAllPending(HANDLE pipe) {
    OVERLAPPED writeOv{};
    writeOv.hEvent = CreateEventA(nullptr, TRUE, FALSE, nullptr);
    if (!writeOv.hEvent) return false;

    bool ok = true;

    for (;;) {
        std::vector<uint8_t> msg;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            if (outbound_.empty()) break;
            msg = std::move(outbound_.front().bytes);
            outbound_.pop_front();
        }

        size_t sent = 0;
        while (sent < msg.size()) {
            ResetEvent(writeOv.hEvent);
            DWORD written = 0;
            BOOL done = WriteFile(pipe, msg.data() + sent,
                                  static_cast<DWORD>(msg.size() - sent),
                                  &written, &writeOv);
            if (!done) {
                if (GetLastError() != ERROR_IO_PENDING) { ok = false; break; }
                // Bloquea ESTE hilo, nunca el flight loop -- esa es toda la
                // razon de ser del hilo.
                HANDLE waits[2] = {writeOv.hEvent, stopEvent_};
                DWORD w = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
                if (w != WAIT_OBJECT_0) {
                    CancelIo(pipe);
                    DWORD dummy = 0;
                    GetOverlappedResult(pipe, &writeOv, &dummy, TRUE);
                    ok = false;
                    break;
                }
                if (!GetOverlappedResult(pipe, &writeOv, &written, FALSE)) {
                    ok = false;
                    break;
                }
            }
            if (written == 0) { ok = false; break; }
            sent += written;
        }

        if (!ok) break;
    }

    CloseHandle(writeOv.hEvent);
    return ok;
}

bool PipeServer::ConsumeBytes(const uint8_t* data, size_t size) {
    lastInbound_.store(GetTickCount64(), std::memory_order_relaxed);
    rx_.insert(rx_.end(), data, data + size);

    size_t offset = 0;
    std::vector<std::vector<uint8_t>> ready;

    for (;;) {
        if (rx_.size() - offset < 4) break;

        uint32_t len = 0;
        std::memcpy(&len, rx_.data() + offset, 4);

        if (len == 0 || len > kMaxMessageBytes) {
            // Framing desincronizado: a partir de aqui no sabemos donde
            // empieza nada. Vaciar el buffer y cortar la conexion es lo
            // unico honesto; el core reconecta y hace HELLO de nuevo.
            QueueLog("PipeServer: longitud de mensaje invalida (%u); cierro la sesion.", len);
            rx_.clear();
            return false;
        }

        if (rx_.size() - offset < 4 + len) break;  // falta cuerpo, ya llegara

        const uint8_t* body = rx_.data() + offset + 4;
        ready.emplace_back(body, body + len);
        offset += 4 + len;
    }

    if (offset > 0) rx_.erase(rx_.begin(), rx_.begin() + offset);

    if (!ready.empty()) {
        std::lock_guard<std::mutex> lock(mutex_);
        for (auto& m : ready) inbound_.push_back(std::move(m));
    }
    return true;
}
