# ADR-0006: Licencia firmada (ES256) y `IFeatureService`

- Fecha: 2026-10-15
- Estado: aceptada

## Contexto

El sistema viejo traía un generador de licencias dentro del programa. `arquitectura-v1-vsa.md` §9 pide una licencia firmada por la nube, verificada con una clave pública embebida, con 15 días de gracia sin conexión, y que vender, facturar, exportar y respaldar nunca dependan de ella (`planes.md` §6).

## Decisión

- La licencia es un JWS compacto ES256 (ECDSA P-256 + SHA-256) con tienda, plan, funciones, límites, emisión y vencimiento. El nodo solo tiene la **clave pública**, escrita en el código (`ServicioLicencia`). La **clave privada** queda fuera del repositorio (es público), con el proveedor; mientras no exista la nube (fase 2), las licencias se emiten con un script local junto a esa clave.
- Se instala con `PUT /api/configuracion/licencia` (luego bajará por Sync) y se guarda en `C:\ProgramData\Toki\licencia.jws`.
- Estados: **Prueba** (sin licencia: 2 cajas), **Activa**, **Gracia** (hasta 15 días después de vencer, todo sigue igual) y **Restringida** (funciones pagas apagadas y límites de prueba). Ningún estado detiene la venta, el e-CF, las exportaciones ni los respaldos; una prueba de arquitectura verifica que Ventas, Fiscal y Caja no dependen de `IFeatureService`.
- El primer límite que se aplica es `Limite.Cajas`, al emparejar una caja. Las funciones (`Funcion.ModuloCarniceria`, …) existen para los módulos vendibles que vienen después; `GET /api/configuracion/funciones` le dice a la caja qué mostrar.
- Solo fuera de Production se puede confiar en otra clave pública (`Licencia:ClavePublica`), para que las pruebas firmen sus propias licencias.

## Consecuencias

- Perder la clave privada obliga a publicar una versión con una clave nueva; filtrarla permite emitir licencias falsas. Se guarda como un secreto del negocio, con copia de seguridad fuera de la PC.
- La licencia todavía no está atada al equipo: con la activación en la nube (fase 2) el nodo registrará su clave y la licencia llevará su identidad.
