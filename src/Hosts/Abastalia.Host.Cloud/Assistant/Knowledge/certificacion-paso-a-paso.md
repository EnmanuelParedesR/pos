---
id: certificacion-paso-a-paso
title: El asistente de certificación en la app, paso a paso
audience: panel
links: [/app/assistant, /app/notifications, /certificacion]
sources: [src/Modules/Fiscal/Abastalia.Fiscal.Contracts/Contracts.cs, src/Hosts/Abastalia.Host.Cloud/Assistant/PanelHelp.cs, src/Hosts/Abastalia.Host.Cloud/Assistant/CertificationFaq.cs, docs/servicio-certificacion.md]
---

## ¿Dónde sigo la certificación con la DGII en Abastalia?
En la app de la tienda de Abastalia, en Administración → Comprobantes electrónicos → Certificación con la DGII. Cada paso tiene su ayuda, y la tarjeta «¿Qué hago ahora?» dice lo siguiente. Marque cada paso cuando lo termine: el panel lo ve, Talia le dice en qué paso va cada tienda ("¿en qué paso voy de la certificación?") y, si lo activa en Avisos, le llega un correo con cada avance.

## ¿Cuáles son los pasos de la certificación en la app?
El asistente de certificación de Abastalia tiene 14 pasos: 1) solicitud FI-GDF-016 en la OFV (usted); 2) pruebas libres en Pre-certificación (TesteCF); 3) postulación; 4) set de pruebas (suba el Excel del portal); 5) pruebas de aprobaciones comerciales; 6) simulación con datos reales en CerteCF; 7) subir el PDF de cada comprobante de la simulación; 8) confirmar las URL de prueba; 9) comunicación: la DGII envía e-CF y espera el acuse; 10) aprobaciones comerciales; 11) URL de producción; 12) declaración jurada; 13) verificación del RNC por la DGII; 14) autorización: pida las secuencias de producción en la OFV, cárguelas y ponga el ambiente en eCF.

## ¿Qué hago en el paso de pruebas libres (TesteCF)?
En Abastalia, importe el certificado en Configuración → Comprobantes, ponga el ambiente en Pre-certificación (TesteCF) y haga una venta de cada tipo que use (31, 32 y 34). Esas ventas de prueba sirven para ver que la firma y el envío funcionan; no cuentan para la DGII ni entran en su 607.

## ¿Por qué la PC principal debe estar encendida durante la certificación?
En los pasos de comunicación de la certificación, la DGII envía e-CF de prueba a las direcciones de Abastalia y espera el acuse; la nube de Abastalia responde las 24 horas, pero la PC principal de la tienda firma cada acuse. Por eso debe quedar encendida y con internet durante esas pruebas, y con la hora de Windows en automático.

## El certificado digital no entra en Abastalia
Abastalia solo acepta un certificado .p12 o .pfx del mismo RNC del negocio y ya vigente. Si es de otra persona o de otro RNC, vino en token USB o está en la nube del emisor, pida uno correcto a la entidad certificadora (Avansi o Digifirma). Revise también que el RNC esté bien escrito en Configuración → Negocio y que escribe la contraseña correcta del archivo.

## ¿Cómo pido el acompañamiento de un técnico?
En la app de la tienda de Abastalia, en Comprobantes electrónicos, toque «Solicitar acompañamiento»: el personal de Abastalia recibe el aviso y usted ve "Solicitud enviada". Un técnico le guía en dos citas remotas cortas, con usted presente. Antes de la primera cita le enviamos por correo la lista de lo que debe preparar. Cuesta US$49, gratis con plan anual y gratis con cualquier plan pagado antes del 31/12/2026.
