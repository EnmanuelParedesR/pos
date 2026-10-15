using System.Text;
using Shouldly;
using Toki.Hardware.Scales;

namespace Toki.Hardware.Tests;

public sealed class ScaleProtocolTests
{
    /// <summary>SOH STX + the 10-byte body + BCC (XOR of the body) + ETX EOT, as a CAS PD-II/ER sends it after ENQ/ACK/DC1.</summary>
    private static byte[] Cas(string body, byte? bcc = null)
    {
        var b = Encoding.ASCII.GetBytes(body);
        b.Length.ShouldBe(10);
        return [0x01, 0x02, .. b, bcc ?? b.Aggregate((byte)0, (x, y) => (byte)(x ^ y)), 0x03, 0x04];
    }

    [Fact]
    public void Cas_reads_a_stable_weight_in_pounds()
    {
        // S, space (positive), " 1.250", lb; BCC = XOR of those 10 bytes = 0x75
        byte[] frame = [0x01, 0x02, (byte)'S', (byte)' ', (byte)' ', (byte)'1', (byte)'.', (byte)'2', (byte)'5', (byte)'0', (byte)'l', (byte)'b', 0x75, 0x03, 0x04];
        ScaleProtocols.ParseCas(frame).ShouldBe(new ScaleReading(1.25m, ScaleStatus.Stable));
    }

    [Fact]
    public void Cas_reports_motion_converts_kilograms_and_flags_overload()
    {
        ScaleProtocols.ParseCas(Cas("U 01.250lb")).ShouldBe(new ScaleReading(1.25m, ScaleStatus.Unstable));
        ScaleProtocols.ParseCas(Cas("S  1.000kg")).ShouldBe(new ScaleReading(2.205m, ScaleStatus.Stable));
        ScaleProtocols.ParseCas(Cas("S- 0.120lb")).ShouldBe(new ScaleReading(-0.12m, ScaleStatus.Unstable)); // below zero never sells
        ScaleProtocols.ParseCas(Cas("S    OL lb")).ShouldBe(new ScaleReading(0m, ScaleStatus.Overload));
    }

    [Fact]
    public void Cas_drops_a_garbled_or_cut_frame()
    {
        ScaleProtocols.ParseCas(Cas("S  1.250lb", bcc: 0x00)).ShouldBeNull();
        ScaleProtocols.ParseCas(Cas("S  1.250lb").AsSpan(0, 8)).ShouldBeNull();
        ScaleProtocols.ParseCas([0x15]).ShouldBeNull(); // NAK
    }

    [Fact]
    public void Toledo_8217_reads_the_weight_or_the_status_byte()
    {
        ScaleProtocols.ParseToledo("\u000201.250\r"u8).ShouldBe(new ScaleReading(1.25m, ScaleStatus.Stable));
        ScaleProtocols.ParseToledo("\u000200125\r"u8).ShouldBe(new ScaleReading(1.25m, ScaleStatus.Stable)); // no decimal point: hundredths
        ScaleProtocols.ParseToledo("\u0002 3.5N\r"u8).ShouldBe(new ScaleReading(3.5m, ScaleStatus.Stable));
        // "?" + status: bits 4 and 5 always set; bit 0 motion, bit 1 over capacity, bit 2 under zero.
        ScaleProtocols.ParseToledo("\u0002?1\r"u8).ShouldBe(new ScaleReading(0m, ScaleStatus.Unstable));
        ScaleProtocols.ParseToledo("\u0002?2\r"u8).ShouldBe(new ScaleReading(0m, ScaleStatus.Overload));
        ScaleProtocols.ParseToledo("\u0002?4\r"u8).ShouldBe(new ScaleReading(0m, ScaleStatus.Unstable));
        ScaleProtocols.ParseToledo("\u000201.2"u8).ShouldBeNull(); // no CR yet
    }

    [Theory]
    [InlineData("ST,GS,+  1.250 lb", 1.25, ScaleStatus.Stable)]
    [InlineData("US,GS,+  1.180 lb", 1.18, ScaleStatus.Unstable)]
    [InlineData("ST,NT,+  0.500kg", 1.102, ScaleStatus.Stable)]
    [InlineData("OL,GS,+ 99.999 lb", 0, ScaleStatus.Overload)]
    [InlineData("ST,GS,-  0.020 lb", -0.02, ScaleStatus.Unstable)]
    [InlineData("    2.40 lb M", 2.4, ScaleStatus.Unstable)]
    public void Continuous_reads_the_common_line_formats(string line, double pounds, ScaleStatus status)
    {
        var frame = ScaleProtocols.ParseContinuous(line).ShouldNotBeNull();
        frame.Pounds.ShouldBe((decimal)pounds);
        frame.Status.ShouldBe(status);
    }

    [Fact]
    public void Continuous_without_a_status_word_settles_after_three_equal_frames()
    {
        ScaleProtocols.ParseContinuous("\n").ShouldBeNull();
        var s = new ContinuousStability();
        s.Next(ScaleProtocols.ParseContinuous("   2.400 lb\r")!.Value).Status.ShouldBe(ScaleStatus.Unstable);
        s.Next(ScaleProtocols.ParseContinuous("   2.410 lb\r")!.Value).Status.ShouldBe(ScaleStatus.Unstable);
        s.Next(ScaleProtocols.ParseContinuous("   2.410 lb\r")!.Value).Status.ShouldBe(ScaleStatus.Unstable);
        s.Next(ScaleProtocols.ParseContinuous("   2.410 lb\r")!.Value).ShouldBe(new ScaleReading(2.41m, ScaleStatus.Stable));
        s.Next(ScaleProtocols.ParseContinuous("   2.380 lb\r")!.Value).Status.ShouldBe(ScaleStatus.Unstable); // moved again
    }
}
