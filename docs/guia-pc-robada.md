# Guía: robaron (o se perdió) una PC de la tienda

R11 C2. Qué hacer el mismo día, en orden. Lo hace el dueño con el técnico de Abastalia.

## Antes de que pase

- **Cifrado del disco en la PC principal.** BitLocker (Windows Pro) o Cifrado de dispositivo (Windows Home con TPM): Configuración de Windows → Privacidad y seguridad → Cifrado de dispositivo. El instalador avisa si falta y Configuración → Salud lo marca ("Cifrado del disco"). Sin cifrado, quien se lleva la PC puede leer la base de datos (ventas, clientes con RNC y cédula), los respaldos sin cifrar que haya en el disco y las llaves de `C:\ProgramData\Abastalia\keys`.
- **La clave de recuperación de BitLocker** se guarda fuera de la PC: en la cuenta Microsoft del dueño o impresa con los papeles de la empresa. Nunca en la misma PC ni en una memoria que viva pegada a ella.
- **Respaldos fuera de la PC** (Configuración → Respaldos → "Copiar también a" y "Copiar también a la nube"), con la frase guardada por el dueño. Sin respaldo reciente no hay cómo seguir en otra PC. La copia en la nube (TK-238) la descarga solo un Dueño desde el panel (Tiendas → "Respaldos en la nube"); va cifrada con la frase, que Abastalia no conoce.
- La llave del certificado e-CF y la de cada caja quedan en el TPM cuando la PC lo tiene: no se pueden copiar a otra PC. Aun así, con la PC encendida o con la sesión de Windows abierta se pueden **usar**, por eso los pasos de abajo.

## Si robaron la PC principal

1. **Certificado e-CF (lo primero).** Llamar a la entidad que lo emitió (la del `.p12`) y **revocarlo**. Con él se pueden firmar facturas electrónicas a nombre del negocio. Pedir uno nuevo.
2. **Nube.** En el panel (Equipos o Nodos), dar de baja la PC robada para que no pueda subir datos ni pedir firmas. Si el negocio dio consentimiento para el receptor en la nube (Key Vault), retirarlo y volver a darlo con el certificado nuevo.
3. **Usuarios.** Cambiar el PIN del dueño y de los supervisores desde la PC nueva (los PIN viajan en la base de datos y en los respaldos). Si alguien usaba el mismo número en otro lado, que lo cambie también.
4. **PC nueva.** Instalar Abastalia ("PC principal") y restaurar **el respaldo más nuevo** con su frase (`docs/instalador-web.md`, "Mudarse a una PC nueva"). Importar el certificado e-CF nuevo (Configuración → Comprobantes) y escribir otra vez la frase de los respaldos.
5. **Cajas.** La PC nueva tiene otra autoridad de certificados: cada caja se vuelve a unir con un código nuevo (Configuración → Equipos). Salud lista las que faltan.
6. **Números e-NCF.** Siguen desde el respaldo. Si la PC robada emitió después del respaldo, cargar los rangos desde el siguiente número libre (TK-221) y revisar con la DGII los comprobantes que haya recibido de esa PC.
7. **DGII y denuncia.** Poner la denuncia y, si hubo facturas emitidas que el negocio no reconoce, avisar a la DGII con la denuncia y la fecha de revocación del certificado.
8. **Contraseñas de la PC.** Cambiar la contraseña de Windows del dueño y la del Wi-Fi de la tienda si estaban guardadas en esa PC.

## Si robaron una caja

1. Configuración → Equipos → **Revocar** esa caja. Su certificado deja de valer al instante y sus sesiones se cierran.
2. Cambiar el PIN de quienes entraban en esa caja.
3. La caja nueva se une con un código nuevo. Los datos están en la PC principal: no se pierde nada.

## Si roban un respaldo o una memoria con respaldos

El respaldo está cifrado con la frase. Si la frase es débil o se sospecha que la conocen: cambiarla (Configuración → Respaldos) y hacer un respaldo nuevo enseguida; los viejos siguen abriéndose con la frase vieja, así que se borran de donde estén. Cambiar los PIN como arriba.
