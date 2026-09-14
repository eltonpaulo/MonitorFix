using System.Globalization;
using System.Text.RegularExpressions;
using MonitorToneFix.Core.Recolor;
using Xunit;

namespace MonitorToneFix.Tests;

public partial class PicomShaderGeneratorTests
{
    private static float ExtractConst(string shader, string name)
    {
        var match = MyConstRegex(name).Match(shader);
        Assert.True(match.Success, $"constante '{name}' nao encontrada no shader gerado");
        return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"const\s+float\s+(?:NAME)\s*=\s*([\d.]+);")]
    private static partial Regex MyConstRegexTemplate();

    private static Regex MyConstRegex(string name) =>
        new(Regex.Escape($"const float {name} = ").Replace(@"\ ", " ") + @"([\d.]+);");

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

        // 240/255 = 0.941176...
        Assert.Contains("thresholdA = 0.941176", shader);
    }

    [Fact]
    public void Generate_BakesInTargetColorsAsUnitFloatRgb()
    {
        var parameters = new RecolorParameters();
        parameters.BandA.TargetColor = new RgbColor(224, 224, 224);

        var shader = PicomShaderGenerator.Generate(parameters);

        // 224/255 = 0.878431...
        Assert.Contains("targetColorA = vec3(0.878431, 0.878431, 0.878431)", shader);
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
