# ADR-0007: Pantallas en Blazor (Hybrid en la tienda, web para desarrollo)

- Fecha: 2026-10-15
- Estado: aceptada

## Contexto

El backend V1 está completo (M0–M10). Faltaban las pantallas de caja y de administración. La arquitectura ya pedía WPF + BlazorWebView con un sistema de diseño propio (Escuadra) y pantallas en una Razor Class Library. Para construir y verificar rápido hacía falta ver las mismas pantallas en un navegador, contra la API real.

## Decisión

- `src/Client/Toki.Escuadra` (RCL): tokens en CSS (`escuadra.css`), componentes, íconos y el logo C5. Marca fija de Toki: no hay tema por revendedor.
- `src/Client/Toki.Client.UI` (RCL): las pantallas (`Paginas/`), los diálogos (`Componentes/`), la sesión, el carrito y un cliente HTTP escrito a mano (`TokiApi`).
- Dos anfitriones con el mismo `Routes`:
  - `Toki.Client.Wpf`: la app de la tienda. WPF + BlazorWebView. El token del equipo se guarda cifrado con DPAPI para el usuario de Windows.
  - `Toki.Client.Web`: solo para desarrollo y pruebas. Blazor Server interactivo. El token va en un archivo del perfil, y recargar la página cierra la sesión.
- Las pantallas no repiten reglas del backend. Mandan el comando y reaccionan al `code` de ProblemDetails:
  - `Autorizacion.Requerida` abre el diálogo único de supervisor y se reintenta con el id de la autorización.
  - `Ventas.TotalCambio` muestra el total del servidor y pide confirmarlo.
  - Sin conexión se ve en ámbar y nunca se pierde el carrito.
- Teclado primero: teclas F visibles, foco siempre de vuelta al escáner y diálogos que reciben cualquier tecla. `escuadra.js` impide que el navegador use F1–F12 (F5 recargaría a mitad de una venta).
- Pruebas con bUnit sobre una API falsa (`tests/Toki.Client.UI.Tests`). La verificación visual se hace con el host web contra la API real.

## Consecuencias

- El cliente HTTP escrito a mano reemplaza por ahora a Kiota. Cuando la API se estabilice, se genera desde OpenAPI (`shortcut:` en `Contratos.cs`).
- Balanza e impresora quedan simuladas: el peso se escribe y la factura es una vista previa (TK-117, TK-118).
- En la tienda, el cliente necesita confiar en el certificado del nodo. Mientras no esté fijado (TK-122 con TK-091), el piloto usa HTTP en la red de la tienda o un certificado de confianza.
- Recargar el host web cierra la sesión, porque la sesión vive en el circuito. En WPF no se recarga.
