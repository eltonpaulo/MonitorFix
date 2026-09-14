namespace MonitorToneFix.Core.Monitors;

/// <summary>
/// Describes one connected display, mirroring the fields the original
/// Windows app showed in "1. Monitor e segurança".
/// </summary>
public sealed class MonitorInfo
{
    /// <summary>XRandR output name, e.g. "HDMI-1", "eDP-1". Stable across reboots for the same port.</summary>
    public required string OutputName { get; init; }

    /// <summary>1-based index in detection order, used for "Monitor N" labels and identify-overlay numbers.</summary>
    public required int Index { get; init; }

    public string? Manufacturer { get; init; }
    public string? ModelCode { get; init; }
    public string? EdidName { get; init; }

    public required int WidthPx { get; init; }
    public required int HeightPx { get; init; }
    public required int PositionX { get; init; }
    public required int PositionY { get; init; }
    public required double RefreshHz { get; init; }
    public double ScalePercent { get; init; } = 100.0;

    public required bool IsPrimary { get; init; }
    public bool HdrEnabled { get; init; }

    /// <summary>
    /// Persistent identifier surviving reconnects/reboots as long as the physical
    /// monitor and cable port are unchanged: EDID hash when available, otherwise
    /// falls back to the output name (still stable across reboots on the same port).
    /// </summary>
    public required string PersistentId { get; init; }

    /// <summary>Friendly label combining manufacturer/model or a generic "DisplayN" fallback, as shown to the user.</summary>
    public string FriendlyName => !string.IsNullOrEmpty(EdidName)
        ? EdidName!
        : $"Display{Index}";

    public string DisplayLabel =>
        $"Monitor {Index} — {FriendlyName} — {WidthPx} × {HeightPx} — {RefreshHz:0.##} Hz";

    public override string ToString() => DisplayLabel;
}
