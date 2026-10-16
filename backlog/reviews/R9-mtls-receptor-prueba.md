# R9 · TK-091, receptor e-CF, prueba, alertas, compras con IA (f0beef5..61704e1)

Alcance: `f0beef5..HEAD` (261 archivos). Leídos completos: `Toki.Host.Cloud/Fe/*`, `Relay/ColdArchive.cs`, `Alerts/Alerts.cs`, `Assistant/SupplierInvoiceReader.cs`, `Accounts/Members.cs`, `StoreCertificateAuthority.cs`, `Toki.Host.Local/Program.cs`, `NodeCertificatePin.cs`, `Session.cs`, `SetupDevice.razor`, `Backups.cs`, `Contingency.cs`, `Certification.cs`, `EcfExchange.cs`, `Receiver.cs`, `License.cs`, `SampleData.cs`, `SupplierInvoiceAi.cs`, `DgiiReconciliation.cs` y las migraciones nuevas; diffs de Identity, Fiscal, Sales/Orders, Customers y Members. Solo lectura: no se compiló ni se corrieron pruebas. 21 hallazgos: 4 🔴, 10 🟡, 7 🟢.

Comprobado sin problema:
- **mTLS:** el servidor exige el uso ClientAuth y la caja ServerAuth, así que el certificado de una caja no se hace pasar por el servidor ni al revés. Un certificado desconocido nunca cae al secreto, y revocar invalida en cada petición.
- **XML del receptor:** sin DTD, sin resolver, con tope de 1 MB. La firma vale solo si cubre todo el documento. El firmante de la nube solo acepta ARECF, ACECF y semillas, y la llave en Key Vault no es exportable y tiene purge protection.
- **Correcciones de R8:** cerradas #1, #3, #4, #5, #6, #7, #12, #14, #19, #20, #22, #23, la carrera de dueños (SERIALIZABLE) y el costo oculto en panel y asistente.
- **Restauración y bajas:** el nombre del respaldo no permite recorrer rutas, y el enlace de baja de alertas usa un token de 256 bits, va después del `#` y se envía por POST.

## Alta

1. 🔴 **El receptor público acepta e-CF firmados por cualquiera** (`Fe/FeSigning.cs:79-91`, `Fe/FeReceiver.cs:50-52,77-81,102-111`).
   - **Problema:** la firma se valida solo contra el certificado incluido en el mismo XML, sin la cadena INDOTEL ni el vínculo con `RNCEmisor`. `validacioncertificado` acepta una semilla autofirmada.
   - **Qué pasa:** un E31 falso con el RNC de un suplidor real se guarda, se responde con un ARECF Estado 0 firmado por el cliente, y el e-CF verdadero llega después como "3 duplicado" y se pierde.
   - **Arreglo:**
     - Validar la cadena INDOTEL en la recepción y en `validacioncertificado`.
     - Confirmar con la DGII (estado o timbre) antes de pasar el documento a la tienda.
     - Ante un mismo e-NCF con contenido distinto, guardar ambos "en conflicto" en vez de responder 3.
2. 🔴 **El receptor no limita peticiones y cada intento firma en Key Vault** (`Program.cs:57-64`, `FeReceiver.cs:84-91,132-141`, `FeSigning.cs:64,150-160`, `FeVault.cs:48-53,73`).
   - **Qué pasa:** cada POST anónimo hace dos llamadas al vault, que es uno solo para todos los clientes, y guarda una fila en `FeSignature`. Además, `Sign` es síncrono y la validación XSD corre bajo un `lock` global.
   - **Arreglo:**
     - Limitador por IP y por RNC.
     - Cachear el cliente criptográfico y el certificado por tenant.
     - No guardar los ARECF de rechazo.
     - Validación XSD en paralelo sin `lock`, y `SignAsync`.
3. 🔴 **Restaurar un respaldo rebobina las secuencias e-NCF y los equipos revocados** (`Settings/Backups.cs:126-165,286-299`).
   - **Qué pasa:** se reutilizan e-NCF que la DGII ya aceptó, una caja revocada vuelve a valer, y las cajas emparejadas después del respaldo quedan fuera.
   - **Arreglo:**
     - Leer el `Next` de cada secuencia y las revocaciones antes de restaurar, y después reaplicar `Next = max(antes, restaurado)` y las revocaciones.
     - Pedir confirmación explícita si el respaldo es más viejo que el último e-CF firmado.
4. 🔴 **El archivo frío pierde el XML de producción si el mismo e-NCF existió en TesteCF o CerteCF** (`CloudDbContext.cs:123`, `Relay/EcfRelay.cs:40-43`, `Relay/ColdArchive.cs:98-101`, `EcfSubmissionQueue.cs:142-147`).
   - **Arreglo:** poner el ambiente en el índice único y en la clave del blob, o no archivar las pruebas. Migración que separe las filas existentes por ambiente.

## Media

5. 🟡 **El receptor confunde ambientes** (`FeModel.cs:128`, `FeReceiver.cs:80`, `FeEndpoints.cs:152`).
   - **Arreglo:** el ambiente en el índice y en la comprobación de duplicado; "Registrar compra" solo para `ecf`.
6. 🟡 **Un tenant puede ocupar el RNC de otro en el receptor** (`FeEndpoints.cs:106-122`, `Fiscal/Features/Receiver.cs:105`, `FeModel.cs:107`).
   - **Arreglo:**
     - Índice único filtrado por los receptores activos.
     - Exigir que el certificado corresponda al RNC (o a la cédula delegada).
     - Verificación manual cuando el RNC esté en disputa.
7. 🟡 **R8 #2 deja un hueco ante una caída** (`EcfSubmissionQueue.cs:45,95`).
   - **Qué pasa:** si el proceso se cae después de que la DGII aceptó y antes de guardar, el siguiente intento reenvía sin consultar primero.
   - **Arreglo:** consultar con `FindAsync` también cuando `SentUtc` no es nulo.
8. 🟡 **Si la CA de la tienda no se puede leer, el servicio no arranca** (`StoreCertificateAuthority.cs:126-135`, `Toki.Host.Local/Program.cs:125`).
   - **Arreglo:** apartar la CA dañada, crear una nueva y avisar en salud que hay que volver a unir las cajas.
9. 🟡 **"Unir a una tienda" envía el código antes de confirmar la huella** (`SetupDevice.razor:28-39,95-104`).
   - **Arreglo:** casilla obligatoria "La huella es igual" antes de enviar, como en Instalar.
10. 🟡 **La aprobación comercial compite entre el panel y la tienda** (`FeEndpoints.cs:175-190`).
    - **Arreglo:** guardar la decisión con un `UPDATE` condicional (0 filas = releer).
11. 🟡 **Regularizar un B04 pierde la factura que modifica** (`Contingency.cs:218-219,225-226`).
    - **Arreglo:** no regularizar 33 ni 34 automáticamente hasta confirmar con la DGII o el contador.
12. 🟡 **La prueba de 14 días arranca con ventas de certificación o de ejemplo** (`License.cs:186-192`).
    - **Arreglo:** contar solo ventas reales: e-CF de `eCF`, o simuladas sin productos de ejemplo.
13. 🟡 **En Restringido, las cajas sobrantes no pueden cerrar su turno** (`Sessions.cs:56-57`, `CloseRegister.razor:112`).
    - **Arreglo:** permitir la sesión solo para cerrar un turno abierto, o cerrar turnos ajenos desde la PC principal.
14. 🟡 **La lectura de facturas con IA no tiene cuota ni mira la licencia** (`SupplierInvoiceReader.cs:16,95-122`).
    - **Arreglo:** cuota diaria por tenant, limitador por nodo, y la licencia del último latido.

## Baja

15. 🟢 El certificado del servidor no se guarda y se emite uno nuevo en cada reinicio; además deja un archivo de llave por reinicio (`StoreCertificateAuthority.cs:47,114`).
16. 🟢 Restaurar en una PC nueva no trae la CA ni el certificado e-CF: documentarlo, o incluir `keys` en el respaldo cifrado.
17. 🟢 El XML recibido se guarda reserializado y siempre como UTF-8: guardar los bytes originales y respetar la codificación (`FeReceiver.cs:128,173`).
18. 🟢 La contingencia no hace cumplir los 15 y 30 días, y el 607 sale con B sin regularizar (`Contingency.cs:24-45,199-214`).
19. 🟢 Al desplegar TK-153 se reparten todos los 31 viejos: limitar a los firmados desde el despliegue (`EcfExchange.cs:131-137`).
20. 🟢 El chequeo de RNC del certificado (R8 #4) se salta si el RNC no estaba configurado al importar: revisarlo también al guardar el RNC.
21. 🟢 En la nube se pueden registrar correos ajenos antes de que llegue la invitación, lo que la bloquea: exigir correo confirmado o limpiar las cuentas sin confirmar.

Estado de R8: #2 sigue abierta por el hallazgo 7, #4 por el 20 y #11 por el 9; #9 quedó resuelta con TK-091.

## Estado de las correcciones

TK-229: nube (#1, #2, #4, #5, #6, #10, #14, #17, #21) y tienda (#3, #7, #8, #9, #11, #12, #13, #15, #16, #18, #19, #20).
