# ReloadTrigger

Plugin auxiliar **independiente de AICopilot**. No toca el vuelo: solo
escucha un datagrama UDP en localhost y, al recibirlo, llama a
`XPLMReloadPlugins()`.

Existe para que [`tools/build_and_deploy_addon.ps1`](../../tools/build_and_deploy_addon.ps1)
(y el global `build_and_deploy.ps1`) del repo principal puedan disparar un
reload de plugins por UDP tras desplegar un `.xpl` nuevo, sin que el
usuario tenga que buscar un menú a mano. X-Plane 12 no tiene la ventana
clásica "Plugin Admin".

## Puerto y carga útil

| | |
|--|--|
| Host | `127.0.0.1` |
| Puerto | `34570` (distinto de la telemetría de AICopilot, 34567/34568) |
| Mensaje | ASCII `RELOAD` |

## Instalación (una vez)

Desde la raíz del repo (o desde esta carpeta):

```powershell
.\devtools\ReloadTrigger\tools\build_and_deploy.ps1
```

Compila con CMake + MSVC y copia a:

`E:\X-Plane 12\Resources\plugins\ReloadTrigger\win_x64\ReloadTrigger.xpl`

La **primera** vez hace falta un reload de plugins a mano dentro de
X-Plane para que quede cargado. A partir de ahí,
`AICopilot\tools\build_and_deploy_addon.ps1` (o el global) ya dispara el
reload por UDP (y confirma el diálogo "Understood" con Enter).

Normalmente no hace falta volver a tocar este plugin: es infraestructura
del pipeline, no del ciclo diario de AICopilot.

## Relación con el flujo de AICopilot

1. Editas código bajo `src/` / `CMakeLists.txt`.
2. Lanzas `tools\build_and_deploy_addon.ps1` (o el global) en el repo principal.
3. Ese script despliega `AICopilot.xpl` y envía `RELOAD` a este puerto.
4. ReloadTrigger llama a `XPLMReloadPlugins()`; el script
   intenta confirmar el diálogo de X-Plane.

Reglas de agentes del repo: [`../../AGENTS.md`](../../AGENTS.md).
