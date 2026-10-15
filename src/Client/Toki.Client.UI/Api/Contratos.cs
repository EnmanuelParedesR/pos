namespace Toki.Client.UI.Api;

// The local API's wire contracts (camelCase JSON, enums by name). Only what the screens use.
// shortcut: hand-written instead of generated from OpenAPI (Kiota, per the architecture doc); generate them once the API stops moving.

public enum MedioPago { Efectivo, Tarjeta, Transferencia, Fiado, SaldoFavor }
public enum TipoEcf { CreditoFiscal31 = 31, Consumo32 = 32 }
public enum NivelPrecio { Detalle, Mayorista }
public enum UnidadMedida { Unidad, Libra }
public enum TasaItbis { General18, Reducida16, Cero, Exento }
public enum TipoMovimientoCaja { Entrada, Retiro, Gasto }
public enum MotivoMovimiento { CambioAdicional, Recogida, DepositoBanco, PagoSuplidor, GastoMenor, Otro }

// Identidad
public sealed record Instalar(string NombreAdministrador, string CodigoAdministrador, string Pin, string NombreDispositivo);
public sealed record Instalado(Guid UsuarioId, Guid DispositivoId, string TokenDispositivo);
public sealed record Emparejado(Guid DispositivoId, string Nombre, string Token);
public sealed record CodigoEmparejamiento(string Codigo, DateTime ExpiraUtc);
public sealed record UsuarioActivo(Guid Id, string Nombre, string Codigo);
public sealed record SesionAbierta(string Token, Guid UsuarioId, string Nombre, DateTime ExpiraUtc);
public sealed record Yo(Guid UsuarioId, string Nombre, Guid DispositivoId, string[] Permisos);
public sealed record AutorizacionDada(Guid AutorizacionId, string Supervisor, DateTime ExpiraUtc);
public sealed record Dispositivo(Guid Id, string Nombre, bool Revocado, DateTime CreadoUtc);
public sealed record Usuario(Guid Id, string Nombre, string Codigo, Guid RolId, string Rol, bool Activo, bool Bloqueado);
public sealed record Rol(Guid Id, string Nombre, string[] Permisos);
public sealed record RolesDisponibles(Rol[] Roles, string[] PermisosDisponibles);

// Configuración
public sealed record Negocio(string Nombre, string? Rnc, string? Direccion, string? Telefono, string? PieRecibo, decimal TopeDescuentoPct);
public sealed record Funciones(string Estado, string? Plan, DateTime? VenceUtc, string[] Activas, int Cajas);
public sealed record Respaldo(string Archivo, long Bytes, DateTime FechaUtc);

// Inventario
public sealed record Categoria(Guid Id, string Nombre, string Color, string[] Notas, bool Activa);
public sealed record ProductoResumen(Guid Id, string Nombre, string Categoria, string Unidad, string TasaItbis, decimal Precio, decimal? PrecioMayorista, decimal? Existencia, bool Activo);
public sealed record ProductoDetalle(Guid Id, int Version, string Nombre, Guid CategoriaId, UnidadMedida Unidad, string? Plu, TasaItbis TasaItbis,
    decimal Precio, decimal? PrecioMayorista, decimal Costo, bool VenderSinExistencia, decimal StockMinimo, bool Activo);
public sealed record CrearProducto(string Nombre, Guid CategoriaId, UnidadMedida Unidad, string? Plu, TasaItbis TasaItbis, decimal Precio,
    decimal? PrecioMayorista, decimal Costo, bool VenderSinExistencia, decimal StockMinimo, string[]? Codigos, decimal ExistenciaInicial);
public sealed record ActualizarProducto(int Version, string Nombre, Guid CategoriaId, UnidadMedida Unidad, string? Plu, TasaItbis TasaItbis,
    decimal Precio, decimal? PrecioMayorista, bool VenderSinExistencia, decimal StockMinimo, bool Activo);
public sealed record ProductoPorCodigo(Guid ProductoId, Guid? PresentacionId, string Nombre, string Unidad, string TasaItbis, decimal Precio,
    decimal? PrecioMayorista, decimal Factor, decimal? CantidadEtiqueta, decimal? MontoEtiqueta, string[] Notas, decimal? Existencia);
public sealed record Creado(Guid Id);

// Caja
public sealed record CajaInfo(Guid Id, string Nombre, bool Activa, Guid? TurnoAbiertoId, string? Cajero);
public sealed record TurnoActual(Guid TurnoId, Guid CajaId, string Caja, decimal FondoInicial, DateTime AbiertoUtc);
public sealed record TurnoAbierto(Guid TurnoId);
public sealed record ConteoDenominacion(decimal Denominacion, int Cantidad);
public sealed record MedioZ(string Medio, decimal Cobros, decimal Anulaciones, decimal Reembolsos, decimal Abonos, decimal Neto);
public sealed record ReporteZ(Guid TurnoId, long? NumeroZ, string Caja, string Cajero, DateTime AbiertoUtc, DateTime? CerradoUtc, decimal FondoInicial,
    MedioZ[] PorMedio, int Ventas, int VentasAnuladas, decimal AbonosClientes, decimal Redondeo, decimal Entradas, decimal Retiros, decimal Gastos,
    decimal EfectivoEsperado, decimal? EfectivoContado, decimal? Diferencia);

// Ventas
public sealed record Descuento(bool EsPorcentaje, decimal Valor);
public sealed record LineaVenta(Guid ProductoId, Guid? PresentacionId, decimal Cantidad, decimal? PrecioUnitario = null, Descuento? Descuento = null,
    string? Nota = null, string? CodigoEtiqueta = null);
public sealed record Pago(MedioPago Medio, decimal Monto, string? Referencia = null);
public sealed record RegistrarVenta(Guid VentaId, Guid TurnoId, List<LineaVenta> Lineas, List<Pago> Pagos, decimal TotalEsperado,
    TipoEcf TipoComprobante, Guid? ClienteId, string? RncCliente, string? NombreCliente, NivelPrecio Nivel, Descuento? DescuentoGlobal, List<Guid> Autorizaciones);
public sealed record LineaComprobante(string Descripcion, decimal Cantidad, string Unidad, decimal Precio, decimal Descuento, decimal Total, string? Nota);
public sealed record ItbisComprobante(string Tasa, decimal Base, decimal Itbis);
public sealed record PagoComprobante(string Medio, decimal Monto, decimal? Recibido);
public sealed record Comprobante(string Negocio, string? Rnc, string? Direccion, string? Telefono, string TipoComprobante, string ENcf, long Numero,
    DateTime FechaLocal, string Caja, string Cajero, string? Cliente, string? RncCliente, LineaComprobante[] Lineas, decimal Subtotal, decimal Descuento,
    ItbisComprobante[] Itbis, decimal Total, PagoComprobante[] Pagos, decimal Cambio, string CodigoSeguridad, string UrlQr, string? Pie, bool Anulada);
public sealed record VentaRegistrada(Guid VentaId, long Numero, string ENcf, decimal Total, decimal Cambio, Comprobante Comprobante);
public sealed record VentaResumen(Guid Id, long Numero, string ENcf, DateTime FechaUtc, string Estado, string Cajero, string? Cliente, decimal Total);
public sealed record LineaDetalle(Guid Id, string Descripcion, decimal Cantidad, decimal PrecioUnitario, decimal Descuento, decimal Total, decimal Itbis,
    decimal CantidadDevuelta, string? Nota);
public sealed record PagoDetalle(string Medio, decimal Monto, decimal? Recibido, string? Referencia);
public sealed record DevolucionDetalle(Guid Id, DateTime FechaUtc, decimal Total, string Medio, string ENcf, string Motivo);
public sealed record VentaDetalle(Guid Id, long Numero, string ENcf, string TipoComprobante, DateTime FechaUtc, string Estado, string Caja, string Cajero,
    Guid? ClienteId, string? Cliente, decimal Descuento, decimal Itbis, decimal Total, decimal Cambio, string? MotivoAnulacion,
    LineaDetalle[] Lineas, PagoDetalle[] Pagos, DevolucionDetalle[] Devoluciones);
public sealed record LineaDevolucion(Guid LineaVentaId, decimal Cantidad, bool Reponer = true);
public sealed record VentaEnEspera(Guid Id, string Etiqueta, decimal Total, DateTime CreadaUtc, System.Text.Json.JsonElement Contenido);

// Clientes
public sealed record ClienteResumen(Guid Id, string Nombre, string? RncCedula, string? Telefono, string Nivel, decimal LimiteCredito, bool Activo);
public sealed record ClienteDetalle(Guid Id, string Nombre, string? RncCedula, bool CreditoFiscal, string Nivel, decimal LimiteCredito, decimal Deuda,
    decimal SaldoFavor, bool Activo);
public sealed record NuevoCliente(string Nombre, string? RncCedula, string? Telefono, string? Direccion, bool CreditoFiscal);

// Reportes y fiscal
public sealed record VentasDia(DateOnly Dia, int Ventas, decimal VentasConItbis, decimal Descuento, decimal Devoluciones, decimal Itbis,
    decimal NetoSinItbis, decimal Costo, decimal Utilidad);
public sealed record VentasProducto(Guid ProductoId, string Producto, decimal Cantidad, decimal NetoSinItbis, decimal Utilidad);
public sealed record ResumenTurno(Guid TurnoId, string Caja, string Cajero, DateTime AbiertoUtc, DateTime? CerradoUtc, long? NumeroZ, decimal FondoInicial,
    decimal? EfectivoEsperado, decimal? EfectivoContado, decimal? Diferencia);
public sealed record AlertaFiscal(string Tipo, long Disponibles, DateOnly? VenceEl, bool Alerta);
