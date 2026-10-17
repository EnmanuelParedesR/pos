import type { ReactNode } from "react";
import { Link } from "react-router";
import { site } from "./config";
import { Contact, Hero, Icon, Section, Steps, Tbd } from "./SiteParts";

/** [row header, first column, second column]; a null second column means one cell for both. */
type Req = [string, ReactNode, ReactNode | null];

// TK-314: every line checked against the product: deploy/installer/abastalia.iss (MinVersion 10.0.17763, x64, WebView2, SQL Express 2025),
// Microsoft's SQL Server 2025 requirements (x64, 1.4 GHz min / 2.0 GHz rec., 6 GB free to install, Windows 10/11 Home, Pro, Enterprise, IoT),
// docs/contingencia-ecf.md (2 minutes of clock), docs/instalador-web.md (disk encryption warning) and the 1366×768 design target of the register.
const windows: Req = [
  "Windows",
  <>Windows 10 de 64 bits, versión 1809 o más nueva: Home, Pro, Enterprise o IoT Enterprise. No sirve en modo S.</>,
  <>Windows 11 Pro al día. Windows 10 ya no recibe actualizaciones de seguridad de Microsoft.</>,
];
const screenReq: Req = ["Pantalla", "1366 × 768", "1920 × 1080"];

const main: Req[] = [
  windows,
  ["Procesador", "Intel o AMD de 64 bits, 2 núcleos a 1.4 GHz", "4 núcleos a 2.0 GHz o más (SQL Server Express usa hasta 4)"],
  ["Memoria", "8 GB", "16 GB"],
  ["Disco", "10 GB libres en C: (SQL Server Express pide 6 GB para instalarse)", "SSD con 50 GB libres: la base de datos y los respaldos crecen con los años"],
  screenReq,
  ["Fecha y hora", <>Automáticas en Windows. Con más de 2 minutos de diferencia, los e-CF esperan a que la hora se corrija.</>, null],
  ["Cifrado del disco", "No es obligatorio: el instalador avisa si falta", "BitLocker o Cifrado de dispositivo activo: protege el certificado y los datos si roban la PC"],
];

const register: Req[] = [
  windows,
  ["Procesador", "Intel o AMD de 64 bits, 2 núcleos", "4 núcleos"],
  ["Memoria", "4 GB", "8 GB"],
  ["Disco", "2 GB libres", "SSD"],
  screenReq,
];

const reqHead: Head = ["Componente", "Mínimo", "Recomendado"];

const devices: Req[] = [
  ["Impresora de recibos", "Térmica ESC/POS de 58 mm o 80 mm. Recomendada: 80 mm con cortador.", "Instalada en Windows (USB, red o paralelo) o en un puerto COM"],
  ["Gaveta de dinero", "La que se abre con la impresora", "Al puerto de gaveta de la impresora"],
  ["Balanza", "CAS PD-II y ER Plus / ER Jr (modo ECR), Mettler Toledo 8217 y las que la emulan, e indicadores que envían el peso sin parar (formato A&D o CAS)", "Puerto COM (RS-232), o USB con puerto COM virtual"],
  ["Etiquetas de la balanza", "EAN-13 con prefijo 20 a 29, con peso o con precio", "Se leen con el lector, como cualquier código"],
  ["Lector de códigos", "Cualquiera en modo teclado que termine con Enter", "USB"],
];

type Head = [string, string, string];

/** Explicit roles: on a phone the rows are blocks (site.css .reqs), and some screen readers drop a table's semantics then. */
function ReqTable({ caption, head, rows }: { caption?: string; head: Head; rows: Req[] }) {
  return (
    <div className="table-wrap reqs">
      <table role="table">
        {caption && <caption>{caption}</caption>}
        <thead role="rowgroup">
          <tr role="row">{head.map((h) => <th key={h} scope="col" role="columnheader">{h}</th>)}</tr>
        </thead>
        <tbody role="rowgroup">
          {rows.map(([what, a, b]) => (
            <tr key={what} role="row">
              <th scope="row" role="rowheader">{what}</th>
              {b === null ? (
                <td colSpan={2} role="cell" data-label={`${head[1]} y ${head[2].toLowerCase()}`}>{a}</td>
              ) : (
                <>
                  <td role="cell" data-label={head[1]}>{a}</td>
                  <td role="cell" data-label={head[2]}>{b}</td>
                </>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function Download() {
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
      <Section id="requisitos">
        <h2>Requisitos</h2>
        <p className="muted">
          El instalador baja de Microsoft lo que falte: SQL Server Express 2025 en la PC principal (unos 750 MB) y WebView2 en todas. No hace falta instalar .NET.
        </p>
        <ReqTable caption="PC principal" head={reqHead} rows={main} />
        <ReqTable caption="Cada caja" head={reqHead} rows={register} />
      </Section>
      <Section className="counter">
        <h2>Red e internet</h2>
        <div className="tiles tiles--2">
          <div className="tile">
            <Icon name="stores" />
            <h3>Red de la tienda</h3>
            <p>Un router o switch con todas las PC en la misma red. Las cajas venden a través de la PC principal, así que debe estar encendida mientras la tienda abre.</p>
            <p>En la PC principal, la red debe estar como <strong>Privada</strong> en Windows (Configuración → Red e Internet); si está como Pública, las cajas no la encuentran.</p>
            <p><strong>Recomendado:</strong> cable de red e IP fija para la PC principal (las cajas la buscan por su nombre o su IP).</p>
          </div>
          <div className="tile">
            <Icon name="download" />
            <h3>Internet</h3>
            <p><strong>Para instalar, sí. Para vender, no:</strong> si se cae, siga vendiendo; cada e-CF se firma en la PC principal y sale solo al volver la conexión.</p>
            <p><strong>A diario</strong> lo usa para enviar los e-CF a la DGII (tiene 72 horas desde cada venta), la copia del respaldo en la nube, las actualizaciones, el panel desde su celular y Talia.</p>
            <p>La licencia se renueva sola con la conexión; si la tienda pasa días sin internet, tiene 15 días de gracia.</p>
          </div>
        </div>
      </Section>
      <Section id="equipos">
        <h2>Equipos compatibles</h2>
        <ReqTable head={["Equipo", "Qué funciona", "Cómo se conecta"]} rows={devices} />
        <p className="muted">¿Su balanza no está en la lista o no sabe el modelo? <Contact className="">Pregúntenos</Contact> antes de comprar: lo confirmamos con usted al instalar.</p>
      </Section>
      <Section className="counter" id="certificado">
        <h2>Certificado digital</h2>
        <p>
          Solo para facturar con e-CF: un certificado de persona física para procedimientos tributarios, a nombre del representante del negocio, en archivo <strong>.p12</strong> o <strong>.pfx</strong> (no en token USB). Se compra a la entidad certificadora y se importa en Configuración → Comprobantes.
        </p>
        <p>
          <Link to="/certificacion">Cómo certificarse con Abastalia</Link>
        </p>
      </Section>
      <Section>
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
