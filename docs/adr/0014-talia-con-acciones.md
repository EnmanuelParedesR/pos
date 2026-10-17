# ADR-0014: Talia con acciones: propuestas confirmadas y Talia dentro de la app de la tienda

- Fecha: 2026-10-17
- Estado: propuesta (TK-329, issue #298)
- Relacionadas: ADR-0008 (nube, nodos y su firma; asistente de solo lectura), ADR-0011 (lo que la tienda firma a pedido de la nube), `docs/talia-acciones.md` (inventario, excepciones, fases y tickets), `docs/permisos.md`, `docs/cuotas.md`, TK-224, TK-250, TK-300, TK-301

## Contexto

Talia vive en la nube (`Host.Cloud`), solo lee la réplica con 10 herramientas y la usan los miembros del panel. El dueño quiere que haga las tareas de los módulos por chat y que lleve a la pantalla correcta. Casi todas esas tareas son de la tienda: corren en la PC principal, con usuarios de la tienda (PIN, permisos finos, autorización de supervisor atada al contenido, R11). Las cuentas del panel no son usuarios de la tienda. La nube no tiene un canal general para mandar comandos a la tienda: solo baja licencias, actualizaciones, firmas del receptor y las decisiones de aprobación comercial de TK-250, siempre pedidas por la tienda.

## Decisión

1. **El modelo nunca escribe.** Cada escritura es una herramienta `propose_<acción>` con argumentos tipados. El servidor valida, resuelve las referencias con su propia búsqueda y devuelve una **propuesta** (id, tipo, argumentos resueltos, resumen escrito por código, vence en 10 minutos). Solo el clic del usuario en **Confirmar** ejecuta el comando, por el mismo handler y con el mismo permiso que su endpoint. Una propuesta por respuesta; el id es la llave de idempotencia. Lo que no se puede hacer por chat (`docs/talia-acciones.md` §2) no tiene herramienta.
2. **Escrituras de la nube**: directo en `Host.Cloud`. Propuestas y su resultado en `cloud.AssistantActions` (también es la auditoría). Confirmar: `POST /api/assistant/actions/{id}/confirm` con la política del endpoint de destino (casi siempre `PanelOwner`).
3. **Escrituras de la tienda: Talia también dentro de la app de la tienda.** La app pregunta por la API local, que llama a la nube como nodo (`POST /api/nodes/assistant/ask`, firmado como en ADR-0008, cuotas del negocio) con el usuario de la tienda y sus permisos como contexto, igual que la lectura de facturas (TK-224). La propuesta vuelve a la app, que muestra la tarjeta con datos locales y, al confirmar, llama al endpoint existente con la sesión del usuario. Solo los endpoints marcados `AllowAssistant(...)` aceptan una propuesta; cada id se ejecuta una vez y la auditoría encadenada lleva `via: talia`. Si la caja pide supervisor, sale su diálogo de siempre: el PIN nunca pasa por la nube.
4. **Navegación**: `navigate` (rutas cerradas de `Panel.tsx`) en el panel y `open_screen` (páginas de `Abastalia.Client.UI`) en la tienda, nunca fuera de una venta en pantalla.
5. **Modelo**: `gpt-5-nano`. Si el arnés de evaluación contra el modelo real da menos de 95 % de aciertos en las propuestas, solo la vuelta que propone pasa a un segundo despliegue `gpt-5-mini`.

## Opción descartada para V1: canal de comandos nube → tienda

El panel crearía el comando y la PC principal lo bajaría por el canal del nodo (sondeo largo como `receiver/sign-requests/next`, o junto al `received/pull` de TK-250), lo ejecutaría como el usuario vinculado y subiría el resultado. Se descarta para V1 porque necesita un vínculo nuevo cuenta del panel ↔ usuario de la tienda (con sus reglas contra la escalada), deja fuera todo lo que pide PIN o supervisor, agrega estados de "pendiente" con la tienda apagada y abre un camino para escribir en la tienda desde internet: una cuenta del panel robada cambiaría precios. Si el piloto pide escrituras remotas, se construye con el patrón de TK-250 (la nube guarda la decisión con quién y cuándo, la tienda la baja, la ejecuta con su auditoría y gana si decidió primero), solo para acciones sin PIN, con un vínculo hecho en la tienda por alguien con `users.manage` que no puede vincular a un usuario con más permisos que los suyos.

## Consecuencias

- Las escrituras de la tienda solo se hacen desde la tienda: el dueño fuera de la tienda lee, navega y hace las acciones de la nube.
- Talia en la tienda necesita internet; sin él, las pantallas siguen y nada queda en cola.
- Ningún cambio de seguridad en los módulos: los mismos endpoints, permisos y autorizaciones; solo se agrega el filtro `AllowAssistant` y la tabla de propuestas hechas.
- Una vuelta más de modelo por acción; el costo queda dentro de las cuotas de TK-248 (≈ US$5–8 por 1,000 conversaciones, `docs/talia-acciones.md` §6).
