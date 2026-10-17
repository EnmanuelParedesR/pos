import type { ReactNode } from "react";
import { Link } from "react-router";
import { site } from "./config";
import { usePlanPrices, type SitePrice } from "./livePrices";
import { useReferralLink } from "../../referral";
import { legalPages } from "./LegalPages";
import { BusinessArt, BusinessCards, Carousel, Contact, CtaBand, EcfFlow, Faq, Hero, HowItWorks, Icon, IconTiles, LoopVideo, ProductArt, ProductVideo, Section, Steps, Tbd, Trial, type IconName } from "./SiteParts";

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

/** A plan or add-on price (TK-313): RD$ first, the US$ that Paddle charges under it. */
const Amount = ({ value }: { value: SitePrice | null }) =>
  value ? (
    <>
      {value.label}
      {value.charged && <small className="price__charged">{value.charged}</small>}
    </>
  ) : (
    <Tbd />
  );

/** TK-313: the launch offer, while there is one. */
const Promo = () => (site.prices.launchPromo ? <p className="note">{site.prices.launchPromo}</p> : null);

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
        sub="Cobre rápido, venda por libra o por unidad y emita el e-CF desde la PC de su tienda."
        cta={both}
        note="Sin tarjeta · Los 14 días empiezan con su primera venta"
        art={<ProductArt />}
      />
      <Section className="counter">
        <h2 className="esc-visually-hidden">Lo principal</h2>
        <IconTiles
          items={[
            ["offline", "Sin internet, sigue vendiendo"],
            ["scale", "Pesa por libra y lee etiquetas"],
            ["ecf", "e-CF directo con la DGII"],
            ["register", "La caja cuadra al cerrar"],
          ]}
        />
      </Section>
      <Section className="scene">
        <div className="scene__grid">
          <figure className="scene__media">
            <LoopVideo name="pesando" poster="/media/pesando-portada.webp" label="Un comerciante pesa un producto en su tienda" />
          </figure>
          <div className="scene__text">
            <h2>Hecho para el mostrador</h2>
            <ul className="checks">
              <li>Por libra y por unidad en la misma venta.</li>
              <li>El fiado y el cuadre, sin cuaderno.</li>
              <li>El e-CF sale solo, con o sin internet.</li>
            </ul>
          </div>
        </div>
      </Section>
      <Section>
        <h2>Se adapta a su negocio</h2>
        <BusinessCards />
      </Section>
      <EcfFlow>
        <Link className="night__link" to="/certificacion">
          ¿Necesita certificarse? Le acompañamos →
        </Link>
      </EcfFlow>
      <Section className="how-it-works">
        <h2>Cómo funciona</h2>
        <ProductVideo />
        <h3 className="how-it-works__lead">Empiece hoy, en 3 pasos</h3>
        <HowItWorks
          items={[
            { title: "Instale Abastalia", text: <>En la PC de la tienda. <Link to="/descargar">Descargar</Link></>, time: "10 minutos" },
            { title: "Cargue sus productos", text: "Por libra o por unidad, o los de ejemplo.", time: "El mismo día" },
            { title: "Venda", text: "Escanee o pese y cobre con F12.", time: "Desde la primera venta" },
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
        <h2 className="esc-visually-hidden">Lo que hace por su carnicería</h2>
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
          ["¿Funciona con mi balanza?", "Se conecta por puerto COM (o USB) con balanzas compatibles con CAS, Mettler Toledo y A&D, y lee las etiquetas EAN-13 con peso o precio de las balanzas etiquetadoras. Si no hay balanza, el peso se escribe. Antes de instalar revisamos su modelo."],
          ["¿Puedo vender con tres decimales?", "Sí. Las libras se guardan con tres decimales y el total se redondea al centavo."],
          [
            "¿Sirve para el e-CF?",
            <>
              Sí: consumo, crédito fiscal y notas de crédito, firmadas en la tienda con su certificado. Vea <Link to="/e-cf">¿Ya le toca el e-CF?</Link> y <Link to="/certificacion">Certifíquese con Abastalia</Link>.
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
        <h2 className="esc-visually-hidden">Lo que hace por su colmado</h2>
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
          ["¿Qué pasa si se va el internet o la luz?", "Sin internet sigue vendiendo y los e-CF salen al volver la conexión (la DGII da 72 horas). Si la PC no puede emitir e-CF (por ejemplo, se venció el certificado), Abastalia lo guía para declarar la contingencia, facturar con serie B y luego regularizar."],
          ["¿El fiado sale en el cuadre de la caja?", "Sí. Lo vendido a crédito y lo cobrado de fiado salen separados en el cierre de cada turno."],
          ["¿Hay un límite de facturas al mes?", "No. Abastalia no cobra por documento ni detiene la facturación por volumen."],
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
        <h2 className="esc-visually-hidden">Lo que hace por su deli</h2>
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
          ["¿Puedo darle crédito fiscal a una empresa?", "Sí. Con el RNC del cliente, Abastalia emite el e-CF de crédito fiscal (tipo 31) y, si la empresa es emisora electrónica, se lo entrega directamente."],
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
        <h2 className="esc-visually-hidden">Lo básico del e-CF</h2>
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
        <p className="muted">
          Paso a paso en la <Link to="/guia-e-cf">guía e-CF</Link>; las palabras, en el <Link to="/glosario">glosario</Link>.
        </p>
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
            <thead><tr><th>Situación</th><th>Qué hace Abastalia</th><th>Plazo de la DGII</th></tr></thead>
            <tbody>
              <tr><td>Hay internet</td><td>Firma en su tienda y envía al momento.</td><td>—</td></tr>
              <tr><td>Se fue el internet</td><td>Sigue firmando con la leyenda de contingencia; envía al volver la conexión.</td><td>72 horas desde la emisión</td></tr>
              <tr><td>No se puede emitir e-CF (certificado vencido, PC dañada)</td><td>Lo guía para declarar la contingencia en la OFV, facturar con serie B y luego emitir los e-CF que los reemplazan.</td><td>Hasta 15 días de contingencia; 30 días para regularizar</td></tr>
              <tr><td>La DGII no responde</td><td>Guarda los e-CF y los envía cuando vuelve el servicio.</td><td>Si pasa de 15 días hábiles, la OFV habilita otra forma de reportar</td></tr>
            </tbody>
          </table>
        </div>
        <p className="muted">La factura de consumo de menos de RD$250,000 se reporta con un resumen firmado; Abastalia guarda el e-CF completo 10 años, como pide la DGII.</p>
      </Section>
      <Faq
        items={[
          [
            "¿Hay una opción gratis de la DGII?",
            <>
              Sí, el Facturador Gratuito, para negocios con poco volumen. Emite e-CF, pero no es un punto de venta con caja, inventario ni balanza. <Link to="/comparar">Compárelos</Link>
            </>,
          ],
          ["¿Puedo usar el certificado que regala la DGII?", "No con Abastalia. Ese certificado solo sirve para el Facturador Gratuito de la DGII."],
          [
            "¿Cuánto tarda la certificación?",
            <>
              Depende en buena parte de la DGII. La parte técnica (set de pruebas y simulación) la hace Abastalia. Vea <Link to="/certificacion">Certifíquese con Abastalia</Link>.
            </>,
          ],
          ["¿Por cuánto tiempo hay que guardar los e-CF?", "Diez años. Abastalia los guarda en la tienda y una copia en la nube."],
        ]}
      />
      <Section>
        <div className="note">
          <p>Esta guía resume lo publicado por la DGII y no es asesoría legal. Las normas que mandan son la Ley 32-23, el Decreto 587-24 y las normas generales de la DGII. Ante cualquier duda, consulte a su contador o a la DGII.</p>
        </div>
        <div className="actions">
          <Contact className="btn">Quiero facturar con Abastalia</Contact>
          <a className="btn btn--ghost" href="https://dgii.gov.do/" rel="noopener">
            Ir a la DGII
          </a>
        </div>
      </Section>
    </>
  );
}

/** TK-263: "updated on" and the official sources behind the DGII facts of a page. */
function Sources({ links }: { links: [string, string][] }) {
  return (
    <Section className="sources">
      <p className="muted">
        Actualizado el <time dateTime="2026-10-16">16/10/2026</time>. Fuentes:{" "}
        {links.map(([text, href], i) => (
          <span key={href}>
            {i > 0 && " · "}
            <a href={href} rel="noopener">
              {text}
            </a>
          </span>
        ))}
        . No es asesoría legal: confirme con la DGII o su contador.
      </p>
    </Section>
  );
}

const dgii = {
  prorroga: "https://dgii.gov.do/noticias/Paginas/DGII-otorga-prorroga-seis-meses-Peque%C3%B1os-Micros-y-no-clasificados.aspx",
  grandes: "https://dgii.gov.do/noticias/Paginas/DGII-informa-emision-exclusiva-de-facturas-electronicas-para-Grandes-Locales-y-Medianos-contribuyentes-desde-noviembre.aspx",
  gratuito: "https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Paginas/facturador-gratuito.aspx",
  gratuitoFaq: "https://dgii.gov.do/cicloContribuyente/facturacion/comprobantesFiscalesElectronicosE-CF/Preguntas%20frecuentes/Generales/Preguntas-Frecuentes-Facturador-Gratuito.pdf",
  formatos: "https://dgii.gov.do/cicloContribuyente/obligacionesTributarias/remisionInformacion/Paginas/formatoEnvioDatos.aspx",
};

/** TK-263: for "cómo empezar con el e-CF": the steps in order, each with where it is done. */
function EcfGuide() {
  return (
    <>
      <Hero eyebrow="Guía e-CF" title="Empiece a emitir e-CF en 6 pasos." sub="De su RNC a la primera factura electrónica." cta={<Contact className="btn">Quiero ayuda con el e-CF</Contact>} />
      <Section className="counter">
        <h2>Los pasos</h2>
        <Steps
          round
          items={[
            ["Confirme su fecha", <>Pequeños, micro y no clasificados: 15/11/2026. <Link to="/e-cf">¿Ya le toca?</Link></>],
            ["RNC y Oficina Virtual", "RNC al día, alta de NCF y acceso a la OFV."],
            ["Certificado digital", <>De persona física, de una entidad autorizada por INDOTEL. Pídalo en archivo <Link to="/glosario">.p12</Link>.</>],
            ["Elija cómo facturar", <>El Facturador Gratuito de la DGII o un programa certificado. <Link to="/comparar">Compare</Link></>],
            ["Certifíquese", <>La DGII prueba el programa con su RNC. <Link to="/certificacion">Cómo es</Link></>],
            ["Secuencias y a vender", "Pida sus e-NCF en la OFV y emita su primer e-CF."],
          ]}
        />
      </Section>
      <Faq
        items={[
          ["¿Tengo que pedir la prórroga al 15 de noviembre?", "No. La DGII la dio en mayo de 2026 a todos los pequeños, micro y no clasificados (Aviso 06-26)."],
          ["¿Hasta cuándo sirven mis comprobantes con serie B?", "Grandes locales y medianos emiten solo e-CF desde el 1 de noviembre de 2026; su serie B queda para contingencia. Para los demás, confirme con la DGII o su contador."],
          ["¿Necesito internet para facturar?", "Con Abastalia, no siempre: firma en la tienda y envía al volver la conexión, dentro de las 72 horas que da la DGII. El Facturador Gratuito sí necesita internet."],
          [
            "¿Cuánto tarda todo?",
            <>
              Lo más lento suele ser la DGII entre una etapa y otra. Con Abastalia, la parte técnica de la <Link to="/certificacion">certificación</Link> es automática.
            </>,
          ],
          ["¿Qué pasa si no cumplo la fecha?", "La Ley 32-23 (art. 27) prevé sanciones. Pregunte a su contador."],
        ]}
      />
      <Sources links={[["Prórroga (DGII, mayo 2026)", dgii.prorroga], ["Grandes y medianos (DGII, 26/08/2026)", dgii.grandes]]} />
      <CtaBand />
    </>
  );
}

const yes = <span className="ok">Sí</span>;

/** Criterion, Abastalia, Facturador Gratuito (the DGII's own FAQ unless dated otherwise), and what to ask any other program (TK-263, TK-294):
 * no claims about other programs, only questions. */
const versus: [IconName, string, ReactNode, ReactNode, string][] = [
  ["tag", "Costo", <>Mensual, por tamaño de tienda. <Link to="/precios">Ver precios</Link></>, "Gratis", "¿Cuánto cobra al mes? ¿Cobra por factura?"],
  ["ecf", "Pensado para", "Tiendas con mostrador, sin cupo ni cobro por documento", "Bajo volumen. Unas 150 facturas al mes, según la DGII (El Día, 12/05/2026)", "¿Tiene cupo o tope de facturas?"],
  ["offline", "Sin internet", <>{yes}: firma y envía al volver (72 horas)</>, "No emite", "¿Factura si se va el internet?"],
  ["scale", "Caja, balanza, inventario y fiado", yes, "No: emite, recibe y consulta e-CF", "¿Lee la balanza y vende por libra?"],
  ["pc", "Dónde se usa", "Programa en la PC de la tienda y panel en el celular", "Portal web de la DGII", "¿Funciona en la PC de la tienda o solo en la web?"],
  ["data", "Guarda sus e-CF", "10 años, en la tienda y en la nube", "Hasta 2 años en el portal; usted debe guardarlos 10", "¿Guarda los e-CF 10 años? ¿Me llevo mis datos si me voy?"],
  ["key", "Certificado digital", "Comprado a una entidad autorizada por INDOTEL", "El gratis de la DGII (solo sirve ahí) o uno comprado", "¿Está certificado ante la DGII?"],
  ["quote", "Tipos de e-CF", "Consumo, crédito fiscal y notas de crédito", "Todos", "¿Emite los tipos que usted usa?"],
];

/** TK-263, TK-294: Abastalia next to the DGII's free tool and any other program, one card per criterion in a carousel. */
function Compare() {
  return (
    <>
      <Hero eyebrow="Comparar" title="¿Facturador Gratuito o un punto de venta?" sub="Los dos emiten e-CF válidos. Cambia lo que pasa en el mostrador." />
      <Carousel
        title="Lado a lado"
        hint={<>{versus.length} puntos. Deslice o use las flechas.</>}
        slides={versus.map(([icon, title, ours, free, ask]) => ({
          icon,
          title,
          body: (
            <dl className="vs">
              <div className="vs__ours"><dt>Abastalia</dt><dd>{ours}</dd></div>
              <div><dt>Facturador Gratuito (DGII)</dt><dd>{free}</dd></div>
              <div className="vs__ask"><dt>Otro programa: pregunte</dt><dd>{ask}</dd></div>
            </dl>
          ),
        }))}
      />
      <Faq
        items={[
          ["¿El Facturador Gratuito es legal para mi colmado?", "Sí. Lo da la DGII a cualquier contribuyente, sobre todo de bajo volumen. Excluye a los grandes contribuyentes nacionales."],
          ["¿Puedo empezar con el Facturador Gratuito y cambiar después?", "Sí, pero el nuevo programa debe certificarse con su RNC. El certificado gratis de la DGII no sirve fuera del Facturador Gratuito."],
          ["¿Abastalia cobra por cada e-CF?", "No. No hay cupo ni cobro por documento, y la venta no se detiene por volumen."],
        ]}
      />
      <Sources links={[["Facturador Gratuito (DGII)", dgii.gratuito], ["Preguntas frecuentes del Facturador Gratuito (DGII)", dgii.gratuitoFaq], ["Límite de 150 al mes (El Día, 12/05/2026)", "https://eldia.com.do/quienes-pueden-usar-el-facturador-electronico-gratuito-de-la-dgii/"]]} />
      <CtaBand />
    </>
  );
}

/** TK-263: the words of the e-CF in one line each. */
function Glossary() {
  return (
    <>
      <Hero eyebrow="Glosario" title="Las palabras del e-CF, sin enredos." sub={<>Para empezar, vea la <Link to="/guia-e-cf">guía en 6 pasos</Link>.</>} />
      <Section className="counter glossary">
        <h2 className="esc-visually-hidden">Términos</h2>
        <IconTiles
          cols={3}
          items={[
            ["ecf", "e-CF", "Comprobante fiscal electrónico: la factura en XML, firmada con su certificado y validada por la DGII."],
            ["tag", "e-NCF", "El número del e-CF: E, el tipo y 10 dígitos. Ejemplo: E320000000001."],
            ["tag", "NCF", "El número del comprobante en papel: B, el tipo y 8 dígitos. Ejemplo: B0200000001."],
            ["quote", "Tipos 31, 32, 33 y 34", "31 crédito fiscal, 32 consumo, 33 nota de débito y 34 nota de crédito."],
            ["data", "RFCE", "Resumen firmado de la factura de consumo de menos de RD$250,000. El e-CF completo se guarda en la tienda."],
            ["qr", "Representación impresa", "La versión en papel del e-CF, con QR y código de seguridad."],
            ["lock", "Certificado .p12", "Certificado digital de persona física, de una entidad autorizada por INDOTEL. Firma cada e-CF."],
            ["key", "OFV", "Oficina Virtual de la DGII: solicitudes, secuencias y declaraciones."],
            ["pc", "TesteCF", "Ambiente de pruebas libres de la DGII."],
            ["shield", "CerteCF", "Ambiente de certificación: set de pruebas y simulación."],
            ["offline", "Contingencia", "Cuando no se puede emitir e-CF: se factura con serie B y luego se regulariza."],
            ["box", "606", "Compras del mes, para la DGII."],
            ["register", "607", "Ventas del mes, para la DGII."],
            ["calendar", "608", "Comprobantes anulados en el mes."],
            ["download", "Facturador Gratuito", <>Portal gratis de la DGII para emitir e-CF con poco volumen. <Link to="/comparar">Comparar</Link></>],
          ]}
        />
        <p className="muted">
          El 606, el 607 y el 608 se envían por la OFV, a más tardar el día 15 del mes siguiente. Abastalia los prepara para su contador. Vea también <Link to="/e-cf">¿Ya le toca el e-CF?</Link>
        </p>
      </Section>
      <Faq
        items={[
          ["¿Cuál es la diferencia entre NCF y e-NCF?", "El NCF numera el comprobante en papel (serie B, 11 caracteres). El e-NCF numera el e-CF (serie E, 13 caracteres)."],
          ["¿El RFCE reemplaza la factura?", "No. El cliente recibe su factura y la tienda guarda el e-CF completo; a la DGII solo va el resumen, si la venta es de consumo y de menos de RD$250,000."],
        ]}
      />
      <Sources links={[["Formatos de envío de datos (DGII)", dgii.formatos], ["Preguntas frecuentes del Facturador Gratuito (DGII)", dgii.gratuitoFaq]]} />
    </>
  );
}

/** TK-235: what the owner prepares (docs/servicio-certificacion.md §5); the session-1 e-mail sends the same list (AccompanimentChecklist). */
export const preparation = [
  "RNC activo y al día con la DGII (sin declaraciones pendientes).",
  "Acceso a la Oficina Virtual (OFV) de la empresa y, si aplica, del representante: usuario y clave que solo usted conoce. Pruebe que entra antes de la cita. Abastalia nunca le pide esa clave.",
  "Alta de NCF (autorización para emitir comprobantes fiscales).",
  "Un representante vinculado al RNC, que será el Usuario Administrador e-CF.",
  "El certificado digital de persona física para procedimientos tributarios, a nombre de ese representante, comprado a Avansi (Viafirma) o a la Cámara de Comercio y Producción de Santo Domingo (Digifirma). Pídelo como archivo .p12 o .pfx con su contraseña (no en token USB ni en la nube del emisor).",
  "La PC principal de la tienda con Abastalia instalado, encendida y con internet durante las citas.",
  "Una hora libre para cada cita, sin atender el mostrador.",
];

function Certification() {
  return (
    <>
      <Hero eyebrow="Certificación e-CF" title="Certifíquese con Abastalia." sub="Abastalia arma, firma y envía las pruebas. Usted hace los pasos con su clave de la Oficina Virtual." cta={<Contact className="btn">Quiero certificarme</Contact>}>
        <p className="hero__key">
          <strong>Abastalia nunca le pide esa clave.</strong>
        </p>
        <Promo />
      </Hero>
      <Section id="preparacion">
        <h2>Antes de empezar</h2>
        <p className="muted">Con acompañamiento, se lo enviamos también en el correo que confirma la primera cita.</p>
        <ul className="checks">
          <li>
            Abastalia instalado en la PC principal de su tienda. <Link to="/descargar">Descargar Abastalia</Link>
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
            ["Pruebas libres", "Abastalia emite facturas de prueba de cada tipo."],
            ["Postulación", "Registra el programa y sus direcciones; Abastalia firma el archivo."],
            ["Set de pruebas", "Sube el Excel de la DGII; Abastalia arma, firma y envía cada comprobante."],
            ["Simulación", "Vende como un día normal, en el ambiente de certificación."],
            ["Comunicación", "La nube de Abastalia responde los acuses, las 24 horas."],
            ["Declaración jurada", "Abastalia la firma con su certificado; la acepta y la sube usted."],
            ["Producción", "La DGII lo habilita; carga sus secuencias e-NCF y factura."],
          ]}
        />
        <p className="muted">
          Los tiempos entre una etapa y otra los pone la DGII. ¿Dudas con un término? Vea el <Link to="/glosario">glosario</Link>.
        </p>
      </Section>
      <Section>
        <h2>Quién hace qué</h2>
        <div className="table-wrap">
          <table>
            <thead><tr><th></th><th>Por su cuenta</th><th>Con acompañamiento de Abastalia</th></tr></thead>
            <tbody>
              <tr><th scope="row">Quién usa el asistente de certificación</th><td>Usted, con la lista paso a paso</td><td>Un técnico de Abastalia, en una sesión remota con usted presente</td></tr>
              <tr><th scope="row">Pasos con su clave de la OFV</th><td>Usted</td><td>Usted escribe su clave; el técnico le dice qué marcar. Abastalia no ve ni guarda la clave</td></tr>
              <tr><th scope="row">Set de pruebas, simulación y acuses</th><td>Abastalia, automático</td><td>Abastalia, automático; el técnico vigila los rechazos</td></tr>
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
            ["Su certificado", "Importe en Abastalia el mismo .p12, si sigue vigente."],
            ["Las direcciones", "En la OFV, \"Mantenimiento Directorio FE\": ponga las de Abastalia completas, no solo el dominio."],
            ["Los números que quedan", "Escriba el último e-NCF emitido; Abastalia sigue desde el siguiente (o anula los sobrantes si la DGII lo pide)."],
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
        title="Instale Abastalia en su tienda."
        sub="Un instalador para todas las PC. La prueba de 14 días empieza con su primera venta."
        cta={
          site.installerUrl ? (
            <a className="btn" href={site.installerUrl}>
              Descargar Abastalia para Windows
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
          ¿Quiere que lo instalemos por usted? <Contact className="">Hablemos</Contact> · ¿Se va a certificar para el e-CF? Vea <Link to="/certificacion">Certifíquese con Abastalia</Link>.
        </p>
      </Section>
    </>
  );
}

function Plan({ name, note, price, children, brand }: { name: string; note: string; price: SitePrice | null; children: ReactNode; brand?: boolean }) {
  return (
    <div className={brand ? "card card--brand" : "card"}>
      <h3>{name}</h3>
      <p className="muted">{note}</p>
      <p className="price">{price ? <Amount value={price} /> : <Tbd>Precio por confirmar</Tbd>}</p>
      <ul className="checks">{children}</ul>
    </div>
  );
}

function Pricing() {
  const { price, provisional } = usePlanPrices(); // TK-249, TK-293: Paddle's, or the provisional ones while billing is off
  const prices = site.prices;
  const referred = useReferralLink(); // TK-255: /precios?ref=CODE
  return (
    <>
      <Hero
        eyebrow="Precios"
        title="Pague por el tamaño de su tienda, no por factura."
        sub="Tres planes según cuántas cajas tiene. Todas las funciones, en todos."
        note={<>{provisional && "Precios provisionales. "}En RD$ a RD${site.prices.dopPerUsd} por US$1; se cobra en US$. Si prefiere, escríbanos y le damos una cotización.</>}
      />
      <Section>
        <h2>Planes</h2>
        <div className="grid grid--3">
          <Plan name="Esencial" note="Para empezar con un solo punto de cobro." price={price("Essential")}>
            <li>Solo la PC principal</li>
            <li>2 cajeros incluidos</li>
            <li>2 usuarios del panel</li>
            <li>100 preguntas al asistente al mes</li>
          </Plan>
          <Plan name="Estándar" note="La PC principal y una caja más." price={price("Default")} brand>
            <li>PC principal + 1 caja</li>
            <li>4 cajeros incluidos</li>
            <li>3 usuarios del panel</li>
            <li>300 preguntas al asistente al mes</li>
          </Plan>
          <Plan name="Pro" note="Para la tienda con fila." price={price("MultiRegister")}>
            <li>PC principal + 3 cajas</li>
            <li>8 cajeros incluidos</li>
            <li>5 usuarios del panel</li>
            <li>600 preguntas al asistente al mes</li>
            <li>Soporte más rápido</li>
          </Plan>
        </div>
        <p className="muted">Todas las funciones en todos los planes: balanza, carnicería, mayoreo, e-CF, inventario, respaldo en la nube y 20 facturas de suplidor leídas con IA al mes.</p>
        {/* TK-249: the account first, then "Plan y facturación" in the panel (pago con tarjeta por Paddle). */}
        <Promo />
        {referred && <p className="note">Viene recomendado por un cliente de Abastalia: su prueba gratis dura 30 días en lugar de 14. Cree su cuenta y elija su plan.</p>}
        <p className="actions">
          <Link className="btn" to="/app/billing">
            Crear su cuenta y elegir plan
          </Link>
        </p>
      </Section>
      <Section>
        <h2>Adapte su plan</h2>
        <div className="table-wrap table-wrap--fit">
          <table>
            <thead><tr><th>Adicional</th><th>Incluye</th><th>Precio al mes</th></tr></thead>
            <tbody>
              <tr><td>Caja adicional</td><td>Una caja más, cuantas necesite, con 2 cajeros más.</td><td><Amount value={price("ExtraRegister")} /></td></tr>
              <tr><td>Cajero adicional</td><td>Un cajero más, con su propio usuario y PIN.</td><td><Amount value={price("ExtraCashier")} /></td></tr>
              <tr><td>Sucursal adicional</td><td>Solo con el plan Pro. Otra tienda con su licencia, 3 cajas y 4 cajeros más.</td><td><Amount value={price("ExtraStore")} /></td></tr>
              <tr><td>Lectura de facturas</td><td>300 facturas de suplidor más al mes, leídas con IA.</td><td><Amount value={price("SupplierInvoiceReading")} /></td></tr>
              <tr><td>Asistente +1,000 preguntas</td><td>1,000 preguntas más al mes al asistente del panel.</td><td><Amount value={price("AssistantPack")} /></td></tr>
              <tr><td>Usuarios del panel +5</td><td>Cinco invitaciones más al panel cada mes.</td><td><Amount value={price("ExtraUsers")} /></td></tr>
            </tbody>
          </table>
        </div>
        <p className="muted">Cajeros: cada persona que vende con su usuario y PIN; el dueño no cuenta. Si alguien deja de trabajar, se desactiva y su lugar queda libre.</p>
        <p className="muted">Pago anual: 10 meses por 12. Paddle cobra por Abastalia y calcula los impuestos.</p>
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
              <tr><td>Instalación</td><td>Instalación de Abastalia en la tienda y puesta en marcha.</td><td><Price value={prices.installation} /></td></tr>
              <tr>
                <td>Acompañamiento en la certificación e-CF</td>
                <td>Un técnico lo guía en cada etapa ante la DGII. <Link to="/certificacion">Ver detalles</Link></td>
                <td><Price value={prices.certificationSupport} /></td>
              </tr>
              <tr><td>Equipos (PC, balanza, impresoras, escáner, gaveta)</td><td>Kit según su negocio.</td><td>Se cotiza</td></tr>
            </tbody>
          </table>
        </div>
        <p className="muted">
          El certificado digital para el e-CF se compra directamente a la entidad certificadora; no lo vende Abastalia. ¿Y el Facturador Gratuito de la DGII? <Link to="/comparar">Compare</Link>
        </p>
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
      <Hero eyebrow="Novedades" title="Lo nuevo en Abastalia" sub="Lo más reciente va primero." />
      <Section>
        <h2>
          <time dateTime="2026-10">Octubre de 2026</time>
        </h2>
        <ul className="log">
          <News title="Guía, glosario y comparación del e-CF">
            <Link to="/guia-e-cf">Los 6 pasos</Link>, <Link to="/glosario">las palabras</Link> y <Link to="/comparar">el Facturador Gratuito al lado de Abastalia</Link>.
          </News>
          <News title="Facturación electrónica directa con la DGII">Firma en la tienda y envía sin intermediarios. Un rechazo se reemplaza con un e-CF nuevo; los números sobrantes se anulan desde la pantalla. Cada e-CF se guarda 10 años en la nube.</News>
          <News title="Asistente de certificación y cambio desde otro programa">Las etapas en una lista, el set de pruebas que se envía solo y un camino para quien ya emite e-CF.</News>
          <News title="Contingencia sin internet o sin certificado">Sin internet, los e-CF salen solos dentro de las 72 horas. Si no se puede firmar, Abastalia guía la contingencia con serie B y su regularización.</News>
          <News title="Comprobantes de sus suplidores">La nube recibe y acusa los e-CF de sus suplidores las 24 horas y los muestra en Compras. Su crédito fiscal llega solo al comprador emisor electrónico.</News>
          <News title="Encargos y cotizaciones">Encargos con fecha, hora y abono, cobrados con el peso real. Cotizaciones con vigencia que se convierten en venta.</News>
          <News title="Catálogo con QR y pedido por WhatsApp">Sus productos y precios en una página pública, sin costos ni existencias, con un QR para el mostrador.</News>
          <News title="Pedido sugerido">Mínimos y máximos; Abastalia calcula cuánto pedir, a qué suplidor y crea la compra en borrador.</News>
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
  title: "Página no encontrada · Abastalia",
  description: "La página que busca no existe. Vuelva al inicio de Abastalia, el punto de venta con e-CF para carnicerías, colmados y delis en República Dominicana.",
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
    title: "Abastalia: punto de venta con e-CF para su negocio",
    description: "Abastalia cobra rápido, vende por libra o por unidad, cuadra la caja y emite el e-CF con la DGII aunque se vaya el internet. Hecho en República Dominicana.",
    element: <Home />,
  },
  {
    path: "/carniceria",
    nav: "Carnicería",
    title: "Abastalia para carnicerías · Por libra y e-CF sin internet",
    description: "Punto de venta para carnicerías: venta por libra con etiquetas de balanza, encargos con abono, cuadre de caja y e-CF aunque se vaya el internet.",
    element: <Butcher />,
  },
  {
    path: "/colmado",
    nav: "Colmado",
    title: "Abastalia para colmados · Fiado, venta rápida y e-CF",
    description: "Punto de venta para colmados: venta rápida con escáner, fiado con límite y estado de cuenta, catálogo con pedido por WhatsApp y e-CF aunque se vaya el internet.",
    element: <Grocery />,
  },
  {
    path: "/deli",
    nav: "Deli",
    title: "Abastalia para delis · Por libra, encargos y cotizaciones",
    description: "Punto de venta para delis: quesos y embutidos por libra, bandejas por encargo con abono, cotizaciones que se convierten en venta y e-CF sin internet.",
    element: <Deli />,
  },
  {
    path: "/e-cf",
    nav: "e-CF",
    title: "¿Ya le toca el e-CF? · Plazo del 15 de noviembre de 2026",
    description: "Guía corta del e-CF en República Dominicana: a quién le toca el 15 de noviembre de 2026, qué necesita y cómo Abastalia factura aunque se vaya el internet.",
    element: <Ecf />,
  },
  {
    path: "/guia-e-cf",
    nav: "Guía e-CF",
    title: "Cómo empezar a emitir e-CF · Guía en 6 pasos",
    description: "Guía para empezar con la facturación electrónica en República Dominicana: fecha, RNC y OFV, certificado digital, certificación y secuencias e-NCF.",
    element: <EcfGuide />,
  },
  {
    path: "/comparar",
    nav: "Comparar",
    title: "Facturador Gratuito de la DGII o punto de venta · Comparar",
    description: "Facturador Gratuito de la DGII al lado de Abastalia: costo, volumen, internet, caja y balanza, cuánto guarda los e-CF y qué preguntar antes de elegir.",
    element: <Compare />,
  },
  {
    path: "/glosario",
    nav: "Glosario",
    title: "Glosario del e-CF: e-NCF, NCF, RFCE, 606, 607 y 608",
    description: "Qué significan e-CF, e-NCF, NCF, RFCE, 606, 607, 608, TesteCF, CerteCF, OFV y el certificado .p12, en una línea cada uno. Para dueños de tienda en RD.",
    element: <Glossary />,
  },
  {
    path: "/certificacion",
    nav: "Certifíquese",
    title: "Certifíquese con Abastalia · e-CF ante la DGII",
    description: "Qué necesita para certificarse como emisor electrónico, las etapas ante la DGII, quién hace qué y qué pasa si ya emite e-CF con otro programa.",
    element: <Certification />,
  },
  {
    path: "/descargar",
    nav: "Descargar",
    title: "Descargar Abastalia · Instalador para Windows",
    description: "Descargue el instalador de Abastalia para Windows 10 y 11: PC principal con servidor y base de datos, o caja. Requisitos y pasos para instalar.",
    element: <Download />,
  },
  {
    path: "/precios",
    nav: "Precios",
    title: "Precios de Abastalia · Planes por tamaño de tienda",
    description: "Planes de Abastalia según sus cajas y sucursales. Lo básico está en todos: balanza, e-CF sin cupo, inventario, cuadre ciego, fiado, 606/607/608 y respaldo.",
    element: <Pricing />,
  },
  {
    path: "/novedades",
    nav: "Novedades",
    title: "Novedades de Abastalia",
    description: "Lo nuevo en Abastalia: facturación electrónica directa con la DGII, encargos, cotizaciones, catálogo por WhatsApp, permisos por acción y más.",
    element: <WhatsNew />,
  },
  ...legalPages,
];
