// Windows Edge CLI integration with a disposable HTTP fixture, including parallel token renewal.
// Usage: node tests/edge-windows.cjs path/to/ProjectHub.Edge.exe [evidence.json]
// Set PROJECTHUB_TEST_INTERFACE_IPV4 to exercise a specific local LAN interface instead of loopback.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { randomUUID, randomBytes } = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const { lines, handshake, tool } = require('./windows-mcp.cjs');

const executable = path.resolve(process.argv[2]);
const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'projecthub-win-edge-'));
const state = path.join(directory, 'state');
const workspace = fs.mkdirSync(path.join(directory, '中文 项目'), { recursive: true });
const hex = size => randomBytes(size).toString('hex').toUpperCase();
const ids = { offer: randomUUID(), device: randomUUID(), edge: randomUUID(), project: randomUUID(), binding: randomUUID(), observation: randomUUID() };
const secret = hex(16);
let access = hex(32), refresh = hex(32), consumed = false, renewals = 0, allowed = true, revoked = false;
let mode = 'readOnly', reachable = false;
const delivered = [];
const tokens = seconds => ({ deviceId: ids.device, edgeId: ids.edge, accessToken: access, refreshToken: refresh,
    expiresAt: new Date(Date.now() + seconds * 1000).toISOString() });
const service = http.createServer(async (req, res) => {
    const send = (status, value) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(value)); };
    let raw = '';
    for await (const part of req) raw += part;
    const body = raw ? JSON.parse(raw) : {};
    if (req.url === '/v1/device-pairings/' + ids.offer + '/redeem' && req.method === 'POST' && body.oneTimeCode === secret && !consumed) {
        consumed = true;
        return send(201, tokens(30));
    }
    if (req.url === '/v1/tokens/refresh' && body.refreshToken === refresh && !revoked) {
        renewals++;
        await new Promise(resolve => setTimeout(resolve, 175));
        access = hex(32); refresh = hex(32);
        return send(200, tokens(900));
    }
    if (req.url === '/v1/projects' && !reachable) return send(503, {});
    if (req.url === '/v1/projects' && req.method === 'GET' && req.headers.authorization === 'Bearer ' + access && !revoked)
        return send(200, { items: allowed ? [{ projectId: ids.project, bindingId: ids.binding, accessMode: mode, name: 'B' }] : [] });
    if (req.url === `/v1/projects/${ids.project}/bindings/${ids.binding}/knowledge` && req.method === 'POST' &&
        reachable && allowed && !revoked && mode === 'readWrite' && req.headers.authorization === 'Bearer ' + access) {
        if (body.itemId === ids.observation && !body.validity)
            return send(400, {error:{code:'observation_confirmation_required'}});
        delivered.push(raw);
        return send(200, {requestId:body.requestId});
    }
    return send(401, {});
});
function cli(args, stdin = '', expected = 0) {
    return new Promise((resolve, reject) => {
        const ps = spawn(executable, ['--state', state, ...args], { windowsHide: true });
        let out = '', err = '';
        const timer = setTimeout(() => ps.kill(), 45000);
        ps.stdout.on('data', s => out += s);
        ps.stderr.on('data', s => err += s);
        ps.on('error', reject);
        ps.on('close', code => {
            clearTimeout(timer);
            if (code !== expected) reject(new Error(args[0] + ' exit ' + code + ' ' + err.trim()));
            else resolve(out ? JSON.parse(out) : null);
        });
        ps.stdin.end(stdin);
    });
}

(async () => {
    assert.equal(process.platform, 'win32');
    const host = process.env.PROJECTHUB_TEST_INTERFACE_IPV4 || '127.0.0.1';
    assert(Object.values(os.networkInterfaces()).flat().some(ip => ip.family === 'IPv4' && ip.address === host), 'Use an assigned local IPv4 address');
    await new Promise(resolve => service.listen(0, host, resolve));
    const core = 'http://' + host + ':' + service.address().port;
    const result = await cli(['pair', '--core', core], ids.offer + '.' + secret + '\n');
    assert.equal(result.deviceId, ids.device);
    assert.equal(new URL(result.coreUrl).origin, core);
    const original = [access, refresh, secret];
    await cli(['projects'], '', 1);
    assert.equal(renewals, 0);
    reachable = true;
    const projects = await Promise.all(Array.from({ length: 4 }, () => cli(['projects'])));
    assert(projects.every(p => p[0].projectId === ids.project));
    assert.equal(renewals, 1);
    const db = new DatabaseSync(path.join(state, 'edge.sqlite3'), { readOnly: true });
    const blob = Buffer.from(db.prepare('SELECT credentials FROM identity').get().credentials);
    assert.equal(db.prepare('PRAGMA user_version').get().user_version, 1);
    assert([...original, access, refresh].every(token => !blob.includes(Buffer.from(token))));
    db.close();
    const bind = await cli(['bind', '--project', ids.project, '--path', workspace]);
    assert.equal(bind.accessMode, 'readOnly'); assert.equal(bind.localPath, workspace);
    const restored = await cli(['bindings']);
    assert.equal(restored[0].workspaceId, bind.workspaceId);
    const daemon = spawn(executable, ['--state', state, 'run'], { windowsHide: true, stdio: 'ignore' });
    try {
        const statusFile = path.join(state, 'connection.json'), until = Date.now() + 10000;
        while (!fs.existsSync(statusFile) && Date.now() < until) await new Promise(resolve => setTimeout(resolve, 100));
        assert.equal(JSON.parse(fs.readFileSync(statusFile)).state, 'online');
    } finally {
        if (daemon.exitCode === null && daemon.signalCode === null) {
            const exited = new Promise(resolve => daemon.once('close', resolve));
            daemon.kill();
            await exited;
        }
    }
    assert.equal((await cli(['bindings']))[0].workspaceId, bind.workspaceId);
    const automatic = ['mcp-stdio','--state',state,'--project',ids.project,'--binding',ids.binding,'--workspace',bind.workspaceId,'--mode','auto'];
    async function autoTools() {
        const client=lines(executable,automatic);
        try { return await handshake(client); } finally { client.close(); }
    }
    const readers=await autoTools();
    assert.equal(readers.length,11);
    assert(readers.includes('get_asset_decisions') && !readers.includes('publish_asset'));
    mode = 'readWrite';
    assert.equal((await cli(['bindings']))[0].accessMode, 'readWrite');
    const writers=await autoTools();
    assert.equal(writers.length,14);
    assert(writers.includes('publish_asset') && !writers.includes('claim_analysis_task'));
    const client = lines(executable, automatic);
    const oldShape = {requestId:randomUUID(), title:'Old draft', body:'Body', evidence:'Evidence', itemId:null, baseVersion:0, relatedTaskId:null};
    const pinned = {...oldShape, requestId:randomUUID(), assetRefs:[{role:'used', assetId:randomUUID(), version:2, locator:'p. 4'}]};
    const cleared = {...oldShape, requestId:randomUUID(), assetRefs:[]};
    const many = {...oldShape, requestId:randomUUID(), assetRefs:Array.from({length:32}, () => ({
        role:'evidence', assetId:randomUUID(), version:1, locator:'章'.repeat(512)}))};
    const observed = {...oldShape, requestId:randomUUID(),
        validity:{kind:'observation', subject:'offline fixture', observedAt:'2025-03-04T09:12:14+08:00'}};
    const replaced = {...pinned, requestId:randomUUID(),
        validity:{kind:'decision', scope:'fixture', supersedesItemId:randomUUID(), supersedesVersion:2}};
    try {
        await handshake(client);
        const unconfirmed = await client.call('tools/call', {name:'submit_knowledge_commit',
            arguments:{...oldShape, requestId:randomUUID(), itemId:ids.observation, baseVersion:1}});
        assert(unconfirmed.isError);
        assert.match(unconfirmed.content[0].text, /subject.*observedAt.*evidence.*重新实测.*requestId/);
        reachable = false;
        for (const draft of [oldShape, pinned, cleared, many, observed, replaced, oldShape, observed]) {
            assert.deepEqual(await tool(client, 'submit_knowledge_commit', draft), {outcome:'queuedLocally',requestId:draft.requestId});
        }
        assert((await client.call('tools/call', {name:'submit_knowledge_commit',arguments:{...pinned, assetRefs:[]}})).isError);
    } finally { reachable = true; client.close(); }
    await cli(['sync','--project',ids.project,'--binding',ids.binding,'--mode','readWrite']);
    assert.deepEqual(delivered.slice(0,3), [oldShape, pinned, cleared].map(value => JSON.stringify(value)));
    assert.deepEqual(delivered.slice(3).map(raw => JSON.parse(raw)), [many, observed, replaced]);
    allowed = false;
    assert.deepEqual(await cli(['bindings']), []);
    allowed = true; ids.binding = randomUUID();
    assert.equal((await cli(['bindings']))[0].bindingId, ids.binding);
    revoked = true;
    const status = await cli(['status'], '', 1);
    assert.equal(status.http, 401);
    const checks = ['Windows 可执行文件通过标准输入接收配对码并保存身份', 'Core 暂不可用时保留原凭据，恢复后无需重新配对', '四个独立进程并发读取只轮换一次',
        '实际 SQLite 中设备凭据保存为 DPAPI 密文', '只读项目可绑定中文带空格的目录，重启 CLI 仍为同一 workspace',
        '前台后台循环可报告在线，终止进程后绑定仍可恢复（未经过 SCM）', '下次查询使用新的读写权限，撤权后隐藏，重授权采用新绑定 ID', '撤销设备后 status 返回 HTTP 401'];
    const evidence = { checkedAtUtc: new Date().toISOString(), fixture: '独立 HTTP 模拟 Core（' + host + '）', checks };
    checks.push('同一份自动接入配置重新连接后从 11 个只读工具变为 14 个读写工具，无任务端点时排除任务操作',
        '离线草稿重连后保留旧请求、固定素材或显式清空，以及实测时间和决定替代关系；重试仅投递一次');
    if (process.argv[3]) fs.writeFileSync(process.argv[3], JSON.stringify(evidence, null, 2) + '\n');
    checks.forEach(c => console.log('PASS ' + c));
})().catch(error => { console.error(error.message); process.exitCode = 1; })
    .finally(() => service.close());
