namespace AICopilotCore.Domain.Agents;

// Avion agnostico: sensores + actuadores. Las secuencias leen de aqui en vez
// de Datarefs (_d.*).
public interface IAircraftBody : IFlightActuators
{
    int XplmIndex { get; }          // 0 = ownship, 1..N = avion IA
    bool IsLocal { get; }
    BodyCaps Caps { get; }

    void Sense();                   // refresca State (una vez por frame)
    FlightState State { get; }
    bool TryGetKinematics(out Kinematics k);

    float AglMeters { get; }
    string AglSource { get; }
    float FlapRatio { get; }
    float SpeedbrakeRatio { get; }
    float QnhInHg { get; }

    void Step(float dt);
    void OnConnectionLost();
}
