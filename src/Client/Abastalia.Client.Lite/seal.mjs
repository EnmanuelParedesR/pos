// TK-324: run after `dotnet publish` (the Publish target calls it): the published index.html gets SRI on its stylesheets and classic scripts
// (the modules already carry theirs in the import map .NET writes), and staticwebapp.config.json gets the CSP's two blanks: the hash of
// that inline import map (it changes with every build) and Host.Lite's origin. Usage: node seal.mjs <publish>/wwwroot [https://<Host.Lite>]
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const root = process.argv[2];
const sha = bytes => `sha256-${createHash('sha256').update(bytes).digest('base64')}`;
const file = name => join(root, name);
// The precompressed copies are of the unsealed files: without them the host compresses the sealed ones.
const dropCompressed = name => ['.br', '.gz'].forEach(ext => rmSync(file(name + ext), { force: true }));

let html = readFileSync(file('index.html'), 'utf8');
html = html.replace(/<(link rel="stylesheet" href|script src)="([^"]+)"/g, (tag, _, href) =>
  /^(https?:)?\/\//.test(href) ? tag : `${tag} integrity="${sha(readFileSync(file(href.split('?')[0])))}"`);
const importMap = html.match(/<script type="importmap">([\s\S]*?)<\/script>/)?.[1];
if (importMap === undefined) throw new Error('index.html has no import map: CSP cannot be sealed.');
writeFileSync(file('index.html'), html);
dropCompressed('index.html');

// The host's URL: the second argument (dotnet publish -p:LiteApiUrl=…) is written into the published appsettings.json.
const settings = JSON.parse(readFileSync(file('appsettings.json'), 'utf8'));
if (process.argv[3]) {
  writeFileSync(file('appsettings.json'), JSON.stringify({ ...settings, Api: { ...settings.Api, Url: process.argv[3] } }, null, 2));
  dropCompressed('appsettings.json');
}
const api = process.argv[3] || settings.Api?.Url;
if (!api) throw new Error('No Api:Url (Abastalia.Host.Lite): publish with -p:LiteApiUrl=https://… or set it in wwwroot/appsettings.json.');

// The browser hashes the inline import map with its line ends normalized to \n.
const config = readFileSync(file('staticwebapp.config.json'), 'utf8')
  .replace('{importmap}', sha(importMap.replace(/\r\n?/g, '\n'))).replace('{api}', new URL(api).origin);
writeFileSync(file('staticwebapp.config.json'), config);
dropCompressed('staticwebapp.config.json');
console.log(`Sealed ${root}: SRI on index.html, CSP for ${new URL(api).origin}.`);
