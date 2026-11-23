// Windows integration test. Supply the accepted Core image (Ubuntu Docker) and Windows Edge CLI:
// node tests/windows-device-handover.cjs <core-image> <ProjectHub.Edge.exe> [evidence.json]
// Uses the same Playwright dependencies as m1.2.cjs. Creates a private database, role and container.
const assert = require('node:assert/strict');
const { spawn, spawnSync } = require('node:child_process');
const { createHash, randomBytes, randomUUID } = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { chromium } = require('playwright-core');
const { visit, setup, login, submit, projectId, openDeviceAddition, openProjectCreation } = require('./core-fixture.cjs');

const [image, edge, evidence] = process.argv.slice(2);
assert(process.platform === 'win32' && image && edge && fs.existsSync(edge), 'Supply Core image and Windows Edge CLI.');
const name = 'phhandover_' + randomBytes(6).toString('hex');
const pg = process.env.PROJECTHUB_TEST_POSTGRES_CONTAINER || 'dev-postgresql';
const network = process.env.PROJECTHUB_TEST_DOCKER_NETWORK || 'shared-services';
const distro = process.env.PROJECTHUB_WSL_DISTRO || 'Ubuntu-26.04-LTS';
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'nodicarta-handover-'));
const checks = [];
function pass(check, detail = {}) {
    checks.push({ check, ...detail });
    console.log('PASS', check);
}
function docker(args, input) {
    const cmd = spawnSync('wsl.exe', ['-d', distro, '-u', 'root', '--', 'docker', ...args],
        { windowsHide: true, timeout: 45000, input, encoding: 'utf8' });
    if (cmd.status !== 0) throw Error('Disposable fixture Docker operation failed: ' + args[0] + ' (' + cmd.status + ')');
    return cmd.stdout.trim();
}
function sql(database, statement) {
    return docker(['exec', '-i', '-u', 'postgres', pg, 'psql', '-XAtq', '-v', 'ON_ERROR_STOP=1', '-d', database], statement);
}
function profile(label) { return path.join(temp, label); }
function cli(state, args, stdin = '') {
    const ps = spawn(path.resolve(edge), ['--state', state, ...args], { windowsHide: true });
    let out = '', err = '';
    ps.stdout.on('data', part => out += part);
    ps.stderr.on('data', part => err += part);
    ps.stdin.on('error', () => {});
    ps.stdin.end(stdin);
    const result = new Promise((resolve, reject) => {
        const timer = setTimeout(() => { ps.kill(); reject(Error('Timed out: ' + args[0])); }, 45000);
        ps.on('error', reject);
        ps.on('close', code => { clearTimeout(timer); resolve({ code, out, err }); });
    });
    return result;
}
async function success(work) {
    const result = await work;
    // Do not include raw stdout in assertions: an unexpected response can contain credentials.
    assert.equal(result.code, 0, 'Expected CLI success: ' + result.err.slice(0, 180));
    return JSON.parse(result.out);
}

// Forward only to this run's Core. Save received credentials in memory for authorization assertions.
// Delays and connection loss occur at exact request boundaries while the real client uses its own store.
async function interpose(core) {
    const tokens = new Map();
    const calls = [];
    const proxy = { tokens, calls, before: undefined, after: undefined };
    const server = http.createServer(async (req, res) => {
        try {
            let raw = '';
            for await (const part of req) raw += part;
            const route = req.url;
            const action = route.split('/').pop();
            if (await proxy.before?.(action)) { res.destroy(); return; }
            const response = await fetch(new URL(route, core), {
                method: req.method, headers: { 'content-type': 'application/json',
                    ...(req.headers.authorization && { authorization: req.headers.authorization }) }, body: raw || undefined
            });
            const bytes = Buffer.from(await response.arrayBuffer());
            calls.push({ action, status: response.status });
            if (['redeem', 'claim', 'confirm'].includes(action) && response.ok) {
                const identity = JSON.parse(bytes);
                tokens.set(identity.deviceId, identity);
            }
            if (await proxy.after?.(action)) { res.destroy(); return; }
            res.writeHead(response.status, { 'content-type': 'application/json' });
            res.end(bytes);
        } catch { res.destroy(); }
    });
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', resolve); });
    return Object.assign(proxy, { url: 'http://127.0.0.1:' + server.address().port, close: () => server.close() });
}
const pair = (state, offer, proxy, ...flags) => cli(state, ['pair', '--core', proxy.url, '--label', 'Windows QA', ...flags], offer + '\n');
function ids(code) {
    const fields = code.split('.');
    const id = fields.at(-2);
    assert.match(id, /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i);
    assert(fields.at(-1).length >= 32);
    return { id, secret: fields.at(-1) };
}
async function add(page, grants) {
    const form = await openDeviceAddition(page);
    await form.locator('#device-label').fill('验收设备');
    for (const [id, mode] of grants) await form.locator('[name="grants[' + id + ']"]').selectOption(mode);
    assert.equal(await submit(page, '生成配对码', form), 200);
    return page.getByRole('dialog').locator('#pairing-code').inputValue();
}
async function openUpdate(page, device) {
    await page.goto('/devices');
    await page.locator('[data-open="update-' + device + '"]').click();
    return page.getByRole('dialog');
}
async function update(page, device) {
    const dialog = await openUpdate(page, device);
    await dialog.getByRole('button', { name: /^(?:重新)?生成更新码$/ }).click();
    const offer = await dialog.locator('#pairing-code').inputValue();
    assert(offer.startsWith('U1.'), 'Update must be distinct from ordinary device addition.');
    return offer;
}
async function grant(page, device, project, mode) {
    await page.goto('/devices/' + device);
    const form = page.locator('form[data-device-id="' + device + '"][data-project-id="' + project + '"]');
    await form.locator('[name=accessMode]').selectOption(mode);
    assert.equal(await submit(page, '保存权限', form), 200);
    await page.reload();
    assert.equal(await form.locator('[name=accessMode]').inputValue(), mode);
}
async function api(core, route, token, body, expected = 200) {
    const reply = await fetch(new URL('/v1' + route, core), {
        method: body === undefined ? 'GET' : 'POST',
        headers: { 'content-type': 'application/json', ...(token && { authorization: 'Bearer ' + token }) },
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    assert.equal(reply.status, expected, route + ' HTTP ' + reply.status);
    return reply.json();
}
const projectPath = p => '/projects/' + p.projectId + '/bindings/' + p.bindingId;

async function resumeWorker(state, expectedId) {
    const worker = spawn(path.resolve(edge), ['--state', state, 'run'], { windowsHide: true, stdio: 'ignore' });
    const stopped = new Promise((resolve, reject) => { worker.once('close', resolve); worker.once('error', reject); });
    try {
        const deadline = Date.now() + 20000;
        while (Date.now() < deadline) {
            const probe = await cli(state, ['status']);
            if (probe.code === 0) {
                const status = JSON.parse(probe.out);
                if (status.deviceId === expectedId && status.online === true) return;
            }
            await new Promise(resolve => setTimeout(resolve, 200));
        }
        throw Error('Starting the Edge background process did not resume the interrupted operation.');
    } finally {
        worker.kill();
        await stopped;
    }
}

async function exercise(page, core, proxy, a, b) {
    const local = profile('same-machine');
    const offer = await add(page, [[a, 'readWrite'], [b, 'readWrite']]);
    const first = await success(pair(local, offer, proxy));
    const original = proxy.tokens.get(first.deviceId);
    const before = await success(cli(local, ['projects']));
    assert.equal(before.length, 2);
    const oldA = before.find(p => p.projectId === a);
    const oldBase = projectPath(oldA);
    const entry = await api(core, oldBase + '/knowledge', original.accessToken,
        { requestId: randomUUID(), title: '设备更新前的记录', body: '旧身份发布', evidence: 'handover evidence' }, 201);
    const work = { requestId: randomUUID(), workspaceId: randomUUID(), repositoryIds: [] };
    await api(core, oldBase + '/workspaces', original.accessToken, work, 201);
    const endpoint = await api(core, oldBase + '/endpoints', original.accessToken, { requestId: randomUUID(), name: '旧机专用' }, 201);
    pass('网页添加设备，真实 Edge 配对并发布历史记录、登记目录和端点', { deviceId: first.deviceId });

    const toCancel = await update(page, first.deviceId);
    const unavailable = profile('cancelled-candidate');
    proxy.before = action => action === 'confirm';
    assert.notEqual((await pair(unavailable, toCancel, proxy)).code, 0);
    proxy.before = undefined;
    const pending = await openUpdate(page, first.deviceId);
    await pending.locator('[data-pairing-id="' + ids(toCancel).id + '"]').waitFor();
    assert.equal(await submit(page, '取消本次接替', pending), 200);
    assert.notEqual((await pair(unavailable, toCancel, proxy)).code, 0);
    assert.equal((await success(cli(local, ['projects']))).length, 2);
    pass('候选已领码时管理员取消，候选无法启用，原设备仍可访问');

    const timed = await update(page, first.deviceId);
    const expiryId = ids(timed).id;
    const duration = Number(sql(name, "SELECT EXTRACT(EPOCH FROM expires_at - now()) FROM projecthub.device_pairings WHERE id='" + expiryId + "';"));
    assert(duration > 540 && duration <= 600, 'Update code should expire in 10 minutes.');
    const expiring = profile('expiring-candidate');
    proxy.before = action => action === 'confirm';
    assert.notEqual((await pair(expiring, timed, proxy)).code, 0);
    proxy.before = undefined;
    const realExpiry = process.env.PROJECTHUB_HANDOVER_REAL_EXPIRY === '1';
    if (realExpiry) {
        const remaining = Number(sql(name, "SELECT EXTRACT(EPOCH FROM expires_at - now()) FROM projecthub.device_pairings WHERE id='" + expiryId + "';"));
        await new Promise(resolve => setTimeout(resolve, Math.max(0,remaining * 1000) + 1000));
    } else {
        assert.equal(sql(name, "UPDATE projecthub.device_pairings SET expires_at=now()-interval '1 second' WHERE id='" + expiryId + "' RETURNING id;"), expiryId);
    }
    assert.notEqual((await pair(expiring, timed, proxy)).code, 0);
    assert.equal((await success(cli(local, ['projects']))).length, 2);
    pass(realExpiry ? '实际等待 10 分钟自然过期后，已领码候选确认被拒且原设备有效'
        : '签发期限为 10 分钟，隔离库前移到期时间后，已领码候选确认被拒且原设备有效');

    const retired = profile('restored-old-profile');
    fs.renameSync(local, retired); // The application loses all state at its original path.
    const recovery = await update(page, first.deviceId);
    const legacy = ids(recovery);
    await api(core, '/device-pairings/' + legacy.id + '/redeem', null,
        { oneTimeCode: legacy.secret, instanceNonce: randomUUID(), deviceLabel: 'legacy' }, 401);
    proxy.before = action => action === 'confirm';
    assert.notEqual((await pair(local, recovery, proxy)).code, 0);
    proxy.before = undefined;
    const candidate = [...proxy.tokens.values()].at(-1);
    assert.notEqual(candidate.deviceId, first.deviceId);
    await api(core, '/projects', candidate.accessToken, undefined, 401);
    await api(core, '/tokens/refresh', null, { refreshToken: candidate.refreshToken }, 401);
    assert.equal((await success(cli(retired, ['projects']))).length, 2);
    await grant(page, first.deviceId, b, 'none');
    await grant(page, first.deviceId, a, 'readOnly');
    const second = await success(pair(local, recovery, proxy));
    assert.equal(second.deviceId, candidate.deviceId);
    const newList = await success(cli(local, ['projects']));
    assert.deepEqual(newList.map(p => [p.projectId, p.accessMode]), [[a, 'readOnly']]);
    assert.notEqual(newList[0].bindingId, oldA.bindingId);
    const nextBase = projectPath(newList[0]);
    const nextTokens = proxy.tokens.get(second.deviceId);
    await api(core, nextBase + '/workspaces', nextTokens.accessToken, work, 403);
    await api(core, '/projects/' + b, nextTokens.accessToken, undefined, 404);
    assert.deepEqual((await api(core, nextBase + '/workspaces', nextTokens.accessToken)).items, []);
    assert.deepEqual((await api(core, nextBase + '/knowledge/history?itemId=' + entry.itemId, nextTokens.accessToken)).items.map(x => [x.commitId,x.deviceId]), [[entry.commitId, first.deviceId]]);
    pass('同机数据移走，在原路径重新配对；新身份沿用确认时的授权，历史回执保留原作者，本机目录未继承',
        { successorId: second.deviceId, originalCommit: entry.commitId });

    assert.notEqual((await cli(retired, ['projects'])).code, 0);
    await api(core, '/projects', original.accessToken, undefined, 401);
    await api(core, '/tokens/refresh', null, { refreshToken: original.refreshToken }, 401);
    assert.equal((await success(cli(local, ['projects'])))[0].projectId, a);
    pass('原目录备份恢复上线，原访问和刷新凭证均为 401，已接替设备仍可读');

    await grant(page, second.deviceId, a, 'readWrite');
    const oldEndpointFeedback = { requestId: randomUUID(), content:'核对旧机端点', targetEndpointId: endpoint.endpointId };
    await api(core, nextBase + '/feedback', nextTokens.accessToken, oldEndpointFeedback, 404);
    assert(!(await api(core, nextBase + '/endpoints', nextTokens.accessToken)).items.some(e => e.endpointId === endpoint.endpointId));
    pass('旧设备端点已停止接受投递');

    // Independent installation with its own existing identity, e.g. another PC that already has an Edge.
    const otherInstall = profile('another-install');
    const separate = await success(pair(otherInstall, await add(page, [[b, 'readOnly']]), proxy));
    assert.notEqual(separate.deviceId, second.deviceId);
    assert.equal((await success(cli(local, ['projects'])))[0].projectId, a);
    const transfer = await update(page, second.deviceId);
    assert.notEqual((await pair(otherInstall, transfer, proxy)).code, 0, 'Existing local credentials require explicit replacement.');
    proxy.after = action => action === 'claim'; // Core records claimant, then the response is lost.
    assert.notEqual((await pair(otherInstall, transfer, proxy, '--replace')).code, 0);
    proxy.after = undefined;
    const recipient = [...proxy.tokens.values()].at(-1);
    assert.equal((await success(cli(otherInstall, ['projects'])))[0].projectId, b);
    assert.equal((await success(cli(local, ['projects'])))[0].projectId, a);
    await resumeWorker(otherInstall, recipient.deviceId); // No second pairing input.
    assert.deepEqual((await success(cli(otherInstall, ['projects']))).map(p => [p.projectId,p.accessMode]), [[a,'readWrite']]);
    await api(core, '/projects', nextTokens.accessToken, undefined, 401);
    pass('另一独立安装显式接替：领码回包丢失后两边暂用原身份，重启后台无需再输码即完成，旧凭据失效',
        { previousId: second.deviceId, recipientId: recipient.deviceId, otherLocalIdentity: separate.deviceId });

    const aState = profile('independent-install-a'), bState = profile('independent-install-b');
    const supersededOffer = await update(page, recipient.deviceId);
    const mutual = await update(page, recipient.deviceId);
    assert.notEqual((await pair(profile('stale-offer'), supersededOffer, proxy)).code, 0);
    let release, arriving = 0;
    const both = new Promise(resolve => { release = resolve; });
    proxy.before = async action => {
        if (action !== 'claim') return false;
        if (++arriving === 2) release();
        await both;
        return false;
    };
    proxy.after = action => action === 'confirm'; // Core commits, winning client loses response.
    const at = proxy.calls.length;
    const attempts = await Promise.all([pair(aState, mutual, proxy), pair(bState, mutual, proxy)]);
    proxy.before = proxy.after = undefined;
    assert(attempts.every(x => x.code !== 0));
    const race = proxy.calls.slice(at);
    assert.deepEqual([...new Set(race.filter(x => x.action === 'claim').map(x => x.status))].sort(), [200, 401]);
    assert(race.some(x => x.action === 'confirm' && x.status === 200));
    await api(core, '/projects', recipient.accessToken, undefined, 401);
    const retries = await Promise.all([pair(aState, mutual, proxy), pair(bState, mutual, proxy)]);
    assert.equal(retries.filter(r => r.code === 0).length, 1);
    const winner = JSON.parse(retries.find(r => r.code === 0).out);
    assert.notEqual(winner.deviceId, recipient.deviceId);
    const winnerState = retries[0].code === 0 ? aState : bState;
    assert.equal((await success(cli(winnerState, ['projects'])))[0].accessMode, 'readWrite');
    pass('两个独立 Windows Edge 安装档案在同一道闸并发领码，仅一份获准；确认回包丢失后重试找回同一胜者', { winnerId: winner.deviceId });

    const audits = sql(name, "SELECT action,count(*) FROM projecthub.audit_log WHERE pairing_id='" + ids(mutual).id + "' GROUP BY action ORDER BY action;");
    for (const event of ['device.update_issued','device.update_claimed','device.update_confirmed','device.superseded'])
        assert(audits.split('\n').includes(event + '|1'), 'Missing/surplus audit for ' + event);
    for (const [oldId,newId] of [[first.deviceId,second.deviceId],[second.deviceId,recipient.deviceId],[recipient.deviceId,winner.deviceId]]) {
        await page.goto('/devices?view=history');
        const oldRow = page.locator('tr[data-device-id="' + oldId + '"]');
        assert.equal(await oldRow.count(), 1);
        assert.equal(await oldRow.locator('[data-open="update-' + oldId + '"]').count(), 0);
        await oldRow.getByRole('link', { name: '详情' }).click();
        const successor = page.getByRole('link', { name: /继任设备与实际授权/ });
        assert.equal(await successor.getAttribute('href'), '/devices/' + newId);
        await successor.click();
        assert.equal(await page.getByRole('link', { name: /接替前的身份/ }).getAttribute('href'), '/devices/' + oldId);
    }
    pass('签发、领码、确认及前任停用各留一条审计，网页新旧设备历史可以双向追溯');
}

async function main() {
    let hasRole = false, hasDb = false, hasCore = false, browser, proxy;
    try {
        const imageId = docker(['image','inspect',image,'--format','{{.Id}}']);
        const password = randomBytes(24).toString('hex');
        sql('postgres', 'CREATE ROLE ' + name + " LOGIN PASSWORD '" + password + "';");
        hasRole = true;
        sql('postgres', 'CREATE DATABASE ' + name + ' OWNER ' + name + ';');
        hasDb = true;
        docker(['run','--pull=never','--name',name,'--network',network,'-p','127.0.0.1::8080','--env-file','/dev/stdin','-d',imageId],
            'ConnectionStrings__Core=Host=' + pg + ';Database=' + name + ';Username=' + name + ';Password=' + password + '\nProjectHub__LocalHttp=true\n');
        hasCore = true;
        const published = docker(['port',name,'8080/tcp']);
        assert.match(published, /^127\.0\.0\.1:\d+$/);
        const base = new URL('http://' + published);
        browser = await chromium.launch({ channel: 'msedge', headless: true });
        const page = await browser.newPage({ baseURL: base.origin, locale: 'zh-CN' });
        await visit(page, base);
        assert.equal(new URL(page.url()).pathname, '/setup', 'Expected private uninitialized database.');
        const code = docker(['logs',name]).match(/首次安装码\s*[:：]\s*(\S+)/u)?.[1];
        assert(code, 'Private container did not emit setup code.');
        const owner = 'qa-owner', ownerPassword = randomBytes(24).toString('base64url');
        assert.equal(await setup(page, code, owner, ownerPassword), 302);
        await page.waitForURL('**/login');
        assert.equal(await login(page, owner, ownerPassword), 302);
        await page.waitForURL('**/projects');
        const projects = [];
        for (const label of ['历史归档', '临时授权']) {
            const form = await openProjectCreation(page);
            await form.getByLabel('项目名称', { exact: true }).fill(label);
            assert.equal(await submit(page, '创建项目', form), 302);
            projects.push(await projectId(page, label));
        }
        proxy = await interpose(base);
        await exercise(page, base, proxy, ...projects);
        const [a, b] = projects;
        await require('./device-handover-transactions.cjs').run({
            page, core:base, api, update, parse:ids, project:a, otherProject:b, sql, name, pass, distro, pg,
            addDevice: async () => {
                const ordinary = await success(pair(profile(randomUUID()), await add(page, [[a,'readWrite']]), proxy));
                return proxy.tokens.get(ordinary.deviceId);
            }
        });
        await require('./windows-handover-pending.cjs').run({ page, core:base, proxy, projectId:a,
            add:() => add(page, [[a,'readWrite']]), update:id => update(page,id), profile, pair, success, cli, pass });
        if (process.env.PROJECTHUB_HANDOVER_UI) {
            await require('./windows-handover-ui.cjs').run({ page, core:base, proxy, projectId:a,
                add:() => add(page, [[a,'readWrite']]), update:id => update(page,id), temp });
            pass('Windows 桌面客户端接替确认和重启恢复');
        }
        if (evidence) {
            const hash = file => createHash('sha256').update(fs.readFileSync(file)).digest('hex');
            const report = { completedAt: new Date().toISOString(), host: os.hostname(), scope: 'One Windows host, separate Edge profiles, disposable PostgreSQL database and Linux Core container', image: imageId,
                edgeSha256: hash(edge), edgeAssemblySha256: hash(path.join(path.dirname(edge),'ProjectHub.Edge.dll')), checks };
            if (process.env.PROJECTHUB_HANDOVER_UI)
                report.desktopAssemblySha256 = hash(path.join(path.dirname(process.env.PROJECTHUB_HANDOVER_UI),'ProjectHub.Edge.Desktop.dll'));
            fs.mkdirSync(path.dirname(path.resolve(evidence)), { recursive: true });
            fs.writeFileSync(evidence, JSON.stringify(report, null, 2) + '\n');
        }
    } finally {
        proxy?.close();
        await browser?.close();
        if (hasCore) docker(['rm','-f',name]);
        if (hasDb) sql('postgres','DROP DATABASE ' + name + ';');
        if (hasRole) sql('postgres','DROP ROLE ' + name + ';');
        assert.equal(path.dirname(path.resolve(temp)),path.resolve(os.tmpdir()));
        assert(path.basename(temp).startsWith('nodicarta-handover-'));
        fs.rmSync(temp, { recursive: true, force: true });
    }
}
main().catch(error => { console.error(error.message); process.exitCode = 1; });
