// What the Lite signer agrees to sign: the rule of ADR-0011 on the device side (ADR-0013, decision 2). Only the business's own documents:
// an e-CF, RFCE or ANECF it issues (its RNC, an allowed type), an ARECF or ACECF it answers as buyer, and the DGII seed. Anything else throws.
// Reads direct children only (never getElementsByTagName): a nested or repeated element cannot pass for the real one.

export class SignRefused extends Error {}

/** @param {Document} doc  @param {{ rnc: string, types: string[] }} policy
 *  @returns {{ document: string, type: string | null, encf: string | null }} what is signed, for the signing log. */
export function checkAllowed(doc, { rnc, types }) {
  const root = doc.documentElement;
  if (root.namespaceURI) throw new SignRefused(`No se firma un ${root.localName} con espacio de nombres.`);
  if (child(root, 'Signature', false)) throw new SignRefused('El documento ya está firmado.');
  const refuse = why => { throw new SignRefused(why); };
  const own = (value, what) => value === rnc || refuse(`${what} no es el de este negocio.`);
  const allowedType = type => types.includes(type) || refuse(`Este negocio no emite el tipo ${type}.`);

  switch (root.tagName) {
    case 'ECF':
    case 'RFCE': {
      const header = child(root, 'Encabezado');
      const type = text(child(header, 'IdDoc'), 'TipoeCF');
      const encf = text(child(header, 'IdDoc'), 'eNCF');
      own(text(child(header, 'Emisor'), 'RNCEmisor'), 'El RNC del emisor');
      allowedType(type);
      if (root.tagName === 'RFCE' && type !== '32') refuse('El RFCE es solo de la factura de consumo (32).');
      if (!new RegExp(`^E${type}\\d{10}$`).test(encf)) refuse(`El e-NCF ${encf} no es de tipo ${type}.`);
      return { document: root.tagName, type, encf };
    }
    case 'ANECF': {
      own(text(child(root, 'Encabezado'), 'RncEmisor'), 'El RNC del emisor');
      const lines = children(child(root, 'DetalleAnulacion'), 'Anulacion');
      if (!lines.length) refuse('La anulación no tiene líneas.');
      for (const line of lines) allowedType(text(line, 'TipoeCF'));
      return { document: 'ANECF', type: null, encf: null };
    }
    case 'ARECF': {
      const detail = child(root, 'DetalleAcusedeRecibo');
      // As the store (RemoteSigning): a refusal (Estado 1) may name another buyer (motivo 4: not for this RNC); a receipt is only for this business.
      if (text(detail, 'Estado') !== '1') own(text(detail, 'RNCComprador'), 'El RNC del comprador');
      return { document: 'ARECF', type: null, encf: text(detail, 'eNCF') };
    }
    case 'ACECF': {
      const detail = child(root, 'DetalleAprobacionComercial');
      own(text(detail, 'RNCComprador'), 'El RNC del comprador');
      return { document: 'ACECF', type: null, encf: text(detail, 'eNCF') };
    }
    case 'SemillaModel': {
      // The DGII's seed (docs/ecf-propio.md §3): exactly valor and fecha. It carries no RNC; the token it buys is the certificate holder's.
      const names = elements(root).map(e => e.tagName).sort().join();
      if (names !== 'fecha,valor') refuse('La semilla no tiene la forma de la DGII.');
      return { document: 'SemillaModel', type: null, encf: null };
    }
    default:
      return refuse(`El firmador no firma un ${root.tagName}.`);
  }
}

const elements = el => [...el.childNodes].filter(n => n.nodeType === 1);
const children = (el, name) => elements(el).filter(e => e.tagName === name);

/** The one direct child named so; missing or repeated refuses (or returns null when not required). */
function child(el, name, required = true) {
  const found = children(el, name);
  if (found.length === 1) return found[0];
  if (!found.length && !required) return null;
  throw new SignRefused(found.length ? `${name} está repetido.` : `Falta ${name}.`);
}

const text = (el, name) => child(el, name).textContent.trim();
