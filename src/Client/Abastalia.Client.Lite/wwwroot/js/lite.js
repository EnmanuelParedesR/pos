// TK-324: the bridge between the Blazor app (LiteDevice.cs) and lite-signer (lite-signer.js, the bundle of src/Client/lite-signer).
// Keys never cross it: the device key and the .p12's key stay CryptoKey objects in IndexedDB, and the .p12 and its password are read here,
// from their inputs, so neither reaches .NET. Bodies cross as Base64.
import {
  pairDevice, deviceFetch, forgetDevice, indexedDbStore, importP12, loadSigner, deviceSigner, dgiiClient, processDocuments, ECF_TYPES,
  DeviceMissing, KeyMissing, PairingFailed,
} from './lite-signer.js';

const store = indexedDbStore();
let config, send;
const dgii = new Map();

/** @param {{ api: string, sendToDgii: boolean }} c */
export function init(c) {
  config = c;
  send = deviceFetch({ baseUrl: c.api, store });
}

export const deviceId = async () => (await store.getDevice())?.deviceId ?? null;

export async function pair(code, name) {
  try {
    return { deviceId: (await pairDevice({ baseUrl: config.api, code, name, store })).deviceId };
  } catch (e) {
    return { error: e instanceof PairingFailed ? e.message : 'No se pudo conectar con Abastalia. Revise el internet e intente de nuevo.' };
  }
}

export const forget = () => forgetDevice(store);

/** status 0 = no answer (no internet, or the host refused the origin). */
export async function fetchSigned(method, path, headers, body) {
  try {
    const r = await send(path, { method, headers, body: body == null ? undefined : fromBase64(body) });
    return {
      status: r.status, body: toBase64(new Uint8Array(await r.arrayBuffer())),
      contentType: r.headers.get('Content-Type'), disposition: r.headers.get('Content-Disposition'),
    };
  } catch (e) {
    if (!(e instanceof DeviceMissing)) return { status: 0 };
    const problem = JSON.stringify({ status: 401, code: 'Lite.UnknownDevice', detail: e.message });
    return { status: 401, body: toBase64(new TextEncoder().encode(problem)), contentType: 'application/problem+json' };
  }
}

export const hasCertificate = async () => (await loadSigner(store)) !== null;

/** Reads the .p12 and its password from their inputs and empties both: the password is never kept. */
export async function importCertificate(fileInput, passwordInput) {
  const file = fileInput.files?.[0];
  const password = passwordInput.value;
  passwordInput.value = '';
  if (!file) return { error: 'Elija el archivo .p12 de su certificado digital.' };
  try {
    const { persisted } = await importP12(file, password, store);
    fileInput.value = '';
    return { persisted };
  } catch (e) {
    // forge says "PKCS#12 MAC could not be verified. Invalid password?"; the signer's own messages are already in Spanish.
    return { error: /mac|password/i.test(e.message) ? 'La contraseña no es la de este certificado. Escríbala de nuevo.' : e.message };
  }
}

let running;

/** One pass over the e-CF waiting for this device (lite-signer fiscal.js); a pass already running is shared, never doubled. */
export function processFiscal(token) {
  return running ??= run(token).finally(() => { running = undefined; });
}

async function run(token) {
  try {
    const answer = await send('/api/settings/business', { headers: { Authorization: `Bearer ${token}` } });
    if (!answer.ok) return { error: `El servidor respondió ${answer.status}.`, items: [] };
    const { rnc } = await answer.json();
    if (!rnc) return { items: [] }; // no RNC, no e-CF: the host builds none until the owner sets it up
    const signer = deviceSigner({ rnc, types: ECF_TYPES, store });
    const dgiiFor = environment => {
      const key = `${environment}.${rnc}`;
      if (!dgii.has(key)) dgii.set(key, dgiiClient({ environment, signer })); // keeps its DGII token in memory between passes
      return dgii.get(key);
    };
    return { items: await processDocuments({ send, token, signer, dgiiFor, rnc, sendToDgii: config.sendToDgii }) };
  } catch (e) {
    return { error: e instanceof KeyMissing ? 'KeyMissing' : String(e?.message ?? e), items: [] };
  }
}

function toBase64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

const fromBase64 = text => Uint8Array.from(atob(text), c => c.charCodeAt(0));
