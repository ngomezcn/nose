namespace AICopilotCore.Connector;

// El catalogo de datarefs del proyecto. Antes estaba repartido entre
// ControlInputs.h y TakeoffSequence.h, dentro del plugin; ahora vive aqui,
// en el core, que es el sitio que le corresponde: QUE datarefs importan es
// una decision de la logica, no del puente. El connector no tiene ni una
// sola ruta escrita a mano.
//
// Añadir un dataref nuevo es una linea aqui y recompilar solo el C#: el
// plugin no se entera, no hay que recargarlo en X-Plane.
public sealed class Datarefs
{
    // Motores: se escribe el mismo valor en los 8 primeros (count: 8), de
    // sobra para cualquier avion civil. Escribir de mas es inocuo: los
    // elementos que sobran son motores que este avion no tiene. (El
    // ArrayLength del DEFINE_ACK NO sirve para saber cuantos hay: estos
    // datarefs son float[16] fijos, asi que X-Plane devuelve 16 tenga el
    // avion dos motores o cuatro.)
    private const int EngineCount = 8;

    // Se guarda para poder CAMBIAR el ritmo de una suscripcion en caliente
    // (ver FocusOtherPlane): que datarefs importan y cada cuanto es una
    // decision de este catalogo, no de quien lo consume.
    private readonly ConnectorClient _c;

    public Datarefs(ConnectorClient c)
    {
        _c = c;

        // --- estado de vuelo: alimenta los PID, hace falta cada frame ----
        IasKt = c.Define("sim/flightmodel/position/indicated_airspeed");
        AltFt = c.Define("sim/flightmodel/misc/h_ind");
        AglMeters = c.Define("sim/flightmodel/position/y_agl");
        VsFpm = c.Define("sim/flightmodel/position/vh_ind_fpm");
        PitchDeg = c.Define("sim/flightmodel/position/theta");
        BankDeg = c.Define("sim/flightmodel/position/phi");
        HeadingDeg = c.Define("sim/flightmodel/position/psi");
        // G normal (perpendicular al eje del avion, la que se siente en el
        // asiento). Hace falta cada frame: es la salvaguarda que corta una
        // maniobra del F-14 (ManeuverSequence) antes de acercarse al limite
        // estructural real (+7.5/-3.0g en el F-14A/B).
        GNormal = c.Define("sim/flightmodel/forces/g_nrml");

        // --- lo que hace falta para ADAPTAR una maniobra, no solo para
        // ejecutarla a ciegas (ver Domain/ManeuverPlanner.cs) -------------
        //
        // El planificador necesita saber en que parte del envolvente esta el
        // avion AHORA para decidir cuanto puede pedirle. Sin estos, todo eso
        // habria que estimarlo: alpha se sacaria de theta menos la
        // trayectoria (mal con alabeo), el Mach de la IAS y una atmosfera de
        // libro (3-4% de error en altura), la velocidad de perdida de un peso
        // supuesto (el F-14 pasa de 40.000 a 74.000 lb segun carga y
        // combustible, y Vs va con la raiz del peso: 30% de diferencia).
        AoaDeg = c.Define("sim/flightmodel/position/alpha");
        Mach = c.Define("sim/flightmodel/misc/machno");
        PitchRateDegPerSec = c.Define("sim/flightmodel/position/Q");
        RollRateDegPerSec = c.Define("sim/flightmodel/position/P");
        TotalWeightKg = c.Define("sim/flightmodel/weight/m_total");
        // VNE del propio .acf del avion cargado: mejor que una constante
        // nuestra, que seria la del F-14 real aunque vuele otro avion.
        VneKias = c.Define("sim/aircraft/view/acf_Vne");
        // Posicion y velocidad propias en el marco local de X-Plane (OpenGL:
        // +X este, +Y arriba, +Z sur, metros). No sirven para pilotar en
        // solitario -- son lo que hace falta para MEDIR contra otro avion
        // (Domain/InterceptSequence.cs). Van en local y no en lat/lon a
        // proposito: los datarefs de posicion de las IAs estan en el MISMO
        // marco, asi que restar dos posiciones da directamente metros al
        // este / arriba / al norte, sin trigonometria esferica ni errores de
        // proyeccion. OJO: X-Plane puede mover el origen de este marco en
        // pleno vuelo; el connector lo compensa (frame: X/Z, ver
        // connector/OriginWatch.h), asi que aqui el marco es estable. Y la Y local, restada entre los dos aviones, da la
        // separacion vertical real sin tener que fiarse de dos altimetros.
        LocalX = c.Define("sim/flightmodel/position/local_x", frame: FrameAxis.X);
        LocalY = c.Define("sim/flightmodel/position/local_y");
        LocalZ = c.Define("sim/flightmodel/position/local_z", frame: FrameAxis.Z);
        LocalVx = c.Define("sim/flightmodel/position/local_vx");
        LocalVz = c.Define("sim/flightmodel/position/local_vz");
        LocalVy = c.Define("sim/flightmodel/position/local_vy");
        // Elevacion MSL en metros (mismo significado que plane_N_el de las IAs).
        ElevMeters = c.Define("sim/flightmodel/position/elevation");

        // En tierra no aplican las leyes de altura/G: rodando, la AGL es 0 y
        // la G normal es la del tren, no la del ala.
        OnGround = c.Define("sim/flightmodel/failures/onground_any");

        // Pausa y velocidad de simulacion. La caja negra deja de muestrear
        // mientras el mundo esta congelado (misma regla que SimDt en el
        // connector: paused != 0, o sim_speed == 0). Cada frame, para no
        // colar una muestra congelada entre el aviso y el refresco de 10 Hz.
        Paused = c.Define("sim/time/paused");
        SimSpeed = c.Define("sim/time/sim_speed");

        // --- mandos que escribimos --------------------------------------
        FlapHandle = c.Define("sim/cockpit2/controls/flap_handle_request_ratio");
        GearHandleDown = c.Define("sim/cockpit2/controls/gear_handle_down");
        // Aerofrenos: la unica forma real de frenar un F-14 en un picado o de
        // perder velocidad sin bajar el morro. Va por Set como los flaps (el
        // simulador anima la superficie y el usuario puede seguir tocandolo);
        // es la palanca, no la superficie -- el dataref de la superficie
        // (flightmodel2) es una lectura de animacion, no un mando.
        SpeedbrakeHandle = c.Define("sim/cockpit2/controls/speedbrake_ratio");
        // Comando y no dataref: el clasico parking_brake_ratio esta marcado
        // "REPLACED" en X-Plane 12.
        ParkBrakeRelease = c.DefineCommand("sim/flight_controls/park_brake_release");
        ParkBrakeSet = c.DefineCommand("sim/flight_controls/park_brake_set");

        // Motores con override real. Escribir throttle_ratio_all sin
        // override no funciona: el eje de gases del usuario (o el FADEC del
        // avion) reescribe ese dataref cada frame y se pelea con nosotros
        // -- eso es lo que se veia como "el throttle salta de 0 a 100% sin
        // parar". Con el override activo, ENGN_thro_use es nuestro en
        // exclusiva.
        OverrideThrottles = c.Define("sim/operation/override/override_throttles");
        EngineThrottleUse = c.Define("sim/flightmodel/engine/ENGN_thro_use",
                                     index: 0, count: EngineCount);

        // La palanca, solo para que se vea. ENGN_thro_use esta al final de
        // la cadena -- es lo que consume el motor -- y no mueve nada en la
        // cabina; la animacion de la palanca y las agujas del panel salen
        // de este otro ("Throttle position of the handle itself", dice
        // DataRefs.txt). Con override_throttles a 1, X-Plane deja ademas de
        // copiar handle -> use, asi que la palanca se quedaria congelada
        // donde la dejo el usuario mientras los motores nos siguen a
        // nosotros. Escribiendo aqui el mismo valor suavizado, la palanca
        // acompaña. Es puramente cosmetico: con el override puesto, este
        // dataref no decide empuje ninguno.
        CockpitThrottleRatio = c.Define("sim/cockpit2/engine/actuators/throttle_ratio",
                                        index: 0, count: EngineCount);

        // Cabeceo/alabeo/guiñada continuos, tambien por override del SDK.
        // Signos documentados por X-Plane: en pitch, -1 es yugo a fondo
        // abajo y +1 a fondo arriba (tirar sube el morro); en roll y
        // heading, -1 izquierda y +1 derecha.
        OverridePitch = c.Define("sim/operation/override/override_joystick_pitch");
        OverrideRoll = c.Define("sim/operation/override/override_joystick_roll");
        OverrideYaw = c.Define("sim/operation/override/override_joystick_heading");
        YokePitch = c.Define("sim/joystick/yoke_pitch_ratio");
        YokeRoll = c.Define("sim/joystick/yoke_roll_ratio");
        YokeHeading = c.Define("sim/joystick/yoke_heading_ratio");

        // Recuadro del mouse yoke: X-Plane lo dibuja (y deja que el raton
        // mueva los mandos con el) cuando cree que no hay yoke fisico
        // conectado. Poniendo este dataref a 1 le hacemos creer que SI lo
        // hay -- desaparece el recuadro Y de paso deja de aceptar clics de
        // raton sobre el yoke, que es justo lo que queremos con la IA
        // volando: ni la molestia visual ni el riesgo de tocar un mando sin
        // querer. A 0 vuelve al comportamiento normal de X-Plane.
        MouseYokeBoxHidden = c.Define("sim/joystick/eq_pfc_yoke");

        // --- lecturas de confirmacion y de pantalla ----------------------
        FlapReadback = FlapHandle;
        ParkBrakeReadback = c.Define("sim/flightmodel/controls/parkbrake");
        QnhPas = c.Define("sim/weather/aircraft/barometer_current_pas");
        BarometerPilot = c.Define("sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot");
        BarometerCopilot = c.Define("sim/cockpit2/gauges/actuators/barometer_setting_in_hg_copilot");

        // --- suscripciones ----------------------------------------------
        // Divisor 1 para lo que entra en los PID o decide una transicion de
        // fase: ahi un dato viejo se traduce en un mando mal puesto.
        // Alpha/Mach/Q/P entran aqui por el mismo motivo: el planificador de
        // maniobras decide con ellos el mando de este frame.
        foreach (DataHandle h in new[] { IasKt, AltFt, AglMeters, VsFpm,
                                         PitchDeg, BankDeg, HeadingDeg,
                                         GNormal, EngineThrottleUse,
                                         AoaDeg, Mach, PitchRateDegPerSec,
                                         RollRateDegPerSec,
                                         LocalX, LocalY, LocalZ, LocalVx, LocalVz,
                                         LocalVy, ElevMeters })
        {
            c.Subscribe(h, 1);
        }

        // El peso cambia despacio (consumo de combustible) y la VNE del avion
        // cargado no cambia nunca; con una vez cada medio segundo sobra.
        c.Subscribe(TotalWeightKg, 30);
        c.Subscribe(VneKias, 30);
        c.Subscribe(OnGround, 6);
        c.Subscribe(Paused, 1);
        c.Subscribe(SimSpeed, 1);
        c.Subscribe(SpeedbrakeHandle, 6);

        // El resto es para pintar en pantalla: a 10 Hz nadie nota la
        // diferencia y no tiene sentido mandarlo 60 veces por segundo.
        c.Subscribe(FlapHandle, 6);
        c.Subscribe(ParkBrakeReadback, 6);
        c.Subscribe(GearHandleDown, 6);

        // El QNH y la subescala del altimetro cambian cada muchos segundos.
        c.Subscribe(QnhPas, 30);
        c.Subscribe(BarometerPilot, 30);

        // Otras IAs. X-Plane numera el avion del usuario como indice 0
        // (XPLM_USER_AIRCRAFT) y no lo mete en estos datarefs: plane1 es la
        // primera IA, plane19 la ultima. El nombre de cada una no esta aqui
        // (llega por Op.Planes); esto es solo para pintar GS / MSL / rumbo
        // al lado. A 10 Hz, igual que el resto de la pantalla.
        //
        // Posicion local (x/y/z) ademas de la elevacion: es lo que convierte
        // "hay un avion por ahi" en "esta a 2.140 m, 30 a la izquierda y 180
        // por encima", que es lo unico con lo que se puede interceptar. v_y y
        // el tren completan lo que hace falta para saber si esta volando o
        // sigue en tierra (ver TargetSnapshot en Domain/Intercept.cs).
        OtherElevMeters = new DataHandle[OtherPlaneSlots];
        OtherHeadingDeg = new DataHandle[OtherPlaneSlots];
        OtherPitchDeg = new DataHandle[OtherPlaneSlots];
        OtherBankDeg = new DataHandle[OtherPlaneSlots];
        OtherVelX = new DataHandle[OtherPlaneSlots];
        OtherVelZ = new DataHandle[OtherPlaneSlots];
        OtherLocalX = new DataHandle[OtherPlaneSlots];
        OtherLocalY = new DataHandle[OtherPlaneSlots];
        OtherLocalZ = new DataHandle[OtherPlaneSlots];
        OtherVelY = new DataHandle[OtherPlaneSlots];
        OtherGearRatio = new DataHandle[OtherPlaneSlots];
        for (int i = 0; i < OtherPlaneSlots; i++)
        {
            string prefix = $"sim/multiplayer/position/plane{i + 1}_";
            OtherElevMeters[i] = c.Define(prefix + "el");
            OtherHeadingDeg[i] = c.Define(prefix + "psi");
            OtherPitchDeg[i] = c.Define(prefix + "the");
            OtherBankDeg[i] = c.Define(prefix + "phi");
            OtherVelX[i] = c.Define(prefix + "v_x");
            OtherVelZ[i] = c.Define(prefix + "v_z");
            OtherLocalX[i] = c.Define(prefix + "x", frame: FrameAxis.X);
            OtherLocalY[i] = c.Define(prefix + "y");
            OtherLocalZ[i] = c.Define(prefix + "z", frame: FrameAxis.Z);
            OtherVelY[i] = c.Define(prefix + "v_y");
            // gear_deploy es float[10] (una entrada por tren): con la primera
            // basta para saber si lo lleva fuera.
            OtherGearRatio[i] = c.Define(prefix + "gear_deploy", index: 0, count: 1);
            foreach (DataHandle h in OtherPlaneHandles(i)) c.Subscribe(h, OtherPlaneIdleDivisor);
        }
    }

    // Los once datarefs de una IA, juntos: se suscriben y se re-suscriben
    // siempre como un bloque, asi que enumerarlos en un sitio evita que
    // FocusOtherPlane se olvide de uno y deje el blanco medio actualizado.
    private IEnumerable<DataHandle> OtherPlaneHandles(int slot)
    {
        yield return OtherElevMeters[slot];
        yield return OtherHeadingDeg[slot];
        yield return OtherPitchDeg[slot];
        yield return OtherBankDeg[slot];
        yield return OtherVelX[slot];
        yield return OtherVelZ[slot];
        yield return OtherLocalX[slot];
        yield return OtherLocalY[slot];
        yield return OtherLocalZ[slot];
        yield return OtherVelY[slot];
        yield return OtherGearRatio[slot];
    }

    // Sube a ritmo de frame los datarefs de UNA IA y devuelve la anterior a
    // 10 Hz. Lo pide la interceptacion: la posicion del blanco entra en un
    // PID igual que la actitud propia, y con datos de hace 100 ms el avion
    // persigue donde estaba el otro, no donde esta -- se nota como un
    // bamboleo al mantener la formacion. Subir las 19 IAs a 60 Hz seria
    // multiplicar por seis el trafico del pipe para tener al dia 18 aviones
    // que no se estan persiguiendo; se sube solo el que importa.
    //
    // slot < 0 = ninguna enfocada (todas vuelven al ritmo de pantalla).
    public void FocusOtherPlane(int slot)
    {
        if (slot >= OtherPlaneSlots) slot = -1;
        if (slot < 0) slot = -1;
        if (slot == _focusedSlot) return;

        if (_focusedSlot >= 0) UnwatchPlane(_focusedSlot);
        _focusedSlot = slot;
        if (slot >= 0) WatchPlane(slot);
    }

    private int _focusedSlot = -1;
    public int FocusedOtherPlane => _focusedSlot;

    // Conjunto de IAs a 60 Hz con refcount: cada Watch suma, cada Unwatch
    // resta; la IA vuelve al ritmo de pantalla cuando el contador llega a 0.
    private readonly int[] _watchCount = new int[OtherPlaneSlots];

    public void WatchPlane(int slot)
    {
        if (slot < 0 || slot >= OtherPlaneSlots) return;
        if (_watchCount[slot]++ == 0)
            foreach (DataHandle h in OtherPlaneHandles(slot)) _c.Subscribe(h, 1);
    }

    public void UnwatchPlane(int slot)
    {
        if (slot < 0 || slot >= OtherPlaneSlots || _watchCount[slot] == 0) return;
        if (--_watchCount[slot] == 0)
            foreach (DataHandle h in OtherPlaneHandles(slot)) _c.Subscribe(h, OtherPlaneIdleDivisor);
    }

    public bool IsWatched(int slot) => slot >= 0 && slot < OtherPlaneSlots && _watchCount[slot] > 0;

    // Ritmo de las IAs cuando solo se estan pintando en pantalla: uno de cada
    // 6 frames (~10 Hz a 60 fps), igual que el resto de la telemetria de UI.
    private const byte OtherPlaneIdleDivisor = 6;

    // Slots de IA que expone X-Plane en sim/multiplayer/position/plane1..19.
    public const int OtherPlaneSlots = 19;

    public DataHandle IasKt { get; }
    public DataHandle AltFt { get; }
    public DataHandle AglMeters { get; }
    public DataHandle VsFpm { get; }
    public DataHandle PitchDeg { get; }
    public DataHandle BankDeg { get; }
    public DataHandle HeadingDeg { get; }
    public DataHandle GNormal { get; }

    public DataHandle AoaDeg { get; }
    public DataHandle Mach { get; }
    public DataHandle PitchRateDegPerSec { get; }
    public DataHandle RollRateDegPerSec { get; }
    public DataHandle TotalWeightKg { get; }
    public DataHandle VneKias { get; }
    public DataHandle OnGround { get; }

    public DataHandle Paused { get; }
    public DataHandle SimSpeed { get; }

    // Mundo congelado: pausa, o tiempo de simulacion a 0. Sin lectura aun
    // se considera en marcha: un dataref que no ha llegado no debe silenciar
    // la caja negra.
    public bool IsSimFrozen =>
        (Paused.HasValue && Paused.Bool) ||
        (SimSpeed.HasValue && SimSpeed.Float == 0f);

    public DataHandle LocalX { get; }
    public DataHandle LocalY { get; }
    public DataHandle LocalZ { get; }
    public DataHandle LocalVx { get; }
    public DataHandle LocalVz { get; }
    public DataHandle LocalVy { get; }
    public DataHandle ElevMeters { get; }

    public DataHandle FlapHandle { get; }
    public DataHandle SpeedbrakeHandle { get; }
    public DataHandle GearHandleDown { get; }
    public DataHandle ParkBrakeRelease { get; }
    public DataHandle ParkBrakeSet { get; }

    public DataHandle OverrideThrottles { get; }
    public DataHandle EngineThrottleUse { get; }
    public DataHandle CockpitThrottleRatio { get; }

    public DataHandle OverridePitch { get; }
    public DataHandle OverrideRoll { get; }
    public DataHandle OverrideYaw { get; }
    public DataHandle YokePitch { get; }
    public DataHandle YokeRoll { get; }
    public DataHandle YokeHeading { get; }
    public DataHandle MouseYokeBoxHidden { get; }

    public DataHandle FlapReadback { get; }
    public DataHandle ParkBrakeReadback { get; }
    public DataHandle QnhPas { get; }
    public DataHandle BarometerPilot { get; }
    public DataHandle BarometerCopilot { get; }

    // Indice 0 de estos arrays = plane1 = primera IA (XPLM index 1).
    public DataHandle[] OtherElevMeters { get; }
    public DataHandle[] OtherHeadingDeg { get; }
    public DataHandle[] OtherPitchDeg { get; }
    public DataHandle[] OtherBankDeg { get; }
    public DataHandle[] OtherVelX { get; }
    public DataHandle[] OtherVelZ { get; }
    // Mismo marco local (OGL) que LocalX/Y/Z: restarlos da metros directos.
    public DataHandle[] OtherLocalX { get; }
    public DataHandle[] OtherLocalY { get; }
    public DataHandle[] OtherLocalZ { get; }
    public DataHandle[] OtherVelY { get; }
    public DataHandle[] OtherGearRatio { get; }
}
