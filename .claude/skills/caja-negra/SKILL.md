---
name: caja-negra
description: >-
  Inspecciona y analiza la caja negra de vuelo de AICopilot (DataLog.csv /
  DataLog.planeN.csv, marcas Inicio/Fin/Fase, telemetría a ~10 Hz) para
  diagnosticar temblores, overshoot de G, pelea de mandos o fallos de la
  computadora de vuelo. Usar cuando el usuario diga inspecciona la caja
  negra, mira el datalog, analiza el vuelo, diagnostica la maniobra, revisa
  el CSV de telemetría, o pida entender qué pasó en un rollo/intercept/
  despegue a partir de los registros.
---

# Caja negra — inspección de vuelo

La caja negra **no decide nada del vuelo**: solo graba lo que el core ya
calcula. Vive en el **core** (`src/core`), no en el connector. Antes de
opinar sobre un temblor o un fallo de control, **lee el CSV**; no inventes
valores ni asumas la sesión anterior.

## Cómo acceder a los datos

Hay **un logger por avión** (XPLM index). Por defecto **ninguno graba**;
hace falta **Empezar a grabar** con esa nave en **EN FOCO**. Los ficheros
están **junto al exe desplegado**, no en el repo:

| Avión | Log actual | Respaldo |
|---|---|---|
| Ownship (índice 0) | `E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv` | `DataLog.previous.csv` |
| IA N (índice 1..19) | `…\DataLog.plane{N}.csv` | `DataLog.plane{N}.previous.csv` |

Ejemplo Airbus del escenario (casi siempre `plane1`):
`DataLog.plane1.csv`.

**Desde el agente / terminal:** lee esos paths. Empieza por el CSV del
avión que el usuario esté mirando (foco / “el Airbus” / “el F-14”); si
solo tiene cabecera o está vacío, mira el `.previous` correspondiente.

**Desde la UI:** panel **CAJA NEGRA** — Empezar/Detener/Borrar y la lista
CSV / gráficos muestran el logger del avión **en foco**. Varias naves
pueden grabar en paralelo; al cambiar el foco la UI cambia de CSV.

Separador `;`, números en `InvariantCulture` (punto decimal). Ritmo
~**10 Hz** solo con grabación activa en ese logger. Buffer tipado/UI
~5 min (`MaxSamples = 3000`); el fichero rota a ~8 MB.

### Telemetría IA vs ownship

- **Ownship:** columnas completas (G, mandos, AoA, obj vs real, etc.).
- **IA:** telemetría **reducida** (GS como IAS aprox., ALT MSL, heading /
  pitch / bank de `planeN_*`; G/mandos/AoA suelen ir vacíos). Útil para
  ver si el hold cinemático va recto; no diagnostica PID del F-14.

### Build / restart de la UI

Un `build_and_deploy_ui.ps1` (o reinicio del core) **no borra** el vuelo:
cada CSV se reabre en append y, al cerrar, se respalda a su `.previous`
si tiene contenido.

### «Borrar todo»

El botón **Borrar todo** vacía **solo el logger del avión en foco**
(buffers + su CSV + su `.previous`). No borra los de otras naves. No lo
pulses ni borres CSV salvo que el usuario lo pida.

## Qué es (código)

| Pieza | Dónde |
|---|---|
| Gestor por avión | `src/core/Domain/FlightDataLogs.cs` |
| Grabación | `src/core/Domain/DataLogger.cs` (+ `BlackBoxSample` / `Marker` / `Snap`) |
| UI | panel **CAJA NEGRA**; viewport **Caja negra** (siempre el foco) |

## Flujo de inspección (síguelo)

1. **Identifica el avión** (ownship vs `planeN`) y elige el CSV correcto.
2. **Lee** ese fichero (o su `.previous` si el actual está casi vacío).
3. **Localiza marcas** en `Nota`: `Inicio:`, `Fin:`, `Fase:`, o avisos.
4. **Aísla la ventana** entre `Inicio:` y `Fin:` / siguiente `Inicio:`.
5. **Lee las series** (G, mandos, pitch/bank obj vs real, IAS…). En IA
   espera huecos; no inventes G/mandos.
6. **Concluye** con evidencia (hora + valores). Si no hay grabación,
   dilo y pide pulsar Empezar con esa nave en foco.

Detalle de columnas y patrones → [reference.md](reference.md).

## Marcas de evento

| Prefijo | Significado |
|---|---|
| `Inicio: …` | Arranca maniobra, intercept, despegue o grabación |
| `Fase: … → …` | Cambio de fase (intercept / despegue) |
| `Fin: …` | Completada, abortada, fin de grabación |
| `AVISO: …` | Alivio de G, suelo AGL, etc. |

Ownship: marcas de takeoff/maneuver/intercept. IA: marcas de
`AiStraightHold` (recto/nivelado) si ese logger está grabando.

## Cómo leer un problema típico

- **Temblores / oscilación** (ownship): pitch/bank **real** vs **obj**;
  `MandoPitch`/`MandoRoll` cambian de signo rápido.
- **Overshoot de G**: `G` vs `G_obj` / `G_pred`; `Proteccion` / `AVISO`.
- **Intercept**: marcas `Fase:` + `rango=` / `cierre=`; cfg en el `Inicio:`.
- **Airbus / IA que no va recta**: `DataLog.plane1.csv` — heading/ALT/GS
  estables vs deriva; no busques columnas de mando del F-14 ahí.

## Qué no hacer

- No digas “reinicia por Plugin Admin” (X-Plane 12 no lo tiene).
- No modifiques el connector para “arreglar” un diagnóstico de vuelo: la
  lógica está en `src/core/Domain/`.
- No borres CSV ni pulses «Borrar todo» salvo que el usuario lo pida.
- No confundes **EN FOCO** / cámara chase con grabación: son independientes.
- No trates “GRAFICOS” del panel izquierdo como la caja negra (overlays 2D).

## Si hay que cambiar código tras el diagnóstico

Dominio / PID / secuencias → `src/core`. Luego
`tools\build_and_deploy_ui.ps1`. Ver [`AGENTS.md`](../../../AGENTS.md).
