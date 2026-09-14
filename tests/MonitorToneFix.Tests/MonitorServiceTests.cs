using MonitorToneFix.Core.Monitors;
using Xunit;

namespace MonitorToneFix.Tests;

public class MonitorServiceTests
{
    private static string LoadFixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xrandr_verbose_sample.txt"));

    [Fact]
    public void ParseAll_ReturnsOnlyConnectedOutputs()
    {
        var monitors = MonitorService.ParseAll(LoadFixture());

        Assert.NotEmpty(monitors);
        Assert.All(monitors, m => Assert.False(string.IsNullOrEmpty(m.OutputName)));
    }

    [Fact]
    public void ParseAll_ExtractsGeometryAndRefreshRate()
    {
        var monitors = MonitorService.ParseAll(LoadFixture());

        var hdmi = Assert.Single(monitors, m => m.OutputName == "HDMI-1");
        Assert.Equal(1920, hdmi.WidthPx);
        Assert.Equal(1080, hdmi.HeightPx);
        Assert.Equal(1366, hdmi.PositionX);
        Assert.Equal(0, hdmi.PositionY);
        Assert.Equal(60, hdmi.RefreshHz);
    }

    [Fact]
    public void ParseAll_DecodesEdidManufacturerFromHdmiOutput()
    {
        var monitors = MonitorService.ParseAll(LoadFixture());

        var hdmi = Assert.Single(monitors, m => m.OutputName == "HDMI-1");
        Assert.Equal("RGT", hdmi.Manufacturer);
        Assert.False(string.IsNullOrEmpty(hdmi.PersistentId));
        Assert.StartsWith("edid:", hdmi.PersistentId);
    }

    [Fact]
    public void ParseAll_AssignsSequentialIndexesStartingAtOne()
    {
        var monitors = MonitorService.ParseAll(LoadFixture());

        var indexes = monitors.Select(m => m.Index).OrderBy(i => i).ToList();
        Assert.Equal(Enumerable.Range(1, monitors.Count), indexes);
    }
}

public class EdidTests
{
    [Fact]
    public void TryParse_RejectsNonEdidData()
    {
        var result = Edid.TryParse(new byte[128]);
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_DecodesKnownManufacturerId()
    {
        // Header + manufacturer bytes 0x48 0xF4 -> "RGT" (verified against xrandr --verbose output).
        byte[] raw = new byte[128];
        byte[] header = { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 };
        header.CopyTo(raw, 0);
        raw[8] = 0x48;
        raw[9] = 0xF4;
        raw[10] = 0x52;
        raw[11] = 0x13;

        var edid = Edid.TryParse(raw);

        Assert.NotNull(edid);
        Assert.Equal("RGT", edid!.ManufacturerId);
        Assert.Equal((ushort)0x1352, edid.ProductCode);
    }
}
