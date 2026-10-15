namespace Toki.BuildingBlocks.Security;

/// <summary>Every action that can be granted to a role. Checked on the server, never only in the UI.</summary>
public static class Permisos
{
    public const string UsuariosGestionar = "usuarios.gestionar";
    public const string DispositivosGestionar = "dispositivos.gestionar";
    public const string ConfiguracionGestionar = "configuracion.gestionar";

    public const string ProductosGestionar = "productos.gestionar";
    public const string PreciosCambiar = "precios.cambiar";
    public const string InventarioVer = "inventario.ver";
    public const string InventarioAjustar = "inventario.ajustar";
    public const string InventarioContar = "inventario.contar";
    public const string InventarioTransferir = "inventario.transferir";

    public const string CajaOperar = "caja.operar";
    public const string CajaRetirar = "caja.retirar";
    public const string CajaAprobarCierre = "caja.aprobar-cierre";
    public const string CajaAbrirGaveta = "caja.abrir-gaveta";

    public const string VentasRegistrar = "ventas.registrar";
    public const string VentasAnular = "ventas.anular";
    public const string VentasDevolver = "ventas.devolver";
    public const string VentasDescuentoMayor = "ventas.descuento-mayor";
    public const string VentasCambiarPrecio = "ventas.cambiar-precio";

    public const string ComprasGestionar = "compras.gestionar";
    public const string ClientesGestionar = "clientes.gestionar";
    public const string FiadoGestionar = "fiado.gestionar";
    public const string FiadoExcederLimite = "fiado.exceder-limite";
    public const string FiscalGestionar = "fiscal.gestionar";
    public const string ReportesVer = "reportes.ver";

    public static readonly IReadOnlyList<string> Todos =
    [
        UsuariosGestionar, DispositivosGestionar, ConfiguracionGestionar,
        ProductosGestionar, PreciosCambiar, InventarioVer, InventarioAjustar, InventarioContar, InventarioTransferir,
        CajaOperar, CajaRetirar, CajaAprobarCierre, CajaAbrirGaveta,
        VentasRegistrar, VentasAnular, VentasDevolver, VentasDescuentoMayor, VentasCambiarPrecio,
        ComprasGestionar, ClientesGestionar, FiadoGestionar, FiadoExcederLimite, FiscalGestionar, ReportesVer,
    ];
}
