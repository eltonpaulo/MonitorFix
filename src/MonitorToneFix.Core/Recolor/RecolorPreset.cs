namespace MonitorToneFix.Core.Recolor;

/// <summary>One named, ready-to-apply combination of slider values ("2. Predefinições").</summary>
public sealed class RecolorPreset
{
    public required string Name { get; init; }
    public required Func<RecolorParameters> Build { get; init; }
}

/// <summary>Built-in presets shipped with the app. "Salvar nova"/"Duplicar"/"Excluir" (user-defined presets) are not implemented yet.</summary>
public static class BuiltInPresets
{
    private static RecolorParameters GrayPreset(byte gray, int intensity, int maxBrightness) => new()
    {
        FilterEnabled = true,
        BandA = new NeutralWhiteBand
        {
            LowerLuminance = 240,
            NeutralityTolerance = 8,
            IntensityPercent = intensity,
            FeatherPercent = 20,
            TargetColor = new RgbColor(gray, gray, gray),
        },
        BandB = new ReddishWhiteBand
        {
            LowerLuminance = 229,
            RedSensitivity = 26,
            IntensityPercent = intensity,
            FeatherPercent = 20,
            TargetColor = new RgbColor(gray, gray, gray),
        },
        Global = new GlobalAdjustments
        {
            MaxHighlightBrightness = maxBrightness,
            LuminancePreservationPercent = 65,
            PreserveHighlightDifferencesPercent = 80,
        },
    };

    public static readonly IReadOnlyList<RecolorPreset> All = new List<RecolorPreset>
    {
        new() { Name = "Branco para cinza muito claro", Build = () => GrayPreset(240, 50, 235) },
        new() { Name = "Branco para cinza claro", Build = () => GrayPreset(224, 70, 220) },
        new() { Name = "Branco para cinza médio", Build = () => GrayPreset(190, 80, 190) },
        new() { Name = "Branco para cinza escuro", Build = () => GrayPreset(150, 90, 150) },
        new() { Name = "Branco para cinza forte (máximo)", Build = () => GrayPreset(120, 100, 120) },
        new()
        {
            Name = "Branco para bege claro",
            Build = () =>
            {
                var p = GrayPreset(224, 70, 220);
                p.BandA.TargetColor = new RgbColor(224, 214, 196);
                p.BandB.TargetColor = new RgbColor(224, 214, 196);
                return p;
            },
        },
        new()
        {
            Name = "Redução de luminosidade sem mudar a cor",
            Build = () =>
            {
                var p = GrayPreset(224, 40, 230);
                p.Global.LuminancePreservationPercent = 100;
                return p;
            },
        },
    }.AsReadOnly();
}
