// node tests/windows-auto-update.cjs <1.0.0 release directory> <1.0.1 release directory> <compiled loopback feed URL> <evidence.json> [--recovery]
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { randomBytes, randomUUID } = require('node:crypto');
const { once } = require('node:events');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');

async function main() {
    const [oldDir, nextDir, address, evidence, option] = process.argv.slice(2);
    const recovery = option === '--recovery';
    assert.ok(!option || recovery);
    const feed = new URL(address);
    assert.equal(feed.hostname, '127.0.0.1');
    assert.equal(feed.protocol, 'http:');
    const read = (dir, extension = '') => fs.readFileSync(path.join(dir, 'nodicarta-edge-stable-win-x64.json' + extension));
    const previous = JSON.parse(read(oldDir)), next = JSON.parse(read(nextDir));
    assert.equal(new URL(next.url).origin, feed.origin);
    const oldSetup = path.resolve(oldDir, path.basename(new URL(previous.url).pathname));
    const newSetup = path.resolve(nextDir, path.basename(new URL(next.url).pathname));
    const manifest = read(nextDir), signature = read(nextDir, '.sig');
    const badSignature = Buffer.from(signature.toString('ascii').trim(), 'base64');
    assert.equal(badSignature.length, 64);
    badSignature[0] ^= 1;
    const fixture = fs.mkdtempSync(path.join(os.tmpdir(), 'nodicarta-auto-update-'));
    let assets = 0, invalid = 0, valid = 0, credentialsSeen = false;
    const updates = http.createServer((req, res) => {
        credentialsSeen ||= Boolean(req.headers.authorization || req.headers.cookie);
        const good = fs.existsSync(path.join(fixture, 'serve-valid'));
        if (req.url === feed.pathname) {
            fs.writeFileSync(path.join(fixture, 'last-probe'), new Date().toISOString());
            return res.end(manifest);
        }
        if (req.url === feed.pathname + '.sig') {
            if (good) valid++; else invalid++;
            res.end(good ? signature : badSignature.toString('base64'));
            fs.writeFileSync(path.join(fixture, 'signature-served'), good ? 'valid' : 'invalid');
            return;
        }
        if (req.url === new URL(next.url).pathname) {
            assets++;
            fs.writeFileSync(path.join(fixture, 'download-count'), String(assets));
            if (!good) fs.writeFileSync(path.join(fixture, 'unsafe-download'), 'unexpected');
            res.writeHead(200, { 'Content-Length': fs.statSync(newSetup).size });
            return fs.createReadStream(newSetup).pipe(res);
        }
        res.writeHead(404).end();
    });
    const offer = randomUUID(), code = randomBytes(16).toString('hex');
    const tokens = { deviceId: randomUUID(), edgeId: randomUUID(), accessToken: randomBytes(32).toString('hex'),
        refreshToken: randomBytes(32).toString('hex'), expiresAt: new Date(Date.now() + 3600000).toISOString() };
    const project = { projectId: randomUUID(), bindingId: randomUUID(), name: 'Auto update fixture', accessMode: 'readWrite' };
    let paired = false, authCalls = 0;
    const core = http.createServer(async (req, res) => {
        function send(status, value) { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); }
        if (req.method === 'POST' && req.url === '/v1/device-pairings/' + offer + '/redeem' && !paired) {
            let body = '';
            for await (const b of req) body += b;
            if (JSON.parse(body).oneTimeCode !== code) return send(403, {});
            paired = true;
            return send(201, tokens);
        }
        if (req.url === '/v1/projects' && req.headers.authorization === 'Bearer ' + tokens.accessToken) {
            authCalls++;
            return send(200, { items: [project] });
        }
        send(404, {});
    });
    try {
        updates.listen(Number(feed.port), feed.hostname);
        await once(updates, 'listening');
        core.listen(0, '127.0.0.1');
        await once(core, 'listening');
        fs.writeFileSync(path.join(fixture, 'pairing.txt'), offer + '.' + code);
        const child = spawn('pwsh.exe', ['-NoProfile', '-File', path.join(__dirname, 'windows-auto-update.ps1'),
            '-OldSetup', oldSetup, '-OldVersion', previous.version, '-NewVersion', next.version, '-Root', fixture,
            '-Core', 'http://127.0.0.1:' + core.address().port, '-ProjectId', project.projectId, ...(recovery ? ['-Recovery'] : [])],
            { windowsHide: true, stdio: ['ignore','inherit','inherit'] });
        const [exit] = await once(child, 'exit');
        assert.equal(exit, 0, 'Isolated desktop installation failed');
        assert.ok(valid >= 1 && paired && authCalls >= 2);
        if (!recovery) assert.ok(invalid >= 1 && assets === 1);
        assert.equal(credentialsSeen, false);
        const report = JSON.parse(fs.readFileSync(path.join(fixture, 'verified.json')));
        Object.assign(report, { invalidSignaturesServed: invalid, validSignaturesServed: valid,
            actualInstallerDownloads: assets, noCoreCredentialsToFeed: !credentialsSeen });
        fs.mkdirSync(path.dirname(path.resolve(evidence)), { recursive:true });
        fs.writeFileSync(evidence, JSON.stringify(report, null, 2) + '\n');
    } finally {
        updates.closeAllConnections(); updates.close(); core.closeAllConnections(); core.close();
        // The PowerShell worker closes only its verified GUI. Never remove a live application's folder.
        if (!fs.existsSync(path.join(fixture, 'resident-live')) &&
            path.dirname(fs.realpathSync(fixture)) === fs.realpathSync(os.tmpdir()) &&
            path.basename(fixture).startsWith('nodicarta-auto-update-')) fs.rmSync(fixture, { recursive:true, force:true, maxRetries:4, retryDelay:300 });
    }
}
main().catch(e => { console.error(e.message); process.exitCode = 1; });
