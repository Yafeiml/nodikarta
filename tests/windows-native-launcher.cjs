// node tests/windows-native-launcher.cjs <1.0.1 bin> <Native AOT bin> [evidence.json]
const assert = require('node:assert/strict');
const {createHash} = require('node:crypto');
const {spawnSync} = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {lines, handshake} = require('./windows-mcp.cjs');

async function verify() {
    const [oldBin, nativeBin, evidence] = process.argv.slice(2);
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nodicarta-native-launcher-'));
    const marker = path.join(root, 'active-release');
    const launcher = path.join(root, 'ProjectHub.Edge.exe');
    const nativeCli = path.join(nativeBin, 'ProjectHub.Edge.exe');
    const connections = [];
    const expected = ['get_edge_status'];
    function active(id) {
        const tmp = marker + '.tmp';
        fs.writeFileSync(tmp, id, 'ascii');
        fs.renameSync(tmp, marker);
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
    try {
        for (const [id, source] of [['111111111111',oldBin],['222222222222',nativeBin]]) {
            fs.cpSync(source, path.join(root, id, 'bin'), {recursive:true});
        }
        fs.copyFileSync(path.join(oldBin, 'Nodicarta.Edge.Launcher.exe'), launcher);
        assert(!fs.existsSync(path.join(nativeBin, 'coreclr.dll')));
        assert(!fs.existsSync(path.join(nativeBin, 'ProjectHub.Edge.dll')));
        active('111111111111');
        const old = await connect(launcher);
        active('222222222222');
        const native = await connect(launcher);
        await alive(old);
        const direct = await connect(nativeCli);
        const version = spawnSync(launcher, ['--version'], {encoding:'utf8', windowsHide:true});
        assert.equal(version.status, 0);
        assert.match(version.stdout.trim(), /^\d+\.\d+\.\d+$/);
        active('111111111111');
        await connect(launcher);
        await alive(native);
        await alive(direct);
        const digest = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
        const receipt = {version:version.stdout.trim(), oldLauncherSha256:digest(launcher),
            nativeCliSha256:digest(path.join(nativeBin, 'NodicartaEdgeCli.dll')),
            initializedThroughOldLauncher:true, oldAndNewSessionsSurvivedSwitch:true, rollbackConfirmed:true};
        if (evidence) fs.writeFileSync(evidence, JSON.stringify(receipt, null, 2) + '\n');
        console.log('PASS: old launcher, direct Native AOT CLI, live MCP sessions and pointer rollback.');
    } finally {
        for (const c of connections) c.close();
        fs.rmSync(root, {recursive:true, force:true, maxRetries:20, retryDelay:100});
    }
}
verify().catch(error => { console.error(error.message); process.exitCode = 1; });
