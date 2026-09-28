using System.Diagnostics;
using MonitorToneFix.Core.Compositor;
using MonitorToneFix.Core.Monitors;
using MonitorToneFix.Core.Recolor;
using Xunit;

namespace MonitorToneFix.Tests;

/// <summary>
/// Covers the two ways the filter used to die silently on a real desktop: another
/// compositor already owning the screen (the distro's picom autostart, or an orphan
/// from a crashed run), and picom disappearing later on — which is what a lock/unlock
/// or suspend cycle does to it. Both left the UI claiming the filter was active over a
/// compositor that was not there, and reopening the app only replayed the same race.
/// </summary>
[Collection(CompositorCollection.Name)]
public class PicomControllerRecoveryTests
{
    [Fact]
    public async Task EvictsStrayCompositorAndRestartsPicomAfterItDies()
    {
        StartStrayPicom();
        await Task.Delay(2000);
        Assert.NotEmpty(RunningPicomPids());

        string tempDir = Path.Combine(Path.GetTempPath(), "mtf-test-" + Guid.NewGuid().ToString("N"));
        var controller = new PicomController(tempDir);
        try
        {
            var monitors = new MonitorService().GetMonitors();
            Assert.NotEmpty(monitors);

            await controller.ApplyAsync(new RecolorParameters { FilterEnabled = true }, monitors[0]);
            Assert.True(controller.IsRunning, "picom nao assumiu o compositor ocupado");

            Process.Start("kill", $"-9 {ControllerPicomPid(tempDir)}")!.WaitForExit();
            await Task.Delay(1000);
            Assert.True(await WaitForAsync(() => controller.IsRunning, TimeSpan.FromSeconds(20)),
                "o watchdog nao reiniciou o picom depois de ele morrer");
        }
        finally
        {
            await controller.ShutdownAsync();
        }

        // Shutdown hands the screen back to the compositor that was there before us.
        Assert.True(await WaitForAsync(() => RunningPicomPids().Count > 0, TimeSpan.FromSeconds(10)),
            "o compositor da sessao nao foi restaurado");
    }

    private static void StartStrayPicom()
    {
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec setsid picom </dev/null >/dev/null 2>&1 &");
        Process.Start(psi)!.WaitForExit();
    }

    private static int ControllerPicomPid(string configDir)
    {
        foreach (int pid in RunningPicomPids())
        {
            string cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            if (cmdline.Contains(configDir))
            {
                return pid;
            }
        }
        throw new InvalidOperationException($"nenhum picom rodando com a config de {configDir}");
    }

    private static List<int> RunningPicomPids()
    {
        var psi = new ProcessStartInfo("pgrep", "-x picom") { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToList();
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(500);
        }
        return condition();
    }
}
