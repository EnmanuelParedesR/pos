# Instalador desde la web (TK-196, plan) 🟡

Plan corto de lo que falta para que una persona baje Toki de la web y empiece la prueba de 14 días **sin técnico**. No está construido; hoy se instala con `deploy/install.ps1` sobre una publicación de `Toki.Host.Local`. La parte dentro de la app (prueba desde la primera venta, avisos, Restringido, "Comprar licencia", asistente de un equipo y datos de ejemplo) sí está hecha.

## Qué tiene que hacer el instalador

1. **Comprobar el equipo:** Windows 10/11 de 64 bits, espacio libre (≥ 5 GB con SQL Server), .NET no hace falta (publicación *self-contained*). Pedir permisos de administrador una sola vez.
2. **Base de datos:** instalar **SQL Server Express 2025** en silencio si no hay una instancia `TOKI` (`/Q /IACCEPTSQLSERVERLICENSETERMS /ACTION=Install /INSTANCENAME=TOKI /TCPENABLED=0`), solo escuchando en la PC. **No LocalDB:** corre como el usuario que inició sesión y no como servicio, así que no sirve para el nodo que atiende a las cajas (`docs/arquitectura-v1.md`, decisión ya tomada). Si ya existe la instancia, reutilizarla.
3. **Servicio:** lo mismo que `deploy/install.ps1`: copiar a `C:\Program Files\Toki`, crear el servicio `Toki` con su cuenta virtual, `prepare-service` (certificado del nodo, `C:\ProgramData\Toki`, login de SQL), regla de firewall 5443 en perfil privado, arrancar.
4. **App de caja:** instalar la app WPF en la misma PC con acceso directo en el escritorio y abrirla al terminar. Abre en "Primera instalación" (el servidor está en esta PC) con la casilla de datos de ejemplo.
5. **Sin licencia:** no pide código. La prueba empieza sola con la primera venta; "Comprar licencia" está en Configuración → Licencia.
6. **Desinstalar:** detener y borrar el servicio y la app; **nunca** borrar la base de datos ni `C:\ProgramData\Toki` sin preguntar (ahí están las ventas y los respaldos).
7. **Mudarse a una PC nueva (R9 #16):** se instala como arriba y se restaura el respaldo con su frase. El respaldo trae **los datos, no las llaves** de la PC vieja: `C:\ProgramData\Toki\keys` está protegido con DPAPI de esa máquina y el certificado digital de e-CF está en su almacén de Windows, no exportable. En la PC nueva hay que: importar otra vez el certificado `.p12` (Configuración → Comprobantes), volver a escribir la frase de los respaldos, y volver a unir cada caja (la tienda crea una CA nueva; las cajas viejas aparecen en salud como "vuelva a unir"). Los números e-NCF no se repiten: siguen desde lo que tenga el respaldo, así que se restaura **el respaldo más nuevo**, y si la PC vieja firmó después, se cargan los rangos desde el siguiente número libre como en TK-221.

## Empaquetado: qué usar

| Opción | A favor | En contra | Uso |
|---|---|---|---|
| **Inno Setup** (gratis) | Un `.exe` firmado que hace todo lo de arriba, incluido SQL Express y el servicio; se descarga de la web | Script propio que mantener | **Elegida para V1** |
| **Velopack** | Actualizaciones delta de la app de caja (`docs/arquitectura-v1.md` §6.3) | No instala servicios ni SQL Server | Para **actualizar**, no para la primera instalación |
| **MSIX** | Instalación limpia de la tienda de Windows | No puede instalar un servicio de Windows clásico ni SQL Express | Descartada para el nodo |
| **winget** | `winget install Toki.Toki` para técnicos | Solo distribuye: necesita el `.exe` de arriba igual | Más adelante, apuntando al mismo `.exe` |

## Pendiente antes de construirlo

- Certificado de firma de código (Authenticode) para que SmartScreen no asuste en la primera descarga.
- Parámetros exactos de la instalación silenciosa de Express 2025 y su tamaño (verificar).
- Página de descarga en la web (TK-197) con el enlace al `.exe` y la explicación de la prueba.
- Medir el embudo `instalada → primera venta` (`docs/metricas-producto.md`): el instalador no envía nada; la nube lo sabe cuando la tienda se conecta.
