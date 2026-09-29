---
name: aicopilot-driver
description: >-
  Controla la UI de AICopilot desde un LLM vía HTTP REST localhost
  (:17890) y el CLI tools/aicopilot_driver.py: health, status,
  intercept, intercept-abort, abort. Usar cuando haya que iniciar o
  parar interceptación sin clic manual. Complementa la skill caja-negra
  (CSV) y AGENTS.md (build/deploy).
---

# Driver AICopilot — LLM ↔ UI

El core expone **HTTP REST JSON** en `http://127.0.0.1:17890`
(solo loopback). Misma fachada que la UI (`FlightDirector`).

CLI: `tools/aicopilot_driver.py` (stdlib; wrapper opcional
`tools/aicopilot_driver.ps1`).

Detalle de endpoints → [reference.md](reference.md).

## Prerrequisitos

1. X-Plane 12 con el plugin `AICopilot.xpl` cargado.
2. `AICopilotCore.exe` en marcha (el HTTP arranca con la UI;
   `--no-driver` lo apaga, `--driver-port=N` cambia puerto).
3. Desde la raíz del repo:

```powershell
python tools/aicopilot_driver.py health
# → {"ok":true}
```

## Endpoints (todos)

| Método | Ruta | Efecto |
|---|---|---|
| GET | `/health` | `{ "ok": true }` |
| GET | `/status` | connected, mode, intercept |
| POST | `/intercept` | Inicia interceptación |
| POST | `/intercept/abort` | Para solo intercept |
| POST | `/abort` | `AbortAll()` |

## CLI

```powershell
python tools/aicopilot_driver.py health
python tools/aicopilot_driver.py status
python tools/aicopilot_driver.py intercept --index 1 --station TailHigh
python tools/aicopilot_driver.py intercept-abort
python tools/aicopilot_driver.py abort
```

Equivale el wrapper: `tools\aicopilot_driver.ps1 <mismos args>`.

## Uso típico

Tras build/deploy (`tools\build_and_deploy_ui.ps1`), con UI arriba y
plugin conectado:

```powershell
python tools/aicopilot_driver.py status
python tools/aicopilot_driver.py intercept --index 1 --station TailHigh
# … observar vuelo / DataLog …
python tools/aicopilot_driver.py intercept-abort
```

Si `/intercept` responde 409 (no connected / en tierra / etc.), el HTTP
está bien; el dominio rechazó la acción.

Observar series → skill [caja-negra](../caja-negra/SKILL.md).

## Código

| Pieza | Ruta |
|---|---|
| HTTP REST | `src/core/Driver/ControlApi.cs` |
| CLI | `tools/aicopilot_driver.py` |
| Fachada | `src/core/Domain/FlightDirector.cs` |
| Arranque | `src/core/Ui/App.xaml.cs` |
