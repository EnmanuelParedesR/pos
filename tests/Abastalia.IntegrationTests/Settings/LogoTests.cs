using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SkiaSharp;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.Fiscal;
using Abastalia.IntegrationTests.Fiscal;
using Abastalia.IntegrationTests.Infra;
using Abastalia.Settings;
using Abastalia.Settings.Contracts;
using Abastalia.Sync.Contracts;

namespace Abastalia.IntegrationTests.Settings;

/// <summary>TK-280: the business logo, uploaded in Configuración → Negocio: checked by its bytes, written again as a small PNG, on the receipt
/// (as a one-ink raster), on the e-CF's printed page, and sent to the cloud for the public catalog. Own app.</summary>
public sealed class LogoTests(AbastaliaApp app) : IClassFixture<AbastaliaApp>
{
    internal static byte[] Image(int width, int height, SKEncodedImageFormat format, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using (var canvas = new SKCanvas(bitmap)) canvas.DrawRect(width / 4f, height / 4f, width / 2f, height / 2f, new SKPaint { Color = color ?? SKColors.Black });
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    private static object Upload(byte[] file) => new { File = Convert.ToBase64String(file) };

    private async Task<List<BusinessLogoChangedV1>> EventsAsync() => await app.InScopeAsync(async sp =>
        (await sp.GetRequiredService<InfraDbContext>().Outbox.Where(m => m.Type == typeof(BusinessLogoChangedV1).FullName).OrderBy(m => m.Sequence)
            .Select(m => m.Payload).ToListAsync()).Select(p => JsonSerializer.Deserialize<BusinessLogoChangedV1>(p, Json.Options)!).ToList());

    [Fact]
    public async Task Only_a_real_PNG_or_JPG_up_to_512_KB_is_taken_and_only_by_whoever_manages_the_settings()
    {
        var admin = app.Client(await app.AdminAsync());
        var before = (await EventsAsync()).Count;
        async Task<string?> RefusedAsync(object body)
        {
            var r = await admin.PutAsJsonAsync("/api/settings/business/logo", body);
            r.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            return await r.ErrorCode();
        }

        (await RefusedAsync(Upload("GIF89a\x01\0\x01\0"u8.ToArray()))).ShouldBe("Settings.LogoType");
        (await RefusedAsync(Upload("<svg onload=\"alert(1)\"/>"u8.ToArray()))).ShouldBe("Settings.LogoType");
        // The first bytes say PNG (or JPG) but the rest is something else: the decoder must agree with them.
        var jpeg = Image(40, 40, SKEncodedImageFormat.Jpeg);
        (await RefusedAsync(Upload([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. jpeg]))).ShouldBe("Settings.LogoUnreadable");
        (await RefusedAsync(Upload([0xFF, 0xD8, 0xFF, .. "not a picture"u8.ToArray()]))).ShouldBe("Settings.LogoUnreadable");
        (await RefusedAsync(Upload([.. Image(40, 40, SKEncodedImageFormat.Png), .. new byte[LogoImage.MaxUploadBytes]]))).ShouldBe("Settings.LogoTooBig");
        (await RefusedAsync(new { File = "esto no es base64" })).ShouldBe("Settings.LogoUnreadable");

        var cashier = app.Client(await app.UserAsync(Roles.Cashier));
        (await cashier.PutAsJsonAsync("/api/settings/business/logo", Upload(jpeg))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.DeleteAsync("/api/settings/business/logo")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cashier.GetAsync("/api/settings/business/logo")).StatusCode.ShouldBe(HttpStatusCode.NotFound); // signed in: it may read it, there is none
        (await EventsAsync()).Count.ShouldBe(before);
    }

    [Fact]
    public async Task The_logo_is_written_again_as_a_small_PNG_and_reaches_the_receipt_the_printed_e_CF_and_the_cloud()
    {
        var admin = app.Client(await app.AdminAsync());
        var before = (await EventsAsync()).Count;
        (await admin.PutAsJsonAsync("/api/settings/business/logo", Upload(Image(1200, 400, SKEncodedImageFormat.Jpeg)))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // A PNG of at most 600 px a side, whatever came in.
        var version = (await admin.GetFromJsonAsync<BusinessProfile>("/api/settings/business"))!.LogoVersion.ShouldNotBeNull();
        var logo = await admin.GetFromJsonAsync<JsonElement>("/api/settings/business/logo");
        logo.GetProperty("version").GetString().ShouldBe(version);
        var png = logo.GetProperty("png").GetBytesFromBase64();
        LogoImage.IsPng(png).ShouldBeTrue();
        png.Length.ShouldBeLessThanOrEqualTo(LogoImage.MaxStoredBytes);
        using (var stored = SKBitmap.Decode(png)) (stored.Width, stored.Height).ShouldBe((600, 200));
        (await app.InScopeAsync(sp => sp.GetRequiredService<InfraDbContext>().AuditRecords.AnyAsync(r => r.Action == "settings.logo"))).ShouldBeTrue();
        (await EventsAsync()).Skip(before).ShouldHaveSingleItem().Png.ShouldBe(png);

        // The thermal raster: the paper's width at most, whole bytes a row, and the black square is ink.
        foreach (var (width, dots) in new[] { (58, 384), (80, 576) })
        {
            var raster = await admin.GetFromJsonAsync<JsonElement>($"/api/settings/business/logo/receipt?width={width}");
            var (w, h, bits) = (raster.GetProperty("width").GetInt32(), raster.GetProperty("height").GetInt32(), raster.GetProperty("bits").GetBytesFromBase64());
            (w % 8, w <= dots, h <= dots / 4, bits.Length).ShouldBe((0, true, true, w / 8 * h));
            bits[h / 2 * (w / 8) + w / 16].ShouldBe((byte)0xFF); // the middle of the square
            bits[0].ShouldBe((byte)0); // white paper in the corner
        }

        // The receipt says which logo to print; the printed e-CF has it top left.
        var cashier = await app.CashierWithShiftAsync();
        var soda = await admin.ProductAsync(await admin.CategoryAsync(), "Refresco", 59m, stock: 10);
        var sale = await cashier.SellAsync(soda, 59m);
        sale.GetProperty("receipt").GetProperty("logoVersion").GetString().ShouldBe(version);
        var encf = sale.GetProperty("eNcf").GetString()!;
        var document = await app.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync(d => d.ENcf == encf));
        var html = await admin.GetStringAsync($"/api/fiscal/documents/{document.Id}/printed");
        html.ShouldContain($"<img class=\"logo\" src=\"data:image/png;base64,{Convert.ToBase64String(png)}\"");

        // Connecting to the cloud later sends it too.
        await app.InScopeAsync(async sp =>
        {
            await sp.GetRequiredService<IEnumerable<ICloudSnapshot>>().OfType<LogoSnapshot>().Single().EnqueueAsync(CancellationToken.None);
            return await sp.GetRequiredService<InfraDbContext>().SaveChangesAsync();
        });
        (await EventsAsync()).Count.ShouldBe(before + 2);

        // Removed: no logo anywhere, and the cloud is told.
        (await admin.DeleteAsync("/api/settings/business/logo")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.GetFromJsonAsync<BusinessProfile>("/api/settings/business"))!.LogoVersion.ShouldBeNull();
        (await (await admin.GetAsync("/api/settings/business/logo")).ErrorCode()).ShouldBe("Settings.NoLogo");
        (await admin.GetAsync("/api/settings/business/logo/receipt?width=80")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await EventsAsync())[^1].Png.ShouldBeNull();
        (await admin.GetStringAsync($"/api/fiscal/documents/{document.Id}/printed")).ShouldNotContain("class=\"logo\"");
    }

    [Fact]
    public void R12_a_small_file_with_a_huge_canvas_is_refused_before_decoding()
    {
        using var flat = new SKBitmap(3000, 2100); // 6.3 MP of one colour: a few KB as PNG
        flat.Erase(SKColors.White);
        using var data = flat.Encode(SKEncodedImageFormat.Png, 100);
        data.Size.ShouldBeLessThan(LogoImage.MaxUploadBytes);
        LogoImage.Normalize(data.ToArray()).Error!.Code.ShouldBe("Settings.LogoTooLarge");
    }

    [Fact]
    public void A_detailed_photo_shrinks_until_it_fits_one_cloud_event_and_transparency_prints_as_paper()
    {
        // Noise does not compress: 600 px of it is far over 47 KB, so it comes out smaller.
        using var noise = new SKBitmap(600, 600);
        var random = new Random(280);
        for (var y = 0; y < 600; y++) for (var x = 0; x < 600; x++) noise.SetPixel(x, y, new SKColor((uint)random.Next() | 0xFF000000));
        using var data = noise.Encode(SKEncodedImageFormat.Jpeg, 95);
        var png = LogoImage.Normalize(data.ToArray()).Value;
        png.Length.ShouldBeLessThanOrEqualTo(LogoImage.MaxStoredBytes);
        using (var shrunk = SKBitmap.Decode(png)) shrunk.Width.ShouldBeLessThan(600);

        // A fully transparent logo is no ink at all (not a black block).
        using var clear = new SKBitmap(100, 50);
        clear.Erase(SKColors.Transparent);
        using var clearPng = clear.Encode(SKEncodedImageFormat.Png, 100);
        LogoImage.Raster(clearPng.ToArray(), 576).Bits.ShouldAllBe(b => b == 0);
    }
}
