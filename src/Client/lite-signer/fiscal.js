// TK-324: the selling device's side of TK-323's flow (ADR-0013, a sale): Host.Lite lists what each e-CF waits for at
// GET /api/fiscal/device/documents (step Sign, SignRfce, Send or Poll) and takes each result at POST /api/fiscal/device/documents/{id},
// answering the next step. Every signature goes to the host before the DGII sees it: the host checks it was the XML it built.
import { DeviceMissing } from './device.js';
import { KeyMissing } from './index.js'; // a cycle, read only once a pass runs

/** Every e-CF type the DGII has. The allowlist's RNC check is what keeps the device to its own documents; the host builds only the types the
 *  business issues, so listing them all refuses nothing it would send. */
export const ECF_TYPES = ['31', '32', '33', '34', '41', '43', '44', '45', '46', '47'];

/** One pass over the due documents. send = deviceFetch; token = the session's; signer = deviceSigner; dgiiFor(environment) = a dgiiClient;
 *  rnc = the business's; sendToDgii = false signs and keeps them on the host without calling the DGII (until TK-141 opens TesteCF).
 *  A document that fails is reported to the host (/failure: it offers it again later) and the pass goes on with the next one.
 *  Throws what affects them all: KeyMissing (re-import the .p12), DeviceMissing, or the host not answering the list.
 *  @returns {Promise<object[]>} each document as the host left it ({ eNcf, step, status, submitDeadlineUtc, deadlineNear, lastError, … }). */
export async function processDocuments({ send, token, signer, dgiiFor, rnc, sendToDgii = true }) {
  const auth = { Authorization: `Bearer ${token}` };
  const list = await send('/api/fiscal/device/documents', { headers: auth });
  if (!list.ok) throw new Error(`El servidor respondió ${list.status} al pedir los comprobantes pendientes.`);
  const done = [];
  for (const item of await list.json()) {
    try {
      done.push(await advance(item));
    } catch (e) {
      if (e instanceof KeyMissing || e instanceof DeviceMissing) throw e;
      done.push(await post(item.id, { message: String(e?.message ?? e).slice(0, 2000) }, '/failure').catch(() => ({ ...item, lastError: e?.message })));
    }
  }
  return done;

  async function post(id, body, suffix = '') {
    const r = await send(`/api/fiscal/device/documents/${id}${suffix}`, { method: 'POST', headers: { ...auth, 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const answer = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(answer.detail ?? `El servidor respondió ${r.status}.`);
    return answer;
  }

  async function advance(item) {
    const dgii = () => dgiiFor(item.environment);
    // Each document moves until it waits for someone: the DGII's answer (Poll, once per pass, never right after sending) or a later pass.
    for (let polled = false; ;) {
      switch (item.step) {
        case 'Sign':
          item = await post(item.id, { signedXml: (await signer.sign(item.xml)).signedXml });
          break;
        case 'SignRfce':
          item = await post(item.id, { signedRfce: (await signer.sign(item.xml)).signedXml });
          break;
        case 'Send': {
          if (!sendToDgii) return item;
          const answer = item.channel === 'Rfce' ? await dgii().sendRfce(rnc, item.eNcf, item.xml) : await dgii().sendEcf(rnc, item.eNcf, item.xml);
          item = await post(item.id, { answer });
          polled = true;
          break;
        }
        case 'Poll': {
          if (polled || !sendToDgii) return item;
          polled = true;
          const answer = item.channel === 'Rfce' ? await dgii().getRfceStatus(rnc, item.eNcf, item.securityCode) : await dgii().getResult(item.trackId);
          if (!answer) return item; // the DGII has nothing for that RFCE yet
          item = await post(item.id, { answer });
          break;
        }
        default:
          return item;
      }
    }
  }
}
