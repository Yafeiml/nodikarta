// node tests/windows-native-launcher.cjs <baseline Native AOT bin> <candidate Native AOT bin> [evidence.json]
const assert = require('node:assert/strict');
const {createHash} = require('node:crypto');
const {spawnSync} = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {setTimeout: delay} = require('node:timers/promises');
const {lines, handshake} = require('./windows-mcp.cjs');

async function verify() {
    const [oldBin, nativeBin, evidence] = process.argv.slice(2);
    assert(oldBin && nativeBin, 'Two extracted Native AOT Windows bundles are required');
    const scratch = fs.realpathSync(os.tmpdir());
    const root = fs.mkdtempSync(path.join(scratch, 'nodicarta-native-launcher-'));
    const installed = path.join(root, 'installed');
    const launcher = path.join(installed, 'ProjectHub.Edge.exe');
    const lease = path.join(installed, '.edge-lease');
    const parkedLease = lease + '.test-hold';
    const journal = path.join(installed, '.nodicarta-install-transaction.json');
    const nativeCli = path.join(nativeBin, 'ProjectHub.Edge.exe');
    const connections = [];
    const expected = ['get_edge_status'];
    const digest = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
    function stage(source) {
        for (const required of ['ProjectHub.Edge.exe', 'ProjectHub.Edge.Desktop.exe', 'NodicartaEdgeCli.dll']) {
            assert(fs.statSync(path.join(source, required)).isFile(), `Missing ${required}`);
        }
        for (const obsolete of ['coreclr.dll', 'hostfxr.dll', 'ProjectHub.Edge.dll', 'Nodicarta.Edge.Launcher.exe']) {
            assert(!fs.existsSync(path.join(source, obsolete)), `Unexpected ${obsolete}`);
        }
        fs.cpSync(source, installed, {recursive: true});
        fs.copyFileSync(path.resolve(source, '..', 'SHA256SUMS'), path.join(installed, 'SHA256SUMS'));
        fs.closeSync(fs.openSync(lease, 'a'));
        assert(!fs.existsSync(path.join(installed, 'bin')));
        assert(!fs.existsSync(path.join(installed, 'active-release')));
        assert(!fs.readdirSync(installed, {withFileTypes:true}).some(entry => entry.isDirectory() && /^[0-9a-f]{12}$/.test(entry.name)));
    }
    async function connect(exe) {
        const c = lines(exe, ['mcp-stdio-global', '--state', path.join(root, '隔离 state')], {cwd:root});
        connections.push(c);
        assert.deepEqual(await handshake(c), expected);
        return c;
    }
    async function alive(c) {
        const r = await c.call('tools/list', {});
        assert.deepEqual(r.tools.map(t => t.name), expected);
    }
    function version(exe) {
        const result = spawnSync(exe, ['--version'], {encoding: 'utf8', windowsHide: true, timeout: 10000});
        assert.equal(result.status, 0, result.stderr || result.error?.message);
        assert.match(result.stdout.trim(), /^\d+\.\d+\.\d+$/);
        return result.stdout.trim();
    }
    function expectFailure(exe, args, message) {
        const result = spawnSync(exe, args, {encoding:'utf8', windowsHide:true, timeout:10000});
        assert.equal(result.status, 1, result.error?.message ?? result.stdout);
        assert.match(result.stderr, message);
        assert.equal(result.stdout, '');
    }
    function renameBlocked() {
        assert.throws(() => fs.renameSync(lease, parkedLease), error => ['EPERM', 'EACCES', 'EBUSY'].includes(error.code));
    }
    async function waitRelease() {
        const deadline = Date.now() + 5000;
        for (;;) {
            try { fs.renameSync(lease, parkedLease); return; }
            catch (error) {
                if (!['EPERM', 'EACCES', 'EBUSY'].includes(error.code) || Date.now() >= deadline) throw error;
                await delay(50);
            }
        }
    }
    try {
        stage(oldBin);
        const baselineVersion = version(launcher);
        assert.equal(baselineVersion, version(path.join(oldBin, 'ProjectHub.Edge.exe')));
        const old = await connect(launcher);
        const peer = await connect(launcher);
        renameBlocked();
        peer.close();
        renameBlocked();
        await alive(old);

        old.close();
        await waitRelease();
        expectFailure(launcher, ['--version'], /installation lease is missing/i);
        fs.renameSync(parkedLease, lease);
        fs.writeFileSync(journal, '{}');
        expectFailure(launcher, ['--version'], /setup has not finished/i);
        fs.unlinkSync(journal);

        stage(nativeBin);
        const native = await connect(launcher);
        const direct = await connect(nativeCli);
        const candidateVersion = version(launcher);
        assert.equal(candidateVersion, version(nativeCli));
        assert.notEqual(candidateVersion, baselineVersion, 'Use two distinct package versions');
        assert(!fs.existsSync(path.join(nativeBin, '.edge-lease')), 'The portable launcher did not modify its package');
        assert.equal(digest(launcher), digest(nativeCli));
        renameBlocked();
        await alive(native);
        await alive(direct);
        const receipt = {baselineVersion, candidateVersion,
            launcherSha256: digest(launcher),
            nativeCliSha256:digest(path.join(nativeBin, 'NodicartaEdgeCli.dll')),
            initializedThroughFixedLauncher:true, portableLauncherVerified:true,
            multipleReadersShareLease:true, installationLeaseHeldUntilCliExit:true,
            incompleteInstallRejected:true, freshSessionAfterFlatReplacement:true};
        if (evidence) fs.writeFileSync(evidence, JSON.stringify(receipt, null, 2) + '\n');
        console.log('PASS: fixed flat and portable Native AOT launchers, shared MCP leases and restart after replacement.');
    } finally {
        for (const c of connections) c.close();
        const realRoot = fs.realpathSync(root);
        assert.equal(path.dirname(realRoot), scratch);
        assert.match(path.basename(realRoot), /^nodicarta-native-launcher-/);
        fs.rmSync(realRoot, {recursive:true, force:true, maxRetries:20, retryDelay:100});
    }
}
verify().catch(error => { console.error(error.message); process.exitCode = 1; });
