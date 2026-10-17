using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;
using Abastalia.BuildingBlocks;
using Abastalia.BuildingBlocks.Endpoints;
using Abastalia.BuildingBlocks.Messaging;
using Abastalia.BuildingBlocks.Persistence;
using Abastalia.BuildingBlocks.Security;
using Abastalia.Settings.Contracts;
using Abastalia.SharedKernel;
using Abastalia.Sync.Contracts;

namespace Abastalia.Settings;

/// <summary>TK-280: the business logo. What the owner uploads (PNG or JPG) is never kept as it came: it is decoded and written again as a plain PNG,
/// at most <see cref="MaxSide"/> px a side and <see cref="MaxStoredBytes"/> (one cloud event carries it), so the receipt, the e-CF page and the
/// public catalog only ever see an image this code wrote. It lives in the Business row, so every backup has it.</summary>
internal static class LogoImage
{
    public const int MaxUploadBytes = 512 * 1024;
    public const int MaxSide = 600;
    /// <summary>The cloud takes events of up to 64 KB of JSON; 48 KB of PNG is 64 KB in Base64.</summary>
    public const int MaxStoredBytes = 47 * 1024;
    /// <summary>A 512 KB PNG can still claim a huge canvas (a decompression bomb): refused before decoding. R12: 6 MP (24 MB decoded, was 100 MB);
    /// a logo is a few hundred pixels a side.</summary>
    internal const long MaxPixels = 6_000_000;

    public static readonly Error TooManyPixels = Error.Validation("Settings.LogoTooLarge",
        "El logo tiene demasiados píxeles (más de 6 megapíxeles). Achíquelo a unos 600 px de ancho y súbalo de nuevo.");

    public static readonly Error TooBig = Error.Validation("Settings.LogoTooBig", "El logo pasa de 512 KB. Use una imagen más liviana (PNG o JPG).");
    public static readonly Error WrongType = Error.Validation("Settings.LogoType", "El logo debe ser una imagen PNG o JPG.");
    public static readonly Error Unreadable = Error.Validation("Settings.LogoUnreadable", "No se pudo leer esa imagen. Guárdela de nuevo como PNG o JPG e intente otra vez.");
    public static readonly Error NotFound = Error.NotFound("Settings.NoLogo", "El negocio no tiene logo.");

    public static bool IsPng(ReadOnlySpan<byte> b) => b.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    public static bool IsJpeg(ReadOnlySpan<byte> b) => b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]);

    /// <summary>The uploaded file as the PNG to keep, or why not. The type comes from the first bytes, and the decoder must agree with them.</summary>
    /// <remarks>shortcut: a JPG's EXIF rotation is ignored (logos are rarely phone photos); apply <c>SKCodec.EncodedOrigin</c> if owners upload sideways ones.</remarks>
    public static Result<byte[]> Normalize(byte[] file)
    {
        if (file.Length > MaxUploadBytes) return TooBig;
        var expected = IsPng(file) ? SKEncodedImageFormat.Png : IsJpeg(file) ? SKEncodedImageFormat.Jpeg : (SKEncodedImageFormat?)null;
        if (expected is null) return WrongType;
        using var data = SKData.CreateCopy(file);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat != expected) return Unreadable;
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        if (w <= 0 || h <= 0) return Unreadable;
        if ((long)w * h > MaxPixels) return TooManyPixels;
        using var source = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(source.Info, source.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return Unreadable;

        // A detailed photo may not fit the cloud's limit at 600 px: smaller until it does (a logo is flat colour and fits at the first try).
        for (var scale = Math.Min(1.0, (double)MaxSide / Math.Max(w, h)); ; scale *= 0.75)
        {
            var png = Encode(source, Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
            if (png.Length <= MaxStoredBytes) return png;
            if (Math.Max(w, h) * scale < 64) return TooBig;
        }
    }

    private static byte[] Encode(SKBitmap source, int width, int height)
    {
        using var resized = source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var png = resized.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    /// <summary>The logo for a thermal printer of <paramref name="dots"/> dots a line (384 on 58 mm, 576 on 80 mm): one ink, Floyd–Steinberg
    /// dithered over white paper, at most the paper's width and a quarter of it tall (18 mm on 80 mm paper), rows of whole bytes with the first pixel in the high bit.</summary>
    public static ReceiptLogo Raster(byte[] png, int dots)
    {
        using var source = SKBitmap.Decode(png);
        var scale = Math.Min(2.0, Math.Min((double)dots / source.Width, dots / 4.0 / source.Height)); // a small logo grows at most 2×, or it blurs
        var (w, h) = (Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
        using var bitmap = source.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKCubicResampler.Mitchell));

        var gray = new float[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = bitmap.GetPixel(x, y);
                var lum = 0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue;
                gray[y * w + x] = 255 - c.Alpha / 255f * (255 - lum); // transparent is paper
            }

        var stride = (w + 7) / 8;
        var bits = new byte[stride * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var old = gray[y * w + x];
                var ink = old < 128;
                if (ink) bits[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
                var err = old - (ink ? 0 : 255);
                if (x + 1 < w) gray[y * w + x + 1] += err * 7 / 16;
                if (y + 1 >= h) continue;
                if (x > 0) gray[(y + 1) * w + x - 1] += err * 3 / 16;
                gray[(y + 1) * w + x] += err * 5 / 16;
                if (x + 1 < w) gray[(y + 1) * w + x + 1] += err * 1 / 16;
            }
        return new ReceiptLogo(stride * 8, h, bits);
    }
}

/// <param name="Width">Dots a row, a multiple of 8.</param>
/// <param name="Bits">One bit a dot (1 = ink), <c>Width / 8</c> bytes a row, top row first.</param>
internal sealed record ReceiptLogo(int Width, int Height, byte[] Bits);

internal static class BusinessLogo
{
    /// <summary>The cache key the receipt and the profile carry: it changes with each upload.</summary>
    public static string? Version(DateTime? changedUtc) => changedUtc?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public sealed record Response(byte[] Png, string Version);

    /// <param name="File">The PNG or JPG, in Base64.</param>
    public sealed record Upload(string File) : ICommand<Ok>;

    public sealed record Remove : ICommand<Ok>;

    internal sealed class Validator : AbstractValidator<Upload>
    {
        // Base64 is 4 characters for every 3 bytes: a bit over 512 KB of file still reaches the handler, which says it is too big.
        public Validator() => RuleFor(c => c.File).NotEmpty().WithMessage("Elija una imagen PNG o JPG.")
            .MaximumLength(800_000).WithMessage("El logo pasa de 512 KB. Use una imagen más liviana (PNG o JPG).");
    }

    internal sealed class UploadHandler(SettingsDbContext db, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Upload, Ok>
    {
        public async Task<Result<Ok>> Handle(Upload c, CancellationToken ct)
        {
            byte[] file;
            try { file = Convert.FromBase64String(c.File); }
            catch (FormatException) { return LogoImage.Unreadable; }
            var png = LogoImage.Normalize(file);
            if (png.IsFailure) return png.Error!;
            var b = await db.Business.SingleAsync(ct);
            (b.Logo, b.LogoChangedUtc) = (png.Value, clock.GetUtcNow().UtcDateTime);
            outbox.Add(new BusinessLogoChangedV1(png.Value));
            // The image itself stays out of the audit trail: its size and when are enough.
            await audit.RecordAsync("settings.logo", "Business", Guid.Empty, new { UploadedBytes = file.Length, StoredBytes = png.Value.Length }, ct);
            return Ok.Value;
        }
    }

    internal sealed class RemoveHandler(SettingsDbContext db, IOutbox outbox, IAuditLog audit, TimeProvider clock) : ICommandHandler<Remove, Ok>
    {
        public async Task<Result<Ok>> Handle(Remove c, CancellationToken ct)
        {
            var b = await db.Business.SingleAsync(ct);
            if (b.Logo is null) return Ok.Value;
            (b.Logo, b.LogoChangedUtc) = (null, clock.GetUtcNow().UtcDateTime);
            outbox.Add(new BusinessLogoChangedV1(null));
            await audit.RecordAsync("settings.logo-removed", "Business", Guid.Empty, new { }, ct);
            return Ok.Value;
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            // Any signed-in user: the cashier's register prints it.
            app.MapGet("/api/settings/business/logo", async (SettingsDbContext db, CancellationToken ct) =>
                    await db.Business.AsNoTracking().Select(b => new { b.Logo, b.LogoChangedUtc }).SingleAsync(ct) is { Logo: { } png } b
                        ? Results.Ok(new Response(png, Version(b.LogoChangedUtc)!))
                        : EndpointExtensions.Problem(LogoImage.NotFound))
                .RequireAuthorization().WithTags("Settings");
            app.MapGet("/api/settings/business/logo/receipt", async (int? width, SettingsDbContext db, CancellationToken ct) =>
                    await db.Business.AsNoTracking().Select(b => b.Logo).SingleAsync(ct) is { } png
                        ? Results.Ok(LogoImage.Raster(png, width == 58 ? 384 : 576))
                        : EndpointExtensions.Problem(LogoImage.NotFound))
                .RequireAuthorization().WithTags("Settings");
            app.MapPut("/api/settings/business/logo", async (Upload c, ICommandHandler<Upload, Ok> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
            app.MapDelete("/api/settings/business/logo", async (ICommandHandler<Remove, Ok> h, CancellationToken ct) =>
                    (await h.Handle(new Remove(), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermission(Permissions.SettingsManage).WithTags("Settings");
        }
    }
}

/// <summary>On connecting to the cloud: the logo uploaded before, so the public catalog shows it.</summary>
internal sealed class LogoSnapshot(SettingsDbContext db, IOutbox outbox) : ICloudSnapshot
{
    public async Task EnqueueAsync(CancellationToken ct)
    {
        if (await db.Business.AsNoTracking().Select(b => b.Logo).SingleAsync(ct) is { } png) outbox.Add(new BusinessLogoChangedV1(png));
    }
}
