using Shouldly;
using Toki.Identity.Domain;

namespace Toki.UnitTests.Identidad;

public sealed class UsuarioTests
{
    private static readonly DateTime Ahora = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static Usuario Nuevo()
    {
        var u = new Usuario { Id = Guid.NewGuid(), Codigo = "01" };
        u.CambiarPin("1234");
        return u;
    }

    [Fact]
    public void El_pin_correcto_reinicia_los_intentos()
    {
        var u = Nuevo();
        u.IntentarPin("0000", Ahora).ShouldBe(ResultadoPin.Incorrecto);
        u.IntentarPin("1234", Ahora).ShouldBe(ResultadoPin.Correcto);
        u.IntentosFallidos.ShouldBe(0);
    }

    [Fact]
    public void Cinco_fallos_bloquean_cinco_minutos_y_luego_se_duplica()
    {
        var u = Nuevo();
        for (var i = 0; i < 5; i++) u.IntentarPin("0000", Ahora);
        u.BloqueadoHastaUtc.ShouldBe(Ahora.AddMinutes(5));
        u.IntentarPin("1234", Ahora.AddMinutes(4)).ShouldBe(ResultadoPin.Bloqueado);

        u.IntentarPin("0000", Ahora.AddMinutes(6)).ShouldBe(ResultadoPin.Incorrecto);
        u.BloqueadoHastaUtc.ShouldBe(Ahora.AddMinutes(16));
    }

    [Fact]
    public void El_bloqueo_no_pasa_de_una_hora()
    {
        var u = Nuevo();
        u.IntentosFallidos = 20;
        u.IntentarPin("0000", Ahora);
        u.BloqueadoHastaUtc.ShouldBe(Ahora.AddMinutes(60));
    }

    [Fact]
    public void Un_usuario_inactivo_no_entra()
    {
        var u = Nuevo();
        u.Activo = false;
        u.IntentarPin("1234", Ahora).ShouldBe(ResultadoPin.Inactivo);
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData(null, false)]
    public void Formato_del_pin(string? pin, bool valido) => Pin.EsValido(pin).ShouldBe(valido);
}
