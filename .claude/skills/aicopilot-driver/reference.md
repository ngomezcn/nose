# Driver — referencia HTTP REST

Base URL: `http://127.0.0.1:17890`  
Respuestas: JSON camelCase. Errores: `{ "ok": false, "error": "…" }`
con 400 / 404 / 409 / 500.

CLI: `python tools/aicopilot_driver.py <comando>` (timeout 5s).

## Endpoints

### `GET /health`

```json
{ "ok": true }
```

### `GET /status`

```json
{
  "ok": true,
  "connected": true,
  "mode": "idle",
  "intercept": {
    "running": false,
    "phase": "Idle",
    "targetIndex": 0,
    "station": "TailHigh"
  }
}
```

`mode`: `idle` | `takeoff` | `takeoff-done` | `maneuver` | `intercept`

### `POST /intercept`

Body:

```json
{ "index": 1, "station": "TailHigh", "label": "opcional" }
```

- `index`: slot multiplayer XPLM (1..19)
- `station`: `TailHigh` | `ParallelLeft` | `ParallelRight` | `Above` | `Below`
- `label`: opcional (default `IA {index}`)

Éxito: `{ "ok": true, "started": true, "index", "station", "label" }`  
Fallo de dominio: **409** con `error`.

### `POST /intercept/abort`

Aborta solo la interceptación. Si no corría:
`{ "ok": true, "aborted": false, "reason": "…" }`.

### `POST /abort`

`FlightDirector.AbortAll()` → `{ "ok": true, "aborted": true }`.

## CLI ↔ HTTP

| CLI | HTTP |
|---|---|
| `health` | `GET /health` |
| `status` | `GET /status` |
| `intercept --index N --station S` | `POST /intercept` |
| `intercept-abort` | `POST /intercept/abort` |
| `abort` | `POST /abort` |

## Curl crudo

```powershell
curl.exe -sS http://127.0.0.1:17890/health
curl.exe -sS http://127.0.0.1:17890/status
curl.exe -sS -X POST -H "Content-Type: application/json" -d "{\"index\":1,\"station\":\"TailHigh\"}" http://127.0.0.1:17890/intercept
curl.exe -sS -X POST -H "Content-Type: application/json" -d "{}" http://127.0.0.1:17890/intercept/abort
curl.exe -sS -X POST -H "Content-Type: application/json" -d "{}" http://127.0.0.1:17890/abort
```

## Caja negra

`E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv` →
skill [caja-negra](../caja-negra/SKILL.md).
