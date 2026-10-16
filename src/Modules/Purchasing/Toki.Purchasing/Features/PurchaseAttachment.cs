using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Purchasing.Domain;
using Toki.SharedKernel;

namespace Toki.Purchasing.Features;

/// <summary>TK-240: the supplier's original invoice (the PDF or photo read with AI, or the e-CF XML) kept with the purchase and downloadable later,
/// for the accountant or a DGII audit. One per purchase: a new one replaces it only with replace=true (R10 #11). A voided purchase keeps it.</summary>
internal static class PurchaseAttachment
{
    public sealed record Command(Guid PurchaseId, byte[] Content, string ContentType, string FileName, bool Replace = false) : ICommand<Ok>;

    public static readonly Error InvalidFile = Error.Validation("Purchasing.InvalidAttachment", "Adjunte un PDF, una foto (JPG, PNG o WEBP) o el XML del e-CF, de hasta 4 MB.");
    public static readonly Error TypeMismatch = Error.Validation("Purchasing.AttachmentTypeMismatch", "El archivo no es lo que dice ser: su contenido no es un PDF, una foto o un XML.");
    public static readonly Error AlreadyAttached = Error.Conflict("Purchasing.AttachmentExists", "Esa compra ya tiene la factura original adjunta. Confirme que quiere reemplazarla.");
    public static readonly Error NoFile = Error.NotFound("Purchasing.NoAttachment", "Esa compra no tiene la factura original adjunta.");

    internal sealed class Handler(PurchasingDbContext db, IAuditLog audit, TimeProvider clock) : ICommandHandler<Command, Ok>
    {
        public async Task<Result<Ok>> Handle(Command c, CancellationToken ct)
        {
            var purchase = await db.Purchases.SingleOrDefaultAsync(p => p.Id == c.PurchaseId, ct);
            if (purchase is null) return PurchasingErrors.PurchaseNotFound;
            var file = await db.Set<PurchaseFile>().SingleOrDefaultAsync(f => f.PurchaseId == c.PurchaseId, ct);
            if (file is not null && !c.Replace) return AlreadyAttached;
            var replaced = file is not null;
            if (file is null) db.Set<PurchaseFile>().Add(file = new PurchaseFile { PurchaseId = c.PurchaseId });
            (file.Content, file.ContentType, file.FileName, file.CreatedUtc) = (c.Content, c.ContentType, c.FileName, clock.GetUtcNow().UtcDateTime);
            purchase.AttachmentName = c.FileName;
            await audit.RecordAsync("purchasing.attach-invoice", "Purchase", purchase.Id, new { purchase.Number, c.FileName, c.ContentType, Bytes = c.Content.Length, Replaced = replaced }, ct);
            return Ok.Value;
        }
    }

    internal sealed record Body(string? File, string? ContentType, string? FileName);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPut("/api/purchasing/purchases/{id:guid}/attachment", async (Guid id, Body b, bool? replace, ICommandHandler<Command, Ok> h, CancellationToken ct) =>
                {
                    byte[]? content = null;
                    try { content = b.File is null ? null : Convert.FromBase64String(b.File); }
                    catch (FormatException) { }
                    var type = b.ContentType?.Trim().ToLowerInvariant();
                    var name = Path.GetFileName(b.FileName?.Trim() ?? "");
                    if (content is not { Length: > 0 and <= PurchaseFile.MaxBytes } || type is null || !PurchaseFile.ContentTypes.Contains(type))
                        return EndpointExtensions.Problem(InvalidFile);
                    if (!PurchaseFile.Matches(type, content)) return EndpointExtensions.Problem(TypeMismatch);
                    if (name.Length == 0) name = "factura";
                    return (await h.Handle(new Command(id, content, type, name.Length > 200 ? name[^200..] : name, replace == true), ct)).ToHttp(_ => Results.NoContent());
                })
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");

            app.MapGet("/api/purchasing/purchases/{id:guid}/attachment", async (Guid id, PurchasingDbContext db, CancellationToken ct) =>
                    await db.Set<PurchaseFile>().AsNoTracking().SingleOrDefaultAsync(f => f.PurchaseId == id, ct) is { } f
                        ? Results.File(f.Content, f.ContentType, f.FileName)
                        : EndpointExtensions.Problem(NoFile))
                .RequirePermission(Permissions.PurchasingManage).WithTags("Purchasing");
        }
    }
}
