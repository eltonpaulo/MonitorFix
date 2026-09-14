namespace MonitorToneFix.Core.Recolor;

/// <summary>
/// One RGB target color (0-255 per channel), as picked in the UI's color selectors.
/// </summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public (float R, float G, float B) ToUnitFloat() => (R / 255f, G / 255f, B / 255f);
}

/// <summary>
/// Faixa A — brancos neutros: #FFFFFF a ~#F0F0F0. Pixels are treated as "white" when
/// luma is high AND the RGB channels are close together (low spread = neutral).
/// </summary>
public sealed class NeutralWhiteBand
{
    /// <summary>"Limite inferior da faixa A" — luminance (0-255) above which pixels start being affected.</summary>
    public int LowerLuminance { get; set; } = 240;

    /// <summary>"Tolerância de neutralidade A" (0-255) — how far apart R/G/B may be and still count as neutral.</summary>
    public int NeutralityTolerance { get; set; } = 8;

    /// <summary>"Intensidade A" (0-100%) — blend amount between original and target color.</summary>
    public int IntensityPercent { get; set; } = 70;

    /// <summary>"Transição suave A" (0-100%) — smoothstep feather width.</summary>
    public int FeatherPercent { get; set; } = 20;

    public RgbColor TargetColor { get; set; } = new(224, 224, 224);
}

/// <summary>
/// Faixa B — brancos avermelhados: #FFFFFF a ~#FFE5E5. Pixels are treated as "reddish
/// white" when green/blue are high (still near-white) AND red is moderately higher than
/// green/blue (a red push, but not full saturation — that's real red, not a display defect).
/// </summary>
public sealed class ReddishWhiteBand
{
    /// <summary>"Limite inferior da faixa B" (0-255) — min(G,B) above which pixels start being affected.</summary>
    public int LowerLuminance { get; set; } = 229;

    /// <summary>"Sensibilidade ao vermelho B" (0-255) — max allowed (R - min(G,B)) push to still count as a defect, not real red.</summary>
    public int RedSensitivity { get; set; } = 26;

    /// <summary>"Intensidade B" (0-100%).</summary>
    public int IntensityPercent { get; set; } = 70;

    /// <summary>"Transição suave B" (0-100%).</summary>
    public int FeatherPercent { get; set; } = 20;

    public RgbColor TargetColor { get; set; } = new(224, 214, 212);
}

/// <summary>Section "5. Ajustes globais e desempenho" of the original spec.</summary>
public sealed class GlobalAdjustments
{
    /// <summary>"Brilho máximo dos tons claros" (100-255).</summary>
    public int MaxHighlightBrightness { get; set; } = 220;

    /// <summary>"Preservação de luminosidade" (0-100%) — keeps white/250/240/220 visually distinct instead of flattening to one color.</summary>
    public int LuminancePreservationPercent { get; set; } = 65;

    /// <summary>"Preservar diferenças entre tons claros" (0-100%).</summary>
    public int PreserveHighlightDifferencesPercent { get; set; } = 80;

    /// <summary>"Nitidez da imagem" (-100 a +100). Not implemented in the picom shader path (no neighboring-pixel access); reserved for a future capture-based pipeline.</summary>
    public int Sharpness { get; set; } = 0;
}

/// <summary>All tunable parameters for one monitor's filter, matching the original app's control panel.</summary>
public sealed class RecolorParameters
{
    public bool FilterEnabled { get; set; }
    public NeutralWhiteBand BandA { get; set; } = new();
    public ReddishWhiteBand BandB { get; set; } = new();
    public GlobalAdjustments Global { get; set; } = new();
}
