# Caja negra — referencia de columnas y patrones

Complemento de [SKILL.md](SKILL.md). Léelo cuando haga falta el detalle;
para inspeccionar, basta el flujo de la skill.

## Cabecera CSV (`DataLogger`)

```
Hora;Fase;Accion;Nota;IAS_kt;ALT_ft;AGL_ft;VS_fpm;
PitchObj_deg;PitchReal_deg;BankObj_deg;BankReal_deg;
RumboObj_deg;RumboReal_deg;G;
ThrottleObj_pct;ThrottleReal_pct;FlapsObj_pct;FlapsReal_pct;
MandoPitch;MandoRoll;MandoYaw;
G_obj;G_pred;AoA_deg;Aerofrenos_pct;Peso_lb;Mach;
PitchRate_dps;RollRate_dps;Adaptacion;Proteccion
```

| Columna | Qué es |
|---|---|
| `Hora` | Reloj local `HH:mm:ss.fff` (eje humano); el buffer tipado usa `TSec` de Stopwatch |
| `Fase` | Fase de despegue (`Prep`, `Rotate`…) o `-` |
| `Accion` | Texto de maniobra/intercept activa, o `-` |
| `Nota` | Eventos discretos (Inicio/Fin/Fase/AVISO) y también log de acciones |
| `IAS_kt`…`VS_fpm` | Telemetría real |
| `PitchObj` / `PitchReal` (igual bank/rumbo) | Objetivo del core vs avión |
| `G` | Factor de carga real (`GNormal`) |
| `Throttle*` / `Flaps*` | Objetivo vs real (%) |
| `MandoPitch/Roll/Yaw` | Yoke −1…+1 que manda el core |
| `G_obj` / `G_pred` | G pedida por la maniobra / anticipada por el limitador |
| `AoA_deg`, `Mach`, `Peso_lb`, `Aerofrenos_pct` | Contexto aerodinámico |
| `PitchRate_dps` / `RollRate_dps` | Q / P (deg/s) |
| `Adaptacion` | Por qué la maniobra salió distinta a lo pedido |
| `Proteccion` | Texto del limitador de envolvente en ese frame |

Filas de **evento**: telemetría vacía, texto en `Nota`. Filas de **muestra**:
`Nota` vacío, números rellenos.

## Rutas de código útiles al diagnosticar

| Pieza | Archivo |
|---|---|
| Grabación CSV + markers | `src/core/Domain/DataLogger.cs` |
| Snapshot tipado | `src/core/Domain/BlackBoxSample.cs` |
| Marca tipada | `src/core/Domain/BlackBoxMarker.cs` |
| Texto de contexto en marcas | `src/core/Domain/BlackBoxSnap.cs` |
| Quién emite Inicio/Fin/Fase | `ManeuverSequence`, `InterceptSequence`, `TakeoffSequence` → `ActionLogged` |
| Cableado a UI | `App.xaml.cs` (`Append(..., markBlackBox: true)`), `ShellWindow` |
| Gráficos | `Ui/BlackBoxView.cs`, `Ui/TimeSeriesChart.cs` |

## Ejemplos de marcas enriquecidas

```
Inicio: Barrel roll | plan G=3.20 ~7.5s dALT=0ft | IAS=340 ALT=12000 AGL=11500 ... G=1.05 ...
Fin: Barrel roll completada — recuperando nivelado | Gobj=3.10 Gpred=2.95 alabeoAcc=358 ... | IAS=...
Fase: AI → Closing | a 1.8 NM — empiezo a frenar (umbral cierre 3500 m) | limites fase bank≤50° ... | rango=3200m ...
Inicio: interceptar AI — En el 6 | cfg dist=200m lat=0m vert=0m · Gsoft=5.5 · umbrales cierre<3500m ...
```

## Rotación y vida del fichero

- Al **arrancar** la UI: si `DataLog.csv` tiene contenido, se copia a
  `DataLog.previous.csv` y se empieza uno nuevo con cabecera.
- Al superar ~8 MB: rota igual (current → previous).
- “Borrar todo” en la UI trunca ambos y vacía el buffer tipado.

## Despliegue

El CSV está **junto al exe desplegado**, no en el repo. Tras
`tools\build_and_deploy_ui.ps1` la ruta sigue siendo
`…\plugins\AICopilot\win_x64\`. Si lees desde el workspace y no hay CSV,
mira esa carpeta de X-Plane.
