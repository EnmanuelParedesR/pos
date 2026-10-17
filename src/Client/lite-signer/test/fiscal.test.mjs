// node --test: TK-324, one pass of the device over Host.Lite's pending e-CF (fiscal.js) against a fake host and a fake DGII client.
// The real host's side of the same flow: Abastalia.Lite.Tests BrowserFiscalTests.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { DOMParser } from '@xmldom/xmldom';
import { importP12, deviceSigner, memoryStore, processDocuments, ECF_TYPES, KeyMissing } from '../index.js';
import { testP12 } from '../fixtures/make-p12.mjs';

const RNC = '131245678';
const ecf31 = readFileSync(new URL('../fixtures/ecf-31.xml', import.meta.url), 'utf8');
const store = memoryStore();
await importP12(testP12('clave').p12, 'clave', store);
const signer = deviceSigner({ rnc: RNC, types: ECF_TYPES, store, DOMParser });
const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });

/** The host's state machine for one 31 (channel Ecf): Sign → Send → Poll → Done. Keeps every call. */
function host(items) {
  const calls = [];
  const send = async (path, init = {}) => {
    const body = init.body ? JSON.parse(init.body) : null;
    calls.push({ path, method: init.method ?? 'GET', auth: init.headers?.Authorization, body });
    if (path === '/api/fiscal/device/documents') return json(items);
    const item = items.find(i => path.startsWith(`/api/fiscal/device/documents/${i.id}`));
    if (path.endsWith('/failure')) return json({ ...item, lastError: body.message, attempts: 1 });
    if (body.signedXml) return json({ ...item, step: 'Send', xml: body.signedXml, securityCode: 'abc123' });
    if (body.answer?.state === 'InProcess') return json({ ...item, step: 'Poll', xml: null, trackId: body.answer.trackId });
    if (body.answer) return json({ ...item, step: 'Done', status: body.answer.state });
    return json({ detail: 'nada' }, 400);
  };
  return { send, calls };
}

function dgii() {
  const sent = [];
  return {
    sent,
    dgiiFor: environment => ({
      async sendEcf(rnc, encf, xml) { sent.push({ environment, rnc, encf, xml }); return { state: 'InProcess', trackId: 'track-1' }; },
      async getResult(trackId) { return { state: 'Accepted', trackId }; },
    }),
  };
}

const pending31 = () => ({ id: 'd1', eNcf: 'E310000000001', channel: 'Ecf', environment: 'TesteCF', step: 'Sign', xml: ecf31 });

test('a pending e-CF is signed, checked by the host first, sent to the DGII and left to poll', async () => {
  const { send, calls } = host([pending31()]);
  const fake = dgii();
  const [done] = await processDocuments({ send, token: 't', signer, dgiiFor: fake.dgiiFor, rnc: RNC });

  assert.deepEqual(calls.map(c => c.method), ['GET', 'POST', 'POST']);
  assert.ok(calls.every(c => c.auth === 'Bearer t'));
  const signed = calls[1].body.signedXml;
  assert.match(signed, /<Signature xmlns="http:\/\/www.w3.org\/2000\/09\/xmldsig#">/); // the signer step ran, and the host got it before the DGII
  assert.deepEqual(fake.sent, [{ environment: 'TesteCF', rnc: RNC, encf: 'E310000000001', xml: signed }]);
  assert.deepEqual(calls[2].body.answer, { state: 'InProcess', trackId: 'track-1' });
  assert.equal(done.step, 'Poll'); // never polled right after sending: the next pass asks
  assert.equal((await signer.log()).at(-1).encf, 'E310000000001');

  const [accepted] = await processDocuments({ send: host([done]).send, token: 't', signer, dgiiFor: fake.dgiiFor, rnc: RNC });
  assert.equal(accepted.status, 'Accepted');
});

test('until the DGII is open (TK-141) the document is signed and kept on the host, never sent', async () => {
  const { send } = host([pending31()]);
  const fake = dgii();
  const [done] = await processDocuments({ send, token: 't', signer, dgiiFor: fake.dgiiFor, rnc: RNC, sendToDgii: false });
  assert.equal(done.step, 'Send');
  assert.equal(fake.sent.length, 0);
});

test('a document the device refuses is reported and the pass goes on; a missing key stops it', async () => {
  const other = { ...pending31(), id: 'd0', xml: ecf31.replace(RNC, '101672919') }; // not this business's RNC: the allowlist refuses
  const { send, calls } = host([other, pending31()]);
  const done = await processDocuments({ send, token: 't', signer, dgiiFor: dgii().dgiiFor, rnc: RNC });
  assert.equal(calls[1].path, '/api/fiscal/device/documents/d0/failure');
  assert.match(done[0].lastError, /RNC del emisor no es el de este negocio/);
  assert.equal(done[1].step, 'Poll');

  const empty = deviceSigner({ rnc: RNC, types: ECF_TYPES, store: memoryStore(), DOMParser });
  await assert.rejects(processDocuments({ send: host([pending31()]).send, token: 't', signer: empty, dgiiFor: dgii().dgiiFor, rnc: RNC }), KeyMissing);
});
