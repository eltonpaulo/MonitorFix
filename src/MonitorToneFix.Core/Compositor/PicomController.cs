using System.Diagnostics;
using MonitorToneFix.Core.Monitors;
using MonitorToneFix.Core.Recolor;
using MonitorToneFix.Core.X11;

namespace MonitorToneFix.Core.Compositor;

/// <summary>
/// Drives picom as the recoloring engine: writes the generated GLSL shader and a
/// picom config scoping it to one monitor's geometry, then asks the running picom
/// process to reinitialize (SIGUSR1). Also takes over compositing from xfwm4 while
/// active, restoring it on shutdown — X11 allows only one compositing manager at a
/// time, so this is unavoidable for any approach that needs live window shaders.
///
/// Taking the compositor over is racy by nature: xfwm4 releases _NET_WM_CM_S&lt;n&gt;
/// asynchronously, distros ship their own picom autostart, and a crashed run of ours
/// leaves an orphan behind. Every start therefore waits for the selection, evicts a
/// stray picom if one is still holding it, and verifies picom is still alive a moment
/// later — picom reports a lost race by exiting, not by failing to start. A watchdog
/// restarts it if it dies later on (a lock/unlock or suspend cycle can take its GLX
/// context down with it).
/// </summary>
public sealed class PicomController : IDisposable
{
    private static readonly TimeSpan SelectionWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SelectionPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);

    private readonly string _configDir;
    private readonly string _shaderPath;
    private readonly string _configPath;
    private readonly string _statePath;
    private readonly string _logPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _picomProcess;
    private CompositorState? _stateToRestore;
    private Timer? _watchdog;
    private volatile bool _wantPicomRunning;
    private volatile bool _disposed;

    public PicomController(string? configDir = null)
    {
        _configDir = configDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MonitorToneFix", "picom");
        Directory.CreateDirectory(_configDir);
        _shaderPath = Path.Combine(_configDir, "shader.glsl");
        _configPath = Path.Combine(_configDir, "picom.conf");
        _statePath = Path.Combine(_configDir, "compositor-state.txt");
        _logPath = Path.Combine(_configDir, "picom.log");

        // A previous run that was killed (crash, session logout, SIGKILL) never got to
        // restore the desktop compositor, and re-reading xfwm4 now would record the
        // value *we* left behind as the one to restore. Prefer the state it persisted.
        _stateToRestore = LoadState();
    }

    public bool IsRunning => _picomProcess is { HasExited: false };

    /// <summary>Applies (or updates) the filter for the given monitor. Pass null params/monitor, or FilterEnabled=false, to fall back to picom's default (unshaded) rendering.</summary>
    public async Task ApplyAsync(RecolorParameters? parameters, MonitorInfo? monitor)
    {
        bool active = parameters is { FilterEnabled: true } && monitor is not null;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (active)
            {
                File.WriteAllText(_shaderPath, PicomShaderGenerator.Generate(parameters!));
            }

            File.WriteAllText(_configPath, BuildConfig(active ? monitor : null));

            if (!active && !IsRunning)
            {
                // The filter is off and we own no compositor: there is nothing to render
                // differently, so don't take the desktop's compositor away just to draw
                // the same pixels.
                _wantPicomRunning = false;
                return;
            }

            bool wasAlreadyRunning = IsRunning;
            await StartPicomAsync().ConfigureAwait(false);
            _wantPicomRunning = true;

            if (wasAlreadyRunning)
            {
                // A freshly started picom already reads this config on its own; sending
                // SIGUSR1 right after Process.Start races its signal-handler setup and,
                // if it loses, the default disposition for SIGUSR1 (terminate) kills it.
                ReloadPicom();
            }

            StartWatchdog();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _wantPicomRunning = false;
            _watchdog?.Dispose();
            _watchdog = null;

            if (_picomProcess is { HasExited: false } p)
            {
                p.Kill();
                try { await p.WaitForExitAsync().ConfigureAwait(false); } catch { /* best effort */ }
            }
            _picomProcess = null;
            RestoreCompositor();
        }
        finally
        {
            _gate.Release();
        }
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

    /// <summary>Starts picom, taking the compositor over first. Must be called holding <see cref="_gate"/>.</summary>
    private async Task StartPicomAsync()
    {
        if (IsRunning)
        {
            return;
        }

        CaptureCompositorState();
        SetXfwm4Compositing(false);
        await ClearCompositorSelectionAsync().ConfigureAwait(false);

        var psi = new ProcessStartInfo("picom")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(_configPath);

        _picomProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Nao foi possivel iniciar o picom.");
        _ = LogOutputAsync(_picomProcess, _logPath);

        // picom parses its config and grabs the compositor selection *after* forking, so
        // a lost race or a bad shader shows up as an immediate exit rather than a failed
        // Process.Start. Without this check the UI happily reports "filtro ativo" for a
        // picom that died milliseconds ago.
        await Task.Delay(StartupGrace).ConfigureAwait(false);
        if (_picomProcess.HasExited)
        {
            _picomProcess = null;
            throw new InvalidOperationException($"O picom encerrou logo apos iniciar. {ReadLastLogLine()}");
        }
    }

    /// <summary>Waits for _NET_WM_CM_S&lt;n&gt; to be free, evicting a stray picom that refuses to let go.</summary>
    private async Task ClearCompositorSelectionAsync()
    {
        if (await WaitForSelectionReleaseAsync().ConfigureAwait(false))
        {
            return;
        }

        // xfwm4 was just told to stop compositing and had its grace period, so whoever
        // still owns the selection is a stray picom: the distro autostart
        // (/etc/xdg/autostart/picom.desktop) or an orphan from a crashed run of ours.
        // Neither can be reloaded into our config, and leaving it alone is what made the
        // filter silently refuse to activate until the session was restarted.
        KillStrayPicoms();

        if (await WaitForSelectionReleaseAsync().ConfigureAwait(false))
        {
            return;
        }

        throw new InvalidOperationException(
            "Outro compositor continua ativo e nao liberou a tela (_NET_WM_CM_S0).");
    }

    private async Task<bool> WaitForSelectionReleaseAsync()
    {
        var deadline = DateTime.UtcNow + SelectionWait;
        while (true)
        {
            if (!IsCompositorSelectionOwned())
            {
                return true;
            }
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }
            await Task.Delay(SelectionPollInterval).ConfigureAwait(false);
        }
    }

    private static bool IsCompositorSelectionOwned()
    {
        IntPtr display = Xlib.XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            int screen = Xlib.XDefaultScreen(display);
            nuint selection = Xlib.XInternAtom(display, $"_NET_WM_CM_S{screen}", onlyIfExists: false);
            return Xlib.XGetSelectionOwner(display, selection) != 0;
        }
        finally
        {
            Xlib.XCloseDisplay(display);
        }
    }

    private void KillStrayPicoms()
    {
        int ours = _picomProcess is { HasExited: false } p ? p.Id : -1;
        foreach (string entry in RunProcess("pgrep", "-x picom").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(entry.Trim(), out int pid) && pid != ours)
            {
                RunProcess("kill", pid.ToString());
            }
        }
    }

    private void StartWatchdog()
    {
        _watchdog ??= new Timer(_ => WatchdogTick(), null, WatchdogInterval, WatchdogInterval);
    }

    private void WatchdogTick()
    {
        // An apply in flight is already doing this work; skip rather than queue up.
        if (!_gate.Wait(0))
        {
            return;
        }
        try
        {
            if (_disposed || !_wantPicomRunning || IsRunning)
            {
                return;
            }
            // picom died on its own. Returning from the screen lock or from suspend can
            // take its GLX context down with it, and nothing else in the app polls for
            // that — the window keeps saying "filtro ativo" over a dead compositor, and
            // reopening the app only replays the same lost race.
            StartPicomAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Another compositor may still be settling; the next tick tries again.
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LogOutputAsync(Process process, string logPath)
    {
        try
        {
            await using var log = new StreamWriter(
                new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            var stdoutTask = PumpAsync(process.StandardOutput, log);
            var stderrTask = PumpAsync(process.StandardError, log);
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch
        {
            // Logging must never take the filter down with it.
        }

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

    /// <summary>The picom log line worth showing the user — its fatal errors are one-liners.</summary>
    private string ReadLastLogLine()
    {
        try
        {
            using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string[] lines = reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return lines.Length > 0 ? lines[^1] : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ReloadPicom()
    {
        if (_picomProcess is { HasExited: false } p)
        {
            RunProcess("kill", $"-SIGUSR1 {p.Id}");
        }
    }

    private void CaptureCompositorState()
    {
        if (_stateToRestore is not null)
        {
            return;
        }

        bool xfwm4Compositing = RunProcess("xfconf-query", "-c xfwm4 -p /general/use_compositing")
            .Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        bool strayPicom = RunProcess("pgrep", "-x picom").Trim().Length > 0;

        _stateToRestore = new CompositorState(xfwm4Compositing, strayPicom);
        SaveState(_stateToRestore);
    }

    private void RestoreCompositor()
    {
        if (_stateToRestore is not { } state)
        {
            return;
        }
        _stateToRestore = null;
        try { File.Delete(_statePath); } catch { /* best effort */ }

        SetXfwm4CompositingValue(state.Xfwm4Compositing);

        if (state.SessionPicom)
        {
            // The session had its own picom before we evicted it; give it its screen back
            // rather than leaving the desktop with no compositor at all.
            StartDetached("picom");
        }
    }

    private void SetXfwm4Compositing(bool enabled)
    {
        CaptureCompositorState();
        SetXfwm4CompositingValue(enabled);
    }

    private static void SetXfwm4CompositingValue(bool enabled)
    {
        RunProcess("xfconf-query", $"-c xfwm4 -p /general/use_compositing -t bool -s {(enabled ? "true" : "false")}");
    }

    private CompositorState? LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return null;
            }
            var values = File.ReadAllLines(_statePath)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

            return new CompositorState(
                values.TryGetValue("xfwm4Compositing", out string? a) && a.Equals("true", StringComparison.OrdinalIgnoreCase),
                values.TryGetValue("sessionPicom", out string? b) && b.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private void SaveState(CompositorState state)
    {
        try
        {
            File.WriteAllText(
                _statePath,
                $"xfwm4Compositing={state.Xfwm4Compositing.ToString().ToLowerInvariant()}\n" +
                $"sessionPicom={state.SessionPicom.ToString().ToLowerInvariant()}\n");
        }
        catch
        {
            // Best effort: losing this only costs us an accurate restore.
        }
    }

    private static void StartDetached(string fileName)
    {
        try
        {
            // setsid so the restored compositor outlives this process and its group.
            Process.Start(new ProcessStartInfo("setsid", fileName) { UseShellExecute = false });
        }
        catch
        {
            // Best effort.
        }
    }

    private static string RunProcess(string fileName, string arguments)
    {
        try
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
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watchdog?.Dispose();
        _watchdog = null;
        ShutdownAsync().GetAwaiter().GetResult();
    }

    /// <summary>What the desktop's compositing looked like before we took it over.</summary>
    private sealed record CompositorState(bool Xfwm4Compositing, bool SessionPicom);
}
