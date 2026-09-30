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

## Cómo acceder a los datos

Los ficheros están **junto al exe desplegado**, no en el repo:

| Artefacto | Ruta absoluta |
|---|---|
| Log actual | `E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv` |
| Copia de respaldo | `E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.previous.csv` |

**Desde el agente / terminal:** lee esos paths con la herramienta de
ficheros o `Get-Content` / Python. Empieza por `DataLog.csv`; si solo
tiene cabecera o está vacío, mira `.previous.csv`.

**Desde la UI:** panel izquierdo **CAJA NEGRA** (muestras en texto) y
pestaña del viewport **Caja negra** (gráficos). La ruta del CSV también
aparece en ese panel. Hace falta haber pulsado **Empezar a grabar**
durante el vuelo; si no, el CSV solo tendrá cabecera (o datos de una
sesión anterior que se hayan conservado).

Separador `;`, números en `InvariantCulture` (punto decimal). Ritmo
~**10 Hz** solo con grabación activa. Buffer tipado/UI ~5 min
(`MaxSamples = 3000`); el fichero rota a ~8 MB.

### Build / restart de la UI

Un `build_and_deploy_ui.ps1` (o reinicio del core) **no borra** el vuelo:
el CSV se reabre en append y, al cerrar, se respalda a `.previous` si
tiene contenido. Dos restarts seguidos no pisan un `.previous` más
grande con uno vacío.

### «Borrar todo»

El botón **Borrar todo** del panel CAJA NEGRA sí lo tira **todo**:

- buffer de líneas de la UI
- muestras tipadas + marcas de los gráficos
- `DataLog.csv` y `DataLog.previous.csv` en disco

Luego recrea `DataLog.csv` solo con la cabecera. No hay otra copia
fuera de esa carpeta: si el usuario lo pulsa, los datos se han ido.
No lo pulses ni borres los CSV salvo que el usuario lo pida.

## Qué es (código)

| Pieza | Dónde |
|---|---|
| Grabación | `src/core/Domain/DataLogger.cs` (+ `BlackBoxSample` / `Marker` / `Snap`) |
| UI | panel **CAJA NEGRA**; viewport **Caja negra** |

## Flujo de inspección (síguelo)

1. **Lee** `DataLog.csv` (o `.previous.csv` si el actual está casi vacío /
   solo cabecera).
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
- No borres el CSV ni pulses «Borrar todo» salvo que el usuario lo pida.
- No trates “GRAFICOS” del panel izquierdo como la caja negra: eso son
  overlays 2D en el sim, no series temporales.

## Si hay que cambiar código tras el diagnóstico

Dominio / PID / secuencias → `src/core`. Luego
`tools\build_and_deploy_ui.ps1`. Ver [`AGENTS.md`](../../../AGENTS.md).
