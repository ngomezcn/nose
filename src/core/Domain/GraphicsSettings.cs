namespace AICopilotCore.Domain;

// Ajustes de overlays 2D que dibuja el connector sobre la escena
// (etiquetas, lineas, marcadores). Viven en el core y viajan al plugin
// con Op.GraphicsConfig; el dibujo en si no puede estar aqui (OpenGL /
// XPLM viven en el proceso de X-Plane).
public sealed class GraphicsSettings
{
    public const byte FlagOwnLabel = 1 << 0;
    public const byte FlagOtherLabels = 1 << 1;
    public const byte FlagLines = 1 << 2;
    public const byte FlagMarkers = 1 << 3;
    public const byte FlagPath = 1 << 4;

    public bool ShowOwnLabel { get; set; } = true;
    public bool ShowOtherLabels { get; set; }
    public bool ShowLines { get; set; }
    public bool ShowMarkers { get; set; }
    public bool ShowPath { get; set; }

    // 0 = Basic (mas pequena), 1 = Proportional (la del label "Jev" actual).
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

    private string _labelText = "Jev";
    public string LabelText
    {
        get => _labelText;
        set
        {
            string t = (value ?? string.Empty).Trim();
            if (t.Length > 32) t = t[..32];
            _labelText = t.Length == 0 ? "Jev" : t;
        }
    }

    public byte Flags
    {
        get
        {
            byte f = 0;
            if (ShowOwnLabel) f |= FlagOwnLabel;
            if (ShowOtherLabels) f |= FlagOtherLabels;
            if (ShowLines) f |= FlagLines;
            if (ShowMarkers) f |= FlagMarkers;
            if (ShowPath) f |= FlagPath;
            return f;
        }
    }

    public void ResetToDefaults()
    {
        ShowOwnLabel = true;
        ShowOtherLabels = false;
        ShowLines = false;
        ShowMarkers = false;
        ShowPath = false;
        Font = 1;
        ColorR = 1.0f;
        ColorG = 0.85f;
        ColorB = 0.1f;
        Scale = 1.0f;
        LabelText = "Jev";
    }
}
