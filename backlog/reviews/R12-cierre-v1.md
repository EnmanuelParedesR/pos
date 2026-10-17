# R12 · Cierre de V1: revisión de seguridad, revisión de código y auditoría Ponytail (17/10/2026)

Tres revisiones sobre `main` (solo lectura, por áreas en paralelo) y sus arreglos aplicados, cada uno con prueba. El detalle por punto está en:

- `R12-fiscal-arreglos.md`: e-CF enviado dos veces, RFCE trabado, doble "Reemplazar", errores que no son de la DGII, recorridos de tabla, fecha del QR, semilla de terceros, archivo, ANECF, consultas a la DGII.
- `R12-nube-arreglos.md`: puerta antes de autenticar en `/api/nodes/*` (subidas de 1 GB), 2FA (fallos, códigos con hash, repetición, `reset-2fa`), respaldos (PC robada, 60 días, cuota, 429), referidos y anual, panel (Safari < 16.4, tema, Talia, CSP del logo, `sharp`), tamaño de la base, una sola instancia, acuses tardíos, consultas pesadas, eventos muertos, capacidad de OpenAI, Paddle sin confiar en el navegador.
- `R12-tienda-arreglos.md`: reloj de licencia (doble cuenta, congelado, futuro, sello), licencia vieja que deshace un bloqueo, licencia atada al RNC, Administradores fuera del tope de cajeros, devuelta en RD$0, referencia en abonos, lote de facturas, respaldos y restauración, `/health`, balanza.

Tickets que salieron de la revisión y se cerraron: TK-304 (búsqueda por nombre en la caja, carrito por cajero, idempotencia de inventario y gaveta, flujo de dinero), TK-305 (pruebas inestables arregladas de raíz, instalador con tiempos límite y sin telemetría, limpieza de docs), TK-308 (prueba de 30 días firmada, RNC en la licencia, hora del servidor firmada), TK-302 (borradores de términos y privacidad).

Después de V1: TK-306 (RFCE que nunca aparece), TK-289, TK-290, TK-284.

Estado al cierre: compilación sin advertencias; `dotnet test Abastalia.slnx` 930 correctas y 4 omitidas, dos corridas seguidas; integración en SQL Express verde; panel 206/206; nube y panel desplegados en `rg-abastalia-prod` (base de 64 GB, sitio detrás del código de acceso).

Lo que no se puede cerrar desde el código (lista 3 de `docs/v1-alcance.md`): DGII (TesteCF/CerteCF y preguntas TK-177), contador (606/607/608, TK-161), equipos reales (balanza, impresora), certificado de firma de código, cuenta en vivo de Paddle, abogado (términos y privacidad), cuentas de Google/Bing y el piloto.
