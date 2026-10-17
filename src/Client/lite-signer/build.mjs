// TK-324: the browser bundle of index.js that Abastalia.Client.Lite serves: one ES module with node-forge's PKCS#12 parts inside.
// Committed, so `dotnet build` needs no Node; test/bundle.test.mjs fails when it is out of date. Run `npm run build` after changing the signer.
import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';

export const out = fileURLToPath(new URL('../Abastalia.Client.Lite/wwwroot/js/lite-signer.js', import.meta.url));

export const bundle = write => build({
  entryPoints: [fileURLToPath(new URL('index.js', import.meta.url))],
  bundle: true, format: 'esm', platform: 'browser', target: 'es2022', minify: true, legalComments: 'eof', charset: 'utf8',
  banner: { js: '// Generated from src/Client/lite-signer by `npm run build` (TK-324). Do not edit.' },
  outfile: out, write, logLevel: 'warning',
});

if (process.argv[1] === fileURLToPath(import.meta.url)) await bundle(true);
