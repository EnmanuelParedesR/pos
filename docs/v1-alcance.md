# Alcance de Abastalia V1 (16/10/2026)

V1 = lo que una tienda piloto necesita para vender, facturar e-CF con la DGII y que el dueño la vea desde la nube, cobrado con Paddle. Cada ticket del tablero queda en una de cuatro listas.

## 1. V1 hecho en código (cerrado o cerrable)
Venta, caja y cuadre ciego; inventario, compras y 606/607/608; clientes y fiado; permisos finos y autorización de supervisor atada al contenido (R11); e-CF directo a la DGII desde la tienda (TK-144 a TK-150, TK-152 a TK-155); receptor en la nube firmado en la tienda (TK-246); consultas públicas a la DGII con caché en tabla (TK-252); copia del respaldo cifrado a la nube (TK-238); nube: réplica, panel, consola con 2FA, receptor, correo, alertas, cuotas con medidor de firmas y 429 respetado (TK-248), cobro con Paddle (TK-249/258), referidos (TK-255/276), cobro por cajero (TK-277), panel separado del sitio (TK-278), umbral del reloj (TK-156), sitio con código de acceso antes del lanzamiento (TK-285), bloqueo por falta de pago también sin conexión (TK-279), actualizaciones por canal (TK-254), asistente Talia; sitio público con SEO (TK-259 a TK-263); instalador y actualizaciones firmadas (TK-242, falta compilarlo); seguridad R11 completa (TK-253, TK-268); logo del negocio en recibo, A4 y catálogo (TK-280); regla del reloj también para ACECF/ARECF (TK-283); alerta de uso justo de acuses (TK-286); el bloqueo llega con la siguiente subida y la caja bloqueada puede cerrar sesión (TK-287); inicio con LCP móvil < 2.5 s (TK-282).

## 2. V1 con código pendiente (los hace el equipo ahora)
| Ticket | Qué falta |
|---|---|
| TK-160 | Probar el instalador y el servicio en la VM de Azure |
| TK-288 | Logo del negocio en el cierre Z y el estado de cuenta |

## 3. V1 bloqueado por terceros o por el dueño (el código está; falta validar)
| Ticket | Bloqueo |
|---|---|
| TK-141, TK-158, TK-237, TK-144–150, TK-152–155, TK-177 | Acceso a TesteCF/CerteCF, certificado de prueba y respuestas de la DGII |
| TK-117, TK-118, TK-114, TK-093 | Probar con balanza, impresora y PC de caja reales |
| TK-160, TK-242, TK-196 | VM limpia, Inno Setup en una PC, certificado de firma de código, alojar el instalador |
| TK-156 | Llave de la API de estatus de la DGII (`statusecf.dgii.gov.do`, §11 #6) y revisión de la guía de contingencia por el dueño (D15); el reloj (D17) ya está |
| TK-249 | Token del cliente y secreto del webhook de Paddle; cuenta en vivo |
| TK-262, TK-264 | Cuentas de Google/Bing del dueño |
| TK-161, TK-172 | Contador; entrevistas con dueños (piloto) |
| TK-219, TK-236 | Precio y condiciones del acompañamiento |

## 4. Después de V1
TK-159 (E41 de compras), TK-247 (custodia en la nube con HSM), TK-266 (versión ligera en la nube), TK-199 (canal de contadores y revendedores), TK-222 por WhatsApp, TK-224 lote de PDF reales (se valida con el piloto), lectura de e-NCF con código de seguridad (TK-252).

## Cierre de V1
Cuando las listas 2 y 3 estén vacías: revisión de seguridad, revisión de código y auditoría Ponytail con sus arreglos aplicados, CI en verde y la tienda piloto certificada (TK-237).
