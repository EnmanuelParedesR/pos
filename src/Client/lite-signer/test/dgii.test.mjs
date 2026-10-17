// node --test: dgii.js against a fetch stub of the DGII as docs/ecf-propio.md §2-§3 documents it (the same contract as FakeDgii in
// Abastalia.IntegrationTests). Not the real DGII: TesteCF access waits for TK-141.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { DOMParser } from '@xmldom/xmldom';
import { importP12, deviceSigner, memoryStore, dgiiClient, DgiiError } from '../index.js';
import { testP12 } from '../fixtures/make-p12.mjs';

const RNC = '131245678';
const ecf31 = readFileSync(new URL('../fixtures/ecf-31.xml', import.meta.url), 'utf8');
const store = memoryStore();
await importP12(testP12('clave').p12, 'clave', store);
const signer = deviceSigner({ rnc: RNC, types: ['31', '32'], store, DOMParser });

const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });

/** The DGII: seed → signed seed → token with `expira`; recepción gives a TrackId and consultaresultado "En Proceso" once, then the result. */
function fakeDgii({ now, lifetimeMs = 3_600_000, result = () => ({ codigo: 1, estado: 'Aceptado', secuenciaUtilizada: true, mensajes: [] }) }) {
  const fake = { requests: [], tokens: [], override: null, polls: {} };
  fake.fetch = async (url, init = {}) => {
    const u = new URL(url);
    const file = init.body?.get('xml');
    const xml = file ? await file.text() : null;
    const request = { host: u.host, path: u.pathname, query: u.searchParams, auth: init.headers?.Authorization, fileName: file?.name, type: file?.type, xml };
    fake.requests.push(request);
    if (u.pathname.endsWith('/autenticacion/semilla')) return new Response('<?xml version="1.0" encoding="utf-8"?><SemillaModel><valor>v</valor><fecha>2026-10-17T10:00:00</fecha></SemillaModel>');
    if (u.pathname.endsWith('/autenticacion/validarsemilla')) {
      if (!/^<\?xml[^>]*\?><SemillaModel>.*<Signature xmlns="http:\/\/www.w3.org\/2000\/09\/xmldsig#">/.test(xml)) return new Response('', { status: 400 });
      const token = `token-${fake.tokens.length + 1}`;
      fake.tokens.push(token);
      return json({ token, expira: new Date(now() + lifetimeMs).toISOString().replace(/\.\d+Z$/, 'Z'), expedido: new Date(now()).toISOString() });
    }
    if (!fake.tokens.includes(request.auth?.slice('Bearer '.length))) return new Response('', { status: 401 });
    if (fake.override) return fake.override(request);
    if (u.pathname.endsWith('/recepcion/api/facturaselectronicas')) return json({ trackId: 'track-1', error: null, mensaje: null });
    if (u.pathname.endsWith('/consultaresultado/api/consultas/estado')) {
      const id = u.searchParams.get('trackid');
      fake.polls[id] = (fake.polls[id] ?? 0) + 1;
      return json(fake.polls[id] === 1 ? { trackId: id, codigo: 3, estado: 'En Proceso', mensajes: [] } : { trackId: id, ...result() });
    }
    if (u.pathname.endsWith('/recepcionfc/api/recepcion/ecf')) return json({ ...result(), encf: 'E320000000001' });
    if (u.pathname.endsWith('/consultarfce/api/Consultas/Consulta'))
      return json(fake.requests.some(r => r.path.endsWith('/recepcionfc/api/recepcion/ecf')) ? result() : { codigo: 0, estado: 'No encontrado' });
    return new Response('', { status: 404 });
  };
  return fake;
}

function setup(options = {}) {
  let clock = Date.parse('2026-10-17T14:00:00Z');
  const now = () => clock;
  const fake = fakeDgii({ now, ...options });
  const client = dgiiClient({ environment: 'TesteCF', signer, fetch: fake.fetch, now });
  return { fake, client, advance: ms => { clock += ms; } };
}

test('URLs per environment', () => {
  for (const [env, path] of [['TesteCF', 'testecf'], ['CerteCF', 'certecf'], ['eCF', 'ecf']])
    assert.deepEqual(dgiiClient({ environment: env, signer, fetch: () => {} }).urls, { ecf: `https://ecf.dgii.gov.do/${path}`, fc: `https://fc.dgii.gov.do/${path}` });
  assert.throws(() => dgiiClient({ environment: 'Simulated', signer, fetch: () => {} }));
});

test('an e-CF: seed signed by the device → token → recepción → TrackId → result, with one token', async () => {
  const { fake, client } = setup();
  const { signedXml } = await signer.sign(ecf31);
  const sent = await client.sendEcf(RNC, 'E310000000001', signedXml);
  assert.deepEqual(sent, { state: 'InProcess', trackId: 'track-1', sequenceUsed: null, messages: null });
  assert.equal((await client.getResult('track-1')).state, 'InProcess');
  assert.deepEqual(await client.getResult('track-1'), { state: 'Accepted', trackId: 'track-1', sequenceUsed: true, messages: null });

  const upload = fake.requests.find(r => r.path.endsWith('/facturaselectronicas'));
  assert.equal(upload.host, 'ecf.dgii.gov.do');
  assert.equal(upload.path, '/testecf/recepcion/api/facturaselectronicas');
  assert.deepEqual([upload.fileName, upload.type, upload.xml, upload.auth], [`${RNC}E310000000001.xml`, 'text/xml', signedXml, 'Bearer token-1']);
  assert.equal(fake.requests.find(r => r.path.endsWith('/validarsemilla')).path, '/testecf/autenticacion/api/autenticacion/validarsemilla');
  assert.deepEqual(fake.tokens, ['token-1']);
  assert.ok((await signer.log()).some(e => e.document === 'SemillaModel'));
});

test('the token is renewed 5 minutes before expira and after a 401', async () => {
  const { fake, client, advance } = setup();
  await client.getResult('x');
  advance(54 * 60_000);
  await client.getResult('x');
  assert.equal(fake.tokens.length, 1);
  advance(2 * 60_000); // 4 minutes left
  await client.getResult('x');
  assert.equal(fake.tokens.length, 2);
  fake.tokens.splice(0); // the DGII forgets every token
  await client.getResult('x');
  assert.equal(fake.tokens.length, 1);
});

test('concurrent calls share one authentication', async () => {
  const { fake, client } = setup();
  await Promise.all([client.getResult('a'), client.getResult('b'), client.getResult('c')]);
  assert.equal(fake.requests.filter(r => r.path.endsWith('/semilla')).length, 1);
});

test('an RFCE: recepcionfc answers right away; consultarfce finds it', async () => {
  const { fake, client } = setup({ result: () => ({ codigo: 4, estado: 'Aceptado Condicional', secuenciaUtilizada: true, mensajes: [{ valor: 'Monto redondeado', codigo: 145 }] }) });
  assert.equal(await client.getRfceStatus(RNC, 'E320000000001', 'abc+12'), null);
  const answer = await client.sendRfce(RNC, 'E320000000001', '<RFCE/>');
  assert.deepEqual(answer, { state: 'AcceptedConditional', trackId: null, sequenceUsed: true, messages: '145: Monto redondeado' });
  assert.equal((await client.getRfceStatus(RNC, 'E320000000001', 'abc+12')).state, 'AcceptedConditional');
  const rfce = fake.requests.find(r => r.path.endsWith('/recepcionfc/api/recepcion/ecf'));
  assert.deepEqual([rfce.host, rfce.path, rfce.fileName], ['fc.dgii.gov.do', '/testecf/recepcionfc/api/recepcion/ecf', `${RNC}E320000000001.xml`]);
  assert.equal(fake.requests.find(r => r.path.endsWith('/Consulta')).query.get('Cod_Seguridad_eCF'), 'abc+12');
});

test('refusals are answers; failures throw', async () => {
  const { fake, client } = setup();
  fake.override = () => json({ error: 'XML no válido', mensaje: ['El campo X falta'] });
  assert.deepEqual(await client.sendEcf(RNC, 'E310000000001', '<ECF/>'), { state: 'Rejected', trackId: null, sequenceUsed: null, messages: 'XML no válido: El campo X falta' });
  assert.equal((await client.sendRfce(RNC, 'E320000000001', '<RFCE/>')).state, 'Rejected'); // {"error": …} without codigo is a rejection (R13)
  for (const [status, body] of [[500, '{}'], [429, '{}'], [404, '<html>proxy</html>'], [200, '<html>firewall</html>']]) {
    fake.override = () => new Response(body, { status });
    await assert.rejects(client.sendEcf(RNC, 'E310000000001', '<ECF/>'), DgiiError, `${status} ${body}`);
  }
});

test('a failed authentication throws', async () => {
  const fake = { fetch: async () => new Response('', { status: 403 }) };
  await assert.rejects(dgiiClient({ environment: 'eCF', signer, fetch: fake.fetch }).getResult('x'), DgiiError);
});
