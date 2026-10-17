// node --test: TK-324, the committed browser bundle (Abastalia.Client.Lite/wwwroot/js/lite-signer.js) is what `npm run build` makes now.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { bundle, out } from '../build.mjs';

test('the browser bundle is up to date (else: npm run build)', async () => {
  const { outputFiles } = await bundle(false);
  assert.equal(readFileSync(out, 'utf8'), outputFiles[0].text);
});
