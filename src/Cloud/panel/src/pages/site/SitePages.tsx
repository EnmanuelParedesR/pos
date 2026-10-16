import type { ReactNode } from "react";
import { Link } from "react-router";
import { contactHref, site } from "./config";

/** The public site (TK-197, TK-198, TK-226, TK-228): one entry per page. `title` and `description` go in <head> (SiteLayout on the client, the prerender at build). */
export interface SitePage {
  path: string;
  /** Label in the menu; pages without one (404) are not listed. */
  nav?: string;
  title: string;
  description: string;
  element: ReactNode;
}

/** Internal paths through the router, everything else (WhatsApp, DGII) as a plain link. */
function To({ href, className, children }: { href: string; className?: string; children: ReactNode }) {
  return href.startsWith("/") ? (
    <Link className={className} to={href}>
      {children}
    </Link>
  ) : (
    <a className={className} href={href} rel="noopener">
      {children}
    </a>
  );
}

const Tbd = ({ children = "Por confirmar" }: { children?: ReactNode }) => <span className="tbd">{children}</span>;

const Price = ({ value }: { value: string | null }) => (value ? <>{value}</> : <Tbd />);

function Hero({ eyebrow, title, children, cta }: { eyebrow?: string; title: string; children?: ReactNode; cta?: ReactNode }) {
  return (
    <section className="hero">
      <div className="wrap">
        {eyebrow && <span className="eyebrow">{eyebrow}</span>}
        <h1>{title}</h1>
        {children}
        {cta && <div className="actions">{cta}</div>}
      </div>
    </section>
  );
}

const Section = ({ id, className, children }: { id?: string; className?: string; children: ReactNode }) => (
  <section id={id} className={className}>
    <div className="wrap">{children}</div>
  </section>
);

const Card = ({ title, children }: { title: string; children: ReactNode }) => (
  <div className="card">
    <h3>{title}</h3>
    <p>{children}</p>
  </div>
);

function Faq({ items }: { items: [string, ReactNode][] }) {
  return (
    <Section className="faq">
      <h2>Preguntas frecuentes</h2>
      {items.map(([q, a]) => (
        <details key={q}>
          <summary>{q}</summary>
          <p>{a}</p>
        </details>
      ))}
    </Section>
  );
}

const Contact = ({ children }: { children: string }) => (
  <To className="btn" href={contactHref}>
    {children}
  </To>
);

const switching: [string, ReactNode] = [
  "Ya tengo otro sistema. ¿Puedo cambiarme?",
  <>
    Sí. Si ya emites e-CF con otro programa, mira <Link to="/certificacion#ya-emito">"Ya emito e-CF con otro programa"</Link>.
  </>,
];

function Home() {
  return (
    <>
      <Hero
        eyebrow="Punto de venta con facturación electrónica"
        title="Si se va el internet, tu caja sigue vendiendo."
        cta={
          <>
            <Contact>Quiero ver Toki</Contact>
            <Link className="btn btn--ghost" to="/e-cf">
              ¿Ya te toca el e-CF?
            </Link>
          </>
        }
      >
        <p>Toki es el punto de venta para carnicerías, colmados y delis. Vende por libra, cuadra la caja, lleva el fiado y emite comprobantes fiscales electrónicos (e-CF) desde tu propia tienda.</p>
      </Hero>
      <Section>
        <h2>Hecho para el mostrador</h2>
        <div className="grid grid--3">
          <Card title="Vende sin internet">La PC principal de la tienda guarda todo y firma cada factura. Sin internet, las cajas siguen vendiendo y los e-CF se envían a la DGII cuando vuelve la conexión, dentro del plazo de 72 horas.</Card>
          <Card title="Por libra y con balanza">Precios por libra con tres decimales y lectura de las etiquetas que imprime la balanza. El peso y el total se ven grandes en la pantalla.</Card>
          <Card title="Caja que cuadra">Turno por caja, cuadre ciego por denominaciones y cierre Z. Las diferencias quedan con nombre, hora y quién autorizó.</Card>
          <Card title="Fiado bajo control">Límite por cliente, antigüedad de la deuda y estado de cuenta en una página para enviarlo por WhatsApp.</Card>
          <Card title="e-CF directo con la DGII">Consumo, crédito fiscal y notas de crédito. Toki firma en tu tienda con tu certificado y envía a la DGII sin intermediarios. Reportes 606, 607 y 608 para tu contador.</Card>
          <Card title="Tu negocio en el celular">Ventas de hoy, lo más vendido y quién te debe, desde el celular, estés donde estés. Y un asistente al que le preguntas "¿cuánto vendí hoy?".</Card>
        </div>
      </Section>
      <Section>
        <h2>Lo que nunca se detiene</h2>
        <ul className="checks">
          <li><strong>Sin cupo de e-CF:</strong> emites los comprobantes que tu negocio necesite, sin cobro por documento.</li>
          <li><strong>Nunca en solo lectura:</strong> aunque se venza la licencia, la tienda sigue vendiendo, emitiendo e-CF, exportando y respaldando.</li>
          <li><strong>Tus datos son tuyos:</strong> los exportas cuando quieras. Tu certificado y tus secuencias de e-NCF también son tuyos.</li>
          <li><strong>Permisos finos:</strong> el cajero no ve costos ni ganancias; abrir y cerrar caja, cobrar fiado y anular tienen permisos separados.</li>
        </ul>
      </Section>
      <Section>
        <h2>Elige tu negocio</h2>
        <div className="grid grid--3">
          <Link className="card" to="/carniceria"><h3>Carnicería</h3><p className="muted">Balanza, etiquetas de peso, encargos con abono.</p></Link>
          <Link className="card" to="/colmado"><h3>Colmado</h3><p className="muted">Fiado, venta rápida y pedidos por WhatsApp.</p></Link>
          <Link className="card" to="/deli"><h3>Deli</h3><p className="muted">Por libra y por unidad, encargos y cotizaciones.</p></Link>
        </div>
      </Section>
    </>
  );
}

function Butcher() {
  return (
    <>
      <Hero eyebrow="Carnicería" title="Cada libra cobrada, cada caja cuadrada." cta={<Contact>Quiero ver Toki en mi carnicería</Contact>}>
        <p>En una carnicería con fila, cada segundo en la caja cuenta. Toki lee la etiqueta de la balanza, suma y cobra, y sigue facturando aunque se caiga el internet.</p>
      </Hero>
      <Section>
        <h2>Lo que resuelve</h2>
        <div className="grid grid--2">
          <Card title="Etiquetas de peso">El cajero escanea la etiqueta que imprime la balanza (EAN-13 con el peso o el precio) y la línea entra con las libras exactas. Si la etiqueta se escanea dos veces, Toki avisa.</Card>
          <Card title="Encargos con abono">"Guárdame 10 libras de costilla para el sábado": fecha y hora de entrega, abono que entra a la caja y, al entregar, se cobra con el peso real.</Card>
          <Card title="Inventario por libra">Compras, mermas, conteos y mínimos por producto. El pedido sugerido calcula cuánto pedir y a qué suplidor, según su último costo.</Card>
          <Card title="Varias cajas, una sola tienda">Todas las cajas comparten existencias y precios a través de la PC principal, con internet o sin él.</Card>
        </div>
      </Section>
      <Faq
        items={[
          ["¿Qué pasa si se va el internet?", "Las cajas siguen vendiendo contra la PC principal de la tienda, que firma cada e-CF. Cuando vuelve la conexión, Toki los envía a la DGII; el plazo que da la DGII es de 72 horas."],
          ["¿Funciona con mi balanza?", "Toki lee las etiquetas EAN-13 con peso o precio que imprimen las balanzas etiquetadoras. La conexión directa de la balanza a la caja está en desarrollo; mientras tanto, el peso también se puede escribir. Antes de instalar revisamos tu modelo."],
          ["¿Puedo vender con tres decimales?", "Sí. Las libras se guardan con tres decimales y el total se redondea al centavo."],
          [
            "¿Sirve para el e-CF?",
            <>
              Sí: factura de consumo, crédito fiscal y notas de crédito, firmadas en la tienda con tu certificado digital. Mira <Link to="/e-cf">¿Ya te toca el e-CF?</Link> y <Link to="/certificacion">Certifícate con Toki</Link>.
            </>,
          ],
          [
            "Ya tengo otro sistema. ¿Puedo cambiarme?",
            <>
              Sí. Si ya emites e-CF con otro programa, no hace falta empezar de cero: mira <Link to="/certificacion#ya-emito">"Ya emito e-CF con otro programa"</Link>.
            </>,
          ],
          ["¿El cajero puede ver mis costos?", "No, salvo que le des ese permiso. Costos, ganancias y valor del inventario solo los ve quien tenga permiso para verlos."],
        ]}
      />
    </>
  );
}

function Grocery() {
  return (
    <>
      <Hero eyebrow="Colmado" title="El fiado claro y la caja al día." cta={<Contact>Quiero ver Toki en mi colmado</Contact>}>
        <p>En el colmado se vende rápido, se fía a los clientes de siempre y se pide por teléfono. Toki lleva las tres cosas sin cuadernos y emite el e-CF de cada venta.</p>
      </Hero>
      <Section>
        <h2>Lo que resuelve</h2>
        <div className="grid grid--2">
          <Card title="Venta rápida">Escáner siempre listo, teclas visibles para todo y el total grande. La venta por libra (arroz, habichuelas, queso) también.</Card>
          <Card title="Fiado con límite">Cada cliente con su límite, sus abonos y la antigüedad de su deuda. El estado de cuenta sale en una página para mandarlo por WhatsApp.</Card>
          <Card title="Pedidos por WhatsApp">Un catálogo público con tus productos y precios, con un código QR para pegar en el mostrador. El cliente elige y te escribe por WhatsApp. No muestra costos ni existencias.</Card>
          <Card title="Reponer a tiempo">Mínimos y máximos por producto y un pedido sugerido con el último suplidor y su costo.</Card>
        </div>
      </Section>
      <Faq
        items={[
          ["Tengo una sola caja. ¿Necesito una PC aparte?", "No. En una tienda de una caja, la misma PC es la caja y la principal."],
          ["¿Qué pasa si se va el internet o la luz?", "Sin internet sigues vendiendo y los e-CF se envían cuando vuelve la conexión (la DGII da 72 horas). Si la PC no puede emitir e-CF (por ejemplo, se venció el certificado), Toki te guía para declarar la contingencia y facturar con comprobantes serie B, y luego regularizarlos."],
          ["¿El fiado sale en el cuadre de la caja?", "Sí. Lo que se vende a crédito y lo que se cobra de fiado salen separados en el cierre de cada turno."],
          ["¿Hay un límite de facturas al mes?", "No. Toki no cobra por documento ni detiene la facturación por volumen."],
          [
            "¿Cuándo me toca el e-CF?",
            <>
              Para pequeños, micro y no clasificados la fecha publicada es el 15 de noviembre de 2026. Mira <Link to="/e-cf">¿Ya te toca el e-CF?</Link>
            </>,
          ],
          switching,
        ]}
      />
    </>
  );
}

function Deli() {
  return (
    <>
      <Hero eyebrow="Deli" title="Del picadero a la factura, sin enredos." cta={<Contact>Quiero ver Toki en mi deli</Contact>}>
        <p>Quesos y embutidos por libra, panes por unidad, bandejas por encargo. Toki maneja las dos formas de vender en la misma factura y lleva los encargos con su abono.</p>
      </Hero>
      <Section>
        <h2>Lo que resuelve</h2>
        <div className="grid grid--2">
          <Card title="Por libra y por unidad">Cada producto se vende como se vende de verdad. La factura muestra "0.750 lb" o "3 unidades", y los reportes también.</Card>
          <Card title="Encargos y bandejas">Fecha y hora de entrega, abono como anticipo y la lista de los encargos de hoy y mañana a la vista en la caja.</Card>
          <Card title="Cotizaciones">Una cotización con vigencia para el cliente que pide precio para un evento. Al aceptarla, se cobra como venta con los precios del día.</Card>
          <Card title="Ventas por hora">Cuánto vendes en cada hora del día comparado con la semana pasada, para saber cuándo reforzar el mostrador.</Card>
        </div>
      </Section>
      <Faq
        items={[
          ["¿Puedo cobrar el encargo con el peso real?", "Sí. Al cobrar, el encargo se carga a la caja, se ajusta el peso y se descuenta lo que el cliente abonó."],
          ["¿Una cotización mueve el inventario?", "No. La cotización no toca el inventario ni la caja hasta que se convierte en venta."],
          ["¿Puedo darle crédito fiscal a una empresa?", "Sí. Con el RNC del cliente, Toki emite el e-CF de crédito fiscal (tipo 31) y, si la empresa es emisora electrónica, se lo entrega directamente."],
          ["¿Qué pasa si se va el internet?", "Sigues vendiendo. Los e-CF se firman en la tienda y se envían a la DGII cuando vuelve la conexión, dentro de las 72 horas que da la DGII."],
          switching,
        ]}
      />
    </>
  );
}

function Ecf() {
  return (
    <>
      <Hero eyebrow="Facturación electrónica" title="¿Ya te toca el e-CF?">
        <p>La Ley 32-23 de Facturación Electrónica obliga a los contribuyentes a emitir comprobantes fiscales electrónicos (e-CF) por etapas. Para los pequeños, micro y no clasificados, la fecha publicada por la DGII es:</p>
        <p className="date">15/11/2026</p>
        <p className="muted">La DGII dio una prórroga de seis meses en mayo de 2026. Confirma tu clasificación y tu fecha en la Oficina Virtual de la DGII o con tu contador.</p>
      </Hero>
      <Section>
        <h2>En tres preguntas</h2>
        <div className="grid grid--3">
          <Card title="¿Qué es un e-CF?">Es la factura con número e-NCF, firmada digitalmente y enviada a la DGII. Al cliente le entregas la representación impresa con un código QR para verificarla.</Card>
          <Card title="¿Me toca a mí?">La obligación entra por grupos según la clasificación de cada contribuyente. Si eres pequeño, micro o no clasificado, tu fecha es el 15 de noviembre de 2026.</Card>
          <Card title="¿Y si no cumplo?">La Ley 32-23 (artículo 27) y el Código Tributario (artículo 257) prevén sanciones. Tu contador te puede decir cuáles aplican a tu caso.</Card>
        </div>
      </Section>
      <Section>
        <h2>Qué necesitas</h2>
        <ul className="checks">
          <li>RNC activo y al día, y acceso a la Oficina Virtual (OFV) de la DGII.</li>
          <li>Un <strong>certificado digital</strong> de persona física para procedimientos tributarios, a nombre de quien firma por el negocio. Lo venden las entidades autorizadas por INDOTEL (Avansi y la Cámara de Comercio y Producción de Santo Domingo con Digifirma). El precio lo fija la entidad.</li>
          <li>Un programa de facturación electrónica <strong>certificado ante la DGII para tu RNC</strong>. Ese es el proceso de <Link to="/certificacion">certificación</Link>.</li>
          <li>Las secuencias de e-NCF que autoriza la DGII en la OFV.</li>
        </ul>
        <p className="muted">La DGII no cobra por certificarte ni por cada documento enviado.</p>
      </Section>
      <Section>
        <h2>Cómo factura Toki, con internet o sin él</h2>
        <div className="table-wrap">
          <table>
            <thead><tr><th>Situación</th><th>Qué hace Toki</th><th>Plazo de la DGII</th></tr></thead>
            <tbody>
              <tr><td>Hay internet</td><td>Firma el e-CF en tu tienda y lo envía a la DGII al momento.</td><td>—</td></tr>
              <tr><td>Se fue el internet</td><td>Sigue firmando y entregando la factura con la leyenda de contingencia; envía todo cuando vuelve la conexión.</td><td>72 horas desde la emisión</td></tr>
              <tr><td>No se puede emitir e-CF (certificado vencido, PC dañada)</td><td>Te guía para declarar la contingencia en la OFV y facturar con comprobantes serie B, y luego emite los e-CF que los reemplazan.</td><td>Hasta 15 días de contingencia; 30 días para regularizar</td></tr>
              <tr><td>La DGII no responde</td><td>Guarda los e-CF y los envía cuando vuelve el servicio.</td><td>Si pasa de 15 días hábiles, la OFV habilita otra forma de reportar</td></tr>
            </tbody>
          </table>
        </div>
        <p>La factura de consumo de menos de RD$250,000 se reporta con un resumen firmado; Toki guarda el e-CF completo, como pide la DGII, durante 10 años.</p>
      </Section>
      <Faq
        items={[
          ["¿Hay una opción gratis de la DGII?", "Sí. La DGII tiene un Facturador Gratuito, pensado para negocios con poco volumen. Sirve para emitir e-CF, pero no es un punto de venta con caja, inventario ni balanza."],
          ["¿Puedo usar el certificado que regala la DGII?", "No con Toki. Ese certificado solo sirve para el Facturador Gratuito de la DGII."],
          [
            "¿Cuánto tarda la certificación?",
            <>
              Depende en buena parte de la DGII, que responde cada solicitud. La parte técnica (set de pruebas y simulación) la hace Toki. Mira <Link to="/certificacion">Certifícate con Toki</Link>.
            </>,
          ],
          ["¿Por cuánto tiempo hay que guardar los e-CF?", "Diez años. Toki los guarda en la tienda y una copia en la nube."],
        ]}
      />
      <Section>
        <div className="note">
          <p>Esta guía resume lo publicado por la DGII y no es asesoría legal. Las normas que mandan son la Ley 32-23, el Decreto 587-24 y las normas generales de la DGII. Ante cualquier duda, consulta a tu contador o a la DGII.</p>
        </div>
        <div className="actions">
          <Contact>Quiero facturar con Toki</Contact>
          <a className="btn btn--ghost" href="https://dgii.gov.do/" rel="noopener">
            Ir a la DGII
          </a>
        </div>
      </Section>
    </>
  );
}

const Step = ({ title, children }: { title: string; children: ReactNode }) => (
  <li>
    <h3>{title}</h3>
    <p>{children}</p>
  </li>
);

/** TK-235: what the owner prepares (docs/servicio-certificacion.md §5); the session-1 e-mail sends the same list (AccompanimentChecklist). */
export const preparation = [
  "RNC activo y al día con la DGII (sin declaraciones pendientes).",
  "Acceso a la Oficina Virtual (OFV) de la empresa y, si aplica, del representante: usuario y clave que solo tú conoces. Prueba que entras antes de la cita. Toki nunca te pide esa clave.",
  "Alta de NCF (autorización para emitir comprobantes fiscales).",
  "Un representante vinculado al RNC, que será el Usuario Administrador e-CF.",
  "El certificado digital de persona física para procedimientos tributarios, a nombre de ese representante, comprado a Avansi (Viafirma) o a la Cámara de Comercio y Producción de Santo Domingo (Digifirma). Pídelo como archivo .p12 o .pfx con su contraseña (no en token USB ni en la nube del emisor).",
  "La PC principal de la tienda con Toki instalado, encendida y con internet durante las citas.",
  "Una hora libre para cada cita, sin atender el mostrador.",
];

function Certification() {
  return (
    <>
      <Hero eyebrow="Certificación e-CF" title="Certifícate con Toki." cta={<Contact>Quiero certificarme</Contact>}>
        <p>
          Para emitir e-CF, la DGII certifica tu RNC con el programa que vas a usar. Toki hace la parte técnica: arma, firma y envía las pruebas. Tú haces los pasos que piden tu clave de la Oficina Virtual. <strong>Toki nunca te pide esa clave.</strong>
        </p>
      </Hero>
      <Section id="preparacion">
        <h2>Antes de empezar</h2>
        <p>Ten esto listo antes de la primera cita (con acompañamiento, te lo enviamos también en el correo que la confirma):</p>
        <ul className="checks">
          <li>
            Toki instalado en la PC principal de tu tienda. <Link to="/descargar">Descargar Toki</Link>
          </li>
          {preparation.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      </Section>
      <Section>
        <h2>Las etapas</h2>
        <ol className="steps">
          <Step title="Solicitud">Envías el formulario FI-GDF-016 desde la OFV. La DGII responde por el buzón con el acceso al portal de certificación.</Step>
          <Step title="Pruebas libres">Toki emite facturas de prueba de cada tipo en el ambiente de pruebas de la DGII, para llegar a la certificación sin sorpresas.</Step>
          <Step title="Postulación">Registras el programa y las direcciones de tu negocio en el portal. Toki te las muestra y firma el archivo que pide la DGII.</Step>
          <Step title="Set de pruebas">Descargas el Excel de la DGII y lo subes a Toki. Toki arma cada comprobante, lo firma, lo envía y sigue cada respuesta.</Step>
          <Step title="Simulación">Vendes en la caja como un día normal, en el ambiente de certificación. Toki envía cada comprobante y prepara lo que se sube al portal.</Step>
          <Step title="Comunicación">La DGII envía comprobantes a tu negocio y espera el acuse de recibo. Lo responde la nube de Toki, las 24 horas.</Step>
          <Step title="Declaración jurada">La descargas, Toki la firma con tu certificado y tú la subes. La aceptas tú: es tu declaración.</Step>
          <Step title="Producción">La DGII verifica tu RNC y te habilita. Pides tus secuencias de e-NCF en la OFV, las cargas en Toki y empiezas a facturar.</Step>
        </ol>
        <p className="muted">Los tiempos de respuesta entre una etapa y otra los pone la DGII.</p>
      </Section>
      <Section>
        <h2>Quién hace qué</h2>
        <div className="table-wrap">
          <table>
            <thead><tr><th></th><th>Por tu cuenta</th><th>Con acompañamiento de Toki</th></tr></thead>
            <tbody>
              <tr><th scope="row">Quién usa el asistente de certificación</th><td>Tú, con la lista paso a paso</td><td>Un técnico de Toki, en una sesión remota contigo presente</td></tr>
              <tr><th scope="row">Pasos con tu clave de la OFV</th><td>Tú</td><td>Tú escribes tu clave; el técnico te dice qué marcar. Toki no ve ni guarda la clave</td></tr>
              <tr><th scope="row">Set de pruebas, simulación y acuses</th><td>Toki, automático</td><td>Toki, automático; el técnico vigila los rechazos</td></tr>
              <tr><th scope="row">Declaración jurada</th><td>La aceptas tú</td><td>La aceptas tú</td></tr>
              <tr>
                <th scope="row">Precio</th>
                <td>Incluido en la licencia</td>
                <td>{site.prices.certificationSupport ?? <Tbd>Precio por confirmar</Tbd>}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p className="muted">El certificado digital no está incluido: lo compras directamente a la entidad certificadora.</p>
      </Section>
      <Section id="ya-emito">
        <h2>Ya emito e-CF con otro programa</h2>
        <p>
          Si tu RNC ya es emisor electrónico, no deberías tener que repetir la certificación: cambias el programa que firma, envía y recibe. <Tbd>Por confirmar con la DGII</Tbd>
        </p>
        <ol className="steps">
          <Step title="Tu certificado">Importas en Toki el mismo archivo .p12 que usabas, si sigue vigente.</Step>
          <Step title="Las direcciones de tu negocio">En la OFV, en "Mantenimiento Directorio FE", cambias las direcciones del otro programa por las de Toki. Cópialas completas: el error más común es escribir solo el dominio.</Step>
          <Step title="Los números que quedan">Escribes en Toki el último e-NCF que emitió el otro programa y Toki sigue desde el siguiente. Si la DGII pide anular los números sobrantes y usar una secuencia nueva, Toki los anula.</Step>
          <Step title="El cambio">Dejas de facturar con el otro programa (dos programas con la misma secuencia repiten números), pasas Toki a producción y revisas que la DGII acepte la primera venta.</Step>
        </ol>
      </Section>
    </>
  );
}

function Download() {
  return (
    <>
      <Hero
        eyebrow="Descargar"
        title="Instala Toki en tu tienda."
        cta={
          site.installerUrl ? (
            <a className="btn" href={site.installerUrl}>
              Descargar Toki para Windows
            </a>
          ) : (
            <Tbd>[URL DEL INSTALADOR]</Tbd>
          )
        }
      >
        <p>Un solo instalador para todas las PC de la tienda. Al abrirlo eliges qué es esa PC. La prueba de 14 días empieza con tu primera venta, sin pedir código.</p>
      </Hero>
      <Section>
        <h2>Elige el papel de cada PC</h2>
        <div className="grid grid--2">
          <Card title="PC principal">
            La que guarda las ventas y atiende a las demás cajas. Instala el servidor de Toki, la base de datos (SQL Server Express, si no la tiene) y la app de caja. Instálala primero.
          </Card>
          <Card title="Caja">Cada PC de cobro adicional. Instala solo la app de caja y se une a la PC principal con un código que ves en Configuración → Equipos.</Card>
        </div>
      </Section>
      <Section>
        <h2>Requisitos</h2>
        <ul className="checks">
          <li>Windows 10 u 11 de 64 bits.</li>
          <li>PC principal: 8 GB de RAM y 10 GB libres en disco. Caja: 4 GB de RAM y 2 GB libres.</li>
          <li>Internet durante la instalación: el instalador baja de Microsoft lo que falte (SQL Server Express y WebView2).</li>
          <li>Todas las PC en la misma red de la tienda.</li>
        </ul>
      </Section>
      <Section>
        <h2>Pasos</h2>
        <ol className="steps">
          <Step title="Descarga">Baja el instalador en la PC principal y ábrelo. Windows pide permiso de administrador una sola vez.</Step>
          <Step title="En la PC principal">Elige "PC principal". Al terminar se abre Toki en "Primera instalación": escribe los datos de tu negocio y crea tu usuario.</Step>
          <Step title="En cada caja">En cada caja abre el mismo instalador, elige "Caja" y une la PC con el código de Configuración → Equipos.</Step>
          <Step title="Actualizaciones">Toki se actualiza solo: la caja al volver a abrirse y el servidor cuando no hay turnos abiertos o fuera del horario.</Step>
        </ol>
        <p className="muted">
          ¿Quieres que lo instalemos por ti? <Contact>Hablemos</Contact> · ¿Te vas a certificar para el e-CF? Mira <Link to="/certificacion">Certifícate con Toki</Link>.
        </p>
      </Section>
    </>
  );
}

function Plan({ name, note, price, children, brand }: { name: string; note: string; price: string | null; children: ReactNode; brand?: boolean }) {
  return (
    <div className={brand ? "card card--brand" : "card"}>
      <h3>{name}</h3>
      <p className="muted">{note}</p>
      <p className="price">{price ?? <Tbd>Precio por confirmar</Tbd>}</p>
      <ul className="checks">{children}</ul>
    </div>
  );
}

function Pricing() {
  const { prices } = site;
  return (
    <>
      <Hero eyebrow="Precios" title="Pagas por el tamaño de tu tienda, no por factura.">
        <p>Tres planes según cuántas cajas y sucursales tienes. Lo esencial para vender y facturar está en todos.</p>
        <p className="note">Estamos cerrando los precios. Los publicamos aquí en cuanto estén aprobados; mientras tanto, escríbenos y te damos una cotización.</p>
      </Hero>
      <Section>
        <h2>Planes</h2>
        <div className="grid grid--3">
          <Plan name="Una caja" note="Para el colmado o la tienda con un solo punto de cobro." price={prices.oneRegister}>
            <li>1 caja</li>
            <li>1 sucursal</li>
          </Plan>
          <Plan name="Varias cajas" note="Para la carnicería o el deli con fila y más de una caja." price={prices.severalRegisters} brand>
            <li>Varias cajas <Tbd>Cantidad por confirmar</Tbd></li>
            <li>1 sucursal</li>
          </Plan>
          <Plan name="Varias sucursales" note="Para el negocio con más de un local." price={prices.severalStores}>
            <li>Cajas por sucursal <Tbd>Cantidad por confirmar</Tbd></li>
            <li>Varias sucursales</li>
          </Plan>
        </div>
        <p className="muted">Entre un plan y otro cambian las cajas, las sucursales y el acceso desde el celular del dueño.</p>
      </Section>
      <Section>
        <h2>En todos los planes</h2>
        <ul className="checks">
          <li>Venta por libra y por unidad, con escáner, etiquetas de balanza, impresora y gaveta, con internet o sin él.</li>
          <li>e-CF de consumo, crédito fiscal y notas de crédito, <strong>sin cupo y sin cobro por documento</strong>.</li>
          <li>Inventario completo: compras, mermas, conteos, mínimos y pedido sugerido.</li>
          <li>Turnos con cuadre ciego y cierre Z; autorización de supervisor con PIN.</li>
          <li>Clientes y fiado con límite y estado de cuenta.</li>
          <li>Reportes 606, 607 y 608 para tu contador.</li>
          <li>Usuarios, roles, permisos por acción y registro de auditoría.</li>
          <li>Respaldo y exportación completa de tus datos cuando quieras.</li>
        </ul>
      </Section>
      <Section>
        <h2>Pagos únicos</h2>
        <div className="table-wrap">
          <table>
            <thead><tr><th>Qué</th><th>Incluye</th><th>Precio</th></tr></thead>
            <tbody>
              <tr><td>Instalación</td><td>Instalación de Toki en la tienda y puesta en marcha.</td><td><Price value={prices.installation} /></td></tr>
              <tr>
                <td>Acompañamiento en la certificación e-CF</td>
                <td>Un técnico te guía en cada etapa ante la DGII. <Link to="/certificacion">Ver detalles</Link></td>
                <td><Price value={prices.certificationSupport} /></td>
              </tr>
              <tr><td>Equipos (PC, balanza, impresoras, escáner, gaveta)</td><td>Kit según tu negocio.</td><td>Se cotiza</td></tr>
            </tbody>
          </table>
        </div>
        <p className="muted">El certificado digital para el e-CF se compra directamente a la entidad certificadora; no lo vende Toki.</p>
      </Section>
      <Section>
        <h2>Reglas que no cambian</h2>
        <ul className="checks">
          <li><strong>La venta nunca se detiene:</strong> ni por volumen de facturas ni por un pago atrasado.</li>
          <li><strong>Nunca en solo lectura:</strong> si la licencia vence, la tienda sigue vendiendo, emitiendo e-CF, exportando y respaldando; solo se apagan funciones adicionales.</li>
          <li><strong>Tus datos, tu certificado y tus secuencias son tuyos</strong>, también si te vas.</li>
        </ul>
      </Section>
      <Section id="contacto">
        <h2>Hablemos</h2>
        <p>Cuéntanos qué vendes y cuántas cajas tienes, y te preparamos una propuesta.</p>
        <p>{site.whatsapp ? <Contact>Escríbenos por WhatsApp</Contact> : <Tbd>Canal de contacto por confirmar</Tbd>}</p>
      </Section>
    </>
  );
}

const News = ({ title, children }: { title: string; children: ReactNode }) => (
  <li>
    <h3>{title}</h3>
    <p>{children}</p>
  </li>
);

function WhatsNew() {
  return (
    <>
      <Hero eyebrow="Novedades" title="Lo nuevo en Toki">
        <p>Lo que cambió, en pocas palabras. Lo más reciente va primero.</p>
      </Hero>
      <Section>
        <h2>Octubre de 2026</h2>
        <ul className="log">
          <News title="Facturación electrónica directa con la DGII">Toki firma en la tienda y envía a la DGII sin intermediarios. Si la DGII rechaza un comprobante, se reemplaza con uno nuevo con los mismos datos. Los números que sobran se anulan desde la pantalla. Cada e-CF se guarda 10 años en la nube.</News>
          <News title="Asistente de certificación y cambio desde otro programa">Los pasos de la certificación con la DGII en una lista, el set de pruebas que se sube y se envía solo, y un camino para quien ya emite e-CF con otro programa.</News>
          <News title="Contingencia sin internet o sin certificado">Sin internet, los e-CF esperan y salen solos dentro de las 72 horas. Si no se puede firmar, Toki guía la contingencia con comprobantes serie B y su regularización.</News>
          <News title="Comprobantes de tus suplidores">La nube recibe los e-CF que te envían tus suplidores, los acusa las 24 horas y los muestra en Compras. Las facturas de crédito fiscal que emites llegan solas al comprador que es emisor electrónico.</News>
          <News title="Encargos y cotizaciones">Encargos con fecha, hora y abono, que se cobran con el peso real. Cotizaciones con vigencia que se convierten en venta.</News>
          <News title="Catálogo con QR y pedido por WhatsApp">Una página pública con tus productos y precios, sin costos ni existencias, y un QR para el mostrador.</News>
          <News title="Pedido sugerido">Mínimos y máximos por producto; Toki calcula cuánto pedir, a qué suplidor y crea la compra en borrador.</News>
          <News title="Permisos más finos">Abrir caja, cerrar caja, cobrar fiado, ver costos y pagar a suplidores son permisos separados. Roles de partida para cajero, inventario y contador. Nadie puede dar un permiso que no tiene.</News>
          <News title="Reportes y documentos">Reporte 608 de comprobantes anulados, ventas por medio de pago y por hora, y el cierre Z y el estado de cuenta del fiado en una página para guardar como PDF.</News>
          <News title="Panel del dueño">Quién te debe, por nombre; lo más vendido en libras o unidades; más de una persona con acceso (dueño o solo lectura) por invitación; modo oscuro.</News>
        </ul>
      </Section>
    </>
  );
}

export const notFound: SitePage = {
  path: "*",
  title: "Página no encontrada · Toki",
  description: "La página que buscas no existe.",
  element: (
    <Hero
      title="No encontramos esa página."
      cta={
        <Link className="btn" to="/">
          Ir al inicio
        </Link>
      }
    >
      <p>Puede que el enlace haya cambiado.</p>
    </Hero>
  ),
};

export const sitePages: SitePage[] = [
  {
    path: "/",
    nav: "Inicio",
    title: "Toki · Punto de venta para carnicerías, colmados y delis",
    description: "Toki vende por libra, cuadra la caja, lleva el fiado y emite e-CF, aunque se vaya el internet. Hecho para el comercio de alimentos en República Dominicana.",
    element: <Home />,
  },
  {
    path: "/carniceria",
    nav: "Carnicería",
    title: "Toki para carnicerías · Balanza, libras y e-CF sin internet",
    description: "Punto de venta para carnicerías: venta por libra con etiquetas de balanza, encargos con abono, cuadre de caja y e-CF aunque se vaya el internet.",
    element: <Butcher />,
  },
  {
    path: "/colmado",
    nav: "Colmado",
    title: "Toki para colmados · Fiado, venta rápida y e-CF",
    description: "Punto de venta para colmados: venta rápida con escáner, fiado con límite y estado de cuenta, catálogo con pedido por WhatsApp y e-CF aunque se vaya el internet.",
    element: <Grocery />,
  },
  {
    path: "/deli",
    nav: "Deli",
    title: "Toki para delis · Por libra, encargos y cotizaciones",
    description: "Punto de venta para delis: quesos y embutidos por libra, bandejas por encargo con abono, cotizaciones que se convierten en venta y e-CF sin internet.",
    element: <Deli />,
  },
  {
    path: "/e-cf",
    nav: "¿Ya te toca el e-CF?",
    title: "¿Ya te toca el e-CF? · Plazo del 15 de noviembre de 2026",
    description: "Guía corta sobre la facturación electrónica (e-CF) en República Dominicana: a quién le toca el 15 de noviembre de 2026, qué necesitas y cómo Toki factura sin internet.",
    element: <Ecf />,
  },
  {
    path: "/certificacion",
    nav: "Certifícate",
    title: "Certifícate con Toki · Facturación electrónica ante la DGII",
    description: "Qué necesitas para certificarte como emisor electrónico, las etapas ante la DGII, quién hace qué y qué pasa si ya emites e-CF con otro programa.",
    element: <Certification />,
  },
  {
    path: "/descargar",
    nav: "Descargar",
    title: "Descargar Toki · Instalador para Windows",
    description: "Descarga el instalador de Toki para Windows 10 y 11: PC principal con servidor y base de datos, o caja. Requisitos y pasos para instalar.",
    element: <Download />,
  },
  {
    path: "/precios",
    nav: "Precios",
    title: "Precios de Toki · Planes por tamaño de tienda",
    description: "Planes de Toki según el número de cajas y sucursales. Lo básico está en todos: balanza, e-CF sin cupo, inventario, cuadre ciego, fiado, 606/607/608 y respaldo.",
    element: <Pricing />,
  },
  {
    path: "/novedades",
    nav: "Novedades",
    title: "Novedades de Toki",
    description: "Lo nuevo en Toki: facturación electrónica directa con la DGII, encargos, cotizaciones, catálogo por WhatsApp, permisos por acción y más.",
    element: <WhatsNew />,
  },
];
