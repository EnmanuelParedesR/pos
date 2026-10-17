// npm run bench: median times of importing the .p12 and signing a 31, in this Node (WebCrypto is native here as in a browser; forge is plain JS).
import { readFileSync } from 'node:fs';
import { DOMParser } from '@xmldom/xmldom';
import { openP12, parseXml, signXml } from './xmldsig.js';
import { testP12 } from './fixtures/make-p12.mjs';

const ecf31 = readFileSync(new URL('./fixtures/ecf-31.xml', import.meta.url), 'utf8');
const median = a => a.sort((x, y) => x - y)[a.length >> 1];
async function time(n, f) {
  const ms = [];
  for (let i = 0; i < n; i++) { const t = performance.now(); await f(); ms.push(performance.now() - t); }
  return median(ms).toFixed(2);
}

for (const algorithm of ['aes256', '3des']) {
  const { p12 } = testP12('clave', algorithm);
  const signer = await openP12(p12, 'clave');
  const sign = () => signXml(parseXml(ecf31, DOMParser), signer);
  await sign(); // warm-up
  console.log(`${algorithm}: import .p12 ${await time(10, () => openP12(p12, 'clave'))} ms, sign 31 ${await time(100, sign)} ms`);
}
