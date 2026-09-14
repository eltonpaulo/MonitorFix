using System.Security.Cryptography;
using System.Text;

namespace MonitorToneFix.Core.Monitors;

/// <summary>
/// Minimal parser for the 128-byte EDID base block (VESA E-EDID 1.4).
/// Extracts the fields needed to identify a monitor the same way the
/// original Windows app did: PNP manufacturer id, product code and,
/// when present, the monitor name descriptor (tag 0xFC).
/// </summary>
public sealed class Edid
{
    public string ManufacturerId { get; }
    public ushort ProductCode { get; }
    public string? MonitorName { get; }
    public string? SerialText { get; }
    public string Sha256Hex { get; }

    private Edid(string manufacturerId, ushort productCode, string? monitorName, string? serialText, string sha256Hex)
    {
        ManufacturerId = manufacturerId;
        ProductCode = productCode;
        MonitorName = monitorName;
        SerialText = serialText;
        Sha256Hex = sha256Hex;
    }

    public static Edid? TryParse(byte[] raw)
    {
        if (raw.Length < 128)
        {
            return null;
        }

        // Header must be 00 FF FF FF FF FF FF 00.
        ReadOnlySpan<byte> header = stackalloc byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 };
        if (!raw.AsSpan(0, 8).SequenceEqual(header))
        {
            return null;
        }

        ushort manufacturerRaw = (ushort)((raw[8] << 8) | raw[9]);
        string manufacturerId = DecodeManufacturer(manufacturerRaw);

        ushort productCode = (ushort)(raw[10] | (raw[11] << 8));

        string? monitorName = null;
        string? serialText = null;

        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (raw[offset] == 0 && raw[offset + 1] == 0 && raw[offset + 2] == 0)
            {
                byte tag = raw[offset + 3];
                if (tag is 0xFC or 0xFF or 0xFE)
                {
                    string text = DecodeDescriptorText(raw, offset + 5);
                    if (tag == 0xFC) monitorName = text;
                    else if (tag == 0xFF) serialText = text;
                }
            }
        }

        string sha256Hex = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();

        return new Edid(manufacturerId, productCode, monitorName, serialText, sha256Hex);
    }

    private static string DecodeManufacturer(ushort value)
    {
        // 3 letters, 5 bits each, packed into the low 15 bits (bit 15 is reserved/0).
        int l1 = (value >> 10) & 0x1F;
        int l2 = (value >> 5) & 0x1F;
        int l3 = value & 0x1F;
        Span<char> chars = stackalloc char[3];
        chars[0] = (char)('A' + l1 - 1);
        chars[1] = (char)('A' + l2 - 1);
        chars[2] = (char)('A' + l3 - 1);
        return new string(chars);
    }

    private static string DecodeDescriptorText(byte[] raw, int start)
    {
        var sb = new StringBuilder(13);
        for (int i = 0; i < 13; i++)
        {
            byte b = raw[start + i];
            if (b == 0x0A) break; // LF terminates the text, remainder is padding (0x20).
            sb.Append((char)b);
        }
        return sb.ToString().TrimEnd();
    }
}
