// Throwaway self-signed .p12 made at test time (never a real certificate, never committed).
import { generateKeyPairSync } from 'node:crypto';
import forge from 'node-forge';

/** @param {'aes256' | '3des'} algorithm  @returns {{ p12: Uint8Array, certPem: string }} */
export function testP12(password, algorithm = 'aes256') {
  const { privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const key = forge.pki.privateKeyFromPem(privateKey.export({ type: 'pkcs1', format: 'pem' }));
  const cert = forge.pki.createCertificate();
  cert.publicKey = forge.pki.setRsaPublicKey(key.n, key.e);
  cert.serialNumber = '01';
  cert.validity.notBefore = new Date(Date.now() - 86_400_000);
  cert.validity.notAfter = new Date(Date.now() + 365 * 86_400_000);
  const subject = [{ name: 'commonName', value: 'Firmante de Prueba' }, { name: 'serialNumber', value: '00112345678' }];
  cert.setSubject(subject);
  cert.setIssuer(subject);
  cert.sign(key, forge.md.sha256.create());
  const der = forge.asn1.toDer(forge.pkcs12.toPkcs12Asn1(key, [cert], password, { algorithm })).getBytes();
  return { p12: forge.util.binary.raw.decode(der), certPem: forge.pki.certificateToPem(cert) };
}
