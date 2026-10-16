import type { ReactNode } from "react";
import { Link } from "react-router";
import { site } from "./config";
import { usePlanPrices } from "./livePrices";
import { useReferralLink } from "../../referral";
import { BusinessArt, BusinessCards, Contact, CtaBand, EcfFlow, Faq, Hero, Icon, IconTiles, ProductArt, Section, Steps, Tbd, Trial } from "./SiteParts";

/** The public site (TK-197, TK-198, TK-226, TK-228; TK-245: less text, more product). `title` and `description` go in <head> (SiteLayout on the client, the prerender at build). */
export interface SitePage {
  path: string;
  /** Label in the menus; pages without one (404) are not listed. */
  nav?: string;
  title: string;
  description: string;
  element: ReactNode;
}

const Price = ({ value }: { value: string | null }) => (value ? <>{value}</> : <Tbd />);

const both = (
  <>
    <Trial />
    <Contact />
  </>
);

const switching: [string, ReactNode] = [
  "Ya tengo otro sistema. ¿Puedo cambiarme?",
  <>
    Sí. Si ya emite e-CF con otro programa, vea <Link to="/certificacion#ya-emito">"Ya emito e-CF con otro programa"</Link>.
  </>,
];

function Home() {
  return (
    <>
      <Hero
        eyebrow="Punto de venta con e-CF"
        title="Venda, pese y facture. Aunque se vaya el internet."
        sub="La caja para carnicerías, colmados y delis."
        cta={both}
        note="Sin tarjeta · Los 14 días empiezan con su primera venta"
        art={<ProductArt />}
      />
      <Section className="counter">
        <IconTiles
          items={[
            ["offline", "Sin internet, sigue vendiendo"],
            ["scale", "Pesa por libra y lee etiquetas"],
            ["ecf", "e-CF directo con la DGII"],
            ["register", "La caja cuadra al cerrar"],
          ]}
        />
      </Section>
      <Section>
        <h2>Hecha para su mostrador</h2>
        <BusinessCards />
      </Section>
      <EcfFlow>
        <Link className="night__link" to="/certificacion">
          ¿Necesita certificarse? Le acompañamos →
        </Link>
      </EcfFlow>
      <Section>
        <h2>Empiece hoy, en 3 pasos</h2>
        <Steps
          round
          items={[
            ["Descargue Toki", <>En la PC de la tienda. <Link to="/descargar">Descargar</Link></>],
            ["Cargue sus productos", "O empiece con los de ejemplo."],
            ["Venda", "Y vea el día desde el celular."],
          ]}
        />
      </Section>
      <CtaBand />
    </>
  );
}

function Butcher() {
  return (
    <>
      <Hero eyebrow="Carnicería" title="Cada libra cobrada, cada caja cuadrada." sub="Lee la etiqueta de la balanza, cobra y factura, aunque se caiga el internet." cta={both} art={<BusinessArt kind="butcher" />} />
      <Section className="counter">
        <IconTiles
          items={[
            ["tag", "Etiquetas de peso", "EAN-13 con peso o precio. Avisa si se escanea dos veces."],
            ["calendar", "Encargos con abono", "Fecha de entrega, abono a la caja y cobro con el peso real."],
            ["box", "Inventario por libra", "Compras, mermas, conteos y pedido sugerido."],
            ["stores", "Varias cajas", "Existencias y precios compartidos, con o sin internet."],
          ]}
        />
      </Section>
      <Faq
        items={[
          ["¿Qué pasa si se va el internet?", "Las cajas siguen vendiendo contra la PC principal, que firma cada e-CF. Al volver la conexión los envía a la DGII, que da 72 horas."],
          ["¿Funciona con mi balanza?", "Lee las etiquetas EAN-13 con peso o precio de las balanzas etiquetadoras. La conexión directa de la balanza a la caja está en desarrollo; mientras tanto, el peso se puede escribir. Antes de instalar revisamos su modelo."],
          ["¿Puedo vender con tres decimales?", "Sí. Las libras se guardan con tres decimales y el total se redondea al centavo."],
          [
            "¿Sirve para el e-CF?",
            <>
              Sí: consumo, crédito fiscal y notas de crédito, firmadas en la tienda con su certificado. Vea <Link to="/e-cf">¿Ya le toca el e-CF?</Link> y <Link to="/certificacion">Certifíquese con Toki</Link>.
            </>,
          ],
          switching,
          ["¿El cajero puede ver mis costos?", "No, salvo que usted le dé ese permiso."],
        ]}
      />
      <CtaBand />
    </>
  );
}

function Grocery() {
  return (
    <>
      <Hero eyebrow="Colmado" title="El fiado claro y la caja al día." sub="Venta rápida, fiado sin cuaderno y el e-CF de cada venta." cta={both} art={<BusinessArt kind="grocery" />} />
      <Section className="counter">
        <IconTiles
          items={[
            ["bolt", "Venta rápida", "Escáner siempre listo y el total grande. Por libra también."],
            ["people", "Fiado con límite", "Abonos, antigüedad y estado de cuenta para WhatsApp."],
            ["qr", "Pedidos por WhatsApp", "Catálogo con QR para el mostrador, sin costos ni existencias."],
            ["box", "Reponer a tiempo", "Mínimos, máximos y pedido sugerido con el último suplidor."],
          ]}
        />
      </Section>
      <Faq
        items={[
          ["Tengo una sola caja. ¿Necesito una PC aparte?", "No. En una tienda de una caja, la misma PC es la caja y la principal."],
          ["¿Qué pasa si se va el internet o la luz?", "Sin internet sigue vendiendo y los e-CF salen al volver la conexión (la DGII da 72 horas). Si la PC no puede emitir e-CF (por ejemplo, se venció el certificado), Toki lo guía para declarar la contingencia, facturar con serie B y luego regularizar."],
          ["¿El fiado sale en el cuadre de la caja?", "Sí. Lo vendido a crédito y lo cobrado de fiado salen separados en el cierre de cada turno."],
          ["¿Hay un límite de facturas al mes?", "No. Toki no cobra por documento ni detiene la facturación por volumen."],
          [
            "¿Cuándo me toca el e-CF?",
            <>
              Para pequeños, micro y no clasificados la fecha publicada es el 15 de noviembre de 2026. Vea <Link to="/e-cf">¿Ya le toca el e-CF?</Link>
            </>,
          ],
          switching,
        ]}
      />
      <CtaBand />
    </>
  );
}

function Deli() {
  return (
    <>
      <Hero eyebrow="Deli" title="Del picadero a la factura, sin enredos." sub="Por libra y por unidad en la misma factura, y los encargos con su abono." cta={both} art={<BusinessArt kind="deli" />} />
      <Section className="counter">
        <IconTiles
          items={[
            ["scale", "Por libra y por unidad", "La factura dice \"0.750 lb\" o \"3 unidades\"."],
            ["calendar", "Encargos y bandejas", "Entrega, abono y los encargos de hoy y mañana a la vista."],
            ["quote", "Cotizaciones", "Con vigencia; al aceptarla se cobra con los precios del día."],
            ["clock", "Ventas por hora", "Comparadas con la semana pasada."],
          ]}
        />
      </Section>
      <Faq
        items={[
          ["¿Puedo cobrar el encargo con el peso real?", "Sí. Se ajusta el peso y se descuenta lo que el cliente abonó."],
          ["¿Una cotización mueve el inventario?", "No. No toca el inventario ni la caja hasta que se convierte en venta."],
          ["¿Puedo darle crédito fiscal a una empresa?", "Sí. Con el RNC del cliente, Toki emite el e-CF de crédito fiscal (tipo 31) y, si la empresa es emisora electrónica, se lo entrega directamente."],
          ["¿Qué pasa si se va el internet?", "Sigue vendiendo. Los e-CF se firman en la tienda y se envían al volver la conexión, dentro de las 72 horas que da la DGII."],
          switching,
        ]}
      />
      <CtaBand />
    </>
  );
}

function Ecf() {
  return (
    <>
      <Hero eyebrow="Facturación electrónica" title="¿Ya le toca el e-CF?" sub="Pequeños, micro y no clasificados (Ley 32-23), según la DGII:">
        <p className="date">15/11/2026</p>
        <p className="muted">La DGII dio una prórroga de seis meses en mayo de 2026. Confirme su clasificación y su fecha en la Oficina Virtual de la DGII o con su contador.</p>
      </Hero>
      <Section className="counter">
        <IconTiles
          cols={3}
          items={[
            ["ecf", "¿Qué es un e-CF?", "La factura con e-NCF, firmada y enviada a la DGII. El cliente recibe una versión impresa con QR."],
            ["calendar", "¿Me toca a mí?", "Entra por grupos. Pequeño, micro o no clasificado: 15 de noviembre de 2026."],
            ["shield", "¿Y si no cumplo?", "La Ley 32-23 (art. 27) y el Código Tributario (art. 257) prevén sanciones. Pregunte a su contador."],
          ]}
        />
      </Section>
      <Section>
        <h2>Qué necesita</h2>
        <IconTiles
          items={[
            ["key", "RNC y Oficina Virtual", "RNC activo y al día, con acceso a la OFV."],
            ["lock", "Certificado digital", "De persona física, de Avansi o la Cámara de Comercio y Producción de Santo Domingo (Digifirma), autorizadas por INDOTEL. Precio de la entidad."],
            ["pc", "Programa certificado", <>Certificado ante la DGII para su RNC: la <Link to="/certificacion">certificación</Link>.</>],
            ["tag", "Secuencias e-NCF", "Las autoriza la DGII en la OFV."],
          ]}
        />
        <p className="muted">La DGII no cobra por certificarle ni por cada documento enviado.</p>
      </Section>
      <EcfFlow />
      <Section>
        <h2>Con internet o sin él</h2>
        <div className="table-wrap">
          <table>
            <thead><tr><th>Situación</th><th>Qué hace Toki</th><th>Plazo de la DGII</th></tr></thead>
            <tbody>
              <tr><td>Hay internet</td><td>Firma en su tienda y envía al momento.</td><td>—</td></tr>
              <tr><td>Se fue el internet</td><td>Sigue firmando con la leyenda de contingencia; envía al volver la conexión.</td><td>72 horas desde la emisión</td></tr>
              <tr><td>No se puede emitir e-CF (certificado vencido, PC dañada)</td><td>Lo guía para declarar la contingencia en la OFV, facturar con serie B y luego emitir los e-CF que los reemplazan.</td><td>Hasta 15 días de contingencia; 30 días para regularizar</td></tr>
              <tr><td>La DGII no responde</td><td>Guarda los e-CF y los envía cuando vuelve el servicio.</td><td>Si pasa de 15 días hábiles, la OFV habilita otra forma de reportar</td></tr>
            </tbody>
          </table>
        </div>
        <p className="muted">La factura de consumo de menos de RD$250,000 se reporta con un resumen firmado; Toki guarda el e-CF completo 10 años, como pide la DGII.</p>
      </Section>
      <Faq
        items={[
          ["¿Hay una opción gratis de la DGII?", "Sí, el Facturador Gratuito, para negocios con poco volumen. Emite e-CF, pero no es un punto de venta con caja, inventario ni balanza."],
          ["¿Puedo usar el certificado que regala la DGII?", "No con Toki. Ese certificado solo sirve para el Facturador Gratuito de la DGII."],
          [
            "¿Cuánto tarda la certificación?",
            <>
              Depende en buena parte de la DGII. La parte técnica (set de pruebas y simulación) la hace Toki. Vea <Link to="/certificacion">Certifíquese con Toki</Link>.
            </>,
          ],
          ["¿Por cuánto tiempo hay que guardar los e-CF?", "Diez años. Toki los guarda en la tienda y una copia en la nube."],
        ]}
      />
      <Section>
        <div className="note">
          <p>Esta guía resume lo publicado por la DGII y no es asesoría legal. Las normas que mandan son la Ley 32-23, el Decreto 587-24 y las normas generales de la DGII. Ante cualquier duda, consulte a su contador o a la DGII.</p>
        </div>
        <div className="actions">
          <Contact className="btn">Quiero facturar con Toki</Contact>
          <a className="btn btn--ghost" href="https://dgii.gov.do/" rel="noopener">
            Ir a la DGII
          </a>
        </div>
      </Section>
    </>
  );
}

/** TK-235: what the owner prepares (docs/servicio-certificacion.md §5); the session-1 e-mail sends the same list (AccompanimentChecklist). */
export const preparation = [
  "RNC activo y al día con la DGII (sin declaraciones pendientes).",
  "Acceso a la Oficina Virtual (OFV) de la empresa y, si aplica, del representante: usuario y clave que solo usted conoce. Pruebe que entra antes de la cita. Toki nunca le pide esa clave.",
  "Alta de NCF (autorización para emitir comprobantes fiscales).",
  "Un representante vinculado al RNC, que será el Usuario Administrador e-CF.",
  "El certificado digital de persona física para procedimientos tributarios, a nombre de ese representante, comprado a Avansi (Viafirma) o a la Cámara de Comercio y Producción de Santo Domingo (Digifirma). Pídelo como archivo .p12 o .pfx con su contraseña (no en token USB ni en la nube del emisor).",
  "La PC principal de la tienda con Toki instalado, encendida y con internet durante las citas.",
  "Una hora libre para cada cita, sin atender el mostrador.",
];

function Certification() {
  return (
    <>
      <Hero eyebrow="Certificación e-CF" title="Certifíquese con Toki." sub="Toki arma, firma y envía las pruebas. Usted hace los pasos con su clave de la Oficina Virtual." cta={<Contact className="btn">Quiero certificarme</Contact>}>
        <p className="hero__key">
          <strong>Toki nunca le pide esa clave.</strong>
        </p>
      </Hero>
      <Section id="preparacion">
        <h2>Antes de empezar</h2>
        <p className="muted">Con acompañamiento, se lo enviamos también en el correo que confirma la primera cita.</p>
        <ul className="checks">
          <li>
            Toki instalado en la PC principal de su tienda. <Link to="/descargar">Descargar Toki</Link>
          </li>
          {preparation.map((item) => (
            <li key={item}>{item}</li>
          ))}
        </ul>
      </Section>
      <Section className="counter">
        <h2>Las etapas</h2>
        <Steps
          items={[
            ["Solicitud", "Usted envía el formulario FI-GDF-016 desde la OFV."],
            ["Pruebas libres", "Toki emite facturas de prueba de cada tipo."],
            ["Postulación", "Registra el programa y sus direcciones; Toki firma el archivo."],
            ["Set de pruebas", "Sube el Excel de la DGII; Toki arma, firma y envía cada comprobante."],
            ["Simulación", "Vende como un día normal, en el ambiente de certificación."],
            ["Comunicación", "La nube de Toki responde los acuses, las 24 horas."],
            ["Declaración jurada", "Toki la firma con su certificado; la acepta y la sube usted."],
            ["Producción", "La DGII lo habilita; carga sus secuencias e-NCF y factura."],
          ]}
        />
        <p className="muted">Los tiempos entre una etapa y otra los pone la DGII.</p>
      </Section>
      <Section>
        <h2>Quién hace qué</h2>
        <div className="table-wrap">
          <table>
            <thead><tr><th></th><th>Por su cuenta</th><th>Con acompañamiento de Toki</th></tr></thead>
            <tbody>
              <tr><th scope="row">Quién usa el asistente de certificación</th><td>Usted, con la lista paso a paso</td><td>Un técnico de Toki, en una sesión remota con usted presente</td></tr>
              <tr><th scope="row">Pasos con su clave de la OFV</th><td>Usted</td><td>Usted escribe su clave; el técnico le dice qué marcar. Toki no ve ni guarda la clave</td></tr>
              <tr><th scope="row">Set de pruebas, simulación y acuses</th><td>Toki, automático</td><td>Toki, automático; el técnico vigila los rechazos</td></tr>
              <tr><th scope="row">Declaración jurada</th><td>La acepta usted</td><td>La acepta usted</td></tr>
              <tr>
                <th scope="row">Precio</th>
                <td>Incluido en la licencia</td>
                <td>{site.prices.certificationSupport ?? <Tbd>Precio por confirmar</Tbd>}</td>
              </tr>
            </tbody>
          </table>
        </div>
        <p className="muted">El certificado digital no está incluido: se compra directamente a la entidad certificadora.</p>
      </Section>
      <Section id="ya-emito" className="counter">
        <h2>Ya emito e-CF con otro programa</h2>
        <p>
          Si su RNC ya es emisor electrónico, no debería repetir la certificación: solo cambia el programa. <Tbd>Por confirmar con la DGII</Tbd>
        </p>
        <Steps
          items={[
            ["Su certificado", "Importe en Toki el mismo .p12, si sigue vigente."],
            ["Las direcciones", "En la OFV, \"Mantenimiento Directorio FE\": ponga las de Toki completas, no solo el dominio."],
            ["Los números que quedan", "Escriba el último e-NCF emitido; Toki sigue desde el siguiente (o anula los sobrantes si la DGII lo pide)."],
            ["El cambio", "Deje el otro programa (la misma secuencia repetiría números), pase a producción y revise la primera venta."],
          ]}
        />
      </Section>
    </>
  );
}

function Download() {
  return (
    <>
      <Hero
        eyebrow="Descargar"
        title="Instale Toki en su tienda."
        sub="Un instalador para todas las PC. La prueba de 14 días empieza con su primera venta."
        cta={
          site.installerUrl ? (
            <a className="btn" href={site.installerUrl}>
              Descargar Toki para Windows
            </a>
          ) : (
            <Tbd>[URL DEL INSTALADOR]</Tbd>
          )
        }
        note="Sin código ni tarjeta"
      />
      <Section className="counter">
        <h2>Elija el papel de cada PC</h2>
        <div className="tiles tiles--2">
          <div className="tile">
            <Icon name="pc" />
            <h3>PC principal</h3>
            <p>Guarda las ventas y atiende a las cajas: servidor, base de datos (SQL Server Express si no la tiene) y caja. Instálela primero.</p>
          </div>
          <div className="tile">
            <Icon name="register" />
            <h3>Caja</h3>
            <p>Cada PC de cobro adicional: solo la app de caja, unida con el código de Configuración → Equipos.</p>
          </div>
        </div>
      </Section>
      <Section>
        <h2>Requisitos</h2>
        <IconTiles
          items={[
            ["pc", "Windows 10 u 11", "De 64 bits."],
            ["data", "Memoria y disco", "Principal: 8 GB de RAM y 10 GB libres. Caja: 4 GB y 2 GB."],
            ["download", "Internet al instalar", "Baja de Microsoft lo que falte (SQL Server Express y WebView2)."],
            ["stores", "Misma red", "Todas las PC en la red de la tienda."],
          ]}
        />
      </Section>
      <Section className="counter">
        <h2>Pasos</h2>
        <Steps
          items={[
            ["Descargue", "Abra el instalador en la PC principal; Windows pide permiso una vez."],
            ["En la PC principal", "Elija \"PC principal\" y complete \"Primera instalación\" con su negocio y su usuario."],
            ["En cada caja", "Mismo instalador, elija \"Caja\" y únala con el código de Configuración → Equipos."],
            ["Actualizaciones", "Solas: la caja al abrir, el servidor sin turnos abiertos o fuera de horario."],
          ]}
        />
        <p className="muted">
          ¿Quiere que lo instalemos por usted? <Contact className="">Hablemos</Contact> · ¿Se va a certificar para el e-CF? Vea <Link to="/certificacion">Certifíquese con Toki</Link>.
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
  const prices = usePlanPrices(); // TK-249: from Paddle; "Precio por confirmar" while billing is off
  const referred = useReferralLink(); // TK-255: /precios?ref=CODE
  return (
    <>
      <Hero eyebrow="Precios" title="Pague por el tamaño de su tienda, no por factura." sub="Tres planes según cajas y sucursales. Lo esencial, en todos.">
        <p className="note">Estamos cerrando los precios. Los publicamos aquí en cuanto estén aprobados; mientras tanto, escríbanos y le damos una cotización.</p>
      </Hero>
      <Section>
        <h2>Planes</h2>
        <div className="grid grid--3">
          <Plan name="Una caja" note="Colmado o tienda con un punto de cobro." price={prices.Default}>
            <li>1 caja</li>
            <li>1 sucursal</li>
          </Plan>
          <Plan name="Varias cajas" note="Carnicería o deli con fila." price={prices.MultiRegister} brand>
            <li>Varias cajas <Tbd>Cantidad por confirmar</Tbd></li>
            <li>1 sucursal</li>
          </Plan>
          <Plan name="Varias sucursales" note="Más de un local." price={prices.MultiStore}>
            <li>Cajas por sucursal <Tbd>Cantidad por confirmar</Tbd></li>
            <li>Varias sucursales</li>
          </Plan>
        </div>
        <p className="muted">Entre planes cambian las cajas, las sucursales y el acceso desde el celular del dueño.</p>
        {/* TK-249: the account first, then "Plan y facturación" in the panel (pago con tarjeta por Paddle). */}
        {referred && <p className="note">Viene recomendado por un cliente de Toki. Cree su cuenta y elija su plan.</p>}
        <p className="actions">
          <Link className="btn" to="/app/billing">
            Crear su cuenta y elegir plan
          </Link>
        </p>
      </Section>
      <Section className="counter">
        <h2>En todos los planes</h2>
        <IconTiles
          items={[
            ["scale", "Venta por libra y unidad", "Escáner, balanza, impresora y gaveta, con o sin internet."],
            ["ecf", "e-CF sin cupo", "Consumo, crédito fiscal y notas de crédito, sin cobro por documento."],
            ["box", "Inventario completo", "Compras, mermas, conteos, mínimos y pedido sugerido."],
            ["register", "Cuadre ciego y cierre Z", "Autorización de supervisor con PIN."],
            ["people", "Clientes y fiado", "Con límite y estado de cuenta."],
            ["quote", "606, 607 y 608", "Para su contador."],
            ["lock", "Permisos por acción", "Usuarios, roles y registro de auditoría."],
            ["data", "Sus datos", "Respaldo y exportación completa cuando quiera."],
          ]}
        />
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
                <td>Un técnico lo guía en cada etapa ante la DGII. <Link to="/certificacion">Ver detalles</Link></td>
                <td><Price value={prices.certificationSupport} /></td>
              </tr>
              <tr><td>Equipos (PC, balanza, impresoras, escáner, gaveta)</td><td>Kit según su negocio.</td><td>Se cotiza</td></tr>
            </tbody>
          </table>
        </div>
        <p className="muted">El certificado digital para el e-CF se compra directamente a la entidad certificadora; no lo vende Toki.</p>
      </Section>
      <Section className="counter">
        <h2>Reglas que no cambian</h2>
        <IconTiles
          cols={3}
          items={[
            ["bolt", "La venta nunca se detiene", "Ni por volumen de facturas ni por un pago atrasado."],
            ["shield", "Nunca en solo lectura", "Si la licencia vence, sigue vendiendo, emitiendo e-CF, exportando y respaldando; solo se apagan funciones adicionales."],
            ["key", "Todo es suyo", "Sus datos, su certificado y sus secuencias, también si se va."],
          ]}
        />
      </Section>
      <Section id="contacto">
        <h2>Hablemos</h2>
        <p>Díganos qué vende y cuántas cajas tiene, y le preparamos una propuesta.</p>
        <div className="actions">
          <Trial />
          {site.whatsapp ? <Contact className="btn btn--ghost">Escríbanos por WhatsApp</Contact> : <Tbd>Canal de contacto por confirmar</Tbd>}
        </div>
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
      <Hero eyebrow="Novedades" title="Lo nuevo en Toki" sub="Lo más reciente va primero." />
      <Section>
        <h2>Octubre de 2026</h2>
        <ul className="log">
          <News title="Facturación electrónica directa con la DGII">Firma en la tienda y envía sin intermediarios. Un rechazo se reemplaza con un e-CF nuevo; los números sobrantes se anulan desde la pantalla. Cada e-CF se guarda 10 años en la nube.</News>
          <News title="Asistente de certificación y cambio desde otro programa">Las etapas en una lista, el set de pruebas que se envía solo y un camino para quien ya emite e-CF.</News>
          <News title="Contingencia sin internet o sin certificado">Sin internet, los e-CF salen solos dentro de las 72 horas. Si no se puede firmar, Toki guía la contingencia con serie B y su regularización.</News>
          <News title="Comprobantes de sus suplidores">La nube recibe y acusa los e-CF de sus suplidores las 24 horas y los muestra en Compras. Su crédito fiscal llega solo al comprador emisor electrónico.</News>
          <News title="Encargos y cotizaciones">Encargos con fecha, hora y abono, cobrados con el peso real. Cotizaciones con vigencia que se convierten en venta.</News>
          <News title="Catálogo con QR y pedido por WhatsApp">Sus productos y precios en una página pública, sin costos ni existencias, con un QR para el mostrador.</News>
          <News title="Pedido sugerido">Mínimos y máximos; Toki calcula cuánto pedir, a qué suplidor y crea la compra en borrador.</News>
          <News title="Permisos más finos">Abrir caja, cerrar caja, cobrar fiado, ver costos y pagar a suplidores son permisos separados. Nadie da un permiso que no tiene.</News>
          <News title="Reportes y documentos">Reporte 608, ventas por medio de pago y por hora, y el cierre Z y el estado de cuenta del fiado en una página para PDF.</News>
          <News title="Panel del dueño">Quién le debe, lo más vendido, más de una persona con acceso por invitación y modo oscuro.</News>
        </ul>
      </Section>
      <CtaBand />
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
    nav: "e-CF",
    title: "¿Ya le toca el e-CF? · Plazo del 15 de noviembre de 2026",
    description: "Guía corta sobre la facturación electrónica (e-CF) en República Dominicana: a quién le toca el 15 de noviembre de 2026, qué necesita y cómo Toki factura sin internet.",
    element: <Ecf />,
  },
  {
    path: "/certificacion",
    nav: "Certifíquese",
    title: "Certifíquese con Toki · Facturación electrónica ante la DGII",
    description: "Qué necesita para certificarse como emisor electrónico, las etapas ante la DGII, quién hace qué y qué pasa si ya emite e-CF con otro programa.",
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
