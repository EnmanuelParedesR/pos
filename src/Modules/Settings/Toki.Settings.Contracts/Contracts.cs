namespace Toki.Settings.Contracts;

/// <summary>Shop data printed on receipts and limits that apply to every register.</summary>
/// <param name="TopeDescuentoPct">Largest discount (percent of each line, after the global discount) a cashier may give without a supervisor.</param>
public sealed record DatosNegocio(string Nombre, string? Rnc, string? Direccion, string? Telefono, string? PieRecibo, decimal TopeDescuentoPct);

public interface IConfiguracionConsultas
{
    Task<DatosNegocio> ObtenerNegocioAsync(CancellationToken ct);
}
