using System.Globalization;
using System.Text.RegularExpressions;
using PcMonitor.Model;

namespace PcMonitor.Whea;

/// <summary>Extracts the primary Bus:Device:Function from a WHEA-Logger message (Italian or English).</summary>
public static partial class WheaParser
{
    [GeneratedRegex(@"Bus[^:]*:(?:Device|Dispositivo):(?:Function|Funzione):\s*0x([0-9A-Fa-f]+):0x([0-9A-Fa-f]+):0x([0-9A-Fa-f]+)")]
    private static partial Regex BdfRegex();

    public static bool TryParseBdf(string? message, out Bdf bdf)
    {
        bdf = default;
        if (string.IsNullOrEmpty(message)) return false;
        var m = BdfRegex().Match(message);
        if (!m.Success) return false;
        bdf = new Bdf(Hex(m.Groups[1].Value), Hex(m.Groups[2].Value), Hex(m.Groups[3].Value));
        return true;
    }

    static int Hex(string s) => int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}
