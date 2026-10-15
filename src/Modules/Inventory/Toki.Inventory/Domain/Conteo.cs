namespace Toki.Inventory.Domain;

internal enum EstadoConteo { Abierto, Aplicado }

/// <summary>Blind physical count: counters never see the expected quantity; differences appear only when it is applied.</summary>
internal sealed class ConteoFisico
{
    public Guid Id { get; set; }
    public Guid AlmacenId { get; set; }
    public Guid? CategoriaId { get; set; }
    public EstadoConteo Estado { get; set; }
    public Guid UsuarioId { get; set; }
    public DateTime CreadoUtc { get; set; }
    public DateTime? AplicadoUtc { get; set; }
    public List<ConteoLinea> Lineas { get; set; } = [];
}

internal sealed class ConteoLinea
{
    public Guid ConteoId { get; set; }
    public Guid ProductoId { get; set; }
    public decimal? Contado { get; set; }
    /// <summary>System stock at the moment the line was counted. Never returned by any endpoint (the count is blind).
    /// Applying the count adds Contado − SistemaAlContar, so sales made after counting are not erased.</summary>
    public decimal? SistemaAlContar { get; set; }
}
