namespace AICopilotCore.Domain;

// Ajustes de overlays 2D que dibuja el connector sobre la escena
// (nombres, lineas, triangulo, marcadores). Viven en el core y viajan al
// plugin con Op.GraphicsConfig; el dibujo en si no puede estar aqui
// (OpenGL / XPLM viven en el proceso de X-Plane).
//
// Las etiquetas de texto no tienen toggle: cada avion manda su nombre
// (casilla en Aviones); vacio = no se dibuja nada encima.
public sealed class GraphicsSettings
{
    public const byte FlagLines = 1 << 2;
    public const byte FlagMarkers = 1 << 3;
    public const byte FlagPath = 1 << 4;
    public const byte FlagTriangle = 1 << 5;

    public bool ShowLines { get; set; }
    public bool ShowMarkers { get; set; }
    public bool ShowPath { get; set; }
    public bool ShowTriangle { get; set; }

    // 0 = Basic (mas pequena), 1 = Proportional.
    private int _font = 1;
    public int Font
    {
        get => _font;
        set => _font = value == 0 ? 0 : 1;
    }

    private float _colorR = 1.0f;
    public float ColorR
    {
        get => _colorR;
        set => _colorR = Math.Clamp(value, 0f, 1f);
    }

    private float _colorG = 0.85f;
    public float ColorG
    {
        get => _colorG;
        set => _colorG = Math.Clamp(value, 0f, 1f);
    }

    private float _colorB = 0.1f;
    public float ColorB
    {
        get => _colorB;
        set => _colorB = Math.Clamp(value, 0f, 1f);
    }

    private float _scale = 1.0f;
    public float Scale
    {
        get => _scale;
        set => _scale = Math.Clamp(value, 0.5f, 4.0f);
    }

    public byte Flags
    {
        get
        {
            byte f = 0;
            if (ShowLines) f |= FlagLines;
            if (ShowMarkers) f |= FlagMarkers;
            if (ShowPath) f |= FlagPath;
            if (ShowTriangle) f |= FlagTriangle;
            return f;
        }
    }

    public void ResetToDefaults()
    {
        ShowLines = false;
        ShowMarkers = false;
        ShowPath = false;
        ShowTriangle = false;
        Font = 1;
        ColorR = 1.0f;
        ColorG = 0.85f;
        ColorB = 0.1f;
        Scale = 1.0f;
    }
}
