// npm run bench: median times of importing the .p12 and signing a 31, in this Node (WebCrypto is native here as in a browser; forge is plain JS).
import { readFileSync } from 'node:fs';
import { DOMParser } from '@xmldom/xmldom';
import { importP12, signXml } from './signer.js';
import { testP12 } from './make-p12.mjs';

const ecf31 = readFileSync(new URL('./fixtures/ecf-31.xml', import.meta.url), 'utf8');
const median = a => a.sort((x, y) => x - y)[a.length >> 1];
async function time(n, f) {
  const ms = [];
  for (let i = 0; i < n; i++) { const t = performance.now(); await f(); ms.push(performance.now() - t); }
  return median(ms).toFixed(2);
}

for (const algorithm of ['aes256', '3des']) {
  const { p12 } = testP12('clave', algorithm);
  const signer = await importP12(p12, 'clave');
  await signXml(ecf31, signer, { DOMParser }); // warm-up
  console.log(`${algorithm}: import .p12 ${await time(10, () => importP12(p12, 'clave'))} ms, sign 31 ${await time(100, () => signXml(ecf31, signer, { DOMParser }))} ms`);
}
