// node --test: the public API (index.js): import and keep the key, eviction, the allowlist and the signing log.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { DOMParser } from '@xmldom/xmldom';
import { importP12, loadSigner, removeSigner, deviceSigner, memoryStore, KeyMissing, SignRefused } from '../index.js';
import { testP12 } from '../fixtures/make-p12.mjs';

const RNC = '131245678';
const fixture = name => readFileSync(new URL(`../fixtures/${name}`, import.meta.url), 'utf8');
const ecf31 = fixture('ecf-31.xml');
const ecf32 = fixture('ecf-32.xml');
const { p12 } = testP12('clave');

async function ready(types = ['31', '32']) {
  const store = memoryStore();
  await importP12(p12, 'clave', store);
  return { store, signer: deviceSigner({ rnc: RNC, types, store, DOMParser }) };
}

const rfce = (rnc = RNC, type = '32', encf = 'E320000000001') =>
  `<RFCE><Encabezado><Version>1.0</Version><IdDoc><TipoeCF>${type}</TipoeCF><eNCF>${encf}</eNCF></IdDoc><Emisor><RNCEmisor>${rnc}</RNCEmisor></Emisor></Encabezado></RFCE>`;
const arecf = (buyer, estado = '0') =>
  `<ARECF><DetalleAcusedeRecibo><Version>1.0</Version><RNCEmisor>101672919</RNCEmisor><RNCComprador>${buyer}</RNCComprador><eNCF>E310000000007</eNCF><Estado>${estado}</Estado></DetalleAcusedeRecibo></ARECF>`;
const acecf = buyer =>
  `<ACECF><DetalleAprobacionComercial><Version>1.0</Version><RNCEmisor>101672919</RNCEmisor><eNCF>E310000000007</eNCF><RNCComprador>${buyer}</RNCComprador><Estado>1</Estado></DetalleAprobacionComercial></ACECF>`;
const anecf = (rnc, type) =>
  `<ANECF><Encabezado><Version>1.0</Version><RncEmisor>${rnc}</RncEmisor></Encabezado><DetalleAnulacion><Anulacion><NoLinea>1</NoLinea><TipoeCF>${type}</TipoeCF></Anulacion></DetalleAnulacion></ANECF>`;
const seed = '<?xml version="1.0" encoding="utf-8"?><SemillaModel xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"><valor>abc</valor><fecha>2026-10-17T10:00:00</fecha></SemillaModel>';

test('the key is kept as a non-extractable CryptoKey, loads back and can be removed', async () => {
  const store = memoryStore();
  const { certificate } = await importP12(new Blob([p12]), 'clave', store); // a File from <input type="file"> is a Blob
  const kept = await loadSigner(store);
  assert.equal(kept.certificate, certificate);
  assert.equal(kept.key.extractable, false);
  assert.ok(!('pkcs8' in kept) && Object.keys(kept).every(k => ['key', 'certificate', 'importedAt'].includes(k))); // no raw key bytes stored
  await removeSigner(store);
  assert.equal(await loadSigner(store), null);
});

test('a wrong password stores nothing', async () => {
  const store = memoryStore();
  await assert.rejects(importP12(p12, 'otra', store));
  assert.equal(await loadSigner(store), null);
});

test('an evicted key asks to re-import the .p12', async () => {
  const { store, signer } = await ready();
  await store.deleteSigner(); // what the browser does when it evicts the site's IndexedDB
  await assert.rejects(signer.sign(ecf32), e => e instanceof KeyMissing && /Vuelva a importar el archivo \.p12/.test(e.message));
});

test('the business own documents are signed and logged without their XML', async () => {
  const { signer } = await ready(['31', '32', '34']);
  const allowed = [
    [ecf31, 'ECF', '31', 'E310000000001'], [ecf32, 'ECF', '32', 'E320000000001'], [rfce(), 'RFCE', '32', 'E320000000001'],
    [anecf(RNC, '34'), 'ANECF', null, null], [arecf(RNC), 'ARECF', null, 'E310000000007'],
    [arecf('101010101', '1'), 'ARECF', null, 'E310000000007'], // a refusal may name another buyer (motivo 4), as in the store
    [acecf(RNC), 'ACECF', null, 'E310000000007'], [seed, 'SemillaModel', null, null],
  ];
  for (const [xml, document, type, encf] of allowed) {
    const signed = await signer.sign(xml);
    assert.deepEqual([signed.document, signed.type, signed.encf], [document, type, encf]);
    assert.match(signed.signedXml, /<\/Signature><\/\w+>$/);
  }
  const log = await signer.log();
  assert.equal(log.length, allowed.length);
  assert.deepEqual(Object.keys(log[0]).sort(), ['digest', 'document', 'encf', 'signedAt', 'type']);
  assert.equal(log[0].encf, 'E310000000001');
  assert.ok(!JSON.stringify(log).includes('<'));
});

test('anything else is refused and not logged', async () => {
  const { signer } = await ready();
  const refused = {
    'e-CF of another RNC': ecf31.replace(`<RNCEmisor>${RNC}`, '<RNCEmisor>101672919'),
    'a type the business does not issue': ecf31.replaceAll('<TipoeCF>31<', '<TipoeCF>33<').replace('E310000000001', 'E330000000001'),
    'e-NCF of another type': ecf31.replace('E310000000001', 'E320000000001'),
    'two issuers': ecf31.replace('</Emisor>', `</Emisor><Emisor><RNCEmisor>${RNC}</RNCEmisor></Emisor>`),
    'issuer RNC only deeper in the tree': ecf31.replace(`<RNCEmisor>${RNC}</RNCEmisor>`, `<X><RNCEmisor>${RNC}</RNCEmisor></X>`),
    'RFCE of another RNC': rfce('101672919'),
    'RFCE that is not a 32': rfce(RNC, '31', 'E310000000001'),
    'ANECF of another RNC': anecf('101672919', '32'),
    'ANECF of a type not issued': anecf(RNC, '34'),
    'ARECF receipt for another buyer': arecf('101672919'),
    'ACECF for another buyer': acecf('101672919'),
    'seed with something else inside': '<SemillaModel><valor>a</valor><fecha>b</fecha><RNC>1</RNC></SemillaModel>',
    'an already signed document': ecf32.replace('</ECF>', '<Signature xmlns="http://www.w3.org/2000/09/xmldsig#"/></ECF>'),
    'a namespaced root': '<ECF xmlns="urn:x"><Encabezado/></ECF>',
    'any other XML': '<Pedido><Total>1</Total></Pedido>',
  };
  for (const [what, xml] of Object.entries(refused))
    await assert.rejects(signer.sign(xml), e => e instanceof SignRefused, what);
  assert.deepEqual(await signer.log(), []);
});

test('the business RNC and its types are required', () => {
  const store = memoryStore();
  assert.throws(() => deviceSigner({ rnc: '1312', types: ['31'], store }));
  assert.throws(() => deviceSigner({ rnc: RNC, types: [], store }));
});
