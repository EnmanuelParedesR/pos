# ADR-0012: El producto se llama Abastalia y la asistente, Talia

- Fecha: 2026-10-16
- Estado: aceptada (TK-267, issue #237)
- Relacionadas: `docs/mascota.md`, `docs/sistema-de-diseno.md`, ADR-0008 (nube)

## Contexto

El dueño decidió cambiar el nombre Toki por **Abastalia** (de *abasto/abastecer*). El dominio `abastalia.com` estaba libre (RDAP de Verisign, 16 oct 2026) y cuesta menos de US$ 20 al año. Para la asistente se pidió "Ava" o un derivado; **Ava** choca con AvaPOS (punto de venta con asistente por WhatsApp, mismo nicho), así que se eligió **Talia**, que sale del propio nombre (abas-*talia*).

## Decisión

- Todo lo que decía Toki dice Abastalia: proyectos y namespaces (`Abastalia.*`), solución (`Abastalia.slnx`), textos de la interfaz, instalador (`AbastaliaSetup-*.exe`, instancia SQL `ABASTALIA`), variables de entorno (`ABASTALIA_TEST_DB`, `ABASTALIA_SIGNTOOL`, …), claves de `localStorage` (`abastalia.*`), cabeceras (`X-Abastalia-Background`), `custom_data.abastalia` de Paddle y nombres de Azure.
- La asistente del panel es **Talia** ("Pregúntale a Talia"); el prompt la presenta como "Talia, la asistente de Abastalia".
- **Se mantiene a propósito:** el formato del respaldo cifrado (bytes mágicos `TOKIRSP2` e info HKDF `toki-respaldo`), para que un respaldo hecho antes siga abriendo; y el prefijo de tickets `TK-` (hay más de 260 issues con ese prefijo).
- Los recursos de Azure no se pueden renombrar: se despliega `rg-abastalia-prod` con el mismo `main.bicep` y, cuando el nuevo responda, se borra `rg-toki-prod` (con confirmación del dueño).

## Consecuencias

- Las bases locales y de prueba se recrean: el outbox guarda el nombre completo del tipo de cada evento (`Toki.…` → `Abastalia.…`). No hay clientes en producción, así que no se migra.
- Licencias: la llave de la nube pasa a llamarse `abastalia-license`. La llave de emisión fuera de línea no cambia (solo la carpeta: `%USERPROFILE%\Abastalia-licencias`).
- Logo, colores y mascota se revisan en una sesión de diseño aparte (el ticket con cara sigue mientras tanto).
