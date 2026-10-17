---
id: conectar-tienda-al-panel
title: Conectar la tienda al panel y respaldos en la nube
audience: panel
links: [/app/stores]
sources: [src/Cloud/panel/src/pages/StoresPage.tsx, src/Client/Abastalia.Client.UI/Pages/Admin/Settings.razor, src/Hosts/Abastalia.Host.Cloud/Assistant/PanelHelp.cs, docs/instalador-web.md]
---

## ¿Cómo conecto mi tienda o mi caja al panel?
Para conectar la tienda al panel de Abastalia: 1) en el panel, en Tiendas, cree la tienda con «Nueva tienda» si todavía no existe; 2) en esa tienda pulse «Conectar una caja» y copie el código de conexión de 8 números (vale 30 minutos y sirve una vez); 3) en la PC principal de la tienda, en la app, vaya a Configuración → Nube y escriba ese código. Verá "Caja conectada. Sus ventas empiezan a llegar a este panel." Solo el Dueño crea tiendas y pide códigos. Si el código venció, pida uno nuevo.

## ¿Cómo desconecto una caja del panel?
En el panel de Abastalia, en Tiendas, el Dueño puede desconectar una caja conectada: esa caja deja de enviar datos al panel. También se desconecta desde la PC principal, en Configuración → Nube. Desconectar no borra las ventas de la tienda.

## ¿Cómo activo y descargo los respaldos en la nube?
En la PC principal de Abastalia: Configuración → Respaldos → «Copiar también a la nube». Desde entonces cada respaldo se copia cifrado a la nube. En el panel, en Tiendas → «Respaldos en la nube», el Dueño los ve y los descarga. Están cifrados con la frase de los respaldos: para restaurar uno en la PC necesita esa frase, que Abastalia no conoce. Para restaurarlo, copie el archivo a la carpeta de respaldos de Abastalia en la PC y use Configuración → Respaldos → Restaurar.
