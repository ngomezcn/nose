# Documentación del X-Plane SDK (espejo local)

Copia local en Markdown de la documentación oficial del SDK de X-Plane
(`developer.x-plane.com`), descargada el 2026-09-29, para que cualquier
IA o persona que trabaje en este proyecto pueda consultarla sin salir
del repo. Reglas de agentes: [`../../AGENTS.md`](../../AGENTS.md). En
Claude Code, skill: `.claude/skills/xplane-sdk/`.

## Qué hay aquí

- **`reference/`** — una página por cada header del SDK (`XPLMxxx.h`,
  `XPWidgets.h`, etc.), con la descripción general del módulo y de cada
  función, tipo y callback que declara. Es el mismo contenido que
  `https://developer.x-plane.com/sdk/<Header>/`.
- **`guides/`** — artículos de la guía de desarrollo de plugins (cómo
  compilar e instalar un plugin, inicialización diferida, reglas de
  dibujado, coordenadas de pantalla, compatibilidad entre versiones,
  OpenGL/Vulkan, sonido con OpenAL/FMOD, tráfico TCAS, etc.).

## Fuente de verdad real: los headers en `third_party/XPSDK`

Los `.h` bajo [`../../third_party/XPSDK/CHeaders/`](../../third_party/XPSDK/CHeaders/)
(`XPLM/` y `Widgets/`) son la referencia más autorizada y más detallada:
son los mismos headers que se compilan, con comentarios doxygen
completos función por función, y la web de `reference/` está generada
a partir de ellos. **Antes de usar una función o tipo del SDK, mira
primero su comentario en el header correspondiente**; usa las páginas
de `reference/` como complemento navegable/legible en Markdown y para
tener el contexto general del módulo, y `guides/` para temas
transversales que no están en ningún header (empaquetado del plugin,
reglas de dibujado, compatibilidad de versiones, etc.).

## Índice rápido de `reference/` (headers usados actualmente por este plugin en negrita)

- **XPLMDataAccess** — leer/escribir datarefs (`XPLMGetDataf`, `XPLMFindDataRef`, registrar datarefs propios...)
- XPLMCamera — control de cámara externa
- **XPLMDefs** — tipos base, `XPLMPluginID`, `XPLM_API`, macros de versión
- XPLMDisplay — dibujado en pantalla, ventanas, hotkeys
- XPLMGraphics — utilidades de gráficos/OpenGL del SDK
- XPLMInstance — dibujado de objetos 3D vía instancing (Vulkan/Metal-friendly)
- XPLMMap — integración con el mapa de X-Plane
- XPLMMenus — menús de plugin
- XPLMNavigation — base de datos de navegación (navaids, fixes, FMS)
- XPLMPanelGraphics — dibujado del panel 2D
- XPLMPlanes — control de aviones IA/multiplayer
- **XPLMPlugin** — ciclo de vida y mensajes entre plugins (`XPLMFindPluginBySignature`, `XPLMSendMessageToPlugin`...)
- **XPLMProcessing** — flight loop callbacks (`XPLMRegisterFlightLoopCallback`...)
- XPLMScenery — consultas de terreno/escenografía
- XPLMSound — reproducción de sonido FMOD
- **XPLMUtilities** — logging, comandos, rutas de archivos, preferencias
- XPLMWeather — datos meteorológicos
- XPStandardWidgets / XPUIGraphics / XPWidgetDefs / XPWidgetUtils / XPWidgets — sistema de widgets (UI clásica de X-Plane)

## Actualizar este espejo

Esta copia es estática y puede quedar desactualizada. Si necesitas la
última versión de una página, tráela de nuevo con `curl` desde
`https://developer.x-plane.com/sdk/<Header>/` (referencia) o
`https://developer.x-plane.com/article/<slug>/` (guía) y repite el
mismo proceso de limpieza (extraer el `<article class="page">` y
convertir a Markdown). No hay un script de actualización automatizado
todavía.
