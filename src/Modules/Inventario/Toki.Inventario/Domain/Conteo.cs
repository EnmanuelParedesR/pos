namespace Toki.Inventario.Domain;

internal enum EstadoConteo { Abierto, Aplicado, Cancelado }

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
    /// <summary>Stock the system had when the count was applied (filled on apply).</summary>
    public decimal? Sistema { get; set; }
}
