namespace AICopilotCore.Domain;

// Marca discreta en la caja negra: inicio/fin de accion, cambio de fase,
// aborto... Comparte el mismo reloj (TSec) que BlackBoxSample para poder
// dibujar una linea vertical en los graficos alineada con la telemetria.
public readonly struct BlackBoxMarker
{
    public readonly double TSec;
    public readonly string Label;

    public BlackBoxMarker(double tSec, string label)
    {
        TSec = tSec;
        Label = label;
    }
}
