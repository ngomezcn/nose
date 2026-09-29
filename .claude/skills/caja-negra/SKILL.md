---
name: caja-negra
description: >-
  Inspecciona y analiza la caja negra de vuelo de AICopilot (DataLog.csv,
  marcas Inicio/Fin/Fase, telemetría a ~10 Hz) para diagnosticar temblores,
  overshoot de G, pelea de mandos o fallos de la computadora de vuelo. Usar
  cuando el usuario diga inspecciona la caja negra, mira el datalog, analiza
  el vuelo, diagnostica la maniobra, revisa el CSV de telemetría, o pida
  entender qué pasó en un rollo/intercept/despegue a partir de los registros.
---

# Caja negra — inspección de vuelo

La caja negra **no decide nada del vuelo**: solo graba lo que el core ya
calcula. Vive en el **core** (`src/core`), no en el connector. Antes de
opinar sobre un temblor o un fallo de control, **lee el CSV**; no inventes
valores ni asumas la sesión anterior.

## Qué es y dónde está

| Artefacto | Ruta |
|---|---|
| Sesión actual | `E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv` |
| Sesión previa (al reiniciar la UI) | `…\DataLog.previous.csv` |
| Código | `src/core/Domain/DataLogger.cs`, `BlackBoxSample.cs`, `BlackBoxMarker.cs`, `BlackBoxSnap.cs` |
| UI graficos | pestaña viewport **Caja negra**; panel izquierdo **CAJA NEGRA** |

Separador `;`, números en `InvariantCulture` (punto decimal). Ritmo ~**10 Hz**
mientras hay conexión. Buffer tipado/UI ~5 min (`MaxSamples = 3000`); el
fichero rota a ~8 MB.

## Flujo de inspección (síguelo)

1. **Lee** `DataLog.csv` (o `.previous.csv` si la UI acaba de reiniciarse y
   el actual está casi vacío / solo cabecera).
2. **Localiza marcas** en la columna `Nota` (o líneas con telemetría vacía y
   texto en `Nota`): empiezan por `Inicio:`, `Fin:`, `Fase:`, o avisos.
3. **Aísla la ventana temporal** entre el `Inicio:` relevante y el `Fin:` /
   siguiente `Inicio:` / fin de fichero.
4. **Lee las series** en esa ventana (G, mandos, pitch/bank obj vs real,
   IAS…). Compara **obj** vs **real** y mira `Adaptacion` / `Proteccion`.
5. **Concluye** con evidencia (hora + valores), no con intuición. Si el CSV
   no existe o está vacío, dilo y pide un vuelo/maniobra grabada.

Detalle de columnas y patrones → [reference.md](reference.md).

## Marcas de evento (lo más importante)

Las acciones de vuelo (despegue / maniobra / intercept) dejan marcas en
`Nota` **y** en los gráficos (líneas verticales). Suelen llevar un snapshot
pegado (`IAS=… G=… bank=…`) vía `BlackBoxSnap`.

| Prefijo | Significado |
|---|---|
| `Inicio: …` | Arranca maniobra, intercept o despegue (+ plan/cfg si aplica) |
| `Fase: … → …` | Cambio de fase (intercept Pursuit/Closing/Station; despegue) |
| `Fin: …` | Completada, abortada, nivelado recuperado, ABORTO duro |
| `AVISO: …` | Alivio de G, suelo AGL, etc. (no siempre es fin) |

Mensajes del docker/config **también** van a `Nota` en el CSV, pero **no**
como marcas de gráfico (`chartMarker=false`).

## Cómo leer un problema típico

- **Temblores / oscilación**: pitch/bank **real** zigzaguea frente a **obj**;
  `MandoPitch`/`MandoRoll` se pelean (signo que cambia rápido).
- **Overshoot de G**: `G` supera `G_obj` / `G_pred`; mira `Proteccion` y
  marcas `AVISO` / `ABORTO`.
- **Maniobra suave de más**: `G_obj` bajo + texto en `Adaptacion` (plan
  recortado por energía/altura).
- **Intercept que no cierra**: marcas `Fase:` + `rango=` / `cierre=`; cfg
  `dist/lat/vert` en el `Inicio:`.
- **Mando vs realidad**: `ThrottleObj` vs `ThrottleReal`, flaps, SB.

## Qué no hacer

- No digas “reinicia por Plugin Admin” (X-Plane 12 no lo tiene).
- No modifiques el connector para “arreglar” un diagnóstico de vuelo: la
  lógica está en `src/core/Domain/`.
- No borres el CSV salvo que el usuario lo pida (la UI tiene “Borrar todo”).
- No trates “GRAFICOS” del panel izquierdo como la caja negra: eso son
  overlays 2D en el sim, no series temporales.

## Si hay que cambiar código tras el diagnóstico

Dominio / PID / secuencias → `src/core`. Luego
`tools\build_and_deploy_ui.ps1`. Ver [`AGENTS.md`](../../../AGENTS.md).
