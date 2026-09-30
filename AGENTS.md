# Reglas para agentes que trabajen en este proyecto

Aplican siempre, las pida el usuario o no, en cualquier sesión de
cualquier agente/herramienta que edite código bajo `src/` o
`CMakeLists.txt` en este repo. (Claude Code: ver también `CLAUDE.md`,
que solo remite aquí, y las skills `.claude/skills/xplane-sdk/` y
`.claude/skills/caja-negra/`.)

## 0. El repo son DOS proyectos, y la frontera entre ellos es dura

- **`src/connector`** (C++ → `AICopilot.xpl`) es un puente: lee/escribe
  datarefs, dispara comandos y mantiene valores puestos (holds). **No
  lleva lógica de dominio**: ni PID, ni máquinas de estado, ni rutas de
  dataref escritas a mano. Si te pide el cuerpo meter ahí algo que
  "decide" un valor, va en el core.
- **`src/core`** (C# → `AICopilotCore.exe`) lleva toda la lógica y la UI.
  Es el sitio por defecto para cualquier cosa nueva.

El contrato de cable está duplicado a mano en
`src/connector/Protocol.h` y `src/core/Connector/Protocol.cs`: si tocas
uno, toca el otro **en el mismo cambio** y sube `kVersion`/`Version` si
cambia algún layout. El connector compara la versión en el HELLO y avisa,
pero un layout cambiado a medias escribe valores sin sentido en datarefs
reales antes de que nadie se entere.

Antes de tocar el connector, lee el comentario de cabecera de
`src/connector/SafetyGuard.h`: explica por qué el plugin tiene que soltar
los overrides él solo y qué pasa si no lo hace (el avión se queda sin
poder pilotarse a mano hasta reiniciar X-Plane).

## 1. Build + deploy al terminar — elige el script que toque

En `tools/` hay **solo tres** scripts. Al terminar una tanda de cambios
de código, ejecuta el que mejor se adecue a lo que has tocado — no hace
falta que el usuario lo pida cada vez:

| Qué has tocado | Script |
|---|---|
| Solo UI / core (`src/core`, `.csproj` del core, XAML, etc.) | `tools\build_and_deploy_ui.ps1` |
| Solo addon / connector (`src/connector`, `CMakeLists.txt`, SDK) | `tools\build_and_deploy_addon.ps1` |
| Ambos lados, o el protocolo (`Protocol.h` / `Protocol.cs`), o duda | `tools\build_and_deploy.ps1` (global) |

```powershell
powershell -ExecutionPolicy Bypass -File "tools\build_and_deploy_ui.ps1"
powershell -ExecutionPolicy Bypass -File "tools\build_and_deploy_addon.ps1"
powershell -ExecutionPolicy Bypass -File "tools\build_and_deploy.ps1"
```

- **UI** (`build_and_deploy_ui.ps1`): `dotnet publish` del core, copia
  `AICopilotCore.exe` a
  `E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\`, **sube el
  contador de build** y **lanza siempre la UI** al acabar.
- **Addon** (`build_and_deploy_addon.ps1`): CMake + MSVC Release del
  connector, copia `AICopilot.xpl` sin cerrar X-Plane (rename + copy) y,
  si `devtools/ReloadTrigger` está cargado, dispara el reload por UDP.
  **No** sube el contador de build. **Lanza siempre la UI** al acabar
  (el `.exe` ya desplegado; no lo recompila).
- **Global** (`build_and_deploy.ps1`): llama a addon y luego a UI. Un solo
  bump de versión (el de la UI) y un solo lanzamiento de la UI al final.

X-Plane 12 **no tiene** la ventana clásica "Plugin Admin"; no inventes
instrucciones tipo "Plugins → Plugin Admin → Reload Plugins". Si
ReloadTrigger no está desplegado/cargado y hace falta un reload a mano,
pregunta al usuario qué menú/atajo usa en su instalación.

Si el script falla porque no compila, arregla el error de compilación y
vuelve a lanzarlo — no des el trabajo por terminado con un build roto.

**Siempre** acaba con la UI lanzada: los tres scripts lo hacen solos; no
dejes el trabajo terminado sin haber ejecutado al menos uno de ellos.

## 2. El contador de build solo sube en la UI

`tools\build_and_deploy_ui.ps1` (y el global, que lo invoca) sube
automáticamente en 1 el contador de `tools\BUILD_NUMBER.txt` y regenera
`src\core\BuildNumber.generated.cs`. El número se ve en la cabecera de
la ventana del core. Sirve para confirmar de un vistazo que se cargó el
build nuevo de la UI.

El addon **no** tiene contador de build: no regeneres ni crees
`BuildNumber.generated.h` en el connector.

- No edites `tools\BUILD_NUMBER.txt` ni `src\core\BuildNumber.generated.cs`
  a mano — son generados.
- Si compilas la UI a mano con `dotnet publish` (sin el script), el
  contador no sube: antes de dar el trabajo por terminado lanza
  `tools\build_and_deploy_ui.ps1` (o el global si también tocaste el
  addon).

## 3. Documentación del SDK de X-Plane: consúltala antes de usar la API

Aplica al **connector** (`src/connector`), que es el único lado que
toca el SDK. Antes de programar o tocar código que use funciones
`XPLM*`/`XP*`, flight loop callbacks, menús, widgets, dibujado, sonido o
cualquier símbolo de `third_party/XPSDK`, consulta en este orden:

1. Los headers reales en `third_party/XPSDK/CHeaders/XPLM/` y
   `.../Widgets/` — fuente de verdad: firmas exactas y macros de
   versión (`XPLM2xx`/`XPLM3xx`/`XPLM400`).
2. El espejo local en Markdown en
   [`docs/xplane-sdk/`](docs/xplane-sdk/README.md) (`reference/` por
   header, `guides/` para temas transversales).
3. En Claude Code, la skill `.claude/skills/xplane-sdk/SKILL.md`
   (detalle de uso y avisos de UI).

No inventes ni copies de memoria la firma o el comportamiento de una
función del SDK — compruébala en esas fuentes primero.

## 4. Caja negra de vuelo: inspeccionar telemetría

Si el usuario pide inspeccionar la caja negra, el DataLog, un temblor,
overshoot de G, o qué pasó en una maniobra/intercept/despegue, lee y
sigue la skill
[`.claude/skills/caja-negra/SKILL.md`](.claude/skills/caja-negra/SKILL.md)
antes de diagnosticar. El CSV está junto al exe desplegado
(`…\plugins\AICopilot\win_x64\DataLog.csv`), no en el repo.
