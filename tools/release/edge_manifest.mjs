// The Windows signing key stays in CNG. An isolated private rehearsal gets its own temporary key.
import assert from 'node:assert/strict';
import { generateKeyPairSync, sign, verify } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';

const [operation, manifest, signature, publicKey] = process.argv.slice(2);
const bytes = readFileSync(manifest);
if (operation === 'sign-sandbox') {
  assert.equal(JSON.parse(bytes).channel, 'rehearsal');
  const key = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  writeFileSync(publicKey, key.publicKey.export({ type: 'spki', format: 'pem' }), { flag: 'wx' });
  const signed = sign('sha256', bytes, { key: key.privateKey, dsaEncoding: 'ieee-p1363' });
  writeFileSync(signature, signed.toString('base64') + '\n', { flag: 'wx' });
} else {
  assert.equal(operation, 'verify');
}
const raw = Buffer.from(readFileSync(signature, 'ascii').trim(), 'base64');
assert.equal(raw.length, 64);
assert(verify('sha256', bytes, { key: readFileSync(publicKey), dsaEncoding: 'ieee-p1363' }, raw),
  'Signature does not match the local trust key and exact manifest bytes');
