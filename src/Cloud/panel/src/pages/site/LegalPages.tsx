import { Children, isValidElement, type ReactNode } from "react";
import { Link } from "react-router";
import { Hero } from "./SiteParts";

/** TK-302: terms and privacy, drafts for the lawyer. Every fact here must match the product (docs/cobro-paddle.md, licencias.md,
 * despliegue-azure.md, cuotas.md, ecf-propio.md). The bracketed fields are for the owner or the lawyer to fill. */
const draftDate = "17 de octubre de 2026";

/** A field the owner or the lawyer must fill in. */
const Fill = ({ children }: { children: string }) => <mark className="legal__fill">[{children}]</mark>;
const company = <Fill>RAZÓN SOCIAL</Fill>;
const rnc = <Fill>RNC</Fill>;
const contact = <Fill>CORREO DE CONTACTO</Fill>;

/** "2. Su cuenta" → "su-cuenta", so each section has an address and the index links to it. */
const untitled = (title: string) => title.replace(/^\d+\.\s*/, "");
const slug = (title: string) =>
  untitled(title)
    .normalize("NFD")
    .replace(/[̀-ͯ]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-|-$/g, "");
const H2 = ({ children }: { children: string }) => <h2 id={slug(children)}>{children}</h2>;

function Legal({ eyebrow, title, sub, children }: { eyebrow: string; title: string; sub: ReactNode; children: ReactNode }) {
  const sections = Children.toArray(children).flatMap((c) => (isValidElement<{ children: string }>(c) && c.type === H2 ? [c.props.children] : []));
  return (
    <>
      <Hero eyebrow={eyebrow} title={title} sub={sub}>
        <p className="legal__draft" role="note">
          <strong>Borrador para revisión legal — no es asesoría legal.</strong> Versión del {draftDate}.
        </p>
      </Hero>
      <section className="legal">
        <div className="wrap legal__grid">
          <nav className="legal__toc" aria-label="En esta página">
            <span className="label">En esta página</span>
            <ol>
              {sections.map((t) => (
                <li key={t}>
                  <a href={`#${slug(t)}`}>{untitled(t)}</a>
                </li>
              ))}
            </ol>
          </nav>
          <div className="legal__body">{children}</div>
        </div>
      </section>
    </>
  );
}

function Terms() {
  return (
    <Legal
      eyebrow="Términos del servicio"
      title="Términos del servicio"
      sub={<>Las reglas para usar Abastalia, en corto. Cómo tratamos sus datos está en la <Link to="/privacidad">política de privacidad</Link>.</>}
    >
      <H2>1. Quién presta el servicio</H2>
      <p>
        Abastalia es un servicio de {company}, RNC {rnc}, República Dominicana. Contacto: {contact}.
      </p>
      <p>Estos términos cubren:</p>
      <ul>
        <li>La app de la tienda (Windows), con su PC principal y sus cajas.</li>
        <li>El panel en línea del dueño (abastalia.com/app) y este sitio.</li>
        <li>Los servicios en la nube: respaldo, archivo de los e-CF, recepción de e-CF de suplidores, catálogo público, la asistente Talia y la lectura de facturas.</li>
      </ul>
      <p>Al crear una cuenta o instalar Abastalia, usted acepta estos términos en nombre de su negocio.</p>

      <H2>2. Su cuenta</H2>
      <ul>
        <li>La cuenta del panel es del dueño. Usted cuida su contraseña y sus códigos de verificación; le recomendamos activar la verificación en dos pasos.</li>
        <li>El dueño invita a su equipo y decide qué puede hacer cada persona. Lo que haga su equipo con su acceso es responsabilidad del negocio.</li>
        <li>Avísenos enseguida si cree que alguien entró a su cuenta.</li>
      </ul>

      <H2>3. Prueba, planes y pagos</H2>
      <ul>
        <li>
          <strong>Prueba:</strong> 14 días desde la primera venta (30 si llegó recomendado). No pedimos tarjeta.
        </li>
        <li>
          <strong>Planes:</strong> los planes, servicios adicionales y cuotas de uso están en <Link to="/precios">Precios</Link> y en "Plan y facturación" del panel.
        </li>
        <li>
          <strong>Pagos:</strong> el pago en línea lo procesa Paddle como comerciante registrado (<em>merchant of record</em>). Usted le compra a Paddle: Paddle cobra, calcula y paga los impuestos de venta y le envía la factura. Abastalia no ve ni guarda los datos de su tarjeta.
        </li>
        <li>
          <strong>Renovación y cancelación:</strong> la suscripción se renueva sola cada mes o cada año. Puede cancelarla cuando quiera desde "Plan y facturación".
        </li>
        <li>
          <strong>Reembolsos:</strong> se piden a Paddle y siguen su política. Abastalia no reembolsa desde su panel.
        </li>
        <li>Mientras el pago en línea no esté disponible, el plan se acuerda con nosotros directamente.</li>
      </ul>

      <H2>4. Si la licencia vence</H2>
      <p>Su tienda funciona sin internet, así que la licencia vive en la PC y avanza por etapas:</p>
      <ul>
        <li>
          <strong>Gracia</strong> (primeros 15 días después del vencimiento): todo sigue igual.
        </li>
        <li>
          <strong>Restringido</strong> (días 15 a 30, o los 30 días después de terminar la prueba): se apagan las funciones pagas y queda una caja además de la PC principal. Todavía vende y factura.
        </li>
        <li>
          <strong>Bloqueada</strong> (después de eso): ya no se puede vender, abrir turnos, crear encargos, anular ni devolver.
        </li>
      </ul>
      <p>
        Aun bloqueada, usted puede entrar, cerrar el turno abierto, enviar a la DGII los e-CF ya emitidos, ver su historial y reportes, exportar el 606, 607 y 608, y respaldar y restaurar. Sus datos no se borran, y una licencia nueva levanta el bloqueo al momento.
      </p>

      <H2>5. Uso aceptable</H2>
      <ul>
        <li>Use Abastalia solo para su negocio y de acuerdo con la ley.</li>
        <li>No lo use para evadir impuestos, emitir comprobantes falsos ni engañar a la DGII o a sus clientes.</li>
        <li>No intente entrar a datos de otros negocios, saltar las licencias o las cuotas, ni sobrecargar el servicio.</li>
        <li>Cada plan tiene cuotas (por ejemplo, preguntas a Talia o facturas leídas). Al llegar al límite, ese servicio se detiene hasta el mes siguiente o hasta que compre un adicional. Los acuses de recibo de e-CF nunca se niegan.</li>
      </ul>
      <p>Si incumple estos términos, podemos suspender o bloquear el negocio, con aviso y un motivo registrado.</p>

      <H2>6. e-CF y obligaciones fiscales</H2>
      <ul>
        <li>Usted es el emisor ante la DGII: responde por los datos de cada venta, su certificado digital, sus secuencias y sus declaraciones.</li>
        <li>Abastalia firma cada e-CF en su tienda con su certificado y lo envía directo a la DGII. Su certificado no sale de la PC principal.</li>
        <li>Guardamos una copia de cada e-CF firmado durante 10 años, el plazo que exige la ley.</li>
        <li>La información sobre la DGII en este sitio, en el panel y en las respuestas de Talia es orientativa: no sustituye a su contador ni lo que publique la DGII.</li>
      </ul>

      <H2>7. Sus datos</H2>
      <p>
        Los datos de su negocio son suyos. Puede exportar sus reportes y respaldos cuando quiera. Cómo los tratamos está en la <Link to="/privacidad">política de privacidad</Link>.
      </p>

      <H2>8. Disponibilidad</H2>
      <ul>
        <li>La tienda sigue vendiendo y facturando sin internet; los e-CF pendientes salen solos al volver la conexión.</li>
        <li>El panel y los servicios en la nube pueden tener interrupciones por mantenimiento o fallas de terceros. Trabajamos para que sean pocas y cortas, pero no garantizamos un tiempo de servicio exacto.</li>
        <li>Haga respaldos: la app los programa y puede copiarlos a una memoria USB o a la nube.</li>
      </ul>

      <H2>9. Responsabilidad</H2>
      <ul>
        <li>Abastalia se ofrece tal como está. No respondemos por pérdidas indirectas, como ganancias dejadas de percibir.</li>
        <li>En lo que la ley permita, nuestra responsabilidad total se limita a lo que usted pagó por el servicio en los 12 meses anteriores al reclamo.</li>
      </ul>

      <H2>10. Fin del servicio</H2>
      <ul>
        <li>Puede dejar de usar Abastalia cuando quiera. Si cancela, la licencia sigue las etapas del punto 4.</li>
        <li>Para cerrar su cuenta y borrar sus datos, escriba a {contact}. El archivo de los e-CF se conserva los 10 años que exige la ley.</li>
      </ul>

      <H2>11. Cambios y ley aplicable</H2>
      <ul>
        <li>Si cambiamos estos términos, le avisaremos por correo o en el panel antes de que apliquen.</li>
        <li>Se rigen por las leyes de la República Dominicana; cualquier disputa va a sus tribunales.</li>
      </ul>
    </Legal>
  );
}

function Privacy() {
  return (
    <Legal
      eyebrow="Política de privacidad"
      title="Política de privacidad"
      sub={<>Qué datos guardamos, para qué, dónde y por cuánto tiempo. Las reglas de uso están en los <Link to="/terminos">términos del servicio</Link>.</>}
    >
      <H2>1. Responsable</H2>
      <p>
        {company}, RNC {rnc}, República Dominicana, es responsable de los datos de las cuentas de Abastalia. Contacto: {contact}.
      </p>
      <p>
        Los datos de los clientes y empleados de su tienda (por ejemplo, quién le debe en las ventas a crédito) son del negocio: usted es el responsable y Abastalia los trata por cuenta suya.
      </p>

      <H2>2. Qué datos guardamos y para qué</H2>
      <ul>
        <li>
          <strong>Su cuenta:</strong> correo, contraseña (guardada de forma que nadie puede leerla) y, si la activa, la verificación en dos pasos. Para que entre al panel y le escribamos sobre su cuenta.
        </li>
        <li>
          <strong>Lo que su tienda sube a la nube:</strong> ventas, pagos, devoluciones, turnos y cuadres, compras, movimientos de inventario, cambios de precio, nombres de cajeros y cajas, y clientes con crédito (nombre y saldo). Para mostrarle sus reportes en el panel.
        </li>
        <li>
          <strong>e-CF:</strong> cada comprobante firmado, su resumen y la respuesta de la DGII, y los e-CF que le envían sus suplidores. Para cumplir con la conservación que exige la ley y para que los busque en el panel.
        </li>
        <li>
          <strong>Respaldos en la nube</strong> (si los activa): se suben ya cifrados con la frase del dueño, que nunca sale de su PC. Nosotros no podemos abrirlos.
        </li>
        <li>
          <strong>Catálogo público</strong> (si lo activa): productos y precios, sin costos ni existencias. Los pedidos los manda su cliente por WhatsApp; no pasan por nosotros.
        </li>
        <li>
          <strong>Pagos:</strong> Paddle recibe los datos de su tarjeta y de facturación. Nosotros solo recibimos el estado de la suscripción, el plan y las fechas de pago.
        </li>
        <li>
          <strong>Registros técnicos:</strong> errores y uso del servicio, para mantenerlo funcionando y protegerlo del abuso.
        </li>
      </ul>
      <p>No vendemos sus datos ni los usamos para publicidad.</p>

      <H2>3. Talia y la lectura de facturas</H2>
      <ul>
        <li>
          Al preguntarle a Talia, se envían a Azure OpenAI (Microsoft) su pregunta, la conversación reciente y los datos que Talia consulta para responder: por ejemplo, ventas por día, productos más vendidos, nombres de cajeros o quién le debe.
        </li>
        <li>Al leer la factura de un suplidor, la foto o el PDF va a Azure OpenAI para sacar los datos. La nube no guarda el archivo.</li>
        <li>No guardamos el texto de sus preguntas en los registros. Según los términos de Microsoft, Azure OpenAI no usa estos datos para entrenar modelos.</li>
      </ul>

      <H2>4. Lo que no sale de su tienda</H2>
      <ul>
        <li>Su certificado digital y su contraseña: se instalan en la PC principal y no se pueden exportar.</li>
        <li>La frase de sus respaldos.</li>
      </ul>

      <H2>5. Dónde se guardan</H2>
      <p>
        En Microsoft Azure, en Estados Unidos: el servicio y los archivos en la región Este de EE. UU. 2 y la base de datos en Centro de EE. UU. Al usar Abastalia, sus datos salen de la República Dominicana hacia esos centros de datos.
      </p>

      <H2>6. Con quién los compartimos</H2>
      <ul>
        <li>
          <strong>Microsoft Azure:</strong> servidores, base de datos, almacenamiento, Azure OpenAI, el envío de correos (Azure Communication Services, sin seguimiento de lectura) y los registros técnicos.
        </li>
        <li>
          <strong>Paddle:</strong> pagos, impuestos, facturas y reembolsos.
        </li>
        <li>
          <strong>Cloudflare:</strong> el DNS del dominio abastalia.com.
        </li>
        <li>
          <strong>La DGII:</strong> recibe sus e-CF porque la ley lo exige. Su tienda los envía directamente.
        </li>
      </ul>
      <p>Solo compartiremos otros datos si una autoridad competente lo ordena conforme a la ley.</p>

      <H2>7. Cookies y almacenamiento del navegador</H2>
      <ul>
        <li>Este sitio no usa cookies ni herramientas de seguimiento o publicidad.</li>
        <li>El navegador guarda, solo en su equipo: el tema claro u oscuro, el código de acceso al sitio cuando está en preparación y el código de recomendación si llegó con uno.</li>
        <li>El panel guarda además la sesión (para no pedirle la contraseña a cada rato) y si el menú está recogido. Al cerrar sesión, la sesión se borra.</li>
      </ul>

      <H2>8. Cuánto tiempo los guardamos</H2>
      <ul>
        <li>
          <strong>Cuenta y datos del panel:</strong> mientras su cuenta esté activa. Si pide cerrarla, los borramos en <Fill>PLAZO</Fill>.
        </li>
        <li>
          <strong>e-CF:</strong> 10 años desde que se emiten, como exige la ley. No se pueden borrar antes, ni a pedido.
        </li>
        <li>
          <strong>Respaldos en la nube:</strong> el último de cada uno de los 7 días anteriores y de las 4 semanas anteriores. Si la tienda deja de enviarlos (por ejemplo, le robaron la PC), guardamos los últimos hasta 400 días para que pueda recuperar sus datos.
        </li>
        <li>
          <strong>Registros técnicos:</strong> 30 días.
        </li>
        <li>
          <strong>Pagos:</strong> Paddle los guarda según su propia política.
        </li>
      </ul>

      <H2>9. Seguridad</H2>
      <ul>
        <li>Todo viaja cifrado. Cada PC de su tienda se identifica con su propia llave al hablar con la nube.</li>
        <li>Verificación en dos pasos para el panel y permisos por persona que maneja el dueño.</li>
        <li>Solo el personal autorizado de Abastalia entra a la consola de soporte, y cada acceso queda registrado.</li>
      </ul>

      <H2>10. Sus derechos</H2>
      <p>
        Según la Ley 172-13 de Protección de Datos de Carácter Personal, usted puede pedir acceso a sus datos, que se corrijan, que se borren (salvo lo que la ley obliga a conservar) y oponerse a su uso. Escriba a {contact} desde el correo de su cuenta; le responderemos en el plazo que fija la ley.
      </p>
      <p>Si un cliente de su tienda nos escribe sobre sus datos, se lo pasaremos a usted, que es el responsable.</p>

      <H2>11. Cambios</H2>
      <p>Si cambiamos esta política, le avisaremos por correo o en el panel antes de que aplique.</p>
    </Legal>
  );
}

export const legalPages = [
  {
    path: "/terminos",
    nav: "Términos",
    title: "Términos del servicio · Abastalia",
    description: "Reglas para usar Abastalia: cuenta, prueba de 14 días, planes y pagos con Paddle, qué pasa si vence la licencia, uso aceptable, e-CF y fin del servicio.",
    element: <Terms />,
  },
  {
    path: "/privacidad",
    nav: "Privacidad",
    title: "Política de privacidad · Abastalia",
    description: "Qué datos guarda Abastalia, para qué, dónde (Microsoft Azure), con quién los comparte, cuánto tiempo y cómo ejercer sus derechos según la Ley 172-13.",
    element: <Privacy />,
  },
];
