// Run against the current public stable release: node tools/release/verify_public.mjs 1.0.0
import assert from 'node:assert/strict';
import { createHash, verify } from 'node:crypto';
import { readFile } from 'node:fs/promises';

const version = process.argv[2];
assert.match(version ?? '', /^\d+\.\d+\.\d+$/, 'Supply the published stable version.');
const repo = 'Yafeiml/nodikarta';
const image = 'yafeiml/nodikarta-core';
const tag = 'v' + version;
const publicKey = await readFile(new URL('../../release/edge-update-public.pem', import.meta.url));

async function get(url, headers = {}) {
  const response = await fetch(url, {headers: {'User-Agent': 'Nodicarta-release-verification', ...headers},
    signal: AbortSignal.timeout(180_000)});
  if (!response.ok) throw new Error(`${new URL(url).origin}${new URL(url).pathname}: HTTP ${response.status}`);
  return response;
}

const release = await (await get(`https://api.github.com/repos/${repo}/releases/latest`)).json();
assert.equal(release.tag_name, tag);
assert.equal(release.draft, false);
assert.equal(release.prerelease, false);
const channel = `https://github.com/${repo}/releases/latest/download/nodicarta-edge-stable-win-x64.json`;
const signedBytes = Buffer.from(await (await get(channel)).arrayBuffer());
const signature = Buffer.from((await (await get(channel + '.sig')).text()).trim(), 'base64');
assert.equal(signature.length, 64);
assert.ok(verify('sha256', signedBytes, {key: publicKey, dsaEncoding: 'ieee-p1363'}, signature),
  'The stable channel must verify with the public release key.');
const descriptor = JSON.parse(signedBytes.toString('utf8'));
assert.equal(descriptor.schema, 1);
assert.equal(descriptor.product, 'nodicarta-edge');
assert.equal(descriptor.channel, 'stable');
assert.equal(descriptor.rid, 'win-x64');
assert.equal(descriptor.version, version);
const name = `Nodicarta-Edge-${version}-win-x64-setup.exe`;
assert.equal(descriptor.url, `https://github.com/${repo}/releases/download/${tag}/${name}`);
const asset = release.assets.find(item => item.name === name);
assert.equal(asset?.digest, 'sha256:' + descriptor.sha256);
assert.equal(asset?.size, descriptor.size);
const hash = createHash('sha256');
let bytes = 0;
for await (const chunk of (await get(descriptor.url)).body) {
  hash.update(chunk);
  bytes += chunk.length;
}
assert.equal(bytes, descriptor.size);
assert.equal(hash.digest('hex'), descriptor.sha256);
console.log(`Verified public installer and signed update channel: ${name} (${bytes} bytes).`);

// Anonymous registry access must work for the image actually documented in this tag.
const guide = await (await get(`https://raw.githubusercontent.com/${repo}/${tag}/deploy/public/init.sh`)).text();
const match = guide.match(/ghcr\.io\/yafeiml\/nodikarta-core:(\d+\.\d+\.\d+)/);
assert.ok(match, 'Deployment defaults must name a published Core version.');
const token = (await (await get(`https://ghcr.io/token?service=ghcr.io&scope=repository:${image}:pull`)).json()).token;
const manifestHeaders = {Authorization: 'Bearer ' + token, Accept: [
  'application/vnd.oci.image.manifest.v1+json', 'application/vnd.oci.image.index.v1+json',
  'application/vnd.docker.distribution.manifest.v2+json', 'application/vnd.docker.distribution.manifest.list.v2+json'
].join(', ')};
const base = `https://ghcr.io/v2/${image}`;
const response = await get(`${base}/manifests/${match[1]}`, manifestHeaders);
const digest = response.headers.get('docker-content-digest');
assert.match(digest ?? '', /^sha256:[a-f0-9]{64}$/);
let manifest = await response.json();
if (manifest.manifests) {
  const target = manifest.manifests.find(item => item.platform.os === 'linux' && item.platform.architecture === 'amd64');
  assert.ok(target, 'Core must include Linux amd64.');
  manifest = await (await get(`${base}/manifests/${target.digest}`, manifestHeaders)).json();
}
const config = await (await get(`${base}/blobs/${manifest.config.digest}`, {Authorization: 'Bearer ' + token})).json();
assert.equal(config.os, 'linux');
assert.equal(config.architecture, 'amd64');
assert.equal(config.config.Labels['org.opencontainers.image.version'], match[1]);
console.log(`Verified anonymous Linux amd64 manifest: ghcr.io/${image}@${digest}.`);
