# AI Copilot — connector + core para X-Plane 12

Dos proyectos, con una separación de responsabilidades estricta:

| | | |
|---|---|---|
| **`src/connector`** | C++ → `AICopilot.xpl` | Un **puente y nada más**. Lee y escribe cualquier dataref, dispara cualquier comando, y mantiene valores puestos (holds). No sabe qué es un despegue. |
| **`src/core`** | C#/.NET 10 WPF → `AICopilotCore.exe` | **Toda** la lógica (etapas, PID, qué datarefs importan) **y** la UI. Solo habla con el plugin. |

Se comunican por un named pipe dúplex en `\\.\pipe\AICopilot.v1`. La
consecuencia práctica, que es la razón de ser del reparto: **añadir una fase
de vuelo, un PID nuevo o un panel nuevo se hace tocando solo C#** — sin
recompilar el plugin ni recargarlo dentro de X-Plane.

Sin ninguna IA de por medio todavía, el core hace de "piloto automático de
despegue" simplificado, pero con control real (PID) en vez de
apretar/soltar: suelta freno, mete flaps, aplica potencia, espera a Vr,
rota con un PID de cabeceo, sube tren, va limpiando flaps, sube hasta una
altitud intermedia donde hace un viraje de salida (PID de rumbo en cascada
con PID de alabeo) y luego sigue subiendo hasta la altitud objetivo, donde
nivela el morro. **No es realista a nivel profesional a propósito** — es
justo lo necesario para "hacer el paripe" con un comportamiento suave (sin
tirones de apretar/soltar) y tener algo que funcione de extremo a extremo.
El día que enchufes a Jev, el sitio natural es
[`src/core/Domain/`](src/core/Domain/): el plugin no se entera.

> **¿No quieres cerrar X-Plane cada vez que recompilas?** No hace falta:
> usa `tools\build_and_deploy_ui.ps1`, `tools\build_and_deploy_addon.ps1`
> o el global `tools\build_and_deploy.ps1` según lo que hayas tocado
> (detalle en [`AGENTS.md`](AGENTS.md) y en "Actualizar el plugin sin
> cerrar X-Plane" más abajo).

## Novedades v4.4 — Interceptar: ir a por otro avión y quedarse con él

Nueva sección en la barra de actividad (**INTERCEPTAR**). Se elige un avión
de la partida, una posición relativa, y el core lo intercepta como lo haría
un caza de alerta: va a por él lo más rápido que el avión da y se queda
pegado a su cola.

**Lo primero es saber si vuela.** X-Plane no publica un dataref de "en
tierra" por cada IA, así que se deduce de lo que sí publica: velocidad sobre
el suelo, ritmo vertical y altura sobre el terreno (estimada con la posición
propia). Si el blanco sigue en tierra, el avión **no sale disparado**: se
queda dando vueltas en la zona con un alabeo suave y espera a que despegue,
avisando por el log cada diez segundos. En cuanto está en el aire, empieza
la persecución.

**Las posiciones.** Por defecto *cola alta* — detrás y un poco por encima,
que es la posición de interceptación real: se ve al otro avión entero, la
separación se controla con el gas y no se vuela dentro de su estela. Además:
*paralelo izquierda*, *paralelo derecha*, *arriba* y *abajo*. Las tres
separaciones (distancia por detrás, lateral y vertical) se editan **desde la
UI**, en metros; 200 / 100 / 40 de fábrica. Cada posición las usa en
proporción, así que bajar la distancia encoge la formación entera sin que
ninguna posición deje de parecerse a sí misma.

**Cómo se llega.** Todo el trabajo se hace en coordenadas locales de X-Plane
(las mismas en las que están el avión propio y las IAs), así que la posición
relativa sale de una resta: ni lat/lon, ni trigonometría esférica, ni dos
altímetros que comparar. Hay tres regímenes:

- **Persecución** — se apunta a donde *estará* el blanco, no a donde está.
  Una persecución pura llega siempre tarde y por fuera.
- **Acercamiento** — el exceso de velocidad se raciona con la distancia que
  queda, con la cuenta de un frenado (`v = √(2·a·d)`). Es lo que evita el
  clásico pasar silbando por delante del blanco y tener que dar la vuelta.
  A 15 km permite 530 kt de exceso; a 300 m, 70.
- **Formación** — ya no se persigue un punto: se copia la velocidad del
  blanco, con correcciones pequeñas por rumbo (lateral) y V/S (vertical).

**Blancos lentos.** Un F-14 no vuela a 110 kt, así que "igualar su
velocidad" no siempre es una orden que se pueda cumplir. Hay tres
herramientas, en este orden: **flaps** (bajan el suelo de velocidad segura
un ~18%, y solo salen por debajo de su límite de extensión),
**aerofrenos** (para quitar velocidad sin tocar la actitud) y, si aún así no
basta, **serpenteo**: se vuela al mínimo seguro zigzagueando alrededor de su
rumbo, con el ángulo que hace que el avance *neto* iguale el suyo
(`cos ζ = v_deseada / v_propia`). Es lo que hace un interceptor real con un
blanco lento, y es lo único que no acaba en entrada en pérdida.

Las protecciones que ya había siguen puestas: el alabeo se limita por la G
que el ala puede dar de verdad a esa velocidad y peso, el suelo de terreno
impide seguir a nadie contra el suelo, el ascenso pedido se desvanece cerca
de la velocidad mínima, y por debajo de 60 m de separación el avión se
descuelga solo. Al cancelar, los aerofrenos se recogen pero **los flaps
no**: si se sacaron para poder volar despacio, recogerlos de golpe a esa
velocidad sería una pérdida — el log lo dice.

Al ser la tercera secuencia que escribe sobre los mismos overrides
(despegue, acciones, interceptación), arrancar cualquiera corta las otras
dos, igual que antes.

## Novedades v4.3 — las maniobras se adaptan al avión, no al revés

Antes, cada maniobra del catálogo era una tabla de **objetivos fijos**:
"picado = morro a −40° y gas 0.9", "looping = palanca de cabeceo a 0.85
durante 15 s". Esos números solo valen en un punto del envolvente, y fuera
de él la maniobra fallaba de dos maneras:

- **Se pasaba de G y el ejecutor la cortaba**, cayendo a "recuperando
  nivelado". El caso que disparó este trabajo: pedir *Caer en picado*
  volando a 300 kt movía el objetivo de morro a 12 °/s, y eso son
  `n = 1 − 12·300/1092 = −2.3 g` — fuera de margen, maniobra abortada. El
  ángulo pedido no tenía nada de malo: lo que estaba mal era **la prisa** con
  la que se bajaba el morro.
- **O no se pasaba de nada y simplemente no hacía lo que decía**: un "viraje
  de crucero" a 170 kt perdía 2.200 fpm, un rollo de 4 s daba 355° a 250 kt
  pero 620° a 500 kt (acababa casi de cuchillo), y un looping a 350 kt no
  completaba la vuelta en los 15 s del guion.

**Ahora no hay fallback: hay adaptación anticipada.** Cada maniobra declara
su *intención* (qué trayectoria, cuánta G se permite, con qué brusquedad,
qué hacer con la velocidad) y un planificador la traduce **cada frame** a
objetivos concretos usando el estado real del avión. Si algo no cabe, se
adapta y se dice por qué.

### Las piezas nuevas

| Fichero | Qué hace |
|---|---|
| [`F14Aero.cs`](src/core/Domain/F14Aero.cs) | La aerodinámica del F-14 como funciones puras: atmósfera, pérdida con el peso real, G disponible, geometría de viraje y de arco vertical, resistencia/empuje, predicción de un looping o un Split-S completo. |
| [`FlightState.cs`](src/core/Domain/FlightState.cs) | Foto inmutable del avión por frame, con AoA, Mach, peso y VNE **leídos del simulador**, no estimados. |
| [`ManeuverPlanner.cs`](src/core/Domain/ManeuverPlanner.cs) | Decide **qué** se puede volar aquí y ahora: presupuesto de G → alabeo → trayectoria → morro y motor. |
| [`EnvelopeProtection.cs`](src/core/Domain/EnvelopeProtection.cs) | Decide **cuánto** mando se permite, de forma continua y anticipada. Sustituye al corte por G. |
| [`ManeuverSequence.cs`](src/core/Domain/ManeuverSequence.cs) | Solo **ejecuta**: persigue los objetivos del plan dentro de los límites de la protección. |

### La ecuación que lo ordena todo

El factor de carga no lo fija el ángulo al que se vuela, lo fija la
velocidad a la que se cambia de ángulo:

```
n = (cos γ + V·γ̇/g) / cos φ        →        γ̇_max = (n_límite·cos φ − cos γ)·g / V
```

Esa segunda forma es la que se usa como **límite de ritmo del objetivo de
cabeceo**, recalculada cada frame. Con ella, el picado de 300 kt baja el
morro a ~3 °/s (14 s hasta −40°, que es lo que tarda un piloto real) y la G
nunca baja de 0.15 — en vez de −2.3 g y maniobra cancelada. Y como el
límite va con `1/V`, se ajusta solo: ninguna constante puede hacer eso.

### Protección continua en vez de corte

El backstop de G era un **detector**: reaccionaba a una G que ya se había
producido por un mando dado medio segundo antes, y era todo o nada. Ahora
hay dos capas:

- **G de trabajo** (±6.0 / −1.5 por defecto): la autoridad de mando se va a
  cero de forma progresiva una banda antes de llegar, y se anticipa la G que
  va a producir el mando que está a punto de darse. La maniobra no se corta:
  se suaviza.
- **Backstop estructural** (+7.0 / −2.6, con 0.3 s de permanencia): lo único
  que sigue cancelando una maniobra. Si salta en vuelo normal, es un fallo
  de las leyes de adaptación, no una condición de vuelo.

Se suman protecciones de **pérdida/AoA**, **VNE/Mach**, **suelo** y
**techo**, y todas escriben restricciones sobre las mismas variables: se
queda la más restrictiva, sin prioridades ni cambios de modo.

### Qué cambia en cada categoría

- **Altitud** — se pide *trayectoria*, no ángulo de morro (el morro que hace
  falta para la misma trayectoria cambia con la velocidad: el AoA va con
  1/V²). El picado recorta su ángulo si no hay altura para salir (la altura
  de recuperación va con V², no es un mínimo AGL constante) y baja gases
  antes de acercarse a la VNE. Los ascensos se lavan solos hasta el ángulo
  que el empuje sostiene, en vez de quedarse colgados perdiendo velocidad.
- **Virajes y combinadas** — el alabeo se recorta a lo que el ala sostiene
  de verdad a esa velocidad y peso, y el morro se calcula con el `1/cos φ`
  del viraje, así que dejan de perder 2.200 fpm a baja velocidad. Los gases
  pasan a perseguir la velocidad de entrada: el viraje cerrado ya no acelera
  14 kt/s hasta salirse de su propio envolvente. El break defensivo es el
  único que canjea altura por velocidad de giro, como haría un piloto.
- **Acrobacias** — se cierran **por ángulo recorrido, no por cronómetro**, y
  se manda **G, no palanca**. Un rollo es 360° a cualquier velocidad; un
  looping se planifica entero antes de empezar (cuánta G hace falta para que
  el arco quepa y para no quedarse sin velocidad arriba). El Split-S calcula
  la altura que va a perder **antes** de entrar — 4.600 ft a 250 kt, 13.800 a
  500 — en vez de fiarse de un mínimo fijo de 6.000 ft que mentía en los dos
  extremos.
- **Velocidad** — Acelerar/Frenar anclan la altitud de entrada (antes decían
  "sin cambiar de altura" y perdían 1.500 fpm al frenar), el objetivo se
  recorta a la ventana real de esa altitud (a 40.000 ft, 300 KIAS ya es
  supersónico) y se usan **aerofrenos** cuando el ralentí no basta.

### En la UI

Los botones ya no se apagan cuando la maniobra "no cabe": se ejecuta
adaptada y el tooltip explica qué cambia (*"alabeo 75→63° porque a 231 kt el
ala no da los 3.9 g que piden 75°"*), en cursiva para que se vea de un
vistazo. Solo se deshabilitan cuando no existe ninguna versión segura de lo
pedido. El panel de telemetría muestra la G objetivo junto a la real y el
AoA, y el CSV del log de datos gana las columnas `G_obj`, `G_pred`, `AoA_deg`,
`Adaptacion` y `Proteccion` — sin ellas no hay forma de saber *por qué* una
maniobra salió más suave de lo pedido.

### Datarefs nuevos (solo lectura salvo los aerofrenos)

`alpha` (AoA real), `machno`, `Q`/`P` (velocidades de rotación),
`m_total` (peso real: la velocidad de pérdida va con su raíz, y un F-14 va
de 40.000 a 74.000 lb), `acf_Vne`, `onground_any` y
`speedbrake_ratio` como mando. Todos son una línea en
[`Datarefs.cs`](src/core/Connector/Datarefs.cs): el plugin no se entera.

## Novedades v4.2 — Acciones: catálogo de maniobras del F-14 Tomcat

El addon se especializa en el **F-14 Tomcat**. Además del guion fijo de
despegue (`TakeoffSequence`), ahora hay un **catálogo de acciones/maniobras
discretas** — el vocabulario con el que, el día de mañana, Jev pedirá cosas
sin tener que darle números: "baja altitud agresivo", "vira cerrado a la
izquierda", "dame un rollo". Jev no es buena con los números; esto existe
para que no tenga que serlo — traduce una intención humana a los objetivos
de actitud/mando que ya sabe ejecutar el PID del proyecto.

El catálogo vive en
[`src/core/Domain/Maneuvers.cs`](src/core/Domain/Maneuvers.cs) (los datos:
qué maniobras hay, en qué categoría, con qué objetivo) y se ejecuta desde
[`src/core/Domain/ManeuverSequence.cs`](src/core/Domain/ManeuverSequence.cs)
(el lazo de control, un tick por frame, igual que `TakeoffSequence`). En la
UI aparece como botones nuevos en el panel izquierdo, agrupados por
categoría, y como texto en el panel de telemetría ("ACCIONES" y "G normal").

**Las cinco categorías** (pensadas para "simular lo que haría una persona
volando", no un ensayo de límites estructurales):

- **Altitud** — cabeceo puro, sin alabeo: bajar leve/agresivo, caer en
  picado, subir morro leve/agresivo, nivelar.
- **Virajes** — alabeo con algo de cabeceo para no perder altura: viraje
  suave y viraje cerrado, a cada lado.
- **Combinadas** — lo que pedía el encargo original ("subir agresivo + giro
  agresivo a la izquierda"): subir/bajar agresivo combinado con un viraje, y
  un break defensivo de alta G a cada lado.
- **Acrobacias** — rollos, barrel rolls, looping ("backflip"), Immelmann y
  Split-S, a cada lado donde aplica.
- **Velocidad** — acelerar/frenar en actitud de crucero, sin tocar la
  altitud.

**Dos formas de ejecutar una acción** (el porqué está en el comentario de
cabecera de `ManeuverMode`, en `Maneuvers.cs`):

- **`AttitudeHold`** (bajar, subir, virar, break...): persigue un pitch/bank
  objetivo con el mismo PID en cascada que usa `TakeoffSequence`, y se queda
  ahí sosteniéndolo indefinidamente, como un autopiloto — no "termina" sola,
  termina cuando llega otra acción o un "Abortar".
- **`Aerobatic`** (rollos, looping, Immelmann, Split-S): recorre **ángulos**
  (360° de alabeo, media vuelta de arco vertical) mandando **G**, no una
  palanca fija durante N segundos — ver "Novedades v4.3", que es donde se
  explica por qué el cronómetro no podía funcionar. El arco se cuenta
  integrando la velocidad de cabeceo real (dataref `Q`), que no tiene el
  problema de `theta` (que se dobla en ±90° y no sirve para contar un
  looping). Al acabar el último tramo, `ManeuverSequence` pasa sola a
  recuperar vuelo nivelado y se queda ahí (mismo comportamiento de
  autopiloto que `AttitudeHold`).

**Límites del F-14A/B usados como referencia** (fuentes públicas, no
clasificadas — sirven de orden de magnitud, no de dato certificado):
límite estructural +7.5 g / -3.0 g (limpio), "corner speed" ~300-350 KCAS
(más baja que en un caza ligero: el Tomcat es pesado y el ala de geometría
variable prioriza sustentación a baja velocidad antes que una corner speed
alta), techo de servicio ~50.000-53.000 ft, VNE estructural a nivel del mar
~750-800 KCAS (Mach ~1.2), velocidad máxima en altura Mach ~2.34, ángulo de
ataque de pérdida ~20-24° (el Tomcat es conocido por mantenerse controlable
a AoA alto gracias al barrido automático del ala). Las maniobras del
catálogo se quedan deliberadamente por debajo de esos números. Desde v4.3 ese
margen ya **no se vigila cortando**: la protección continua va quitando
autoridad de mando antes de llegar al límite de trabajo (±6.0 / −1.5 g) y
anticipa la G del mando que está a punto de darse; el corte solo queda como
backstop estructural (+7.0 / −2.6 g, con permanencia).

**"Volar en crucero" — el botón de reset.** Justo debajo de "Abortar /
manual" hay un botón que no es un Abortar más: "Abortar" suelta los
overrides y devuelve el avión a mando manual tal cual estaba; "Volar en
crucero" en cambio corta cualquier despegue/acción en marcha y **engancha**
el autopiloto de nivelado (la maniobra `LevelWings` del catálogo, misma que
usa la recuperación automática) — vuelo recto y nivelado a potencia de
crucero. Es el "pon el avión a volar normal" entre una acción y la
siguiente, sin tener que soltar el control manual de por medio.

**Los botones dicen lo que va a pasar, no apagan la acción.** Cada
`ManeuverDefinition` sigue llevando un envolvente de entrada
(`MinIasKt`/`MaxIasKt`/`MinAglFt`, en `Maneuvers.cs`), pero desde v4.3 eso ya
no es un interruptor: `ManeuverPlanner.Preview()` dice, diez veces por
segundo, si la maniobra sale tal cual, sale **adaptada** (y en qué: alabeo
recortado, tirón más suave, arco más abierto) o no existe de ninguna forma
segura. El botón solo se deshabilita en ese tercer caso; en el segundo se
muestra en cursiva con el motivo en el tooltip, y al pulsarlo la explicación
queda también en el log.

**Exclusión mutua con el despegue.** `TakeoffSequence` y `ManeuverSequence`
tocan los mismos overrides de `AircraftControls`, así que no pueden correr
las dos a la vez: el shell aborta la que esté activa antes de arrancar la
otra (`ShellWindow.xaml.cs`, `OnStartClick`/`OnManeuverClick`), y "Abortar /
manual" corta cualquiera de las dos.

**Lo que quedaba pendiente de esta primera versión** — el backstop de G como
única protección, las acrobacias de lazo abierto y la falta de noción de
energía antes de permitirlas — es exactamente lo que resuelve "Novedades
v4.3", justo arriba.

## Novedades v4.1 — la UI deja de ser overlays y pasa a ser un shell

Hasta ahora el core eran dos ventanas flotantes con `Topmost="True"`
(telemetría y log) puestas encima de X-Plane. Funcionaba, pero el reparto
era el contrario del que interesa: el simulador ocupaba toda la pantalla y
la información se le pegaba por encima, tapándola.

Ahora hay **una sola ventana**,
[`src/core/Ui/ShellWindow.xaml`](src/core/Ui/ShellWindow.xaml), con la
disposición de un editor de código:

```
┌─ AI Copilot ───────────────────── build N ─ ─ □ ✕ ┐
│ Iniciar despegue │ Abortar │ Anclar X-Plane │ ... │  barra de herramientas
├──┬────────────┬──────────────────────┬────────────┤
│⚙ │ PARÁMETROS │  X-Plane 12          │ TELEMETRÍA │
│▤ │  Vr, pitch │   (su ventana,       │  fase      │
│▥ │  altitudes │    encajada aquí)    │  IAS / V/S │
│  │  acciones  ├──────────────────────┤  actitud   │
│  │            │  LOG DE ACCIONES     │  mandos    │
├──┴────────────┴──────────────────────┴────────────┤
│ Conectado a ...        X-Plane: anclado · build N │  barra de estado
└───────────────────────────────────────────────────┘
```

El hueco central es un `Border` vacío. Quien pone ahí el simulador es
[`src/core/Ui/XPlaneDocker.cs`](src/core/Ui/XPlaneDocker.cs), que traduce
el rectángulo en pantalla de ese `Border` a un `SetWindowPos` sobre la
ventana de X-Plane, le quita la barra de título para que no se vea la
costura, y la reinserta justo por encima del shell cada vez que algo la
manda detrás. Mover la ventana, maximizarla, arrastrar un separador o
plegar un panel mueven el simulador con ellos; minimizar uno minimiza el
otro.

**Por qué no se reparenta con `SetParent`.** Sería la forma obvia de
"meter" una ventana dentro de otra, y da recorte perfecto, pero cuando dos
procesos quedan en relación padre/hijo Windows engancha sus colas de
entrada y las dos interfaces pasan a bloquearse la una a la otra: un tirón
repintando el shell congelaría el simulador, y un frame largo del
simulador congelaría el shell. Con un sim a 60 fps eso no es un riesgo
teórico. Por lo mismo, todas las llamadas del camino normal usan
`SWP_ASYNCWINDOWPOS`/`ShowWindowAsync`: encolan la petición en el hilo de
X-Plane y vuelven al instante, en vez de esperar a que el simulador
atienda su cola.

Consecuencia práctica: **X-Plane tiene que estar en modo ventana**, no en
pantalla completa. Si algo se tuerce, el interruptor "Anclar X-Plane" de
la barra de herramientas lo suelta y le devuelve su posición y su marco
originales — y cerrar el core hace lo mismo.

**El apilado solo se puede mantener bajando el shell, nunca subiendo
X-Plane.** Windows no permite a un proceso colocar una ventana por encima de
la ventana *activa*: medido con el core parado y otra aplicación en primer
plano, ni `SetWindowPos(xplane, esaVentana, …)` ni `HWND_TOP` mueven nada —
las dos llamadas devuelven éxito y el orden Z se queda igual. Así que en
cuanto pulsas un panel y el shell pasa a ser la ventana activa, subir
X-Plane por encima de él es imposible, y la única operación que siempre
funciona es bajar la ventana propia. Que el shell quede debajo no le quita
el foco: activa y encima son cosas distintas, y los paneles siguen
visibles y utilizables porque están fuera del rectángulo del simulador.

**El hueco central no puede ser menor de 1280x720.** Ese es el tamaño
mínimo que X-Plane impone a su propia ventana, y lo aplica él al procesar
el `SetWindowPos`: se le puede pedir menos, pero lo que queda es más
grande que el hueco y se derrama sobre los paneles. Por eso el shell
arranca maximizado y los paneles vienen a 250 / 290 / 170 px — con eso, en
1920x1080, el hueco queda en 1322x731. Si lo estrechas por debajo del
mínimo, el docker deja de insistir y lo dice en el log en vez de pelearse
cuatro veces por segundo con una petición que X-Plane no va a cumplir.

**Cómo se ejecuta.** El core no lo lanza el plugin: se abre a mano el
`AICopilotCore.exe` que `build_and_deploy.ps1` deja junto al `.xpl`, en
`Resources\plugins\AICopilot\win_x64\`.

## Novedades v4.0 — el addon se parte en dos

Hasta la v3.0 el plugin **era** la aplicación: la máquina de estados de
despegue (646 líneas), los PID y la lista de datarefs vivían dentro del
`.xpl`, y el overlay en C# era una pantalla tonta que pintaba paquetes UDP
de texto con los campos ya formateados desde C++. Cambiar una fase obligaba
a tocar el C++, el formato del paquete y el C# a la vez, y a recompilar y
recargar el plugin.

Ahora el reparto es el de la tabla de arriba. Lo que eso cambia de verdad:

- **El connector no tiene ni una ruta de dataref escrita a mano.** El core
  las declara en [`src/core/Connector/Datarefs.cs`](src/core/Connector/Datarefs.cs)
  y las registra con `DEFINE`, que devuelve un `u16` id; a partir de ahí
  todo el tráfico por frame usa solo ids, sin resolver strings.
- **Suscripciones en vez de peticiones.** El core dice una vez qué quiere y
  cada cuántos frames, y la telemetría le llega sola. Con
  petición-respuesta cada lectura costaría un frame de ida y otro de vuelta,
  y con los PID fuera del plugin eso sería retraso metido directamente
  dentro del lazo de control.
- **Primitivas de actuación en el connector:** `HOLD` (reescribe un valor
  cada frame), `TOGGLE` y `PULSE`. Están para que el core no tenga que
  mandar tráfico por frame para cosas que son constantes durante minutos,
  como los overrides.
- **Un watchdog de seguridad que antes no hacía falta.** Ver la sección
  siguiente: es el punto más importante de todo el rediseño.

### Por qué el connector tiene que soltar los overrides él solo

Mientras la lógica vivía dentro del plugin, "el que decide" y "el que
escribe en los datarefs" eran el mismo proceso: si moría uno moría el otro,
y X-Plane llamaba a `XPluginDisable`, donde se soltaba todo.

Ahora quien decide es otro proceso, que se puede cerrar, colgar, quedarse en
un breakpoint o petar a mitad de la rotación. Si eso pasa con los overrides
de cabeceo/alabeo/guiñada/motores puestos, X-Plane sigue creyendo que un
plugin tiene el control exclusivo de esos ejes y **el avión queda imposible
de pilotar a mano** — no es que se pilote mal: los mandos del usuario dejan
de tener efecto, y no hay forma de recuperarlo sin reiniciar el simulador.

Por eso [`src/connector/SafetyGuard.h`](src/connector/SafetyGuard.h) no
confía en que el core se despida bien, y suelta todo ante cualquiera de
estas cuatro cosas:

1. El pipe se rompe (el core cerró o murió). El caso limpio.
2. El core sigue conectado pero lleva 1 s sin decir nada — cubre el core
   colgado o en un breakpoint, donde el socket sigue abierto y el caso 1 no
   salta. De ahí que el core mande un `PING` cada 250 ms.
3. X-Plane nos desactiva (`XPluginDisable`).
4. Cambia la situación: choque, aeropuerto nuevo, avión recargado.

Y "soltar" significa **restaurar el valor que el dataref tenía antes** del
hold, no solo dejar de reescribirlo: si `override_joystick_pitch` se quedara
en 1 porque nadie escribe el 0, el problema seguiría exactamente igual. Ver
el comentario de cabecera de [`src/connector/Holds.h`](src/connector/Holds.h).

## Novedades v2.1

Reportaste dos problemas gordos jugando con la v2.0: el throttle "saltaba
de 0 a 100% sin parar" y, al rotar, bajaba solo a un ~30% sin que nadie se
lo pidiera; y en general los movimientos eran bruscos, no como los haría
una persona. Las dos causas:

1. **El throttle nunca tenía el control en exclusiva.** Escribíamos
   `sim/cockpit2/engine/actuators/throttle_ratio_all` como un dataref
   normal, sin activar ningún "override". Ese mismo dataref también lo
   escribe cada frame el eje de gases normal (tu mando/teclado, o el
   propio avión), así que cada frame había una pelea por quién manda ahí
   — eso es exactamente lo que se veía como el salto 0-100% y el "30%"
   caído del cielo (era la posición de tu eje físico, no algo que
   pusiera el plugin). Solución: ahora se activa
   `sim/operation/override/override_throttles` y se escribe en
   `sim/flightmodel/engine/ENGN_thro_use`, que es la forma que el propio
   SDK documenta para tener el control exclusivo de motores — nada más
   puede pelear por ese dataref mientras el override está activo.
2. **Nada suavizaba los movimientos.** Un PID, en cuanto ve un error
   grande (por ejemplo, el instante en que arranca la secuencia), pide de
   golpe el máximo. Ahora hay un `SlewLimiter` (hoy
   `src/core/Domain/SlewLimiter.cs`) en cada
   eje — throttle, cabeceo, alabeo, guiñada — que fuerza a que el valor
   real avance hacia el objetivo a un ritmo máximo por segundo, así que
   todo se mueve de forma progresiva, como lo haría un piloto moviendo
   una maneta o un yugo, se pida lo que se pida.

De paso arreglé un bug relacionado: el throttle solo se
"pedía" una vez, justo en el instante de cada cambio de fase; combinado
con el suavizado, eso lo dejaba a medio camino para siempre en vez de
terminar de llegar al objetivo. Ahora se pide todos los frames según la
fase actual (`ThrottleTargetForPhase()`), así el `SlewLimiter` tiene
ocasión real de completar la rampa.

**Sistema de log.** Todo lo que el plugin registra (cambios de fase,
avisos de watchdog, arranques/paradas) pasa ahora por `Logger.h` (hoy
`src/connector/Logger.h`, solo para el lado C++; los avisos del core se ven
en su ventana de log), que
escribe a la vez en `Log.txt` de X-Plane y en un fichero propio,
`AICopilot_log.txt`, guardado junto al `.xpl`
(`Resources/plugins/AICopilot/win_x64/AICopilot_log.txt`). Tenerlo aparte
hace mucho más fácil encontrar qué ha pasado en una sesión, sin bucear
entre miles de líneas de otros plugins.

## Novedades v2.0

La versión anterior (sin número, la "v1" de facto) tenía tres problemas
que esta corrige:

1. **El avión no mantenía el centro de la pista.** No había ningún
   control de rumbo mientras el avión rodaba por tierra — ahora hay un
   PID de guiñada (timón/rueda de morro) que mantiene el rumbo de pista
   desde que arranca la secuencia hasta que rota.
2. **Las fases avanzaban por cronómetro, no por objetivo cumplido.** Por
   ejemplo, "meter gas" avanzaba a la siguiente fase cuando pasaban 4
   segundos, sin comprobar si el motor había respondido de verdad. Si
   algo bloqueaba ese input (ver más abajo), el plugin seguía adelante
   igualmente y luego se quedaba esperando una velocidad de rotación que
   nunca iba a llegar. Ahora cada fase comprueba el dataref real antes de
   avanzar, y si tarda demasiado en cumplirse, se escribe un aviso de
   diagnóstico en `Log.txt` explicando qué mirar (sin abortar solo, para
   que puedas decidir tú).
3. **No había forma de saber, de un vistazo, qué build tenías cargada.**
   Ahora el título de la ventana y la primera línea que el plugin escribe
   en `Log.txt` al arrancar incluyen el número de versión (`v2.0`).
   Súbelo cada vez que cambies el comportamiento para no volver a tener
   dudas de si X-Plane cargó el `.xpl` nuevo o el viejo.

La ventana también ahora muestra un **checklist de etapas** y el par
**objetivo / valor real** de cada variable que controla el plugin, para
poder ver en vivo si el PID está pidiendo algo razonable y si el avión
responde (ver "Uso" más abajo).

**Sobre "no ha despegado":** si el avión no arrancaba a rodar, la causa
más probable no es un bug del guion sino que el avión que estés usando no
respeta `sim/cockpit2/engine/actuators/throttle_ratio_all`. Los aviones
de estudio muy completos (por ejemplo el 737 de Zibo Mod) implementan su
propia lógica de manetas de gas y pueden estar reescribiendo ese dataref
ellos mismos cada frame, así que lo que escribe el plugin se pierde. El
nuevo watchdog de la fase "Aplicando potencia" te avisará de esto por
`Log.txt` si pasa, y en la ventana verás "Throttle obj/real" quedarse con
el real por debajo del objetivo — es la pista definitiva. Con el avión
por defecto de X-Plane (o la mayoría de aviones "normales", no de
estudio) no debería pasar.

## Ya viene compilado

`dist/win_x64/AICopilot.xpl` es un DLL de Windows x64 ya compilado (probado:
exporta correctamente `XPluginStart/Stop/Enable/Disable/ReceiveMessage`).
Puedes instalarlo tal cual sin compilar nada — ver "Instalación" más abajo.

Lo compilé de forma cruzada (Linux → Windows) con Clang/LLVM-MinGW, así que
si en algún momento quieres tocar el código y recompilar, lo normal es
hacerlo con Visual Studio en tu propio PC (ver "Compilar tú mismo").

## Estructura del proyecto

```
AICopilot/
├── AGENTS.md                # reglas para agentes (fuente única)
├── CLAUDE.md                # stub Claude Code → AGENTS.md
├── CMakeLists.txt           # build system (multiplataforma)
├── README.md
├── .claude/skills/xplane-sdk/  # skill Claude: cómo consultar el SDK
├── dist/win_x64/AICopilot.xpl  # binario ya compilado, listo para instalar
├── docs/xplane-sdk/         # espejo local de la doc oficial del SDK
├── devtools/ReloadTrigger/  # plugin auxiliar: reload por UDP (ver su README)
├── src/
│   ├── connector/           # C++  → AICopilot.xpl   (el puente)
│   │   ├── plugin.cpp           # callbacks XPLM + flight loop, cero lógica
│   │   ├── Protocol.h           # contrato de cable (gemelo de Protocol.cs)
│   │   ├── PipeServer.h/.cpp    # named pipe en su propio hilo
│   │   ├── DatarefRegistry.*    # nombre → handle + tipo, cacheado
│   │   ├── Holds.h              # hold / toggle / pulse
│   │   ├── Subscriptions.h      # qué se emite y cada cuántos frames
│   │   └── SafetyGuard.h        # watchdog + release de overrides
│   └── core/                # C#   → AICopilotCore.exe  (lógica + UI)
│       ├── Connector/           # Protocol.cs, Wire.cs, ConnectorClient.cs,
│       │                        #   Datarefs.cs (el catálogo de datarefs)
│       ├── Domain/              # Pid, SlewLimiter, AircraftControls,
│       │                        #   TakeoffSequence, Maneuvers (catálogo)
│       │                        #   + ManeuverSequence (ejecutor),
│       │                        #   Intercept (geometría y posiciones)
│       │                        #   + InterceptSequence (ejecutor)
│       └── Ui/                  # App + tema, ShellWindow (la UI entera),
│                                #   XPlaneDocker + XPlaneWindowWatcher
├── third_party/XPSDK/       # SDK oficial de Laminar (headers + libs)
└── tools/                   # build_and_deploy_ui / _addon / global
```

Tres scripts de build/deploy (UI, addon, global): ver
[`AGENTS.md`](AGENTS.md).

## Para agentes / IA

Reglas operativas (build/deploy, contador de build, consulta del SDK):
[`AGENTS.md`](AGENTS.md). Documentación local del SDK:
[`docs/xplane-sdk/README.md`](docs/xplane-sdk/README.md).

## Instalación en X-Plane 12

1. Copia la carpeta `dist/win_x64` (renombrada a `win_x64`, tal cual está) a:

   ```
   E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\AICopilot.xpl
   ```

   Es decir: crea `Resources\plugins\AICopilot\` y dentro pega la carpeta
   `win_x64` con el `.xpl` adentro.

   Copia `AICopilotCore.exe` a esa **misma carpeta**. Es un `.exe`
   autocontenido (no necesita que tengas .NET instalado) y vive ahí solo
   por comodidad: no es un plugin, X-Plane no lo carga ni lo mira.

2. Arranca X-Plane 12 y carga cualquier avión (probado pensando en el
   B737, pero funciona igual con cualquier otro — no hay nada hardcodeado
   específico del 737 salvo los valores por defecto de Vr/flaps).

3. Abre `AICopilotCore.exe` **a mano** cuando quieras usarlo. El plugin no
   lo lanza solo a propósito: arrancar X-Plane no tiene por qué llenarte el
   escritorio de ventanas. El orden da igual y puedes abrirlo y cerrarlo
   tantas veces como quieras con X-Plane corriendo — el core reintenta
   conectar al pipe en bucle, y el punto verde de su cabecera se enciende
   en cuanto engancha con el plugin.

## Uso

Con `AICopilotCore.exe` abierto (lo abres tú; ver "Ejecutarlo"), en su
ventana principal verás:

- Una línea de **checklist** con las 9 etapas del despegue en formato
  `[x]Prep [x]Potencia [>]Pista [ ]Rotar [ ]Flaps [ ]Subida1 [ ]Viraje
  [ ]Subida2 [ ]Nivel` — `[x]` completada, `[>]` la que está corriendo
  ahora, `[ ]` pendiente. Es justo lo que pediste: ver por qué etapa va
  la secuencia de un vistazo, no solo el nombre largo de la fase actual.
- Telemetría en vivo con el par **objetivo / real** en cada variable que
  el plugin controla: IAS, V/S, altitud, AGL, pitch, bank, rumbo,
  throttle, flaps, freno de parking y tren. Si en algún momento ves que
  el "real" no se mueve hacia el "objetivo" (por ejemplo Throttle
  obj=100% pero real se queda en 25%), ahí tienes la pista de que algo —
  normalmente el propio avión — está peleando con el plugin por ese
  control.

Y cinco campos editables:

- **Vr objetivo (kt)** — velocidad de rotación, por defecto 145.
- **Pitch rotación (deg)** — actitud de morro que mantiene al rotar/subir,
  por defecto 12°.
- **Altitud de viraje (ft)** — a qué altitud (MSL) empieza el viraje de
  salida, por defecto 3000 ft.
- **Cambio de rumbo (deg)** — cuánto gira respecto al rumbo de despegue;
  positivo = derecha, negativo = izquierda. Por defecto 90°.
- **Altitud objetivo (ft)** — a qué altitud (MSL) empieza a nivelar,
  por defecto 10000 ft (para que el "paripe" no tarde una eternidad;
  cámbialo si quieres subir a crucero de verdad). Se ajusta sola para
  quedar siempre por encima de la altitud de viraje.

Botones:

- **Iniciar despegue** — arranca la secuencia desde donde esté el avión
  (recomendado: avión parado en pista, motores en marcha, listo para
  rodar).
- **Abortar / manual** — desactiva el override de cabeceo y alabeo que el
  plugin esté aplicando y vuelve a control 100% manual en el acto
  (throttle y flaps se quedan como estén; puedes tocarlos tú a mano con
  normalidad).

## Cómo ejecutarlo y depurarlo

### Ejecutarlo

Ya está instalado (`Resources/plugins/AICopilot/win_x64/`), así que solo
hace falta:

1. Arranca X-Plane 12, carga un avión y ponlo listo en pista (motores en
   marcha, freno de parking puesto). El plugin arranca con el simulador y
   se queda escuchando en el pipe, sin abrir ninguna ventana.
2. Abre `AICopilotCore.exe` desde
   `Resources\plugins\AICopilot\win_x64\`. Salen sus dos ventanas
   (controles/telemetría y log de acciones) y el punto de la cabecera se
   pone verde al conectar.
3. Si acabas de recompilar y X-Plane ya estaba abierto, no hace falta
   reiniciar el simulador entero: usa el flujo de abajo
   (`tools\build_and_deploy.ps1`).

Cerrar el core no deja nada colgado: el connector ve el pipe roto y suelta
los overrides por su cuenta, así que el avión queda pilotable a mano.

### Actualizar el plugin sin cerrar X-Plane

Copiar el `.xpl` nuevo directamente encima del instalado falla mientras
X-Plane está abierto (`Device or resource busy` / *sharing violation*),
porque Windows bloquea la **escritura** sobre un DLL que sigue mapeado
en memoria. Desactivar el plugin no lo descarga: solo un reload de
plugins o cerrar X-Plane lo hace. X-Plane 12 **no tiene** la ventana
clásica "Plugin Admin"; el reload en este repo se automatiza con
[`devtools/ReloadTrigger`](devtools/ReloadTrigger/README.md).

Lo que sí permite Windows, incluso con el fichero cargado, es
**renombrarlo o borrarlo** (el mismo truco que usan los instaladores
que se autoactualizan):

1. Renombra el `.xpl` instalado (el que sigue en memoria) a un nombre
   temporal.
2. Copia el `.xpl` nuevo con el nombre original.
3. Borra los restos de swaps anteriores.

**Flujo recomendado** — elige según lo tocado (ver [`AGENTS.md`](AGENTS.md)):

```powershell
.\tools\build_and_deploy_ui.ps1      # solo core / UI (bump + lanza UI)
.\tools\build_and_deploy_addon.ps1   # solo connector (reload + lanza UI)
.\tools\build_and_deploy.ps1         # ambos
```

El contador (`tools\BUILD_NUMBER.txt`) solo sube con el script de la UI
(y el global) y se ve en la cabecera de la ventana del core. Con
ReloadTrigger cargado, el script del addon dispara el reload solo; si
no, usa el menú/atajo que tengas configurado (no inventes "Plugin Admin").

### "Debugear" sin necesidad de un debugger — los dos logs

Desde la v4.0 hay **dos sitios distintos donde mirar**, uno por proyecto, y
conviene saber cuál es cuál antes de ponerse a buscar:

| Qué buscas | Dónde |
|---|---|
| Decisiones de la secuencia: cambios de fase, "Flaps → 10%", "Positive rate → Tren arriba", avisos de watchdog | **La ventana de log del core**, en pantalla, en vivo |
| Conexión: pipe abierto, core conectado/desconectado, datarefs que no existen en este avión, "SUELTO TODO" del watchdog de seguridad | `AICopilot_log.txt` (el del plugin) |

Es decir: `AICopilot_log.txt` ya **no** lleva el guion del despegue — eso
vive ahora en el core y se ve en su segunda ventana. El log del plugin se
ha quedado en lo que solo el plugin puede saber, que es el estado del
enlace y de los datarefs.

Todo lo que registra el plugin se escribe a la vez en dos sitios, vía
`src/connector/Logger.h`:

- `E:\X-Plane 12\Log.txt` — el log general de X-Plane, mezclado con el de
  todos los demás plugins.
- `E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\AICopilot_log.txt`
  — un fichero **solo del plugin**, mucho más fácil de leer porque no
  tiene que buscar entre miles de líneas ajenas.

Cada línea lleva hora, nivel (`INFO`/`AVISO`) y el mensaje, por ejemplo:

```
[14:27:31][AICopilot][INFO] AICopilot Connector v4.0 build36 cargado (protocolo v1).
[14:27:31][AICopilot][INFO] PipeServer escuchando en \\.\pipe\AICopilot.v1
[14:27:31][AICopilot][INFO] Core conectado (sesion 1).
[14:26:42][AICopilot][INFO] SUELTO TODO (el core lo pidio): 4 holds restaurados, 0 comandos soltados.
```

Abre `AICopilot_log.txt` con el Bloc de notas (o mejor, con algo que
refresque solo, como `Get-Content -Wait` en PowerShell) mientras vuelas.
Las dos líneas que más dicen:

- `DEFINE id=N 'ruta' NO resuelto` — ese dataref no existe en el avión
  cargado. Si falta uno de los de actitud, la secuencia no puede funcionar.
- `SUELTO TODO (motivo)` — el `SafetyGuard` soltó los overrides, y dice por
  qué. Si aparece sin que hayas pulsado "Abortar", el core se cayó o se
  quedó colgado más de un segundo.

Para seguir el guion del despegue frame a frame, lo que quieres es la
ventana de log del core, o añadir `LogAction(...)` en
`src/core/Domain/TakeoffSequence.cs` — por ejemplo dentro de `HoldPitch` o
`HoldHeading` para ver el error del PID. Ten cuidado de no hacerlo en cada
frame sin más, porque satura la lista rápido; mejor cada X frames o solo
cuando algo cambia de verdad.

### DataRefTool — inspeccionar datarefs en vivo (muy recomendable)

Para verificar que el plugin está escribiendo lo que crees (throttle,
flaps, `yoke_pitch_ratio`, etc.) sin tener que adivinarlo por el
comportamiento del avión, instala el plugin gratuito **DataRefTool** (o su
predecesor DataRefEditor) — búscalo en x-plane.org. Te deja buscar
cualquier dataref por nombre, verlo en vivo, y hasta congelarlo/forzar un
valor a mano, lo cual es oro para depurar plugins de este tipo.

### Depuración "de verdad" con breakpoints (Visual Studio)

El `.xpl` que hay en `dist/` está compilado en modo Release y de forma
cruzada (Linux → Windows), así que no trae símbolos de depuración
utilizables por Visual Studio. Para poder poner breakpoints y hacer step
por el código:

1. Compílalo tú en tu PC en modo **Debug** con Visual Studio (ver
   "Compilar tú mismo" más abajo, pero usando `--config Debug` en vez de
   `Release`), y copia ese `.xpl` a
   `Resources\plugins\AICopilot\win_x64\` en vez del que ya está.
2. Arranca X-Plane 12 normalmente.
3. En Visual Studio: **Debug → Attach to Process...**, busca
   `X-Plane.exe` y adjúntate.
4. Pon un breakpoint donde quieras (por ejemplo, al principio de
   `TakeoffSequence::Update` o dentro de `HoldPitch`) y pulsa "Iniciar
   despegue" en la ventana del plugin — Visual Studio debería pararse ahí
   con las variables inspeccionables con normalidad.

Nota: en modo Debug el PID puede comportarse algo distinto porque vas a
estar parando la ejecución (el `dt` entre frames se dispara si te quedas
parado en un breakpoint mucho rato) — es normal, no es un bug del PID.

## Cómo está hecho el control (para cuando conectes Jev)

> Todo lo de esta sección vive ahora en el **core** (C#), no en el plugin:
> qué datarefs se usan está en
> [`src/core/Connector/Datarefs.cs`](src/core/Connector/Datarefs.cs) y cómo
> se tocan en
> [`src/core/Domain/AircraftControls.cs`](src/core/Domain/AircraftControls.cs).
> El connector solo ejecuta lo que le mandan.

- **Motores**: override real (`sim/operation/override/override_throttles`)
  y escritura en `sim/flightmodel/engine/ENGN_thro_use` (0.0–1.0, el mismo
  valor en los 8 primeros motores). Sin el override, el eje de gases del
  usuario o el FADEC del avión reescriben el dataref cada frame y se pelean
  con nosotros — ver "Novedades v2.0".
- **Flaps**: dataref `sim/cockpit2/controls/flap_handle_request_ratio`
  (0.0–1.0, escribible; el sim anima el movimiento solo). Es el sustituto
  moderno de `flap_ratio`, que X-Plane 12 marca como obsoleto.
- **Freno de parking**: comando `sim/flight_controls/park_brake_release`
  (el dataref clásico `parking_brake_ratio` está marcado "REPLACED" en tu
  propia instalación, así que usamos el comando soportado en su lugar).
- **Tren**: `sim/cockpit2/controls/gear_handle_down` (0/1).
- **Cabeceo, alabeo y guiñada (PID, continuo)**: se activa un "override"
  por eje (`sim/operation/override/override_joystick_pitch` / `..._roll`
  / `..._heading`, los tres confirmados en tu propia instalación) y,
  mientras está activo, cada frame se escribe un valor continuo en
  `sim/joystick/yoke_pitch_ratio` / `yoke_roll_ratio` / `yoke_heading_
  ratio` (-1..1; el propio X-Plane documenta el signo: en pitch, -1 es
  yugo a fondo abajo y +1 a fondo arriba — tirar sube el morro; en roll y
  heading, -1 es izquierda y +1 es derecha). Un PID por eje
  (`Domain/Pid.cs`) calcula ese valor a partir del error entre el objetivo
  y el valor real. Los overrides y los ejes van por `HOLD`, no por `SET`,
  precisamente para que el `SafetyGuard` del connector pueda restaurarlos
  si el core desaparece. Al terminar la secuencia o pulsar "Abortar" se
  sueltan los tres y el control manual vuelve al instante (flaps y freno
  nunca se "overridean": son escritura directa, y el usuario debería poder
  seguir tocándolos).
- **Eje de pista (en tierra)**: desde que arranca la secuencia hasta que
  rota, un PID de guiñada mantiene el rumbo con el que estaba el avión al
  pulsar "Iniciar despegue" (`HoldRunwayHeading` en `TakeoffSequence.cs`),
  escribiendo directamente en el eje de guiñada — no pasa por alabeo,
  porque con el avión en tierra alabear no gira el morro.
- **Viraje de salida (en vuelo)**: es un control en cascada, distinto del
  anterior. El error de rumbo (objetivo − actual, normalizado a
  [-180,180]) se convierte en un ángulo de alabeo objetivo (proporcional,
  limitado a ±25°); el error entre ese alabeo objetivo y el alabeo real
  alimenta el PID de roll. Es la misma técnica que usa cualquier
  autopiloto sencillo de rumbo. El eje de guiñada se suelta al rotar (ya
  no hace falta: la coordinación de viraje la aporta el propio X-Plane).
- **Lectura de variables**: `sim/flightmodel/position/indicated_airspeed`,
  `sim/flightmodel/misc/h_ind` (altitud), `sim/flightmodel/position/y_agl`
  (AGL), `sim/flightmodel/position/vh_ind_fpm` (V/S),
  `sim/flightmodel/position/theta` (pitch), `sim/flightmodel/position/phi`
  (alabeo), `sim/flightmodel/position/psi` (rumbo verdadero).

**Añadir un dataref nuevo** es una línea en `Datarefs.cs`
(`c.Define("ruta/del/dataref")`, más un `c.Subscribe(...)` si quieres
recibirlo por telemetría) y recompilar solo el C#: el plugin no se entera y
no hace falta recargarlo en X-Plane.

El día que enchufes a Jev, lo natural es que la IA llame directamente a
métodos de `AircraftControls` (o que añadas los que hagan falta) en vez de
pasar por `TakeoffSequence`, que es solo el guion fijo de demo. Las dos
clases están en [`src/core/Domain/`](src/core/Domain/), en el mismo proceso
que la IA: sin IPC de por medio.

## Limitaciones a propósito (es una demo, no un FMS)

- El PID de guiñada en tierra usa el eje de guiñada del joystick
  (`yoke_heading_ratio`), que en la mayoría de aviones también mueve el
  timón y/o la rueda de morro a través del modelo de vuelo genérico de
  X-Plane. En aviones de estudio muy completos, puede que la rueda de
  morro tenga su propia lógica (por ejemplo, solo activa por debajo de
  cierta velocidad) y el comportamiento en tierra no sea perfecto — es la
  misma limitación de fondo que con el throttle (ver "Novedades v2.0").
- Las ganancias de los PID (`kPitchKp/Ki/Kd`, `kBankKp/Ki/Kd`,
  `YawKp/Ki/Kd` al final de `TakeoffSequence.cs`) están puestas a ojo para que se
  comporten de forma razonable en un avión tipo 737; en otro avión mucho
  más ligero o pesado puede que oscilen un poco más de la cuenta — son
  constantes al principio del fichero, fáciles de tocar.
- Los umbrales de velocidad de retracción de flaps (160/185 kt) y los
  valores de potencia de ascenso/crucero están fijos en
  `TakeoffSequence.cs` como constantes — cámbialos ahí si quieres afinar
  el comportamiento para el 737 en concreto.
- No hay autothrottle ni gestión de energía tras "Done": el throttle se
  queda en el último valor que puso la secuencia.

## Compilar tú mismo (opcional)

El binario en `dist/` ya funciona, pero si tocas el código:

### Opción A — Visual Studio (recomendado en tu PC con Windows)

Necesitas Visual Studio con el workload "Desarrollo para el escritorio con
C++" (incluye CMake).

```powershell
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

El resultado queda en `build\install\AICopilot\win_x64\AICopilot.xpl`.

### Opción B — línea de comandos con MSVC (Developer Command Prompt)

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
```

### Nota

`third_party/XPSDK` ya trae los headers y las librerías de enlazado
(`XPLM_64.lib`, `XPWidgets_64.lib`) necesarias — no hace falta descargar
nada del SDK de Laminar por separado.
