// TK-321 (Lite F1-3): the selling device's own key and its signed requests to Abastalia.Host.Lite. The browser makes a non-extractable
// ECDSA P-256 key, sends its public half with the code the owner got in the panel, and signs every request after that in ADR-0008's node
// format: X-Device-Signature = deviceId.unixSeconds.nonce.signature over `deviceId.unixSeconds.nonce.METHOD /path?query bodyHash`,
// bodyHash = Base64Url(SHA-256(body)) also sent in X-Body-Sha256. The host accepts each nonce once, within 15 minutes of its clock.

const ECDSA = { name: 'ECDSA', namedCurve: 'P-256', hash: 'SHA-256' };

/** The device was never paired here, the browser lost its key, or the owner revoked it (the host answers 401): pair again with a new code. */
export class DeviceMissing extends Error {
  constructor() { super('Este dispositivo no está unido a su negocio. Pida un código en el panel, en Tiendas, y únalo de nuevo.'); }
}

/** The host refused the code or the key; message = what to tell the owner (the host's own text), code = 'Lite.InvalidCode', 'Lite.DeviceLimit'… */
export class PairingFailed extends Error {
  constructor(message, code, status) { super(message); this.code = code; this.status = status; }
}

/** Pairs this browser with the code from the panel. A device already kept in the store is replaced only once the host accepted the new one.
 *  @returns {Promise<{ deviceId: string }>} */
export async function pairDevice({ baseUrl, code, name, store, fetch = globalThis.fetch, subtle = globalThis.crypto.subtle }) {
  const { privateKey, publicKey } = await subtle.generateKey(ECDSA, false, ['sign', 'verify']); // the public half is always exportable
  const spki = new Uint8Array(await subtle.exportKey('spki', publicKey));
  const response = await fetch(`${baseUrl}/lite/devices/pair`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ code: String(code ?? '').replace(/\s/g, ''), publicKey: base64(spki), name }),
  });
  const answer = await response.json().catch(() => ({}));
  if (!response.ok) throw new PairingFailed(answer.detail ?? 'No se pudo unir el dispositivo. Intente de nuevo.', answer.code ?? answer.title, response.status);
  await store.putDevice({ deviceId: answer.deviceId, key: privateKey, pairedAt: new Date().toISOString() });
  return { deviceId: answer.deviceId };
}

/** The two headers for one request. body = the exact bytes sent (string or Uint8Array; none for a GET). now = ms, for a clock the caller corrected. */
export async function signRequest({ deviceId, key }, method, pathAndQuery, body, { now = Date.now(), subtle = globalThis.crypto.subtle } = {}) {
  const bytes = body == null ? new Uint8Array() : typeof body === 'string' ? new TextEncoder().encode(body) : body;
  const bodyHash = base64Url(new Uint8Array(await subtle.digest('SHA-256', bytes)));
  const seconds = Math.floor(now / 1000);
  const nonce = base64Url(globalThis.crypto.getRandomValues(new Uint8Array(16)));
  const payload = `${deviceId}.${seconds}.${nonce}.${method.toUpperCase()} ${pathAndQuery} ${bodyHash}`;
  const signature = base64Url(new Uint8Array(await subtle.sign(ECDSA, key, new TextEncoder().encode(payload)))); // IEEE P1363 (r‖s), as .NET verifies
  return { 'X-Device-Signature': `${deviceId}.${seconds}.${nonce}.${signature}`, 'X-Body-Sha256': bodyHash };
}

/** fetch for the host with every request signed by the paired device. path = '/api/…?…' exactly as sent; init.body a string or bytes.
 *  Throws DeviceMissing without a device; a 401 from the host is returned as is (the session may have expired: sign in again first). */
export function deviceFetch({ baseUrl, store, fetch = globalThis.fetch, subtle = globalThis.crypto.subtle, now = Date.now }) {
  return async (path, init = {}) => {
    const device = await store.getDevice();
    if (device?.key?.type !== 'private' || !device.deviceId) throw new DeviceMissing();
    const method = init.method ?? 'GET';
    const signed = await signRequest(device, method, path, init.body, { now: now(), subtle });
    return fetch(`${baseUrl}${path}`, { ...init, method, headers: { ...init.headers, ...signed } });
  };
}

export const forgetDevice = store => store.deleteDevice();

function base64(bytes) {
  let s = '';
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s);
}

const base64Url = bytes => base64(bytes).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
