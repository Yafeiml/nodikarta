// UUIDv5 scoped to the Core project, so repeated initialization can address the same topic and operation.
const { createHash } = require('node:crypto');

function uuidV5(namespace, name) {
    const bytes = createHash('sha1')
        .update(Buffer.from(namespace.replaceAll('-', ''), 'hex'))
        .update(name, 'utf8').digest().subarray(0, 16);
    bytes[6] = (bytes[6] & 0x0f) | 0x50;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    const hex = bytes.toString('hex');
    return [hex.slice(0,8), hex.slice(8,12), hex.slice(12,16), hex.slice(16,20), hex.slice(20)].join('-');
}

const [projectId, kind, key, baseVersion] = process.argv.slice(2);
if (process.argv.length !== 6 || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(projectId) ||
    !['knowledge', 'asset'].includes(kind) || !key.trim() || !/^(0|[1-9]\d*)$/.test(baseVersion)) {
    console.error('Usage: node init-ids.cjs <projectId> knowledge|asset <stable-source-key> <baseVersion>');
    process.exit(1);
}
// This namespace predates the product rename and preserves existing topic IDs.
const topicId = uuidV5(projectId, JSON.stringify(['projecthub-init', kind, key.normalize('NFC')]));
const requestId = uuidV5(topicId, 'version:' + baseVersion);
console.log(JSON.stringify(kind === 'knowledge' ? { itemId: topicId, requestId } : { requestId }));
