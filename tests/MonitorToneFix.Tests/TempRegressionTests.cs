using System.Diagnostics;
using MonitorToneFix.Core.Compositor;
using MonitorToneFix.Core.Monitors;
using MonitorToneFix.Core.Recolor;
using Xunit;
using Xunit.Abstractions;

namespace MonitorToneFix.Tests;

public class TempRegressionTests
{
    private readonly ITestOutputHelper _out;
    public TempRegressionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task EvictsStrayCompositor_AndWatchdogRestartsPicomAfterItDies()
    {
        // Cenario 1: um picom "de sessao" ja segurando o compositor (o autostart do
        // sistema, ou um orfao de uma execucao anterior) — era aqui que o filtro
        // silenciosamente nao ativava.
        Process.Start(new ProcessStartInfo("/bin/sh", "-c \"exec setsid picom </dev/null >/dev/null 2>&1 &\""))!.WaitForExit();
        await Task.Delay(2000);
        _out.WriteLine($"picom(s) antes: {Pgrep()}");
        Assert.NotEmpty(Pgrep());

        string tempDir = Path.Combine(Path.GetTempPath(), "mtf-reg-" + Guid.NewGuid().ToString("N"));
        var controller = new PicomController(tempDir);
        try
        {
            var monitor = new MonitorService().GetMonitors()[0];
            await controller.ApplyAsync(new RecolorParameters { FilterEnabled = true }, monitor);
            Assert.True(controller.IsRunning);
            _out.WriteLine("OK: iniciou mesmo com outro compositor segurando a tela");

            // Cenario 2: o picom morre sozinho (o que acontece ao voltar do bloqueio /
            // suspensao). Antes, o app nunca percebia.
            RunKill(OurPicomPid(tempDir));
            await Task.Delay(1000);
            _out.WriteLine("picom derrubado a forca");

            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!controller.IsRunning && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
            Assert.True(controller.IsRunning, "o watchdog nao reergueu o picom");
            _out.WriteLine("OK: watchdog restaurou o filtro sozinho");
        }
        finally
        {
            await controller.ShutdownAsync();
        }

        await Task.Delay(2000);
        _out.WriteLine($"picom(s) depois do shutdown (sessao devolvida): {Pgrep()}");
        Assert.NotEmpty(Pgrep());
    }

    private static int OurPicomPid(string tempDir)
    {
        foreach (string pid in Pgrep().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            if (cmdline.Contains(tempDir)) return int.Parse(pid);
        }
        throw new Xunit.Sdk.XunitException("picom do controller nao encontrado");
    }

    private static void RunKill(int pid) => Process.Start("kill", $"-9 {pid}")!.WaitForExit();

    private static string Pgrep()
    {
        var psi = new ProcessStartInfo("pgrep", "-x picom") { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        string s = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return string.Join(' ', s.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
