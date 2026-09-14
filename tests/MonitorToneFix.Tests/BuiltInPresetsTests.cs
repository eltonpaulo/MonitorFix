using MonitorToneFix.Core.Recolor;
using Xunit;

namespace MonitorToneFix.Tests;

public class BuiltInPresetsTests
{
    [Fact]
    public void All_ContainsSevenDistinctlyNamedPresets()
    {
        Assert.Equal(7, BuiltInPresets.All.Count);
        Assert.Equal(BuiltInPresets.All.Count, BuiltInPresets.All.Select(p => p.Name).Distinct().Count());
    }

    [Theory]
    [InlineData("Branco para cinza muito claro", 240)]
    [InlineData("Branco para cinza claro", 224)]
    [InlineData("Branco para cinza médio", 190)]
    [InlineData("Branco para cinza escuro", 150)]
    [InlineData("Branco para cinza forte (máximo)", 120)]
    public void GrayPresets_TargetNeutralGrayAtExpectedLevel(string name, byte expectedGray)
    {
        var preset = BuiltInPresets.All.Single(p => p.Name == name);
        var parameters = preset.Build();

        Assert.Equal(expectedGray, parameters.BandA.TargetColor.R);
        Assert.Equal(expectedGray, parameters.BandA.TargetColor.G);
        Assert.Equal(expectedGray, parameters.BandA.TargetColor.B);
        Assert.True(parameters.FilterEnabled);
    }

    [Fact]
    public void Default_IsGrayMedioWithUserRequestedGlobalOverrides()
    {
        var parameters = BuiltInPresets.Default.Build();

        Assert.Equal("Branco para cinza médio", BuiltInPresets.Default.Name);
        Assert.Equal(210, parameters.Global.MaxHighlightBrightness);
        Assert.Equal(70, parameters.Global.LuminancePreservationPercent);
    }

    [Fact]
    public void Build_ReturnsFreshInstanceEachCall_SoMutatingOneDoesNotAffectTheNext()
    {
        var preset = BuiltInPresets.All.First();

        var first = preset.Build();
        first.BandA.TargetColor = new RgbColor(1, 2, 3);
        var second = preset.Build();

        Assert.NotEqual(first.BandA.TargetColor, second.BandA.TargetColor);
    }
}
