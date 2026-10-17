// node --test: TK-321, the selling device (device.js): pairing with a non-extractable P-256 key and requests signed in ADR-0008's node format.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, webcrypto } from 'node:crypto';
import { pairDevice, signRequest, deviceFetch, forgetDevice, clockOffset, memoryStore, DeviceMissing, PairingFailed } from '../index.js';

const { subtle } = webcrypto;
const ECDSA = { name: 'ECDSA', namedCurve: 'P-256', hash: 'SHA-256' };
const DEVICE = '0199e5a0-1111-7000-8000-000000000001';
const fromB64Url = s => Buffer.from(s.replace(/-/g, '+').replace(/_/g, '/'), 'base64');
const sha256 = s => createHash('sha256').update(s).digest('base64url');

/** A fake host: answers the pairing and keeps what it got. */
function host(answer = { status: 200, body: { deviceId: DEVICE } }) {
  const calls = [];
  const fetch = async (url, init) => {
    calls.push({ url, ...init });
    return new Response(JSON.stringify(answer.body), { status: answer.status, headers: { 'Content-Type': 'application/json' } });
  };
  return { fetch, calls };
}

test('pairing sends the code and the public key, and keeps a non-extractable private key', async () => {
  const store = memoryStore();
  const { fetch, calls } = host();
  assert.deepEqual(await pairDevice({ baseUrl: 'https://lite.test', code: '1234 5678', name: 'Celular', store, fetch }), { deviceId: DEVICE });

  assert.equal(calls[0].url, 'https://lite.test/lite/devices/pair');
  const sent = JSON.parse(calls[0].body);
  assert.equal(sent.code, '12345678');
  assert.equal(sent.name, 'Celular');
  const device = await store.getDevice();
  assert.equal(device.deviceId, DEVICE);
  assert.equal(device.key.extractable, false);
  await assert.rejects(subtle.exportKey('pkcs8', device.key));
  // The public key the host got verifies what the kept key signs.
  const publicKey = await subtle.importKey('spki', Buffer.from(sent.publicKey, 'base64'), ECDSA, false, ['verify']);
  const signature = await subtle.sign(ECDSA, device.key, new TextEncoder().encode('x'));
  assert.ok(await subtle.verify(ECDSA, publicKey, signature, new TextEncoder().encode('x')));
});

test('a refused code keeps the device there was and says why', async () => {
  const store = memoryStore();
  await store.putDevice({ deviceId: 'old' });
  const { fetch } = host({ status: 409, body: { title: 'Lite.DeviceLimit', detail: 'Este negocio ya tiene un dispositivo de venta.' } });
  const error = await pairDevice({ baseUrl: '', code: '12345678', store, fetch }).catch(e => e);
  assert.ok(error instanceof PairingFailed);
  assert.equal(error.code, 'Lite.DeviceLimit');
  assert.equal(error.status, 409);
  assert.match(error.message, /ya tiene un dispositivo/);
  assert.equal((await store.getDevice()).deviceId, 'old');
});

test('a request is signed over id, time, nonce, method, path and the body hash', async () => {
  const keys = await subtle.generateKey(ECDSA, false, ['sign', 'verify']);
  const body = '{"code":"dueno","pin":"1234"}';
  const now = Date.UTC(2026, 9, 17, 12, 0, 0);
  const a = await signRequest({ deviceId: DEVICE, key: keys.privateKey }, 'post', '/api/identity/sessions?x=1', body, { now });
  const b = await signRequest({ deviceId: DEVICE, key: keys.privateKey }, 'post', '/api/identity/sessions?x=1', body, { now });

  assert.equal(a['X-Body-Sha256'], sha256(body));
  const [id, seconds, nonce, signature] = a['X-Device-Signature'].split('.');
  assert.equal(id, DEVICE);
  assert.equal(seconds, String(now / 1000));
  assert.ok(nonce.length >= 16 && nonce.length <= 64);
  assert.notEqual(nonce, b['X-Device-Signature'].split('.')[2]); // a new nonce each time
  assert.equal(fromB64Url(signature).length, 64); // IEEE P1363 r‖s, what .NET's VerifyData takes
  const payload = text => new TextEncoder().encode(text);
  const expected = `${DEVICE}.${seconds}.${nonce}.POST /api/identity/sessions?x=1 ${sha256(body)}`;
  assert.ok(await subtle.verify(ECDSA, keys.publicKey, fromB64Url(signature), payload(expected)));
  assert.ok(!await subtle.verify(ECDSA, keys.publicKey, fromB64Url(signature), payload(expected.replace('POST', 'GET'))));

  // No body: the hash of nothing, as the host computes it for a GET.
  const get = await signRequest({ deviceId: DEVICE, key: keys.privateKey }, 'GET', '/api/identity/me', undefined, { now });
  assert.equal(get['X-Body-Sha256'], '47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU');
});

test('deviceFetch signs what it sends and needs a paired device', async () => {
  const store = memoryStore();
  const { fetch, calls } = host();
  const send = deviceFetch({ baseUrl: 'https://lite.test', store, fetch });
  await assert.rejects(send('/api/identity/me'), DeviceMissing);

  await pairDevice({ baseUrl: 'https://lite.test', code: '12345678', store, fetch });
  await send('/api/sales', { method: 'POST', body: '{"a":1}', headers: { Authorization: 'Bearer t' } });
  const call = calls.at(-1);
  assert.equal(call.url, 'https://lite.test/api/sales');
  assert.equal(call.method, 'POST');
  assert.equal(call.headers.Authorization, 'Bearer t');
  assert.equal(call.headers['X-Body-Sha256'], sha256('{"a":1}'));
  assert.match(call.headers['X-Device-Signature'], new RegExp(`^${DEVICE}\\.\\d+\\.[\\w-]+\\.[\\w-]+$`));

  await forgetDevice(store);
  await assert.rejects(send('/api/identity/me'), DeviceMissing);
});

test('TK-324: the clock offset comes from the Date header, centered on the round trip', () => {
  const host = Date.UTC(2026, 9, 17, 12, 0, 0);
  assert.equal(clockOffset(new Date(host).toUTCString(), host - 20 * 60_000 - 100, host - 20 * 60_000 + 100), 20 * 60_000 + 500);
  assert.equal(clockOffset(null, 0, 10), null);
  assert.equal(clockOffset('no es una fecha', 0, 10), null);
});

test('TK-324: a phone 20 minutes behind signs with the host time it learned from /health', async () => {
  const store = memoryStore();
  const hostNow = Date.UTC(2026, 9, 17, 12, 0, 0);
  const calls = [];
  const fetch = async (url, init = {}) => {
    calls.push({ url, ...init });
    const body = url.endsWith('/pair') ? { deviceId: DEVICE } : {};
    return new Response(JSON.stringify(body), { headers: { Date: new Date(hostNow).toUTCString() } });
  };
  await pairDevice({ baseUrl: 'https://lite.test', code: '12345678', store, fetch });
  const send = deviceFetch({ baseUrl: 'https://lite.test', store, fetch, now: () => hostNow - 20 * 60_000 });
  await send('/api/identity/me');
  await send('/api/identity/me');

  assert.deepEqual(calls.slice(1).map(c => c.url), ['https://lite.test/health', 'https://lite.test/api/identity/me', 'https://lite.test/api/identity/me']);
  assert.equal(calls[1].headers, undefined); // /health goes unsigned
  for (const c of calls.slice(2)) {
    const seconds = Number(c.headers['X-Device-Signature'].split('.')[1]);
    assert.ok(Math.abs(seconds * 1000 - hostNow) <= 1000, `signed at ${new Date(seconds * 1000).toISOString()}`);
  }
});

test('TK-324: refused while the clock was off, it signs once more with the corrected time', async () => {
  const store = memoryStore();
  const hostNow = Date.UTC(2026, 9, 17, 12, 0, 0);
  let date = null; // the first answer carries no Date (a proxy dropped it), the next ones the host's
  const signedAt = [];
  const fetch = async (url, init = {}) => {
    if (url.endsWith('/pair')) return new Response(JSON.stringify({ deviceId: DEVICE }));
    const signature = init.headers?.['X-Device-Signature'];
    if (signature) signedAt.push(Number(signature.split('.')[1]) * 1000);
    const headers = date ? { Date: date } : {};
    date = new Date(hostNow).toUTCString();
    const tooFar = signature && Math.abs(signedAt.at(-1) - hostNow) > 15 * 60_000;
    return new Response('{}', { status: tooFar ? 401 : 200, headers });
  };
  await pairDevice({ baseUrl: '', code: '12345678', store, fetch });
  const response = await deviceFetch({ baseUrl: '', store, fetch, now: () => hostNow - 30 * 60_000 })('/api/identity/me');
  assert.equal(response.status, 200);
  assert.equal(signedAt.length, 2);
  assert.ok(Math.abs(signedAt[1] - hostNow) <= 1000);
});
