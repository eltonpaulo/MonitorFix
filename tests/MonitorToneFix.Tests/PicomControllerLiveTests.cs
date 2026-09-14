using MonitorToneFix.Core.Compositor;
using MonitorToneFix.Core.Monitors;
using MonitorToneFix.Core.Recolor;
using Xunit;

namespace MonitorToneFix.Tests;

/// <summary>
/// Exercises the real picom lifecycle (start, reload, shutdown) against the X server
/// running the test suite. Restores xfwm4 compositing in a finally block so a failed
/// assertion never leaves the desktop compositor swapped.
/// </summary>
public class PicomControllerLiveTests
{
    [Fact]
    public async Task ApplyAsync_StartsPicomAndScopesRuleToSelectedMonitor()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "mtf-test-" + Guid.NewGuid().ToString("N"));
        var controller = new PicomController(tempDir);
        try
        {
            var monitors = new MonitorService().GetMonitors();
            Assert.NotEmpty(monitors);
            var monitor = monitors[0];

            var parameters = new RecolorParameters { FilterEnabled = true };

            await controller.ApplyAsync(parameters, monitor);

            Assert.True(controller.IsRunning);

            string config = File.ReadAllText(Path.Combine(tempDir, "picom.conf"));
            Assert.Contains($"x >= {monitor.PositionX}", config);
            Assert.Contains($"x < {monitor.PositionX + monitor.WidthPx}", config);

            string shader = File.ReadAllText(Path.Combine(tempDir, "shader.glsl"));
            Assert.Contains("window_shader", shader);
        }
        finally
        {
            await controller.ShutdownAsync();
        }
    }
}
