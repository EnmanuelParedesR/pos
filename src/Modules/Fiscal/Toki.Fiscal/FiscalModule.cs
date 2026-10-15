using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Toki.BuildingBlocks;
using Toki.BuildingBlocks.Persistence;
using Toki.Configuracion.Contracts;
using Toki.Fiscal.Contracts;
using Toki.Fiscal.Domain;
using Toki.SharedKernel;

namespace Toki.Fiscal;

public static class FiscalModule
{
    public static IServiceCollection AddFiscalModule(this IServiceCollection services)
    {
        services.AddModuleDbContext<FiscalDbContext>();
        services.AddScoped<IComprobantesFiscales, ComprobantesFiscales>();
        services.AddSingleton<IEcfFirmador, FirmadorSimulado>();
        return services;
    }
}

internal sealed class FiscalDbContext(DbContextOptions<FiscalDbContext> options) : ModuleDbContext(options)
{
    protected override string Schema => "fiscal";

    public DbSet<Secuencia> Secuencias => Set<Secuencia>();
    public DbSet<DocumentoFiscal> Documentos => Set<DocumentoFiscal>();
}

internal sealed class FiscalConfiguration : IEntityTypeConfiguration<Secuencia>, IEntityTypeConfiguration<DocumentoFiscal>
{
    public void Configure(EntityTypeBuilder<Secuencia> b)
    {
        b.ToTable("SecuenciasEcf");
        b.Property(s => s.Tipo).HasConversion<int>();
        b.Ignore(s => s.Usados);
        b.Ignore(s => s.Total);
        b.Ignore(s => s.PorcentajeUsado);
    }

    public void Configure(EntityTypeBuilder<DocumentoFiscal> b)
    {
        b.ToTable("DocumentosEcf");
        b.Property(d => d.ENcf).HasMaxLength(13);
        b.HasIndex(d => d.ENcf).IsUnique();
        b.HasIndex(d => d.ReferenciaId);
        b.HasIndex(d => new { d.Estado, d.FechaLimiteEnvioUtc });
        b.Property(d => d.Tipo).HasConversion<int>();
        b.Property(d => d.Estado).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.CodigoSeguridad).HasMaxLength(6);
        b.Property(d => d.ENcfModificado).HasMaxLength(13);
        b.Property(d => d.RncComprador).HasMaxLength(11);
        b.Property(d => d.XmlFirmado).HasMaxLength(ModuleDbContext.TextoLargo * 4);
        b.Property(d => d.UltimoError).HasMaxLength(2000);
    }
}

/// <summary>Signs the e-CF XML. Returns the signed document and its security code (first 6 characters of the signature).</summary>
internal interface IEcfFirmador
{
    /// <summary>True for the stand-in signer: its documents are stored as Simulado and never sent.</summary>
    bool EsSimulado { get; }
    (string XmlFirmado, string CodigoSeguridad) Firmar(XDocument xml);
}

/// <summary>Stand-in until the real certificate and provider are contracted (e-CF on hold, see pos-ecf-deferred).</summary>
/// <remarks>shortcut: hash instead of XMLDSig with the taxpayer's .p12; replace with CertStoreEcfSigner before certification (TK-055).</remarks>
internal sealed class FirmadorSimulado : IEcfFirmador
{
    public bool EsSimulado => true;

    public (string XmlFirmado, string CodigoSeguridad) Firmar(XDocument xml)
    {
        var firma = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting))));
        xml.Root!.Add(new XElement("FirmaSimulada", firma));
        return (xml.ToString(SaveOptions.DisableFormatting), firma[..6]);
    }
}

internal sealed class ComprobantesFiscales(FiscalDbContext db, IConfiguracionConsultas configuracion, IEcfFirmador firmador, IOutbox outbox, TimeProvider reloj)
    : IComprobantesFiscales
{
    public static readonly TimeSpan PlazoEnvio = TimeSpan.FromHours(72);

    public async Task<Result<ComprobanteEmitido>> EmitirAsync(DocumentoAEmitir doc, CancellationToken ct)
    {
        if (doc.Tipo == TipoEcf.CreditoFiscal31 && !Rnc.EsValido(doc.RncComprador)) return FiscalErrors.RncRequerido;
        if (doc.Tipo == TipoEcf.Consumo32 && doc.Total >= LimitesEcf.ConsumoIdentificado && !Rnc.EsValido(doc.RncComprador)) return FiscalErrors.RncRequerido;
        if (doc.Tipo is TipoEcf.NotaCredito34 or TipoEcf.NotaDebito33 && (doc.ENcfModificado is null || doc.CodigoModificacion is null))
            return FiscalErrors.ReferenciaRequerida;
        var negocio = await configuracion.ObtenerNegocioAsync(ct);
        if (!Rnc.EsValido(negocio.Rnc)) return FiscalErrors.SinRncEmisor;

        var ahora = reloj.GetUtcNow().UtcDateTime;
        var numero = await ReservarAsync(doc.Tipo, DiaNegocio.De(ahora), ct);
        if (numero is null) return FiscalErrors.SinSecuencia(doc.Tipo);

        var encf = ENcf.Formatear(doc.Tipo, numero.Value);
        var (xml, codigo) = firmador.Firmar(ArmarXml(doc, encf, negocio, ahora));

        var documento = new DocumentoFiscal
        {
            Id = Guid.CreateVersion7(), ENcf = encf, Tipo = doc.Tipo, ReferenciaId = doc.ReferenciaId, ENcfModificado = doc.ENcfModificado,
            RncComprador = doc.RncComprador, NombreComprador = doc.NombreComprador, Itbis = doc.Itbis, Total = doc.Total,
            FechaFirmaUtc = ahora, CodigoSeguridad = codigo, XmlFirmado = Comprimir(xml), Estado = firmador.EsSimulado ? EstadoEnvio.Simulado : EstadoEnvio.Pendiente,
            FechaLimiteEnvioUtc = ahora + PlazoEnvio,
        };
        db.Documentos.Add(documento);
        outbox.Agregar(new ComprobanteEmitidoV1(documento.Id, encf, doc.Tipo, doc.ReferenciaId, doc.Total, doc.Itbis));
        return new ComprobanteEmitido(documento.Id, encf, codigo, ahora, UrlQr(negocio.Rnc, doc, encf, ahora, codigo));
    }

    /// <summary>Takes the next number of the oldest valid range. The update locks the range row until commit; a rollback gives the number back.</summary>
    private async Task<long?> ReservarAsync(TipoEcf tipo, DateOnly hoy, CancellationToken ct)
    {
        var candidatos = await db.Secuencias.Where(s => s.Tipo == tipo && s.Siguiente <= s.Hasta && s.VenceEl >= hoy)
            .OrderBy(s => s.Desde).Select(s => s.Id).ToListAsync(ct);
        foreach (var id in candidatos)
        {
            var filas = await db.Secuencias.Where(s => s.Id == id && s.Siguiente <= s.Hasta)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Siguiente, x => x.Siguiente + 1), ct);
            if (filas == 1) return await db.Secuencias.Where(s => s.Id == id).Select(s => s.Siguiente - 1).SingleAsync(ct);
        }
        return null;
    }

    private static XDocument ArmarXml(DocumentoAEmitir doc, string encf, DatosNegocio negocio, DateTime fechaUtc)
    {
        var local = fechaUtc + DiaNegocio.Desfase;
        return new XDocument(new XElement("ECF",
            new XElement("Encabezado",
                new XElement("TipoeCF", (int)doc.Tipo),
                new XElement("eNCF", encf),
                new XElement("RNCEmisor", negocio.Rnc),
                new XElement("RazonSocialEmisor", negocio.Nombre),
                new XElement("FechaEmision", local.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture)),
                new XElement("RNCComprador", doc.RncComprador),
                new XElement("RazonSocialComprador", doc.NombreComprador),
                new XElement("NCFModificado", doc.ENcfModificado),
                new XElement("CodigoModificacion", (int?)doc.CodigoModificacion),
                new XElement("TipoPago", (int)doc.TipoPago),
                new XElement("MontoTotal", doc.Total.ToString("0.00", CultureInfo.InvariantCulture)),
                new XElement("TotalITBIS", doc.Itbis.ToString("0.00", CultureInfo.InvariantCulture))),
            new XElement("DetallesItems", doc.Lineas.Select((l, i) => new XElement("Item",
                new XElement("NumeroLinea", i + 1),
                new XElement("NombreItem", l.Descripcion),
                new XElement("CantidadItem", l.Cantidad.ToString("0.###", CultureInfo.InvariantCulture)),
                new XElement("PrecioUnitarioItem", l.PrecioUnitario.ToString("0.00", CultureInfo.InvariantCulture)),
                new XElement("DescuentoMonto", l.Descuento.ToString("0.00", CultureInfo.InvariantCulture)),
                new XElement("IndicadorFacturacion", Indicador(l.Tasa)),
                new XElement("MontoItem", l.Total.ToString("0.00", CultureInfo.InvariantCulture))))),
            new XElement("FechaHoraFirma", local.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture))));
    }

    // DGII billing indicator: 1 = 18 %, 2 = 16 %, 3 = 0 %, 4 = exempt.
    private static int Indicador(TasaItbis t) => t switch { TasaItbis.General18 => 1, TasaItbis.Reducida16 => 2, TasaItbis.Cero => 3, _ => 4 };

    private static string UrlQr(string? rncEmisor, DocumentoAEmitir doc, string encf, DateTime fechaUtc, string codigo)
    {
        var local = fechaUtc + DiaNegocio.Desfase;
        return "https://ecf.dgii.gov.do/ecf/ConsultaTimbre"
            + $"?RncEmisor={rncEmisor}&RncComprador={doc.RncComprador}&ENCF={encf}"
            + $"&FechaEmision={local:dd-MM-yyyy}&MontoTotal={doc.Total.ToString("0.00", CultureInfo.InvariantCulture)}"
            + $"&FechaFirma={Uri.EscapeDataString(local.ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture))}&CodigoSeguridad={Uri.EscapeDataString(codigo)}";
    }

    private static byte[] Comprimir(string texto)
    {
        using var salida = new MemoryStream();
        using (var gzip = new GZipStream(salida, CompressionLevel.SmallestSize)) gzip.Write(Encoding.UTF8.GetBytes(texto));
        return salida.ToArray();
    }
}
