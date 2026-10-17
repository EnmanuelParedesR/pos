// TK-322 (Lite F1-4): the browser signer the Lite PWA (TK-324) calls. ADR-0013 decision 2: the owner's .p12 is read on the device, its key kept
// as a non-extractable CryptoKey in IndexedDB, and only the business's own documents are signed (allowlist.js), each one written to a local log.
import { openP12, parseXml, signXml } from './xmldsig.js';
import { checkAllowed } from './allowlist.js';

export { SignRefused } from './allowlist.js';
export { indexedDbStore, memoryStore } from './store.js';
export { dgiiClient, DgiiError } from './dgii.js';
export { pairDevice, signRequest, deviceFetch, forgetDevice, clockOffset, DeviceMissing, PairingFailed } from './device.js';
export { processDocuments, ECF_TYPES } from './fiscal.js';

/** The key is not on this device: never imported, removed, or the browser evicted it (Safari after 7 days unused, private mode). */
export class KeyMissing extends Error {
  constructor() { super('El certificado de firma no está en este dispositivo (el navegador pudo borrarlo). Vuelva a importar el archivo .p12.'); }
}

/** Reads the .p12 (a File, Blob or bytes), keeps its key as a non-extractable CryptoKey in the store and asks the browser not to evict it.
 *  Drop the file and the password afterwards. @returns {Promise<{ certificate: string, persisted: boolean }>} certificate = X.509 DER, Base64. */
export async function importP12(file, password, store, subtle = globalThis.crypto.subtle) {
  const bytes = new Uint8Array(typeof file.arrayBuffer === 'function' ? await file.arrayBuffer() : file);
  const { key, certificate } = await openP12(bytes, password, subtle);
  await store.putSigner({ key, certificate, importedAt: new Date().toISOString() });
  // false means the browser may still evict it: the PWA should ask to install it (ADR-0013, risk 1).
  const persisted = (await globalThis.navigator?.storage?.persist?.()) ?? false;
  return { certificate, persisted };
}

/** @returns {Promise<{ key: CryptoKey, certificate: string, importedAt: string } | null>} null = re-import the .p12 (see KeyMissing). */
export async function loadSigner(store) {
  const signer = await store.getSigner();
  return signer?.key?.type === 'private' && signer.key.usages.includes('sign') && signer.certificate ? signer : null;
}

export const removeSigner = store => store.deleteSigner();

/** The device signer of one business. rnc = its RNC; types = the e-CF types it issues ('31', '32', …).
 *  sign(xml) throws KeyMissing, SignRefused (not one of the business's own documents) or Error (malformed XML).
 *  log() = what was signed: { document, type, encf, digest, signedAt }, never the XML. */
export function deviceSigner({ rnc, types, store, DOMParser = globalThis.DOMParser, subtle = globalThis.crypto.subtle }) {
  if (!/^(\d{9}|\d{11})$/.test(rnc ?? '')) throw new Error('El RNC del negocio debe tener 9 u 11 dígitos.');
  if (!types?.length) throw new Error('Falta la lista de tipos de e-CF que emite el negocio.');
  const policy = { rnc, types: types.map(String) };
  return {
    async sign(xml) {
      const signer = await loadSigner(store);
      if (!signer) throw new KeyMissing();
      const doc = parseXml(xml, DOMParser);
      const what = checkAllowed(doc, policy);
      const { signedXml, securityCode, digest } = await signXml(doc, signer, subtle);
      // shortcut: the log grows ~100 bytes per signature with no trimming; prune old entries once TK-324 shows it to the owner.
      await store.addLog({ ...what, digest, signedAt: new Date().toISOString() });
      return { signedXml, securityCode, digest, ...what };
    },
    log: () => store.log(),
  };
}
