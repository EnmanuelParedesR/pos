using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Toki.BuildingBlocks.Endpoints;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Inventory.Contracts;
using Toki.Inventory.Domain;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.Inventory.Features;

internal enum MotivoAjuste { Merma, Danio, ConsumoInterno, Regalo, Correccion }

/// <summary>Adds or removes stock with a reason. Removing more than there is fails: count first.</summary>
internal static class AjustarInventario
{
    public sealed record Command(Guid ProductoId, Guid? AlmacenId, decimal Cantidad, MotivoAjuste Motivo, string? Nota) : ICommand<Response>;
    public sealed record Response(Guid AjusteId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Cantidad).NotEqual(0).PrecisionScale(19, 3, true);
            RuleFor(c => c.Motivo).IsInEnum();
            RuleFor(c => c.Nota).MaximumLength(180);
        }
    }

    internal sealed class Handler(InventarioDbContext db, MovimientosInventario movimientos, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            var almacen = c.AlmacenId ?? Almacen.PrincipalId;
            if (!await db.Almacenes.AnyAsync(a => a.Id == almacen, ct)) return InventarioErrors.AlmacenNoEncontrado;
            var id = Guid.CreateVersion7();
            var linea = new[] { new LineaMovimiento(c.ProductoId, Math.Abs(c.Cantidad)) };
            var motivo = string.IsNullOrWhiteSpace(c.Nota) ? c.Motivo.ToString() : $"{c.Motivo}: {c.Nota.Trim()}";
            var r = c.Cantidad > 0
                ? await movimientos.EntradaAsync(TipoMovimiento.Ajuste, id, linea, actualizarCosto: false, motivo, ct, almacen)
                : await movimientos.SalidaAsync(TipoMovimiento.Ajuste, id, linea, permitirNegativoSiProductoLoPermite: false, motivo, ct, almacen);
            if (r.IsFailure) return r.Error!;
            await auditoria.RegistrarAsync("inventario.ajuste", "Producto", c.ProductoId, c, ct);
            return new Response(id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventario/ajustes", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermiso(Permisos.InventarioAjustar).WithTags("Inventario");
    }
}

internal static class TransferirInventario
{
    public sealed record Linea(Guid ProductoId, decimal Cantidad);
    public sealed record Command(Guid Origen, Guid Destino, IReadOnlyList<Linea> Lineas) : ICommand<Response>;
    public sealed record Response(Guid TransferenciaId);

    internal sealed class Validator : AbstractValidator<Command>
    {
        public Validator()
        {
            RuleFor(c => c.Destino).NotEqual(c => c.Origen).WithMessage("El destino debe ser otro almacén.");
            RuleFor(c => c.Lineas).NotEmpty();
            RuleForEach(c => c.Lineas).ChildRules(l => l.RuleFor(x => x.Cantidad).GreaterThan(0).PrecisionScale(19, 3, true));
        }
    }

    internal sealed class Handler(InventarioDbContext db, MovimientosInventario movimientos, IAuditoria auditoria) : ICommandHandler<Command, Response>
    {
        public async Task<Result<Response>> Handle(Command c, CancellationToken ct)
        {
            if (await db.Almacenes.CountAsync(a => (a.Id == c.Origen || a.Id == c.Destino) && a.Activo, ct) != 2) return InventarioErrors.AlmacenNoEncontrado;
            var id = Guid.CreateVersion7();
            var lineas = c.Lineas.Select(l => new LineaMovimiento(l.ProductoId, l.Cantidad)).ToList();
            var salida = await movimientos.SalidaAsync(TipoMovimiento.TransferenciaSalida, id, lineas, permitirNegativoSiProductoLoPermite: false, null, ct, c.Origen);
            if (salida.IsFailure) return salida.Error!;
            var entrada = await movimientos.RegistrarEntradaAsync(TipoMovimiento.TransferenciaEntrada, id, lineas, actualizarCosto: false, ct, c.Destino);
            if (entrada.IsFailure) return entrada.Error!;
            await auditoria.RegistrarAsync("inventario.transferencia", "Transferencia", id, c, ct);
            return new Response(id);
        }
    }

    internal sealed class Endpoint : IEndpoint
    {
        public void Map(IEndpointRouteBuilder app) =>
            app.MapPost("/api/inventario/transferencias", async (Command c, ICommandHandler<Command, Response> h, CancellationToken ct) =>
                    (await h.Handle(c, ct)).ToHttp())
                .RequirePermiso(Permisos.InventarioTransferir).WithTags("Inventario");
    }
}
