using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Toki.BuildingBlocks.Messaging;
using Toki.BuildingBlocks.Persistence;
using Toki.Settings;
using Toki.IntegrationTests.Infra;
using Toki.Inventory.Persistence;
using Toki.SharedKernel;

namespace Toki.IntegrationTests.Plataforma;

/// <summary>TK-003/TK-004: a command writes every module, the outbox and the audit trail, or nothing at all.</summary>
public sealed class TransaccionTests(TokiApp app) : IClassFixture<TokiApp>
{
    private sealed record Estado(bool Categoria, bool Outbox, long CabezaAuditoria, string Negocio);

    private Task<Estado> EstadoAsync(Guid marca) => app.EnScopeAsync(async sp =>
    {
        var infra = sp.GetRequiredService<InfraDbContext>();
        return new Estado(
            await sp.GetRequiredService<InventarioDbContext>().Categorias.AnyAsync(c => c.Id == marca),
            await infra.Outbox.AnyAsync(m => m.Id == marca),
            (await infra.CabezaAuditoria.SingleAsync()).Secuencia,
            (await sp.GetRequiredService<ConfiguracionDbContext>().Negocio.SingleAsync()).Nombre);
    });

    private async Task<Result<Ok>> EjecutarAsync(EscribirYFallar c)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandHandler<EscribirYFallar, Ok>>().Handle(c, CancellationToken.None);
    }

    [Fact]
    public async Task Un_error_deshace_todo()
    {
        var marca = Guid.CreateVersion7();
        var antes = await EstadoAsync(marca);

        var r = await EjecutarAsync(new EscribirYFallar(marca, Falla.Error));

        r.Error!.Code.ShouldBe("Prueba.Fallo");
        (await EstadoAsync(marca)).ShouldBe(antes);
    }

    [Fact]
    public async Task Una_excepcion_deshace_todo()
    {
        var marca = Guid.CreateVersion7();
        var antes = await EstadoAsync(marca);

        await Should.ThrowAsync<InvalidOperationException>(() => EjecutarAsync(new EscribirYFallar(marca, Falla.Excepcion)));

        (await EstadoAsync(marca)).ShouldBe(antes);
    }

    [Fact]
    public async Task Sin_falla_se_guarda_todo_junto()
    {
        var marca = Guid.CreateVersion7();
        var antes = await EstadoAsync(marca);

        (await EjecutarAsync(new EscribirYFallar(marca, Falla.Ninguna))).IsSuccess.ShouldBeTrue();

        var despues = await EstadoAsync(marca);
        despues.Categoria.ShouldBeTrue();
        despues.Outbox.ShouldBeTrue();
        despues.CabezaAuditoria.ShouldBe(antes.CabezaAuditoria + 1);
        despues.Negocio.ShouldBe("NO DEBE QUEDAR");
        (await app.VerificarAuditoriaAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task La_auditoria_fuera_de_un_comando_se_rechaza() =>
        await Should.ThrowAsync<InvalidOperationException>(() => app.EnScopeAsync(async sp =>
        {
            await sp.GetRequiredService<IAuditoria>().RegistrarAsync("x", "x", Guid.Empty, new { }, CancellationToken.None);
            return 0;
        }));
}
