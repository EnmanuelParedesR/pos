// TK-322: the browser talks to the DGII directly (ADR-0013 decision 2; the DGII answers Access-Control-Allow-Origin: *, checked 16/10/2026).
// A port of the store's DgiiClient (src/Modules/Fiscal/Abastalia.Fiscal/DgiiClient.cs, docs/ecf-propio.md §2-§3 and §10.9): seed → signed seed →
// token kept in memory only until 5 minutes before its `expira` (or a 401), multipart uploads with the field `xml` named RNC + e-NCF + .xml.
// No retries of its own: the PWA's queue retries what throws.
// UNTESTED AGAINST THE DGII: TesteCF access waits for TK-141. Tested with a fetch stub of the documented answers (test/dgii.test.mjs).

/** A transient failure (5xx, 429, a failed authentication, an answer that is not the DGII's): retry later. A DGII refusal is not an error but an answer. */
export class DgiiError extends Error {
  constructor(message, status = null) { super(message); this.status = status; }
}

export const RENEW_BEFORE_MS = 5 * 60_000;
const PATHS = { TesteCF: 'testecf', CerteCF: 'certecf', eCF: 'ecf' };
const CODES = { 0: 'NotFound', 1: 'Accepted', 2: 'Rejected', 3: 'InProcess', 4: 'AcceptedConditional' };
const STATES = { aceptado: 'Accepted', rechazado: 'Rejected', 'aceptado condicional': 'AcceptedConditional', 'en proceso': 'InProcess' };

/** @param {'TesteCF' | 'CerteCF' | 'eCF'} environment */
export function dgiiUrls(environment) {
  const path = PATHS[environment];
  if (!path) throw new Error(`Ambiente de la DGII desconocido: ${environment}. Use TesteCF, CerteCF o eCF.`);
  return { ecf: `https://ecf.dgii.gov.do/${path}`, fc: `https://fc.dgii.gov.do/${path}` };
}

/** @param {{ environment: string, signer: { sign(xml: string): Promise<{ signedXml: string }> }, fetch?: typeof fetch, now?: () => number, timeoutMs?: number }} options
 *  signer = the deviceSigner (it signs the seed). Every answer is { state, trackId, sequenceUsed, messages }, state one of
 *  NotFound | Accepted | Rejected | InProcess | AcceptedConditional. */
export function dgiiClient({ environment, signer, fetch = globalThis.fetch.bind(globalThis), now = Date.now, timeoutMs = 30_000 }) {
  const urls = dgiiUrls(environment);
  let cached; // { token, expires }
  let pending;

  const call = (url, init = {}) => fetch(url, { ...init, signal: AbortSignal.timeout(timeoutMs) });
  const upload = (fileName, xml) => {
    const body = new FormData();
    body.append('xml', new Blob([xml], { type: 'text/xml' }), fileName);
    return body;
  };

  async function authenticate() {
    const auth = `${urls.ecf}/autenticacion/api/autenticacion`;
    const seed = await call(`${auth}/semilla`);
    ensureAuthentication(seed);
    const { signedXml } = await signer.sign(await seed.text());
    const answer = await call(`${auth}/validarsemilla`, { method: 'POST', body: upload('semilla.xml', signedXml) });
    ensureAuthentication(answer);
    const json = await jsonOf(answer);
    const token = str(json, 'token');
    if (!token) throw new DgiiError('La DGII no devolvió el token.');
    cached = { token, expires: parseDate(str(json, 'expira')) ?? now() + 3_600_000 };
    return token;
  }

  async function token() {
    if (cached && now() < cached.expires - RENEW_BEFORE_MS) return cached.token;
    return pending ??= authenticate().finally(() => { pending = undefined; });
  }

  /** With the cached token; a 401 drops it and tries once more with a new one. 5xx and 429 throw; other answers are read by the caller. */
  async function send(url, body) {
    for (let attempt = 1; ; attempt++) {
      const bearer = await token();
      const response = await call(url, { method: body ? 'POST' : 'GET', body: body?.(), headers: { Authorization: `Bearer ${bearer}`, Accept: 'application/json' } });
      if (response.status === 401 && attempt === 1) { cached = undefined; continue; }
      if (response.status >= 500 || response.status === 429 || response.status === 401) throw new DgiiError(`La DGII respondió ${response.status}.`, response.status);
      return { status: response.status, json: await jsonOf(response) };
    }
  }

  // R12 #4 of the store: an answer without the DGII's JSON (a firewall page, a proxy's 404) is not a refusal.
  const notDgii = status => new DgiiError(`La DGII no dio una respuesta válida (HTTP ${status}).`, status);

  return {
    environment,
    urls,
    token,

    /** The whole e-CF to recepción: InProcess with a TrackId, or Rejected. */
    async sendEcf(issuerRnc, encf, signedXml) {
      const { status, json } = await send(`${urls.ecf}/recepcion/api/facturaselectronicas`, () => upload(`${issuerRnc}${encf}.xml`, signedXml));
      const trackId = str(json, 'trackId');
      if (trackId) return { state: 'InProcess', trackId, sequenceUsed: null, messages: null };
      const why = join(str(json, 'error'), messages(json));
      if (why) return { state: 'Rejected', trackId: null, sequenceUsed: null, messages: why };
      throw notDgii(status);
    },

    /** consultaresultado: the result of a TrackId (InProcess while the DGII works on it). */
    async getResult(trackId) {
      const { json } = await send(`${urls.ecf}/consultaresultado/api/consultas/estado?trackid=${encodeURIComponent(trackId)}`);
      return answer(json, trackId);
    },

    /** The RFCE of a 32 under RD$250,000 to recepcionfc: the final answer comes right away. */
    async sendRfce(issuerRnc, encf, signedRfce) {
      const { status, json } = await send(`${urls.fc}/recepcionfc/api/recepcion/ecf`, () => upload(`${issuerRnc}${encf}.xml`, signedRfce));
      if (!isObject(json)) throw notDgii(status);
      const result = answer(json, null);
      // R13 of the store: a refusal without codigo/estado ({"error": …}) is a rejection; read as "not found" it would wait for good.
      const why = join(str(json, 'error'), messages(json));
      return result.state === 'NotFound' && why ? { ...result, state: 'Rejected', messages: why } : result;
    },

    /** consultarfce: what the DGII has for an RFCE (after a timeout, before resending), or null if nothing. */
    async getRfceStatus(issuerRnc, encf, securityCode) {
      const { json } = await send(`${urls.fc}/consultarfce/api/Consultas/Consulta?RNC_Emisor=${encodeURIComponent(issuerRnc)}` +
        `&ENCF=${encodeURIComponent(encf)}&Cod_Seguridad_eCF=${encodeURIComponent(securityCode)}`);
      const result = answer(json, null);
      return result.state === 'NotFound' ? null : result;
    },
  };
}

function ensureAuthentication(response) {
  if (!response.ok) throw new DgiiError(`La autenticación con la DGII falló (${response.status}): revise el certificado y la delegación del firmante.`, response.status);
}

async function jsonOf(response) {
  try { return JSON.parse(await response.text()); } catch { return null; }
}

/** `expira` is yyyy-MM-ddTHH:mm:ssZ; without a zone it is UTC (as the store reads it), never the device's local time. */
function parseDate(text) {
  if (!text) return null;
  const ms = Date.parse(/(z|[+-]\d\d:?\d\d)$/i.test(text) ? text : text + 'Z');
  return Number.isNaN(ms) ? null : ms;
}

function answer(json, trackId) {
  const state = CODES[Number.parseInt(str(json, 'codigo'), 10)] ?? STATES[str(json, 'estado')?.toLowerCase()] ?? 'NotFound';
  const used = json?.secuenciaUtilizada;
  return { state, trackId: str(json, 'trackId') ?? trackId, sequenceUsed: typeof used === 'boolean' ? used : null, messages: messages(json) };
}

/** `mensajes` / `mensaje`: strings or { valor, codigo }, joined in one line. */
function messages(json) {
  if (!isObject(json)) return null;
  const parts = [];
  for (const name of ['mensajes', 'mensaje']) {
    if (!(name in json)) continue;
    for (const item of [json[name]].flat()) {
      const part = typeof item === 'string' ? item : isObject(item) ? join(str(item, 'codigo'), str(item, 'valor')) : null;
      if (part?.trim()) parts.push(part);
    }
  }
  const text = parts.join(' | ');
  return text ? text.slice(0, 2000) : null;
}

const isObject = v => v !== null && typeof v === 'object' && !Array.isArray(v);
const join = (a, b) => [a, b].filter(s => s).join(': ') || null;
function str(json, name) {
  const v = isObject(json) ? json[name] : undefined;
  return typeof v === 'string' ? v : typeof v === 'number' ? String(v) : null;
}
