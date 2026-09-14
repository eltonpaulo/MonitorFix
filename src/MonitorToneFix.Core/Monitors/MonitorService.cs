using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MonitorToneFix.Core.Monitors;

/// <summary>
/// Enumerates connected monitors on X11 by parsing `xrandr --verbose`.
/// Parsing xrandr's text output (rather than hand-marshaling the XRandR C
/// structs over P/Invoke) trades a small amount of process-spawn overhead
/// for far lower risk of struct-layout bugs; xrandr itself links libXrandr,
/// so the data is exactly what the native API would return.
/// </summary>
public sealed partial class MonitorService
{
    private static readonly Regex HeaderRegex = MyHeaderRegex();
    private static readonly Regex GeometryRegex = MyGeometryRegex();
    private static readonly Regex PhysicalSizeRegex = MyPhysicalSizeRegex();
    private static readonly Regex HexLineRegex = MyHexLineRegex();
    private static readonly Regex RefreshHzRegex = MyRefreshHzRegex();

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        string output = RunXrandrVerbose();
        return ParseAll(output);
    }

    internal static string RunXrandrVerbose()
    {
        var psi = new ProcessStartInfo("xrandr", "--verbose")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Não foi possível iniciar 'xrandr'.");
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            string stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"xrandr falhou (código {process.ExitCode}): {stderr}");
        }
        return stdout;
    }

    internal static IReadOnlyList<MonitorInfo> ParseAll(string xrandrVerboseOutput)
    {
        var lines = xrandrVerboseOutput.Replace("\r\n", "\n").Split('\n');

        var blocks = new List<(string Header, List<string> Body)>();
        foreach (string line in lines)
        {
            var headerMatch = HeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                blocks.Add((line, new List<string>()));
            }
            else if (blocks.Count > 0)
            {
                blocks[^1].Body.Add(line);
            }
        }

        var result = new List<MonitorInfo>();
        int index = 0;
        foreach (var (header, body) in blocks)
        {
            var headerMatch = HeaderRegex.Match(header);
            string outputName = headerMatch.Groups["name"].Value;
            bool connected = headerMatch.Groups["state"].Value == "connected";
            if (!connected)
            {
                continue;
            }

            index++;
            bool isPrimary = header.Contains(" primary ") || header.Contains(" primary\t");

            var geometryMatch = GeometryRegex.Match(header);
            int width = 0, height = 0, posX = 0, posY = 0;
            if (geometryMatch.Success)
            {
                width = int.Parse(geometryMatch.Groups["w"].Value);
                height = int.Parse(geometryMatch.Groups["h"].Value);
                posX = int.Parse(geometryMatch.Groups["x"].Value);
                posY = int.Parse(geometryMatch.Groups["y"].Value);
            }

            double refreshHz = FindCurrentRefreshHz(body);
            byte[]? edidRaw = TryExtractEdid(body);
            Edid? edid = edidRaw is not null ? Edid.TryParse(edidRaw) : null;

            string persistentId = edid is not null
                ? $"edid:{edid.Sha256Hex}"
                : $"output:{outputName}";

            result.Add(new MonitorInfo
            {
                OutputName = outputName,
                Index = index,
                Manufacturer = edid?.ManufacturerId,
                ModelCode = edid is not null ? edid.ProductCode.ToString("x") : null,
                EdidName = edid?.MonitorName,
                WidthPx = width,
                HeightPx = height,
                PositionX = posX,
                PositionY = posY,
                RefreshHz = refreshHz,
                IsPrimary = isPrimary,
                HdrEnabled = false, // xrandr/X11 has no standard HDR signaling; treated as unsupported.
                PersistentId = persistentId,
            });
        }

        return result;
    }

    private static double FindCurrentRefreshHz(List<string> body)
    {
        for (int i = 0; i < body.Count; i++)
        {
            if (body[i].Contains("*current"))
            {
                for (int j = i + 1; j < body.Count && j < i + 3; j++)
                {
                    var m = RefreshHzRegex.Match(body[j]);
                    if (m.Success)
                    {
                        return Math.Round(double.Parse(m.Groups["hz"].Value, System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
        }
        return 0.0;
    }

    private static byte[]? TryExtractEdid(List<string> body)
    {
        int edidLineIndex = body.FindIndex(l => l.TrimEnd().EndsWith("EDID:"));
        if (edidLineIndex < 0) return null;

        var hex = new System.Text.StringBuilder();
        for (int i = edidLineIndex + 1; i < body.Count; i++)
        {
            var m = HexLineRegex.Match(body[i]);
            if (!m.Success) break;
            hex.Append(m.Groups["hex"].Value);
        }

        string hexStr = hex.ToString();
        if (hexStr.Length == 0 || hexStr.Length % 2 != 0) return null;

        byte[] bytes = new byte[hexStr.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(hexStr.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    [GeneratedRegex(@"^(?<name>\S+)\s+(?<state>connected|disconnected)\b")]
    private static partial Regex MyHeaderRegex();

    [GeneratedRegex(@"(?<w>\d+)x(?<h>\d+)\+(?<x>-?\d+)\+(?<y>-?\d+)")]
    private static partial Regex MyGeometryRegex();

    [GeneratedRegex(@"(?<mw>\d+)mm x (?<mh>\d+)mm")]
    private static partial Regex MyPhysicalSizeRegex();

    [GeneratedRegex(@"^\s*(?<hex>[0-9a-fA-F]{16,})\s*$")]
    private static partial Regex MyHexLineRegex();

    [GeneratedRegex(@"clock\s+(?<hz>[\d.]+)Hz")]
    private static partial Regex MyRefreshHzRegex();
}
