using Shouldly;
using Toki.BuildingBlocks.Persistence;
using Toki.SharedKernel;

namespace Toki.UnitTests.SharedKernel;

public sealed class SharedKernelTests
{
    [Theory]
    [InlineData(1.005, 1.01)]
    [InlineData(1.004, 1.00)]
    [InlineData(-1.005, -1.01)]
    public void Redondeo_de_dinero_lejos_de_cero(decimal monto, decimal esperado) => Dinero.Redondear(monto).ShouldBe(esperado);

    [Fact]
    public void El_dia_de_negocio_es_hora_de_Santo_Domingo()
    {
        // 02:30 UTC on the 16th is still 22:30 on the 15th in the Dominican Republic.
        DiaNegocio.De(new DateTime(2026, 10, 16, 2, 30, 0, DateTimeKind.Utc)).ShouldBe(new DateOnly(2026, 10, 15));
        var (desde, hasta) = DiaNegocio.RangoUtc(new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 15));
        desde.ShouldBe(new DateTime(2026, 10, 15, 4, 0, 0));
        desde.Kind.ShouldBe(DateTimeKind.Utc);
        hasta.Kind.ShouldBe(DateTimeKind.Utc);
        hasta.ShouldBe(new DateTime(2026, 10, 16, 4, 0, 0));
    }

    [Fact]
    public void La_cadena_de_auditoria_detecta_un_cambio()
    {
        var r = new RegistroAuditoria { Secuencia = 1, Id = Guid.NewGuid(), FechaUtc = DateTime.UtcNow, Accion = "a", RecursoTipo = "t", Datos = "{}", HashPrevio = new byte[32] };
        r.Hash = CadenaAuditoria.Hash(r);
        var copia = CadenaAuditoria.Hash(r);
        copia.ShouldBe(r.Hash);

        r.Datos = "{\"monto\":1}";
        CadenaAuditoria.Hash(r).ShouldNotBe(copia);
    }

    [Theory]
    [InlineData(118.00, TasaItbis.General18, 18.00)]
    [InlineData(116.00, TasaItbis.Reducida16, 16.00)]
    [InlineData(100.00, TasaItbis.Exento, 0)]
    [InlineData(100.00, TasaItbis.Cero, 0)]
    [InlineData(185.00, TasaItbis.General18, 28.22)]
    public void El_itbis_incluido_se_extrae_del_precio(decimal monto, TasaItbis tasa, decimal itbis) => Itbis.Incluido(monto, tasa).ShouldBe(itbis);

    [Theory]
    [InlineData("131245678", true)]
    [InlineData("00112345678", true)]
    [InlineData("1312456", false)]
    [InlineData("13124567A", false)]
    public void Formato_de_RNC_o_cedula(string valor, bool valido) => Rnc.EsValido(valor).ShouldBe(valido);

    [Fact]
    public void Un_pago_va_a_las_facturas_mas_viejas_primero_sin_pasar_su_saldo()
    {
        var abiertas = new[] { ("A", 100m), ("B", 0m), ("C", 250m), ("D", 50m) };
        Asignacion.EnOrden(300m, abiertas).ShouldBe([("A", 100m), ("C", 200m)]);
        Asignacion.EnOrden(1000m, abiertas).Sum(x => x.Monto).ShouldBe(400m); // the rest (600) is left to the caller
        Asignacion.EnOrden(0m, abiertas).ShouldBeEmpty();
    }

    [Fact]
    public void Un_resultado_fallido_no_entrega_valor()
    {
        Result<int> r = Error.NotFound("X", "no");
        r.IsFailure.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => r.Value);
    }
}
