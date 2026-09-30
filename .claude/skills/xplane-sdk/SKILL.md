---
name: xplane-sdk
description: >
  Consulta la documentación local del X-Plane SDK (headers XPLM/XPWidgets
  y guías) y devuelve firmas/macros exactas. Usar solo al tocar la API de
  X-Plane en el connector (funciones XPLM*/XP*, callbacks, widgets, dibujo,
  sonido), no por menciones genéricas a datarefs en el core C#.
when_to_use: >
  Editar src/connector, CMakeLists del plugin, o preguntar la firma/versión
  de una función XPLM*/XP*. No usar en trabajo solo de UI/core C#.
argument-hint: "[función o tema SDK]"
paths:
  - "src/connector/**"
  - "third_party/XPSDK/**"
  - "docs/xplane-sdk/**"
  - "CMakeLists.txt"
context: fork
agent: Explore
model: haiku
effort: low
background: false
allowed-tools: Read, Grep, Glob
---

# Consulta X-Plane SDK

Investiga `$ARGUMENTS` (o el símbolo/tema SDK del encargo) y responde
solo con hallazgos concretos. No inventes firmas.

## Orden de búsqueda

1. Header C real:
   - `third_party/XPSDK/CHeaders/XPLM/<Header>.h`
   - `third_party/XPSDK/CHeaders/Widgets/<Header>.h`
   Fuente de verdad: firma, doxygen, macros `XPLM200`/`XPLM301`/`XPLM400`…
2. Espejo Markdown: `docs/xplane-sdk/reference/<Header>.md`
3. Temas transversales: `docs/xplane-sdk/guides/*.md`
4. Índice: `docs/xplane-sdk/README.md`

Comprueba macros de versión frente a la mínima del proyecto
(`CMakeLists.txt` / `XPLM_MIN_SDK` si existe).

## Respuesta (breve)

- Firma exacta + tipos + valor de retorno
- Macro de disponibilidad y si hace falta liberar/desregistrar
- Si el tema es guía (dibujo, coords, OpenAL, TCAS…): 3–6 bullets del
  guide relevante, con ruta del fichero
- Si el espejo local (2026-09-29) no cuadra o falta algo, dilo

## Reglas del proyecto (no olvidar)

- X-Plane 12 **no** tiene "Plugin Admin"; no inventes
  "Plugins → Plugin Admin → Reload". El reload aquí va por
  `devtools/ReloadTrigger` + `tools/build_and_deploy_addon.ps1`.
- El connector (`src/connector`) es puente dataref/comando/hold; la
  lógica de dominio va en `src/core`.
- Wrappers ya existentes en este repo (no reinventar sin leerlos):
  - `AiControl.h` → `XPLMAcquirePlanes` / `DisableAI` / `ReleasePlanes`
  - `CameraFollow.h` → `XPLMControlCamera` / chase sobre `planeN_*`
  - `ScenarioPlace.h` → PlaceUser + Place AI
  Si cambias el payload de un `Op.*`, bump `kVersion` en Protocol.h/.cs.
