using System.Diagnostics;
using MonitorToneFix.Core.Monitors;
using MonitorToneFix.Core.Recolor;

namespace MonitorToneFix.Core.Compositor;

/// <summary>
/// Drives picom as the recoloring engine: writes the generated GLSL shader and a
/// picom config scoping it to one monitor's geometry, then asks the running picom
/// process to reinitialize (SIGUSR1). Also takes over compositing from xfwm4 while
/// active, restoring it on shutdown — X11 allows only one compositing manager at a
/// time, so this is unavoidable for any approach that needs live window shaders.
/// </summary>
public sealed class PicomController : IDisposable
{
    private readonly string _configDir;
    private readonly string _shaderPath;
    private readonly string _configPath;
    private Process? _picomProcess;
    private bool? _previousXfwm4Compositing;

    public PicomController(string? configDir = null)
    {
        _configDir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MonitorToneFix", "picom");
        Directory.CreateDirectory(_configDir);
        _shaderPath = Path.Combine(_configDir, "shader.glsl");
        _configPath = Path.Combine(_configDir, "picom.conf");
    }

    public bool IsRunning => _picomProcess is { HasExited: false };

    /// <summary>Applies (or updates) the filter for the given monitor. Pass null params/monitor, or FilterEnabled=false, to fall back to picom's default (unshaded) rendering.</summary>
    public async Task ApplyAsync(RecolorParameters? parameters, MonitorInfo? monitor)
    {
        bool active = parameters is { FilterEnabled: true } && monitor is not null;

        if (active)
        {
            File.WriteAllText(_shaderPath, PicomShaderGenerator.Generate(parameters!));
        }

        File.WriteAllText(_configPath, BuildConfig(active ? monitor : null));

        bool wasAlreadyRunning = IsRunning;
        await EnsureStartedAsync();
        if (wasAlreadyRunning)
        {
            // A freshly started picom already reads this config on its own; sending
            // SIGUSR1 right after Process.Start races its signal-handler setup and,
            // if it loses, the default disposition for SIGUSR1 (terminate) kills it.
            ReloadPicom();
        }
    }

    public async Task ShutdownAsync()
    {
        if (_picomProcess is { HasExited: false })
        {
            _picomProcess.Kill();
            try { await _picomProcess.WaitForExitAsync(); } catch { /* best effort */ }
        }
        _picomProcess = null;
        RestoreXfwm4Compositing();
    }

    private string BuildConfig(MonitorInfo? monitor)
    {
        string rule = monitor is null
            ? string.Empty
            : $"""
              window-shader-fg-rule = [
                "{_shaderPath}:x >= {monitor.PositionX} && x < {monitor.PositionX + monitor.WidthPx} && y >= {monitor.PositionY} && y < {monitor.PositionY + monitor.HeightPx} && !override_redirect"
              ];
              """;

        return $"""
            backend = "glx";
            vsync = true;
            use-damage = true;
            log-level = "warn";

            {rule}
            """;
    }

    private Task EnsureStartedAsync()
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        SetXfwm4Compositing(false);

        var psi = new ProcessStartInfo("picom")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(_configPath);
        _picomProcess = Process.Start(psi);

        if (_picomProcess is not null)
        {
            string logPath = Path.Combine(_configDir, "picom.log");
            _ = LogOutputAsync(_picomProcess, logPath);
        }

        return Task.CompletedTask;
    }

    private static async Task LogOutputAsync(Process process, string logPath)
    {
        await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        var stdoutTask = PumpAsync(process.StandardOutput, log);
        var stderrTask = PumpAsync(process.StandardError, log);
        await Task.WhenAll(stdoutTask, stderrTask);

        static async Task PumpAsync(StreamReader reader, StreamWriter writer)
        {
            char[] buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer)) > 0)
            {
                await writer.WriteAsync(buffer, 0, read);
            }
        }
    }

    private void ReloadPicom()
    {
        if (_picomProcess is { HasExited: false } p)
        {
            RunProcess("kill", $"-SIGUSR1 {p.Id}");
        }
    }

    private void SetXfwm4Compositing(bool enabled)
    {
        if (_previousXfwm4Compositing is null)
        {
            string current = RunProcess("xfconf-query", "-c xfwm4 -p /general/use_compositing").Trim();
            _previousXfwm4Compositing = current.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        RunProcess("xfconf-query", $"-c xfwm4 -p /general/use_compositing -t bool -s {(enabled ? "true" : "false")}");
    }

    private void RestoreXfwm4Compositing()
    {
        if (_previousXfwm4Compositing is { } previous)
        {
            RunProcess("xfconf-query", $"-c xfwm4 -p /general/use_compositing -t bool -s {(previous ? "true" : "false")}");
            _previousXfwm4Compositing = null;
        }
    }

    private static string RunProcess(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi);
        if (process is null) return string.Empty;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    public void Dispose()
    {
        ShutdownAsync().GetAwaiter().GetResult();
    }
}
