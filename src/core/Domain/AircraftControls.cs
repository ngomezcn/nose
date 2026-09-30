using AICopilotCore.Connector;

namespace AICopilotCore.Domain;

// Todo lo que hace falta para "hacer input" sobre el avion: motores, flaps,
// freno de parking, tren y cabeceo/alabeo/guiñada continuos. Puerto de
// src/ControlInputs.h, con la misma API publica; lo unico que cambia es que
// debajo ya no hay XPLMSetDataf sino ordenes al connector.
//
// -- Hold, no Set, para todo lo que tiene que quedarse puesto -------------
//
// Los overrides y los ejes de mando van por Hold (el connector reescribe el
// valor cada frame y, sobre todo, RESTAURA el valor previo si suelta). Eso
// es lo que hace que matar este proceso a mitad de una rotacion deje el
// avion pilotable: el SafetyGuard del plugin suelta los holds y los
// overrides vuelven solos a 0. Con Set no habria red ninguna -- el
// override se quedaria a 1 para siempre y el avion no respondería a los
// mandos del usuario nunca mas.
//
// Los flaps y el tren van por Set a proposito: el simulador anima el
// movimiento por su cuenta y el usuario deberia poder seguir tocandolos.
// La palanca de gases de la cabina tambien va por Set, pero por un motivo
// distinto -- ver SetThrottle.
public sealed class AircraftControls
{
    private readonly ConnectorClient _c;
    private readonly Datarefs _d;
    private readonly ControlTuning _tuning;

    // El valor inicial del SlewLimiter no importa mucho -- se reescribe con
    // MaxRate desde _tuning en cada Set*Input/SetThrottle, asi que si el
    // usuario cambia la rampa en la pestana de Config el efecto se nota en
    // el proximo frame, no solo en la proxima reconexion.
    private readonly SlewLimiter _throttleSlew = new(1.5f);
    private readonly SlewLimiter _pitchSlew = new(3.5f);
    private readonly SlewLimiter _rollSlew = new(3.5f);
    private readonly SlewLimiter _yawSlew = new(3.5f);

    // Handles con hold puesto por ESTA instancia, en orden de creacion. Permite
    // soltar solo lo propio (en orden inverso) sin el ReleaseAll global del
    // connector, que tambien soltaria los holds cinematicos de las IAs.
    private readonly List<DataHandle> _held = new();

    private void Hold(DataHandle h, double value)
    {
        if (!_held.Contains(h)) _held.Add(h);
        _c.Hold(h, value);
    }

    private void ReleaseTracked(DataHandle h)
    {
        if (_held.Remove(h)) _c.Release(h);
    }

    private bool _throttleOverride;
    private bool _pitchOverride;
    private bool _rollOverride;
    private bool _yawOverride;

    public AircraftControls(ConnectorClient client, Datarefs datarefs, ControlTuning tuning)
    {
        _c = client;
        _d = datarefs;
        _tuning = tuning;
    }

    // --- Motores (override real, 0.0 ralenti .. 1.0 maxima potencia) -----

    public void EnableThrottleOverride()
    {
        if (_throttleOverride) return;
        Hold(_d.OverrideThrottles, 1);
        _throttleOverride = true;
        _throttleSlew.Reset(ThrottleReadback);
    }

    // Aplicado en Set + Set*Input y no una vez al arrancar: es la unica
    // manera de que un cambio de rampa en la pestana de Config se note en
    // caliente, con la maniobra/despegue ya en marcha.

    public void DisableThrottleOverride()
    {
        if (!_throttleOverride) return;
        ReleaseTracked(_d.EngineThrottleUse);
        ReleaseTracked(_d.OverrideThrottles);
        _throttleOverride = false;
    }

    // target: 0.0..1.0, suavizado antes de escribirse.
    public void SetThrottle(float target, float dt)
    {
        EnableThrottleOverride();
        _throttleSlew.MaxRate = _tuning.ThrottleSlewRate;
        float smoothed = _throttleSlew.Update(target, dt);
        Hold(_d.EngineThrottleUse, smoothed);

        // Y la palanca de la cabina detras, para que se vea moverse. Va por
        // Set y no por Hold a proposito, aunque se escriba igual de a
        // menudo: soltar un hold RESTAURA el valor previo, y este dataref
        // deja de ser cosmetico en el instante en que cae el override --
        // X-Plane vuelve a mandar el empuje desde la posicion de la palanca.
        // Con Hold, soltar en pleno ascenso devolveria la palanca al ralenti
        // con el que empezo y los motores detras; con Set se queda donde la
        // dejamos, que es lo que hace un autothrottle real al desconectar y
        // lo que deja el avion volando si el core se muere a mitad.
        _c.Set(_d.CockpitThrottleRatio, smoothed);
    }

    // Valor real leido del dataref que controlamos en exclusiva. Llega por
    // telemetria, leida por el connector DESPUES de aplicar los holds, asi
    // que refleja lo que de verdad hay escrito.
    public float ThrottleReadback => _d.EngineThrottleUse.Float;

    // --- Flaps ------------------------------------------------------------
    // 0.0 (arriba) .. 1.0 (completos). El simulador anima el movimiento.
    public void SetFlaps(float value) => _c.Set(_d.FlapHandle, value);

    // --- Aerofrenos ---------------------------------------------------------
    // 0.0 (dentro) .. 1.0 (fuera del todo). Por Set, como los flaps: el
    // simulador anima la superficie y el usuario puede seguir tocandola. Es la
    // herramienta que le faltaba al planificador para frenar sin cambiar la
    // actitud -- en un picado o al pedir "Frenar", bajar el morro o tirar de
    // gases no basta, y sin esto la unica salida era recortar la maniobra.
    // Valores negativos (-0.5 = ARMADO) no se usan: aqui siempre es un mando
    // explicito.
    //
    // Solo se escribe cuando cambia de verdad: lo llama el lazo de maniobras
    // en cada frame, y mandar el mismo valor sesenta veces por segundo llenaria
    // el pipe de mensajes identicos (el resto de mandos continuos van por Hold,
    // que el connector reescribe solo; este va por Set como los flaps).
    private float _lastSpeedbrake = float.NaN;
    public void SetSpeedbrake(float ratio01)
    {
        float value = Math.Clamp(ratio01, 0f, 1f);
        if (!float.IsNaN(_lastSpeedbrake) && MathF.Abs(value - _lastSpeedbrake) < 0.02f) return;
        _lastSpeedbrake = value;
        _c.Set(_d.SpeedbrakeHandle, value);
    }

    // --- Freno de parking --------------------------------------------------
    public void ReleaseParkingBrake() => _c.Command(_d.ParkBrakeRelease);
    public void SetParkingBrake() => _c.Command(_d.ParkBrakeSet);

    // Config de suelo / idle tras un reset de escenario: gases a ralenti,
    // flaps abajo, freno de parking, tren abajo, aerofrenos dentro. Los
    // overrides de yoke se dejan sueltos (AbortAll/ReleaseAll ya los quito).
    public void ApplyGroundIdle()
    {
        EnableThrottleOverride();
        _throttleSlew.Reset(0f);
        Hold(_d.EngineThrottleUse, 0.0);
        _c.Set(_d.CockpitThrottleRatio, 0.0);

        SetFlaps(1f);
        _lastSpeedbrake = float.NaN;
        SetSpeedbrake(0f);
        SetGearDown(true);
        SetParkingBrake();
        // Por si el comando no pilla en este avion, el dataref de lectura
        // sigue siendo escribible en XP12 y deja el freno puesto.
        _c.Set(_d.ParkBrakeReadback, 1.0);
    }

    // --- Tren --------------------------------------------------------------
    public void SetGearDown(bool down) => _c.Set(_d.GearHandleDown, down ? 1 : 0);

    // --- Subescala del altimetro (inHg), piloto y copiloto ----------------
    // Los dos en un solo mensaje: son el mismo gesto y asi no pueden quedar
    // descuadrados si la conexion se corta justo entre uno y otro.
    public void SetBarometer(float inHg) =>
        _c.SetMany((_d.BarometerPilot, inHg), (_d.BarometerCopilot, inHg));

    // --- Cabeceo (continuo, -1..1) ----------------------------------------

    public void EnablePitchOverride()
    {
        if (_pitchOverride) return;
        Hold(_d.OverridePitch, 1);
        _pitchOverride = true;
        _pitchSlew.Reset(0f);
    }

    public void DisablePitchOverride()
    {
        if (!_pitchOverride) return;
        ReleaseTracked(_d.YokePitch);
        ReleaseTracked(_d.OverridePitch);
        _pitchOverride = false;
    }

    public void SetPitchInput(float target, float dt)
    {
        _pitchSlew.MaxRate = _tuning.PitchSlewRate;
        Hold(_d.YokePitch, _pitchSlew.Update(target, dt));
    }

    // Valor realmente mantenido en el eje (post-rampa, lo ultimo que se
    // escribio de verdad en el Hold). Para el log de datos: distingue si lo
    // que oscila es el objetivo que persigue un PID o el propio mando que
    // llega al simulador.
    public float PitchInputCmd => _pitchSlew.Value;

    // --- Alabeo (continuo, -1..1) -----------------------------------------

    public void EnableRollOverride()
    {
        if (_rollOverride) return;
        Hold(_d.OverrideRoll, 1);
        _rollOverride = true;
        _rollSlew.Reset(0f);
    }

    public void DisableRollOverride()
    {
        if (!_rollOverride) return;
        ReleaseTracked(_d.YokeRoll);
        ReleaseTracked(_d.OverrideRoll);
        _rollOverride = false;
    }

    public void SetRollInput(float target, float dt)
    {
        _rollSlew.MaxRate = _tuning.RollSlewRate;
        Hold(_d.YokeRoll, _rollSlew.Update(target, dt));
    }

    public float RollInputCmd => _rollSlew.Value;

    // --- Guiñada / rueda de morro en tierra (continuo, -1..1) -------------

    public void EnableYawOverride()
    {
        if (_yawOverride) return;
        Hold(_d.OverrideYaw, 1);
        _yawOverride = true;
        _yawSlew.Reset(0f);
    }

    public void DisableYawOverride()
    {
        if (!_yawOverride) return;
        ReleaseTracked(_d.YokeHeading);
        ReleaseTracked(_d.OverrideYaw);
        _yawOverride = false;
    }

    public void SetYawInput(float target, float dt)
    {
        _yawSlew.MaxRate = _tuning.YawSlewRate;
        Hold(_d.YokeHeading, _yawSlew.Update(target, dt));
    }

    public float YawInputCmd => _yawSlew.Value;

    // --- Soltarlo todo -----------------------------------------------------
    // Suelta SOLO los holds de esta instancia (ejes y overrides), en orden
    // inverso al de creacion (ejes antes que su override). No usa el
    // ReleaseAll global del connector: soltaria tambien los holds de las IAs.
    public void ReleaseAllOverrides()
    {
        for (int i = _held.Count - 1; i >= 0; i--) _c.Release(_held[i]);
        _held.Clear();
        _throttleOverride = _pitchOverride = _rollOverride = _yawOverride = false;
    }

    // Se llama cuando se reconecta el pipe: el connector arranca sin holds
    // ni ids, asi que las banderas de "ya tengo el override puesto" que
    // teniamos aqui ya no son verdad.
    public void ForgetOverrideState()
    {
        _throttleOverride = _pitchOverride = _rollOverride = _yawOverride = false;
        _held.Clear();
        // El connector arranca sin nada escrito, asi que el ultimo valor de
        // aerofrenos que creiamos puesto ya no es verdad: se fuerza a
        // reescribirlo en el proximo mando.
        _lastSpeedbrake = float.NaN;
    }

    // Las rampas (cuanto puede cambiar cada eje como maximo por segundo) ya
    // no viven aqui como const: son ControlTuning.ThrottleSlewRate/
    // PitchSlewRate/RollSlewRate/YawSlewRate, editables en caliente desde la
    // pestana de Config del shell (Ui/ShellWindow.xaml) -- ver ControlTuning.cs
    // para los valores por defecto (los mismos que estaban aqui antes) y el
    // razonamiento de que se expone y que no.
}
