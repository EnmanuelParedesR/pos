using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.BuildingBlocks.Security;
using Toki.Configuracion;
using Toki.Inventario.Contracts;
using Toki.Inventario.Domain;
using Toki.Inventario.Persistence;
using Toki.SharedKernel;

namespace Toki.IntegrationTests.Infra;

public enum Falla { Ninguna, Error, Excepcion }

/// <summary>Writes to two modules, the outbox and the audit trail, then fails (or not). Proves the transaction is all-or-nothing.</summary>
internal sealed record EscribirYFallar(Guid Marca, Falla Falla) : ICommand<Ok>;

/// <summary>A command that needs a supervisor, to exercise the authorization decorator.</summary>
internal sealed record AccionSupervisada(Guid RecursoId, Guid? AutorizacionSupervisorId) : ICommand<Ok>, IRequiereAutorizacion
{
    public string Permiso => Permisos.VentasAnular;
}

internal sealed class EscribirYFallarHandler(InventarioDbContext inventario, ConfiguracionDbContext config, IOutbox outbox, IAuditoria auditoria)
    : ICommandHandler<EscribirYFallar, Ok>
{
    public async Task<Result<Ok>> Handle(EscribirYFallar c, CancellationToken ct)
    {
        inventario.Categorias.Add(new Categoria { Id = c.Marca, Nombre = "marca-" + c.Marca });
        await inventario.SaveChangesAsync(ct); // an early save inside the transaction must roll back too
        await config.Negocio.ExecuteUpdateAsync(s => s.SetProperty(n => n.Nombre, "NO DEBE QUEDAR"), ct);
        outbox.Agregar(new PrecioCambiadoV1(c.Marca, 1, 2, null, null) { EventId = c.Marca });
        await auditoria.RegistrarAsync("prueba", "Prueba", c.Marca, new { }, ct);
        return c.Falla switch
        {
            Falla.Error => Error.Validation("Prueba.Fallo", "falla a propósito"),
            Falla.Excepcion => throw new InvalidOperationException("falla a propósito"),
            _ => Ok.Valor,
        };
    }
}

/// <summary>Moves stock through the public contract the way a sale (out) or a purchase (in, with cost) will.</summary>
internal sealed record MoverInventario(Guid ProductoId, decimal Cantidad, bool Entrada, decimal? Costo = null) : ICommand<Ok>;

internal sealed class MoverInventarioHandler(IMovimientosInventario movimientos) : ICommandHandler<MoverInventario, Ok>
{
    public async Task<Result<Ok>> Handle(MoverInventario c, CancellationToken ct)
    {
        var linea = new[] { new LineaMovimiento(c.ProductoId, c.Cantidad, c.Costo) };
        var r = c.Entrada
            ? await movimientos.RegistrarEntradaAsync(TipoMovimiento.Compra, Guid.CreateVersion7(), linea, actualizarCosto: true, ct)
            : await movimientos.RegistrarSalidaAsync(TipoMovimiento.Venta, Guid.CreateVersion7(), linea, ct);
        return r.IsFailure ? r.Error! : Ok.Valor;
    }
}

internal sealed class AccionSupervisadaHandler : ICommandHandler<AccionSupervisada, Ok>
{
    public Task<Result<Ok>> Handle(AccionSupervisada c, CancellationToken ct) => Task.FromResult<Result<Ok>>(Ok.Valor);
}

internal static class ComandosDePrueba
{
    public static void Registrar(IServiceCollection s)
    {
        Decorado<EscribirYFallar, EscribirYFallarHandler>(s);
        Decorado<AccionSupervisada, AccionSupervisadaHandler>(s);
        Decorado<MoverInventario, MoverInventarioHandler>(s);
    }

    // Same pipeline as production commands: Validacion → Transaccion → Autorizacion → handler.
    private static void Decorado<TCommand, THandler>(IServiceCollection s)
        where TCommand : ICommand<Ok> where THandler : class, ICommandHandler<TCommand, Ok>
    {
        s.AddScoped<ICommandHandler<TCommand, Ok>, THandler>();
        s.Decorate<ICommandHandler<TCommand, Ok>, AutorizacionDecorator<TCommand, Ok>>();
        s.Decorate<ICommandHandler<TCommand, Ok>, TransaccionDecorator<TCommand, Ok>>();
        s.Decorate<ICommandHandler<TCommand, Ok>, ValidacionDecorator<TCommand, Ok>>();
    }
}
