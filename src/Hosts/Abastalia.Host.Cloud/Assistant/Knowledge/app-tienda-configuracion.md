---
id: app-tienda-configuracion
title: Configuración de la app de la tienda
audience: panel
links: [/app/stores]
sources: [src/Hosts/Abastalia.Host.Cloud/Assistant/PanelHelp.cs, src/Client/Abastalia.Client.UI/Pages/Admin/Settings.razor, src/Client/Abastalia.Client.UI/Components/Admin.razor, src/Client/Abastalia.Client.UI/Help/HelpContent.cs]
---

## ¿Qué hay en la Administración de la app de la tienda?
En la app de Abastalia en la PC de la tienda, la Administración tiene: Resumen, Productos, Inventario, Compras y gastos, Turnos y cuadres, Clientes, Pedidos y catálogo, Facturas, Reportes, Auditoría, Comprobantes e-CF (Comprobantes electrónicos) y Configuración. Cada persona ve solo las secciones de sus permisos. F2 lleva a vender y F1 abre la ayuda de la pantalla.

## ¿Dónde cambio el nombre, el RNC o el logo del negocio?
En la app de la tienda de Abastalia, Configuración → Negocio: nombre, RNC, dirección, teléfono y pie de la factura, que salen en cada factura y en los reportes para la DGII. En Configuración → Negocio → Logo suba un PNG o JPG de hasta 512 KB: sale arriba en la factura impresa, en la representación impresa de los e-CF, en el cierre Z, en el estado de cuenta y en el catálogo en línea. Cada sección de Configuración se guarda por separado.

## ¿Dónde creo cajas, fijo el fondo o cargo las secuencias e-NCF?
En la app de la tienda de Abastalia: Configuración → Cajas para crear y renombrar cajas y su fondo estándar (abrir con otro monto pide un supervisor). Configuración → Comprobantes para cargar las secuencias NCF/e-NCF que autorizó la DGII (cópielas tal como aparecen en la autorización), importar el certificado digital (.p12 o .pfx, la contraseña se escribe una vez y no se guarda) y elegir el ambiente de la DGII: Pre-certificación (TesteCF), Certificación (CerteCF) o Producción (eCF). Abastalia avisa en el Resumen cuando quedan pocos números y 30 días antes de que venza el certificado.

## ¿Cómo uno otra caja o pruebo la impresora y la balanza?
En la PC principal de Abastalia, Configuración → Equipos → «Generar código»: escriba ese código en la PC nueva, en "Unir a una tienda"; vale 10 minutos y sirve una vez. La huella del servidor que muestra la caja debe ser igual a la que ve el dueño. En Equipos también se revoca una PC perdida. En cada caja, Configuración → Equipos de la caja: elija y pruebe la impresora, la gaveta y la balanza conectadas a esa PC.

## ¿Dónde creo usuarios, cambio un PIN o desactivo a un empleado?
En la app de la tienda de Abastalia, Configuración → Usuarios: cree a cada persona con su rol y su PIN (use el rol Cajero para quien solo vende), cambie el PIN o pulse «Desactivar» cuando alguien deja de trabajar; desactivar libera su lugar de cajero y guarda su historial. Los roles y sus permisos se editan en Configuración → Roles. El plan limita cuántos cajeros puede tener habilitados.

## ¿Dónde están los respaldos, la nube, la licencia y las actualizaciones?
En la app de la tienda de Abastalia: Configuración → Respaldos para la frase de los respaldos (guárdela en papel), «Respaldar ahora», restaurar y copiar también a otro disco o a la nube. Configuración → Nube para conectar la tienda al panel con el código de Tiendas → Conectar una caja, ver el estado y desconectarla. Configuración → Licencia para ver el plan y «Activar licencia». Salud dice qué revisar, y Actualizaciones muestra la versión de cada PC. Configuración → Autorizaciones decide qué acciones piden supervisor.
