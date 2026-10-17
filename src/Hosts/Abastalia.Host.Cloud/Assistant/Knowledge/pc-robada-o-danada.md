---
id: pc-robada-o-danada
title: Me robaron o se dañó una PC de la tienda
audience: panel
links: [/app/stores, /precios#contacto]
sources: [docs/guia-pc-robada.md, docs/instalador-web.md, docs/contingencia-ecf.md, src/Cloud/panel/src/pages/StoresPage.tsx]
---

## Me robaron la PC principal de la tienda. ¿Qué hago?
Si robaron o perdió la PC principal de Abastalia, haga esto el mismo día, en orden: 1) llame a la entidad que emitió su certificado digital (.p12) y pida revocarlo, porque con él se pueden firmar facturas a nombre del negocio, y pida uno nuevo; 2) avise a Abastalia para dar de baja esa PC en la nube; 3) desde la PC nueva, cambie el PIN del dueño y de los supervisores; 4) instale Abastalia como "PC principal" y restaure el respaldo más nuevo con su frase; 5) importe el certificado nuevo en Configuración → Comprobantes y escriba otra vez la frase de los respaldos; 6) vuelva a unir cada caja con un código nuevo (Configuración → Equipos). Ponga la denuncia y, si hay facturas que no reconoce, avise a la DGII.

## Se dañó la PC principal. ¿Cómo paso Abastalia a una computadora nueva?
Para mudar Abastalia a una PC nueva: instale Abastalia como "PC principal" y restaure el respaldo más nuevo desde Configuración → Respaldos con su frase. Si el respaldo está en la nube, descárguelo primero en el panel (Tiendas → «Respaldos en la nube» → Descargar) y cópielo a la carpeta de respaldos de Abastalia en la PC nueva. El respaldo trae los datos, no las llaves: hay que importar otra vez el certificado .p12, escribir la frase de los respaldos y volver a unir cada caja. Los números e-NCF siguen desde el respaldo; si la PC vieja emitió después, cargue los rangos desde el siguiente número libre. Mientras no pueda emitir e-CF, use la contingencia con serie B.

## Me robaron una caja (no la principal). ¿Qué hago?
Si robaron una PC de caja de Abastalia: en la PC principal vaya a Configuración → Equipos y pulse «Revocar» en esa caja; su certificado deja de valer al instante y sus sesiones se cierran. Cambie el PIN de quienes entraban en esa caja. La caja nueva se une con un código nuevo. Los datos están en la PC principal, así que no se pierde nada.

## ¿Cómo protejo la tienda antes de que pase? Cifrado y respaldos
Para estar preparado con Abastalia: active el cifrado del disco en la PC principal (BitLocker en Windows Pro o Cifrado de dispositivo en Windows Home); Configuración → Salud avisa si falta. Guarde la clave de recuperación de BitLocker fuera de la PC. Active los respaldos fuera de la PC (Configuración → Respaldos → "Copiar también a" y "Copiar también a la nube") y guarde la frase de los respaldos en papel. Sin un respaldo reciente no hay cómo seguir en otra PC.

## Me robaron una memoria con respaldos
Los respaldos de Abastalia están cifrados con su frase. Si la frase es débil o sospecha que alguien la conoce, cámbiela en Configuración → Respaldos y haga un respaldo nuevo enseguida; los respaldos viejos se siguen abriendo con la frase vieja, así que bórrelos de donde estén. Cambie también los PIN de los usuarios.
