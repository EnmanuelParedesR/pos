using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Toki.Hardware.Scales;

/// <summary>The bytes each protocol sends and receives. Pure: no port here, so every frame is tested with fixtures. Sources in README.md.</summary>
public static partial class ScaleProtocols
{
    public const byte Stx = 0x02, Etx = 0x03, Eot = 0x04, Enq = 0x05, Ack = 0x06, Cr = 0x0D, Lf = 0x0A, Dc1 = 0x11;
    private const decimal PoundsPerKilogram = 2.20462262m, GramsPerPound = 453.59237m;

    /// <summary>Toledo 8217 weight request.</summary>
    public static readonly byte[] ToledoRequest = "W"u8.ToArray();

    /// <summary>CAS ECR: <c>SOH STX STA SIGN W×6 UNIT×2 BCC ETX EOT</c>, sent after ENQ → ACK → DC1.
    /// STA is S (stable) or U (unstable); BCC is the XOR of STA through UNIT. Null when the frame is incomplete or garbled.</summary>
    public static ScaleReading? ParseCas(ReadOnlySpan<byte> frame)
    {
        var start = frame.IndexOf(Stx);
        if (start < 0 || frame.Length < start + 13 || frame[start + 12] != Etx) return null;
        var body = frame.Slice(start + 1, 10);
        byte bcc = 0;
        foreach (var b in body) bcc ^= b;
        if (bcc != frame[start + 11]) return null;

        var text = Encoding.ASCII.GetString(body);
        var weight = text[2..8].Trim();
        if (weight.Contains("OL", StringComparison.OrdinalIgnoreCase)) return new(0m, ScaleStatus.Overload);
        if (!decimal.TryParse(weight, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        var pounds = ToPounds(text[1] == '-' ? -value : value, text[8..10]);
        return new(pounds, text[0] == 'S' && pounds >= 0 ? ScaleStatus.Stable : ScaleStatus.Unstable);
    }

    /// <summary>Toledo 8217 answer to "W": <c>STX weight CR</c> (e.g. "01.250", in pounds) or, when it cannot weigh, <c>STX ? status CR</c>.
    /// Status bits: 0 in motion, 1 over capacity, 2 under zero, 3 outside the zero range. Without a decimal point the digits are hundredths.</summary>
    public static ScaleReading? ParseToledo(ReadOnlySpan<byte> frame)
    {
        var start = frame.IndexOf(Stx);
        if (start < 0) return null;
        var end = frame[start..].IndexOf(Cr);
        if (end < 2) return null;
        var text = Encoding.ASCII.GetString(frame.Slice(start + 1, end - 1)).Trim();
        if (text[0] == '?')
        {
            if (text.Length < 2) return null;
            return (text[1] & 0b10) != 0 ? new(0m, ScaleStatus.Overload) : new(0m, ScaleStatus.Unstable);
        }
        text = text.TrimEnd('N', 'n'); // some scales add N (net)
        if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return new(text.Contains('.', StringComparison.Ordinal) ? value : value / 100m, ScaleStatus.Stable);
    }

    /// <summary>One line of a scale that sends its weight continuously ("ST,GS,+  1.250 lb", "US,NT,  0.53kg", "   2.40 lb").
    /// Status words: ST stable, US/M/MOTION unstable, OL/OVER overload. Null when the line carries no weight.
    /// A line without a status word has <c>Status = null</c>: see <see cref="ContinuousStability"/>.</summary>
    public static (decimal Pounds, ScaleStatus? Status)? ParseContinuous(string line)
    {
        ScaleStatus? status = null;
        foreach (Match w in Word().Matches(line))
        {
            switch (w.Value.ToUpperInvariant())
            {
                case "OL" or "OVER": return (0m, ScaleStatus.Overload);
                case "US" or "M" or "MOTION": status = ScaleStatus.Unstable; break;
                case "ST": status ??= ScaleStatus.Stable; break;
            }
        }
        var m = Weight().Match(line);
        if (!m.Success) return null;
        var value = decimal.Parse(m.Groups["num"].Value, CultureInfo.InvariantCulture) * (m.Groups["sign"].Value == "-" ? -1 : 1);
        var pounds = ToPounds(value, m.Groups["unit"].Value);
        return (pounds, pounds < 0 ? ScaleStatus.Unstable : status);
    }

    /// <summary>Weights are sold in pounds, to the thousandth.</summary>
    internal static decimal ToPounds(decimal value, string unit) => Math.Round(unit.Trim().ToUpperInvariant() switch
    {
        "KG" => value * PoundsPerKilogram,
        "G" => value / GramsPerPound,
        "OZ" => value / 16m,
        _ => value, // lb, or no unit: the scales sold here are set to pounds
    }, 3, MidpointRounding.AwayFromZero);

    [GeneratedRegex(@"[A-Za-z]+")]
    private static partial Regex Word();

    [GeneratedRegex(@"(?<sign>[-+])?\s*(?<num>\d+(\.\d+)?)\s*(?<unit>kg|lbs?|oz|g)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex Weight();
}

/// <summary>Scales in continuous mode that send no status word: the weight counts as stable once the same value arrives three frames in a row.</summary>
public sealed class ContinuousStability
{
    public const int FramesToSettle = 3;
    private decimal _last = -1;
    private int _same;

    public ScaleReading Next((decimal Pounds, ScaleStatus? Status) frame)
    {
        _same = frame.Pounds == _last ? _same + 1 : 1;
        _last = frame.Pounds;
        return new(frame.Pounds, frame.Status ?? (_same >= FramesToSettle && frame.Pounds >= 0 ? ScaleStatus.Stable : ScaleStatus.Unstable));
    }
}
