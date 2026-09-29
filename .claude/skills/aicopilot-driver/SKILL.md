---
name: aicopilot-driver
description: >-
  Acciona AICopilot desde un LLM o script vía API REST local
  (http://127.0.0.1:17890) o el CLI tools/aicopilot_driver.py: status,
  despegar (takeoff), interceptar (intercept), abortar (abort). Usar
  cuando haya que pulsar esos botones sin clic manual. Complementa la
  skill caja-negra (CSV) y AGENTS.md (build/deploy).
---

# Driver AICopilot — API REST

`AICopilotCore.exe` levanta al arrancar una API REST (ASP.NET Core
minimal API) en `http://127.0.0.1:17890`, solo loopback. Cada endpoint
hace lo mismo que el botón de la UI (pasa por `FlightDirector`, en el
hilo de la UI). Parámetros por query string, respuestas JSON.

| Método | Ruta | Botón / efecto |
|---|---|---|
| GET | `/status` | conectado, estado de despegue/maniobra/intercept, últimas 10 líneas de log |
| POST | `/takeoff?style=Relaxed` | Despegar. `style`: `Relaxed` (defecto), `Combat`, `Emergency` |
| POST | `/intercept?index=1&station=TailHigh` | Interceptar el avión IA `index` (defecto 1). `station`: `TailHigh` (defecto), `ParallelRight`, `ParallelLeft`, `Above`, `Below` |
| POST | `/abort` | Abortar todo |

Códigos: `200` hecho · `400` parámetro inválido (el error dice los
valores válidos) · `409` el dominio lo rechaza ahora mismo (sin
conexión con el plugin, en tierra, < 120 kt…; el `error` lo explica).
Los valores de enum no distinguen mayúsculas.

## CLI (Python, sin dependencias)

```powershell
python tools/aicopilot_driver.py status
python tools/aicopilot_driver.py takeoff Combat
python tools/aicopilot_driver.py intercept 1 TailHigh
python tools/aicopilot_driver.py abort
```

Sale con código 1 si la API responde error o no está levantada.

## curl

```powershell
curl http://127.0.0.1:17890/status
curl -X POST "http://127.0.0.1:17890/takeoff?style=Combat"
curl -X POST "http://127.0.0.1:17890/intercept?index=1&station=Above"
curl -X POST http://127.0.0.1:17890/abort
```

## Uso típico

1. X-Plane con `AICopilot.xpl` y la UI abierta (`status` → `"connected": true`).
2. `takeoff`, y consultar `status` cada poco hasta que el despegue acabe
   (`takeoff.running: false`).
3. `intercept 1` (necesita estar en vuelo, ≥ 120 kt).
4. Observar series → skill [caja-negra](../caja-negra/SKILL.md).

## Código

| Pieza | Ruta |
|---|---|
| API REST | `src/core/Driver/ControlApi.cs` |
| CLI | `tools/aicopilot_driver.py` |
| Fachada | `src/core/Domain/FlightDirector.cs` |
| Arranque | `src/core/Ui/App.xaml.cs` |

Para añadir un botón más: un `MapPost` en `ControlApi.cs` que llame al
método de `FlightDirector` correspondiente, y una línea en el CLI.
