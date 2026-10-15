using Shouldly;
using Toki.Inventario.Domain;

namespace Toki.UnitTests.Inventario;

public sealed class EtiquetaBalanzaTests
{
    private static readonly FormatoEtiqueta[] Formatos = [.. FormatoEtiqueta.Predeterminados()];

    private static string ConVerificador(string doce) => doce + EtiquetaBalanza.DigitoVerificador(doce);

    [Fact]
    public void Lee_el_peso_de_una_etiqueta_20()
    {
        // 20 + PLU 00123 + 02350 (2.350 lb)
        var r = EtiquetaBalanza.Leer(ConVerificador("200012302350"), Formatos)!;
        r.IsSuccess.ShouldBeTrue();
        r.Value.Plu.ShouldBe("00123");
        r.Value.Peso.ShouldBe(2.350m);
        r.Value.Precio.ShouldBeNull();
    }

    [Fact]
    public void Lee_el_precio_de_una_etiqueta_22()
    {
        // 22 + PLU 0456 + 038775 (RD$ 387.75)
        var r = EtiquetaBalanza.Leer(ConVerificador("220456038775"), Formatos)!;
        r.Value.Plu.ShouldBe("0456");
        r.Value.Precio.ShouldBe(387.75m);
        r.Value.Peso.ShouldBeNull();
    }

    [Fact]
    public void Un_digito_verificador_malo_es_un_error()
    {
        var bueno = ConVerificador("200012302350");
        var malo = bueno[..12] + ((bueno[12] - '0' + 1) % 10);
        EtiquetaBalanza.Leer(malo, Formatos)!.Error!.Code.ShouldBe("Inventario.EtiquetaMalLeida");
    }

    [Theory]
    [InlineData("7460312004518")]   // regular product barcode
    [InlineData("2900123023501")]   // prefix 29 not configured
    [InlineData("12345")]
    public void Lo_que_no_es_etiqueta_de_balanza_devuelve_null(string codigo) => EtiquetaBalanza.Leer(codigo, Formatos).ShouldBeNull();

    [Fact]
    public void Digito_verificador_de_un_EAN13_conocido() =>
        EtiquetaBalanza.DigitoVerificador("400638133393").ShouldBe(1); // 4006381333931
}

public sealed class CostoPromedioTests
{
    [Fact]
    public void Pondera_por_cantidad() =>
        CostoPromedio.Calcular(existencia: 10, costoActual: 100, cantidad: 30, costoEntrada: 140).ShouldBe(130m);

    [Fact]
    public void Sin_existencia_toma_el_costo_de_la_entrada() =>
        CostoPromedio.Calcular(existencia: -2, costoActual: 100, cantidad: 5, costoEntrada: 120).ShouldBe(120m);

    [Fact]
    public void Anular_una_compra_la_saca_del_promedio()
    {
        // 50 lb at 100, then 100 lb typed at 1,000 → 700. Voiding it leaves 50 lb and gives back 100.
        var promedio = CostoPromedio.Calcular(existencia: 50, costoActual: 100, cantidad: 100, costoEntrada: 1000);
        CostoPromedio.Revertir(existenciaDespues: 50, costoActual: promedio, cantidad: 100, costoEntrada: 1000).ShouldBe(100m);
        // Most of that purchase was sold already: the cost stays rather than going to zero or below.
        CostoPromedio.Revertir(existenciaDespues: 5, costoActual: 700, cantidad: 100, costoEntrada: 1000).ShouldBeNull();
        CostoPromedio.Revertir(existenciaDespues: 0, costoActual: 700, cantidad: 100, costoEntrada: 1000).ShouldBeNull();
    }
}
