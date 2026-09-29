---
name: xplane-sdk
description: Consulta la documentación local del X-Plane SDK (headers XPLM/XPWidgets y guías de desarrollo de plugins) antes de escribir o modificar código que use la API de X-Plane. Úsala siempre que el trabajo toque funciones XPLM*/XP*, datarefs, flight loop callbacks, menús, widgets, dibujado, sonido, o cualquier símbolo definido en third_party/XPSDK.
---

# X-Plane SDK docs

Este proyecto es un plugin de X-Plane (`src/plugin.cpp` + CMake). Las
reglas de build/deploy viven en [`AGENTS.md`](../../../AGENTS.md). Antes
de programar o tocar cualquier cosa que use el SDK de X-Plane (funciones
`XPLM*`, `XP*`, datarefs, callbacks, tipos, macros de versión, etc.),
consulta la documentación local en lugar de fiarte de memoria o inventar
la firma de una función.

## Dónde mirar, en este orden

1. **El header C real** en `third_party/XPSDK/CHeaders/XPLM/<Header>.h`
   o `third_party/XPSDK/CHeaders/Widgets/<Header>.h`. Es la fuente de
   verdad: firma exacta, comentario doxygen completo, macros de
   disponibilidad por versión (`XPLM200`, `XPLM301`, `XPLM400`...).
   Comprueba siempre estas macros contra la versión mínima de SDK que
   soporta este proyecto (ver `CMakeLists.txt` / `XPLM_MIN_SDK` si
   existe) antes de usar una función marcada como añadida en una
   versión reciente.
2. **`docs/xplane-sdk/reference/<Header>.md`** — la misma información
   que la página web `developer.x-plane.com/sdk/<Header>/`, en
   Markdown, útil para leer de un vistazo todas las funciones de un
   módulo con su descripción.
3. **`docs/xplane-sdk/guides/*.md`** — artículos transversales que no
   están en ningún header: cómo compilar/instalar un plugin
   (`building-and-installing-plugins.md`), reglas de dibujado
   (`drawingrules.md`), coordenadas de pantalla
   (`screencoordinates.md`), compatibilidad entre versiones del SDK
   (`plugin-compatibility-guide-for-x-plane-11-50.md`), inicialización
   diferida (`deferredinitialization.md`), sonido OpenAL
   (`openal.md`), estado de OpenGL (`openglstate.md`), tráfico/TCAS
   (`overriding-tcas-and-providing-traffic-information.md`,
   `plugin-traffic-wake-turbulence.md`), etc.
4. Lee primero [`docs/xplane-sdk/README.md`](../../../docs/xplane-sdk/README.md)
   para el índice completo y saber qué headers usa ya este plugin.

## Cómo usar esto en la práctica

- Antes de llamar a una función `XPLM*`/`XP*` que no reconozcas con
  certeza, abre su header o su página en `reference/` y comprueba:
  firma exacta, tipos de parámetros, qué devuelve, y si necesita
  liberarse/desregistrarse (p. ej. callbacks o datarefs registrados).
  No la inventes ni la copies de memoria.
- Si vas a añadir una dependencia a una función nueva, comprueba la
  macro de versión (`XPLM2xx`/`XPLM3xx`/`XPLM400`) en el header y
  confirma que es compatible con la versión mínima de X-Plane que
  soporta este plugin.
- Si el tema es "cómo se hace X en general" (empaquetar el plugin,
  reglas de dibujado, coordenadas, compatibilidad, threading...) y no
  una función concreta, busca primero en `docs/xplane-sdk/guides/`.
- Esta copia es estática (descargada 2026-09-29) y puede quedar
  desactualizada frente a `developer.x-plane.com`; si algo no cuadra o
  parece faltar, dilo y, si hay acceso a red, trae la página actual
  con `curl` en vez de asumir que el espejo local es exhaustivo.

## Ojo: la UI de gestión de plugins ha cambiado

X-Plane 12 **ya no tiene la ventana clásica "Plugin Admin"** (el diálogo
centralizado con lista de plugins + botones Enable/Disable/Reload que sí
existía en versiones más viejas y que aparece mencionado por todas
partes en tutoriales antiguos y en el propio entrenamiento del modelo).
Ahora es cada plugin el que se organiza a su manera vía el SDK (menús
propios con `XPLMAppendMenuItem`, comandos con `XPLMCreateCommand`,
etc.) — no asumas ni escribas instrucciones tipo "Plugins → Plugin Admin
→ Reload Plugins", es información obsoleta y confunde al usuario. Si
necesitas que el usuario dispare un reload de plugins a mano y no sabes
qué menú/atajo tiene configurado en su instalación, pregúntaselo en vez
de inventar una ruta de menú.

(Nota de contexto: en este proyecto el reload se automatiza con un
plugin auxiliar, `devtools/ReloadTrigger` — ver su
[`README.md`](../../../devtools/ReloadTrigger/README.md) — que escucha
un UDP en localhost y llama a `XPLMReloadPlugins()`. El script
`tools/build_and_deploy.ps1` del repo principal le envía la señal. Eso
evita tener que tocar ningún menú de X-Plane en el flujo normal.)
