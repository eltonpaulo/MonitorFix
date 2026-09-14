using MonitorToneFix.Core.Recolor;
using Xunit;

namespace MonitorToneFix.Tests;

public class PicomShaderGeneratorTests
{
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
