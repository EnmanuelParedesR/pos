using Abastalia.Client.UI.Api;
using Abastalia.Hardware;

namespace Abastalia.Client.UI.State;

/// <summary>TK-280: the business logo on this register. The receipt only says which version the business has; the PNG (for the screen) and the
/// one-ink raster for this printer's width (dithered by the store server) are fetched once per version and kept. Any failure prints without it.</summary>
public sealed class BusinessLogo(AbastaliaApi api)
{
    private sealed record Image(byte[] Png, string Version);

    private readonly Dictionary<(string Version, int Width), ReceiptLogo?> _rasters = [];
    private Image? _image;

    /// <summary>The logo as a data URL for an &lt;img&gt;, or null (none, or the server did not answer).</summary>
    public async Task<string?> DataUrlAsync(string? version)
    {
        if (version is null) return null;
        if (_image?.Version != version && (await api.GetAsync<Image>("/api/settings/business/logo")).Value is { } fetched) _image = fetched;
        return _image is { } i ? "data:image/png;base64," + Convert.ToBase64String(i.Png) : null;
    }

    /// <summary>The logo as it comes out of a printer of <paramref name="width"/> mm (58 or 80), or null.</summary>
    public async Task<ReceiptLogo?> RasterAsync(string? version, int width)
    {
        if (version is null) return null;
        if (_rasters.TryGetValue((version, width), out var kept)) return kept;
        var r = await api.GetAsync<ReceiptLogo>($"/api/settings/business/logo/receipt?width={width}");
        if (r.Value is null && r.Problem is not { Status: 404 }) return null; // the server did not answer: try again on the next receipt
        return _rasters[(version, width)] = r.Value;
    }

    /// <summary>The receipt with its logo for this register's printer, unless this PC prints it without (<see cref="HardwareSettings.PrintLogo"/>).</summary>
    public async Task<Receipt> ForPrinterAsync(Receipt receipt, HardwareSettings hardware) =>
        receipt.LogoVersion is null || hardware is not { PrintLogo: true, Printer: { } printer } ? receipt
            : receipt with { Logo = await RasterAsync(receipt.LogoVersion, printer.Width == 58 ? 58 : 80) };

    /// <summary>The raster as a 1-bit BMP data URL: the screen shows exactly the dots the printer gets.</summary>
    public static string BmpDataUrl(ReceiptLogo logo)
    {
        var src = logo.Width / 8;
        var row = (src + 3) / 4 * 4; // BMP rows are padded to 4 bytes and go bottom-up
        var bmp = new byte[62 + row * logo.Height];
        void Int(int at, int v) => BitConverter.TryWriteBytes(bmp.AsSpan(at), v);
        (bmp[0], bmp[1]) = ((byte)'B', (byte)'M');
        Int(2, bmp.Length);
        Int(10, 62);
        Int(14, 40);
        Int(18, logo.Width);
        Int(22, logo.Height);
        (bmp[26], bmp[28]) = (1, 1); // one plane, one bit a pixel
        Int(46, 2);
        // Palette: 0 = paper (white), 1 = ink (black), as in the raster.
        (bmp[54], bmp[55], bmp[56]) = (255, 255, 255);
        for (var y = 0; y < logo.Height; y++) logo.Bits.AsSpan(y * src, src).CopyTo(bmp.AsSpan(62 + (logo.Height - 1 - y) * row));
        return "data:image/bmp;base64," + Convert.ToBase64String(bmp);
    }
}
