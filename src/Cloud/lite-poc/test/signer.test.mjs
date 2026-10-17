// node --test. The byte-for-byte comparison with the store and its .NET verifier is LiteBrowserSigningTests (tests/Abastalia.Cloud.Tests).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash, createVerify } from 'node:crypto';
import { DOMParser } from '@xmldom/xmldom';
import { importP12, signXml, canonical } from '../signer.js';
import { testP12 } from '../make-p12.mjs';

const ecf32 = readFileSync(new URL('../fixtures/ecf-32.xml', import.meta.url), 'utf8');
const parse = xml => new DOMParser().parseFromString(xml, 'text/xml');
const dsig = (doc, name) => doc.getElementsByTagNameNS('http://www.w3.org/2000/09/xmldsig#', name)[0];

for (const algorithm of ['aes256', '3des']) {
  test(`a ${algorithm} .p12 imports as a non-extractable signing key and its signature verifies`, async () => {
    const { p12, certPem } = testP12('clave-de-prueba', algorithm);
    const signer = await importP12(p12, 'clave-de-prueba');
    assert.equal(signer.key.extractable, false);
    assert.deepEqual(signer.key.usages, ['sign']);
    await assert.rejects(crypto.subtle.exportKey('pkcs8', signer.key));

    const { signedXml, securityCode } = await signXml(ecf32, signer, { DOMParser });
    const doc = parse(signedXml);
    const signature = dsig(doc, 'Signature');
    assert.equal(doc.documentElement.lastChild, signature); // enveloped: last child of ECF
    const value = dsig(doc, 'SignatureValue').textContent;
    assert.equal(securityCode, value.slice(0, 6));
    assert.ok(createVerify('RSA-SHA256').update(canonical(dsig(doc, 'SignedInfo'))).verify(certPem, value, 'base64'));

    // The digest is over the document without the signature (enveloped transform).
    doc.documentElement.removeChild(signature);
    assert.equal(createHash('sha256').update(canonical(doc.documentElement)).digest('base64'), dsig(parse(signedXml), 'DigestValue').textContent);
  });
}

test('a wrong password is refused', async () => {
  const { p12 } = testP12('clave-de-prueba');
  await assert.rejects(importP12(p12, 'otra'));
});

test('C14N: sorted attributes, inherited namespaces at the subset apex, expanded empty elements, escapes', () => {
  const doc = parse('<r xmlns:b="urn:b" xmlns="urn:a"><e z="1" a="x&quot;&#9;"><s/>a&amp;b&gt;c</e></r>');
  assert.equal(canonical(doc.documentElement.firstChild), '<e xmlns="urn:a" xmlns:b="urn:b" a="x&quot;&#x9;" z="1"><s></s>a&amp;b&gt;c</e>');
});
