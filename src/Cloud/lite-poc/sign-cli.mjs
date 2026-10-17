// For the .NET test (tests/Abastalia.Cloud.Tests/LiteBrowserSigningTests.cs): stdin {p12: base64, password, documents: [xml]},
// stdout {extractable, signed: [{signedXml, securityCode}]}. The password comes by stdin, never on the command line.
import { DOMParser } from '@xmldom/xmldom';
import { importP12, signXml } from './signer.js';

let input = '';
for await (const chunk of process.stdin) input += chunk;
const { p12, password, documents } = JSON.parse(input);
const signer = await importP12(Buffer.from(p12, 'base64'), password);
const signed = [];
for (const xml of documents) signed.push(await signXml(xml, signer, { DOMParser }));
process.stdout.write(JSON.stringify({ extractable: signer.key.extractable, signed }));
