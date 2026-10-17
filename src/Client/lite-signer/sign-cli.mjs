// For the .NET test (tests/Abastalia.Cloud.Tests/LiteBrowserSigningTests.cs): stdin {p12: base64, password, rnc, types, documents: [xml]},
// stdout {extractable, signed: [{signedXml, securityCode, …}], log}. The password comes by stdin, never on the command line.
// Goes through the public API (import → allowlist → sign → log), with the in-memory store instead of IndexedDB.
import { DOMParser } from '@xmldom/xmldom';
import { importP12, loadSigner, deviceSigner, memoryStore } from './index.js';

let input = '';
for await (const chunk of process.stdin) input += chunk;
const { p12, password, rnc, types, documents } = JSON.parse(input);
const store = memoryStore();
await importP12(Buffer.from(p12, 'base64'), password, store);
const signer = deviceSigner({ rnc, types, store, DOMParser });
const signed = [];
for (const xml of documents) signed.push(await signer.sign(xml));
process.stdout.write(JSON.stringify({ extractable: (await loadSigner(store)).key.extractable, signed, log: await signer.log() }));
