using System.Globalization;
using System.Text.RegularExpressions;
using MonitorToneFix.Core.Recolor;
using Xunit;

namespace MonitorToneFix.Tests;

public class PicomShaderGeneratorTests
{
    private static float ExtractConst(string shader, string name)
    {
        var match = MyConstRegex(name).Match(shader);
        Assert.True(match.Success, $"constante '{name}' nao encontrada no shader gerado");
        return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static Regex MyConstRegex(string name) =>
        new(Regex.Escape($"const float {name} = ") + @"([\d.]+);");

    [Fact]
    public void Generate_ProducesValidWindowShaderEntryPoint()
    {
        var shader = PicomShaderGenerator.Generate(new RecolorParameters());

        Assert.Contains("vec4 window_shader()", shader);
        Assert.Contains("uniform sampler2D tex;", shader);
        Assert.Contains("default_post_processing", shader);
    }

    [Fact]
    public void Generate_BakesInBandAThresholdFromLuminance255Scale()
    {
        var parameters = new RecolorParameters();
        parameters.BandA.LowerLuminance = 240;

        var shader = PicomShaderGenerator.Generate(parameters);

        Assert.Equal(240f / 255f, ExtractConst(shader, "thresholdA"), precision: 4);
    }

    [Fact]
    public void Generate_BakesInTargetColorsAsUnitFloatRgb()
    {
        var parameters = new RecolorParameters();
        parameters.BandA.TargetColor = new RgbColor(224, 224, 224);

        var shader = PicomShaderGenerator.Generate(parameters);

        var match = Regex.Match(shader, @"targetColorA = vec3\(([\d.]+), ([\d.]+), ([\d.]+)\);");
        Assert.True(match.Success);
        Assert.Equal(224f / 255f, float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), precision: 4);
    }

    [Fact]
    public void Generate_DifferentIntensityProducesDifferentStrengthConstant()
    {
        var low = new RecolorParameters();
        low.BandA.IntensityPercent = 30;
        var high = new RecolorParameters();
        high.BandA.IntensityPercent = 90;

        string shaderLow = PicomShaderGenerator.Generate(low);
        string shaderHigh = PicomShaderGenerator.Generate(high);

        Assert.Contains("strengthA = 0.3", shaderLow);
        Assert.Contains("strengthA = 0.9", shaderHigh);
    }
}
