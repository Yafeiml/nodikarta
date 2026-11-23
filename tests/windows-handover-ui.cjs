// Optional desktop checks for windows-device-handover.cjs. Both windows use the disposable Core.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { once } = require('node:events');

async function child(file, args, input) {
    const process = spawn(file, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let output = '', error = '';
    process.stdout.on('data', b => output += b);
    process.stderr.on('data', b => error += b);
    const closed = once(process, 'close');
    process.stdin.end(input + '\n');
    const [code] = await closed;
    assert.equal(code, 0, error.slice(-2000));
    return output;
}

exports.run = async ({ proxy, add, update, temp }) => {
    const desktop = path.resolve(process.env.PROJECTHUB_HANDOVER_UI);
    const old = path.join(temp, 'desktop-old');
    const executable = path.join(path.dirname(desktop), 'ProjectHub.Edge.exe');
    const device = JSON.parse(await child(executable, ['--state', old, 'pair', '--core', proxy.url], await add()));
    const code = await update(device.deviceId);
    const allow = path.join(temp, 'desktop-confirm-enabled');
    let interrupted = false;
    proxy.before = action => {
        if (action === 'confirm' && !fs.existsSync(allow)) { interrupted = true; return true; }
        return false;
    };
    try {
        await child('pwsh.exe', ['-NoProfile', '-File', path.join(__dirname, 'windows-handover-ui.ps1'), '-Executable', desktop,
            '-Fixture', temp], JSON.stringify({ code, core: proxy.url }));
        assert(interrupted, 'Expected a GUI confirmation request before restarting the desktop.');
    } finally { proxy.before = undefined; }
};
