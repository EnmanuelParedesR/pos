using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Compras.Domain;
using Toki.SharedKernel;

namespace Toki.Compras.Features;

/// <summary>Creates (Id null) or updates a supplier.</summary>
internal static class GuardarSuplidor
{
    public sealed record Command(Guid? Id, string Nombre, string? Rnc, string? Telefono, string? Contacto, int DiasCredito, bool Activo = true) : ICommand<Response>;
    public sealed record Response(Guid Id);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Nombre).NotEmpty().MaximumLength(150);
            RuleFor(c => c.Rnc).Must(r => Rnc.EsValido(Rnc.Normalizar(r))).When(c => !string.IsNullOrWhiteSpace(c.Rnc)).WithMessage("El RNC debe tener 9 dígitos (u 11 si es cédula).");
            RuleFor(c => c.Telefono).MaximumLength(30);
            RuleFor(c => c.Contacto).MaximumLength(100);
            RuleFor(c => c.DiasCredito).InclusiveBetween(0, 365);
        }
    }

    internal sealed class Handler(ComprasDbContext db, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var s = c.Id is { } id ? await db.Suplidores.FirstOrDefaultAsync(x => x.Id == id, ct) : null;
            if (c.Id is not null && s is null) return ComprasErrors.SuplidorNoEncontrado;
            if (s is null) db.Suplidores.Add(s = new Suplidor { Id = Guid.CreateVersion7() });
            s.Nombre = c.Nombre.Trim();
            s.Rnc = Rnc.Normalizar(c.Rnc);
            s.Telefono = c.Telefono?.Trim();
            s.Contacto = c.Contacto?.Trim();
            s.DiasCredito = c.DiasCredito;
            s.Activo = c.Activo;
            await auditoria.RegistrarAsync("compras.guardar-suplidor", "Suplidor", s.Id, c, ct);
            return new Response(s.Id);
        }
    }

    internal sealed record Body(string Nombre, string? Rnc, string? Telefono, string? Contacto, int DiasCredito, bool Activo = true);

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app)
        {
            app.MapPost("/api/compras/suplidores", async (Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(null, b.Nombre, b.Rnc, b.Telefono, b.Contacto, b.DiasCredito, b.Activo), ct)).ToHttp(r => Results.Created($"/api/compras/suplidores/{r.Id}", r)))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
            app.MapPut("/api/compras/suplidores/{id:guid}", async (Guid id, Body b, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(new Command(id, b.Nombre, b.Rnc, b.Telefono, b.Contacto, b.DiasCredito, b.Activo), ct)).ToHttp(_ => Results.NoContent()))
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
            app.MapGet("/api/compras/suplidores", async (string? buscar, ComprasDbContext db, CancellationToken ct) =>
                {
                    var q = db.Suplidores.AsNoTracking();
                    if (!string.IsNullOrWhiteSpace(buscar)) q = q.Where(s => s.Nombre.Contains(buscar.Trim()) || s.Rnc == Rnc.Normalizar(buscar));
                    return Results.Ok(await q.OrderBy(s => s.Nombre).Take(200).ToListAsync(ct));
                })
                .RequirePermiso(Permisos.ComprasGestionar).WithTags("Compras");
        }
    }
}
