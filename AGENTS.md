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

## 4. Avión en foco, cámara y caja negra por avión

### Foco (quién recibe las órdenes)

En la UI (pestaña **Aviones**) el usuario elige una fila y pulsa
**Poner en foco**. La franja **EN FOCO** del sidebar muestra siempre a
quién van despegue / crucero / acciones (o “GLOBAL” si no hay nave):

- Índice **-1** = **Global** (por defecto): opciones de zona (gráficos,
  inicios de simulación, vista aérea) y **caja negra de todo el roster**
  (Empezar graba todos los aviones a la vez; cada uno en su CSV). Sin
  órdenes de vuelo (despegue / maniobras / intercept).
- Índice XPLM **0** = ownship (F-14 / avión del usuario).
- Índices **1..19** = IAs (`sim/multiplayer/position/planeN_*`).

`FlightDirector.SetFocus` / `FocusedXplmIndex` / `IsGlobalFocus` viven en
el core. Al poner foco Global el core pide al connector la **vista aérea**
(`Op.CameraFollow` con `planeIndex = 255`) y activa rombos + líneas en
gráficos.

### Un agente por avión (plug&play)

`src/core/Domain/Agents/`. Cada avión (XPLM 0..19) es un `AircraftAgent`
creado bajo demanda por `AircraftWorld.Get(idx)`, con **sus propias**
secuencias (despegue, maniobras, `CruisePilot`, intercept), su exclusión
mutua y su estado. Todos admiten **exactamente las mismas órdenes**
(despegue, ruta, maniobras, crucero, intercept): no hay ramas por tipo de
avión ni opciones "solo ownship". Lo que un cuerpo no tiene lo declara
`BodyCaps` y la maniobra se adapta (no se corta).

- **Foco = a quién van las órdenes de la UI**, nada más. `FlightDirector`
  solo resuelve el destinatario y delega en el agente en foco
  (`Focused` / `FocusedView`). Cambiar el foco **no aborta ni toca** a
  ningún otro avión: los demás siguen con su ruta / maniobra / intercept.
  Los paneles (telemetría, listado, Interceptar, botones de acciones)
  reflejan el `AgentView` del avión enfocado. Con foco Global, Abortar
  actúa sobre todos.
- **Cuerpo**: `LocalAircraftBody` (índice 0: `Datarefs` +
  `AircraftControls`) y `KinematicAiBody` (1..19). Un avión IA sin orden
  está *Observing* (no se toca; `State` reducido con NaN en G/AoA/mandos,
  la UI pinta "—"); al recibir una orden pasa a *Simulating*.
- **`KinematicAiBody`**: modelo cinemático F-14 (`F14Profile`) para todas
  las IAs; la pose se reescribe por Holds + `Op.AiControl`. El terreno se
  asume **plano** al nivel del punto de captura (AGL = altura sobre ese
  plano). X-Plane **no reactiva la IA nativa** de un avión suelto: al
  soltar se deja en la pose actual. El crucero de una IA es `CruisePilot`
  sobre su `KinematicAiBody` (ya no existe `AiStraightHold`).
- **Intercept**: el interceptor es el avión en foco; el blanco se elige en
  la lista (cualquier avión distinto del interceptor, incluido el local).
  `InterceptRegistry` (en `AircraftWorld`, compartido) permite **un
  objetivo por interceptor** y rechaza la autointercepción, el cruce
  mutuo (A→B con B→A, también si es solo una intención pendiente tras
  despegue) y los ciclos; el motivo llega a la UI (log y panel Interceptar)
  en vez de fallar mudo. Cada avión muestra a quién intercepta según su
  propio `AgentView`.
- **Telemetría de IAs a ritmo de frame**: `AircraftWorld.Watch/Unwatch`
  (refcount sobre `Datarefs.WatchPlane`); la UI observa la IA en foco y la
  suelta al cambiar de foco. Ya no se usa `FocusOtherPlane` en la UI.

### Cámara chase / vista aérea

- **Vista aérea (foco Global):** `Op.CameraFollow` payload `255`
  (`CameraOverviewIndex`). El connector enmarca ownship + IAs activas
  desde arriba (`CameraFollow.h` → `StartOverview`).
- **Chase sobre una IA:** botones **Seguir con cámara** / **Soltar
  cámara**. Solo aplica a IAs (índice ≥ 1). Payload `1..19`.
- Payload `0` = soltar. Subir `kVersion`/`Version` si se toca el layout.
- Core: `ConnectorClient.FollowCamera` / `StartOverviewCamera` /
  `ReleaseCamera`.
- `ReleaseEverything` del plugin también suelta la cámara.

No es lo mismo que el “foco” de órdenes (aunque al cambiar el foco a una
IA la UI también engancha la cámara chase).

### Caja negra: una por avión, off por defecto

- **Por defecto nadie graba.** Empezar / Detener / Borrar del panel
  **CAJA NEGRA** actúan sobre el logger del avión **en foco**.
- Con foco **GLOBAL**, la pestaña CAJA NEGRA también está disponible:
  Empezar graba **todo el roster** a la vez (y las naves que aparezcan
  después mientras siga activo); Detener / Borrar actúan sobre todas.
  Cada avión sigue escribiendo su propio CSV. La lista CSV / gráficos
  del panel muestran LOCAL como vista representativa.
- Varias naves pueden grabar a la vez también cambiando foco y pulsando
  Empezar en cada una. `Refresh` alimenta todos los `IsRecording`.
- Ficheros junto al exe desplegado (`…\plugins\AICopilot\win_x64\`):
  - Ownship: `DataLog.csv` (+ `.previous.csv`)
  - IA N: `DataLog.plane{N}.csv` (+ `DataLog.plane{N}.previous.csv`)
- Código: `FlightDataLogs` + `DataLogger` en `src/core/Domain/`. La
  telemetría se registra por un único camino por agente (`State` de su
  cuerpo): completa para el local y las IAs *Simulating*; reducida (NaN en
  G/mandos/AoA) para una IA *Observing*. Las líneas del CSV no llevan
  prefijo de avión (marcas `Inicio:`/`Fin:`/`Fase:`); el log de pantalla sí
  antepone `[etiqueta]` a las líneas de una IA.

Si el usuario pide inspeccionar la caja negra, el DataLog, un temblor,
overshoot de G, o qué pasó en una maniobra/intercept/despegue, lee y
sigue la skill
[`.claude/skills/caja-negra/SKILL.md`](.claude/skills/caja-negra/SKILL.md)
antes de diagnosticar. Los CSV están junto al exe, no en el repo.
Pregunta o deduce **qué avión** (ownship vs `planeN`) antes de leer el
fichero equivocado.

## 5. Agentes en paralelo: worktrees de Cursor (no pelearse en el mismo árbol)

Varios agentes en el mismo checkout se pisan. Para tareas en paralelo
usa worktrees de Cursor (no hace falta crearlos a mano con `git
worktree add`):

- **Agents Window:** al crear/mover un agente, elige worktree.
- **IDE:** comando `/worktree` (o `/best-of-n` para comparar modelos).
- Setup automático en [`.cursor/worktrees.json`](.cursor/worktrees.json)
  (`dotnet restore` del core). Cursor crea/limpia el worktree solo;
  al acabar aplica con `/apply-worktree` o merge/PR desde el panel.

Reparto: zonas que no se solapen (`src/connector` vs `src/core`, etc.).
Si dos tareas tocan el mismo archivo, serialízalas.
