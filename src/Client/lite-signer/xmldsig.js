// The XMLDSig of the Lite signer (TK-317's PoC, kept by TK-322): sign a DGII e-CF in the browser with the owner's .p12, which never leaves the device.
// Low level: signs whatever it is given. The app calls index.js, which checks the allowlist and logs every signature.
// Same signature as the store's CertificateEcfSigner (docs/ecf-propio.md §4.1): enveloped XMLDSig, C14N 1.0, RSA-SHA256, SHA-256,
// one Reference URI="" and the certificate in KeyInfo/X509Data. The output is byte for byte what the store produces for the same input.
// Runs unchanged in a browser (globalThis.crypto, DOMParser) and in Node (pass @xmldom/xmldom's DOMParser).
// Only forge's PKCS#12 and X.509 parts: ~206 KB minified, ~55 KB gzip (the whole library ~284 / ~79 KB).
import forge from 'node-forge/lib/forge.js';
import 'node-forge/lib/pkcs12.js';
import 'node-forge/lib/pki.js';

const DSIG = 'http://www.w3.org/2000/09/xmldsig#';
const XMLNS = 'http://www.w3.org/2000/xmlns/';
const C14N = 'http://www.w3.org/TR/2001/REC-xml-c14n-20010315';
const RSA_SHA256 = 'http://www.w3.org/2001/04/xmldsig-more#rsa-sha256';
const SHA256 = 'http://www.w3.org/2001/04/xmlenc#sha256';
const ENVELOPED = 'http://www.w3.org/2000/09/xmldsig#enveloped-signature';
const RSA = { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' };

/** Opens the .p12 (PKCS#12: WebCrypto cannot) and imports its RSA key as a NON-extractable CryptoKey that can only sign.
 *  The decrypted PKCS#8 bytes live in JS memory only during this call; keep the returned CryptoKey (IndexedDB can store it) and drop the file and password.
 *  @returns {{ key: CryptoKey, certificate: string }} certificate = the signer's X.509 DER in Base64, as KeyInfo carries it. */
export async function openP12(p12Bytes, password, subtle = globalThis.crypto.subtle) {
  const p12 = forge.pkcs12.pkcs12FromAsn1(forge.asn1.fromDer(forge.util.binary.raw.encode(p12Bytes)), true, password);
  const keys = [forge.pki.oids.pkcs8ShroudedKeyBag, forge.pki.oids.keyBag].flatMap(t => p12.getBags({ bagType: t })[t] ?? []);
  const certs = p12.getBags({ bagType: forge.pki.oids.certBag })[forge.pki.oids.certBag] ?? [];
  if (keys.length !== 1 || !keys[0].key) throw new Error('El archivo .p12 debe tener exactamente una llave RSA.');
  const rsa = keys[0].key;
  // A .p12 may carry the chain too: the signer's certificate is the one with the key's public modulus.
  const cert = certs.find(b => b.cert?.publicKey?.n?.equals(rsa.n))?.cert;
  if (!cert) throw new Error('El archivo .p12 no tiene el certificado de su llave.');

  const pkcs8 = forge.asn1.toDer(forge.pki.wrapRsaPrivateKey(forge.pki.privateKeyToAsn1(rsa))).getBytes();
  const key = await subtle.importKey('pkcs8', forge.util.binary.raw.decode(pkcs8), RSA, false, ['sign']);
  return { key, certificate: forge.util.encode64(forge.asn1.toDer(forge.pki.certificateToAsn1(cert)).getBytes()) };
}

/** Parses XML text; throws on malformed XML. */
export function parseXml(xml, DOMParser = globalThis.DOMParser) {
  const doc = new DOMParser().parseFromString(xml, 'text/xml');
  if (!doc.documentElement || doc.getElementsByTagName('parsererror').length) throw new Error('XML mal formado.');
  return doc;
}

/** Signs an unsigned e-CF, RFCE, ANECF, ACECF, ARECF or SemillaModel (a parsed document, changed in place) as the store does.
 *  @returns {{ signedXml: string, securityCode: string, digest: string }} digest = the DigestValue (SHA-256 of the canonical unsigned document, Base64). */
export async function signXml(doc, { key, certificate }, subtle = globalThis.crypto.subtle) {
  const root = doc.documentElement;
  dropWhitespace(root); // the store signs with PreserveWhitespace = false

  const digest = base64(new Uint8Array(await subtle.digest('SHA-256', utf8(canonical(root))))); // enveloped transform: the signature is not there yet
  const el = (parent, name, text, attrs = {}) => {
    const e = doc.createElementNS(DSIG, name);
    for (const [k, v] of Object.entries(attrs)) e.setAttribute(k, v);
    if (text !== undefined) e.appendChild(doc.createTextNode(text));
    parent?.appendChild(e);
    return e;
  };
  const signature = el(null, 'Signature');
  signature.setAttributeNS(XMLNS, 'xmlns', DSIG);
  const info = el(signature, 'SignedInfo');
  el(info, 'CanonicalizationMethod', undefined, { Algorithm: C14N });
  el(info, 'SignatureMethod', undefined, { Algorithm: RSA_SHA256 });
  const ref = el(info, 'Reference', undefined, { URI: '' });
  el(el(ref, 'Transforms'), 'Transform', undefined, { Algorithm: ENVELOPED });
  el(ref, 'DigestMethod', undefined, { Algorithm: SHA256 });
  el(ref, 'DigestValue', digest);
  root.appendChild(signature); // before canonicalizing SignedInfo: it inherits the root's namespaces (a SemillaModel may declare xsi/xsd)

  const value = base64(new Uint8Array(await subtle.sign(RSA, key, utf8(canonical(info)))));
  el(signature, 'SignatureValue', value);
  el(el(el(signature, 'KeyInfo'), 'X509Data'), 'X509Certificate', certificate);

  return { signedXml: '<?xml version="1.0" encoding="utf-8"?>' + outerXml(root), securityCode: value.slice(0, 6), digest };
}

/** Canonical XML 1.0 without comments (inclusive) of an element and its descendants, with the namespaces in scope from its ancestors.
 *  shortcut: xml:* attributes inherited from ancestors are not copied to a subset apex; DGII documents have none. */
export function canonical(element) {
  const inherited = {};
  const ancestors = [];
  for (let p = element.parentNode; p?.nodeType === 1; p = p.parentNode) ancestors.unshift(p);
  for (const a of ancestors) Object.assign(inherited, declarations(a));
  return c14n(element, {}, inherited);
}

function c14n(el, rendered, inherited) {
  const scope = { ...inherited, ...declarations(el) };
  const ns = Object.keys(scope).sort()
    .filter(p => rendered[p] !== scope[p] && !(p === '' && scope[p] === '' && !rendered['']))
    .map(p => ` ${p ? 'xmlns:' + p : 'xmlns'}="${attr(scope[p])}"`);
  const attrs = [...el.attributes].filter(a => a.namespaceURI !== XMLNS)
    .sort((a, b) => cmp(a.namespaceURI ?? '', b.namespaceURI ?? '') || cmp(a.localName, b.localName))
    .map(a => ` ${a.name}="${attr(a.value)}"`);
  let out = `<${el.tagName}${ns.join('')}${attrs.join('')}>`;
  for (const n of el.childNodes) {
    if (n.nodeType === 1) out += c14n(n, scope, scope);
    else if (n.nodeType === 3 || n.nodeType === 4) out += text(n.data).replace(/\r/g, '&#xD;');
    else if (n.nodeType === 7) out += `<?${n.target}${n.data ? ' ' + n.data : ''}?>`;
  }
  return out + `</${el.tagName}>`;
}

const declarations = el => Object.fromEntries([...el.attributes].filter(a => a.namespaceURI === XMLNS).map(a => [a.prefix ? a.localName : '', a.value]));
const cmp = (a, b) => (a < b ? -1 : a > b ? 1 : 0);
const text = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const attr = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;').replace(/\t/g, '&#x9;').replace(/\n/g, '&#xA;').replace(/\r/g, '&#xD;');

/** The serialization of .NET's XmlDocument.OuterXml (attributes in document order, childless elements as <x />), so the bytes match the store's. */
function outerXml(el) {
  const attrs = [...el.attributes].map(a => ` ${a.name}="${a.value.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')}"`).join('');
  if (!el.childNodes.length) return `<${el.tagName}${attrs} />`;
  let out = `<${el.tagName}${attrs}>`;
  for (const n of el.childNodes) out += n.nodeType === 1 ? outerXml(n) : n.nodeType === 3 || n.nodeType === 4 ? text(n.data) : '';
  return out + `</${el.tagName}>`;
}

function dropWhitespace(el) {
  for (const n of [...el.childNodes]) {
    if (n.nodeType === 3 && !n.data.trim()) el.removeChild(n);
    else if (n.nodeType === 8) el.removeChild(n);
    else if (n.nodeType === 1) dropWhitespace(n);
  }
}

const utf8 = s => new TextEncoder().encode(s);
function base64(bytes) {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s);
}
