using System.Text;
using Shouldly;
using Toki.Hardware.EscPos;

namespace Toki.Hardware.Tests;

public sealed class EscPosTests
{
    internal static readonly Receipt Sample = new("Carnicería La Peña", "131000001", "Av. Duarte 25, Santiago", "809-555-0101",
        "Factura de Consumo Electrónica", "E320000000123", 123, new DateTime(2026, 10, 15, 15, 45, 0), "Caja 1", "Yudelka Martínez", null, null,
        [
            new("Chuleta ahumada de cerdo", 2.345m, "Pound", 185m, 0m, 433.83m, null),
            new("Salami súper especial Induveca 1 lb", 2m, "Unit", 210m, 20m, 400m, null),
        ],
        706.64m, 20m, [new("ITBIS 18%", 706.64m, 127.19m)], 833.83m, [new("Efectivo", 833.83m, 1000m)], 166m, "Ab3xY9",
        "https://ecf.dgii.gov.do/ecf/ConsultaTimbreFC?RncEmisor=131000001&ENCF=E320000000123&MontoTotal=833.83&CodigoSeguridad=Ab3xY9",
        "¡Gracias por su compra! Vuelva pronto.", false);

    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    public void The_receipt_bytes_match_the_snapshot(int columns)
    {
        Snapshot.Match($"receipt-{columns}.txt", Snapshot.Hex(EscPos.EscPos.Render(Sample, columns, openDrawer: true)));
        Snapshot.Match($"receipt-{columns}.text.txt", EscPos.EscPos.Text(Sample, columns));
    }

    [Fact]
    public void Every_line_fits_the_paper()
    {
        foreach (var columns in new[] { 32, 48 })
            foreach (var line in EscPos.EscPos.Text(Sample with { BusinessName = "Supermercado y Carnicería Hermanos Rodríguez Peña SRL" }, columns).Split('\n'))
                (line.StartsWith("[QR]", StringComparison.Ordinal) ? 0 : line.Length).ShouldBeLessThanOrEqualTo(columns, line);
    }

    [Fact]
    public void Spanish_letters_go_out_in_code_page_850()
    {
        var bytes = EscPos.EscPos.Render(Sample with { Footer = "ñÑáéíóúü¿¡", QrUrl = "" }, 48, openDrawer: false);
        bytes[..5].ShouldBe(new byte[] { 0x1B, 0x40, 0x1B, 0x74, 0x02 }); // ESC @, ESC t 2 (PC850)
        Contains(bytes, [0xA4, 0xA5, 0xA0, 0x82, 0xA1, 0xA2, 0xA3, 0x81, 0xA8, 0xAD]).ShouldBeTrue();
        Contains(bytes, Encoding.ASCII.GetBytes("Carnic")).ShouldBeTrue();
        bytes.ShouldNotContain((byte)'?'); // nothing fell back to "?" (the QR URL is left out: it has one)
    }

    [Fact]
    public void The_qr_is_model_2_with_the_receipt_url_then_the_paper_is_cut()
    {
        var bytes = EscPos.EscPos.Render(Sample, 48, openDrawer: false);
        Contains(bytes, [0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00]).ShouldBeTrue(); // model 2
        var url = Encoding.ASCII.GetBytes(Sample.QrUrl);
        var store = (byte[])[0x1D, 0x28, 0x6B, (byte)((url.Length + 3) % 256), (byte)((url.Length + 3) / 256), 0x31, 0x50, 0x30, .. url];
        Contains(bytes, store).ShouldBeTrue();
        Contains(bytes, [0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]).ShouldBeTrue(); // print
        bytes[^4..].ShouldBe(new byte[] { 0x1D, 0x56, 0x42, 0x03 }); // feed and cut, last: no drawer
    }

    [Fact]
    public void The_drawer_kick_follows_the_cut_only_when_asked()
    {
        EscPos.EscPos.Render(Sample, 32, openDrawer: true)[^5..].ShouldBe(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA });
        EscPos.EscPos.DrawerKick.ShouldBe(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA });
    }

    [Fact]
    public void The_dgii_fields_print_when_present()
    {
        var text = EscPos.EscPos.Text(Sample with
        {
            SignedAt = new DateTime(2026, 10, 15, 14, 5, 9),
            SequenceExpiresOn = new DateOnly(2027, 12, 31),
            ContingencyLegend = "Emitido en contingencia",
            DgiiStatus = "Aceptada por la DGII", // TK-208
        }, 48);
        text.ShouldContain("Aceptada por la DGII");
        text.ShouldContain("Firmada: 15/10/2026 2:05:09 PM");
        text.ShouldContain("31/12/2027");
        text.ShouldContain("Emitido en contingencia");
    }

    [Fact]
    public void A_receipt_without_qr_url_prints_no_qr()
    {
        Contains(EscPos.EscPos.Render(Sample with { QrUrl = "" }, 48, false), [0x1D, 0x28, 0x6B]).ShouldBeFalse();
        EscPos.EscPos.Text(Sample with { IsVoided = true }, 48).ShouldContain("*** ANULADA ***");
    }

    internal static readonly ShiftReport SampleZ = new("Carnicería La Peña", "131000001", "Cierre Z #12", "Caja 1", "Yudelka Martínez",
        new DateTime(2026, 10, 15, 7, 58, 0), new DateTime(2026, 10, 15, 18, 4, 0),
        [
            new("Por forma de pago (neto)", [new("Efectivo", 18450.25m), new("Tarjeta", 6320m), new("Fiado", 1200m)]),
            new("Turno",
            [
                new("Ventas", null, "84 (1 anulada)"), new("Fondo inicial", 2000m), new("Abonos de fiado", 500m), new("Abonos de pedidos", 0m),
                new("Entradas de efectivo", 300m), new("Retiros", 10000m), new("Gastos pagados de la caja", 150m), new("Redondeo", 3.75m),
                new("Aperturas de gaveta sin venta", null, "2"),
            ]),
            new("Efectivo", [new("Esperado", 11104m), new("Contado", 11054m), new("Faltaron", 50m, Bold: true), new("Autorizó el cierre", null, "Ramón Peña")]),
        ],
        new DateTime(2026, 10, 15, 18, 5, 0), "#4521 9f2c11ab");

    [Theory]
    [InlineData(48)]
    [InlineData(32)]
    public void The_z_report_bytes_match_the_snapshot_and_every_line_fits(int columns)
    {
        var bytes = EscPos.EscPos.Render(SampleZ, columns);
        Snapshot.Match($"z-{columns}.txt", Snapshot.Hex(bytes));
        var text = EscPos.EscPos.Text(SampleZ, columns);
        Snapshot.Match($"z-{columns}.text.txt", text);
        foreach (var line in text.Split('\n')) line.Length.ShouldBeLessThanOrEqualTo(columns, line);
        bytes[^4..].ShouldBe(new byte[] { 0x1D, 0x56, 0x42, 0x03 }); // cut, and never the drawer
    }

    [Theory]
    [InlineData("COM4", true)]
    [InlineData("com12", true)]
    [InlineData("EPSON TM-T20II Receipt", false)]
    [InlineData("COMANDA", false)]
    public void A_com_port_goes_serial_anything_else_to_the_windows_printer(string port, bool serial) =>
        EscPosPrinter.IsComPort(port).ShouldBe(serial);

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}

/// <summary>Compares output with a file under Snapshots/. A missing file is written (in the source tree) and the test fails once,
/// so a new snapshot is always looked at before it is committed.</summary>
internal static class Snapshot
{
    public static void Match(string name, string actual)
    {
        var source = Path.Combine(SourceDir(), "Snapshots", name);
        if (!File.Exists(source))
        {
            File.WriteAllText(source, actual);
            throw new InvalidOperationException($"New snapshot {name} written; review it and run again.");
        }
        actual.ShouldBe(File.ReadAllText(source).ReplaceLineEndings("\n"), $"{name} changed. If it is right, delete the file and run again.");
    }

    /// <summary>16 bytes per line: offset, hex and the printable ASCII.</summary>
    public static string Hex(byte[] bytes)
    {
        var s = new StringBuilder();
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var row = bytes.AsSpan(i, Math.Min(16, bytes.Length - i));
            s.Append(i.ToString("X4")).Append("  ").Append(Convert.ToHexString(row).ToLowerInvariant().PadRight(32)).Append("  ");
            foreach (var b in row) s.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            s.Append('\n');
        }
        return s.ToString();
    }

    private static string SourceDir([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;
}
