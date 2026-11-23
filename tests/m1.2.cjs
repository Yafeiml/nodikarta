// Run against a disposable, initialized M1.1 database upgraded to M1.2.
// Usage: node tests/m1.2.cjs <loopback URL> <container> <evidence directory>
const assert = require('node:assert/strict');
const { randomUUID, randomBytes } = require('node:crypto');
const { mkdirSync, writeFileSync } = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright-core');
const { resetPassword, login, visit, submit, projectId, openProjectCreation, openDeviceAddition, windows } = require('./core-fixture.cjs');

const [url, container, evidenceDir] = process.argv.slice(2);
const owner = process.env.PROJECTHUB_TEST_ADMIN;
assert(url && container && evidenceDir && owner, 'Supply the disposable URL, container, evidence directory and PROJECTHUB_TEST_ADMIN.');
const base = new URL(url);
assert(['localhost', '127.0.0.1', '[::1]'].includes(base.hostname), 'Use a loopback URL.');
const checks = [];
const secrets = new Set();

function pass(description) {
    checks.push(description);
    console.log('PASS', description);
}

// Simulated Edges have no browser cookie jar. Only an explicitly supplied bearer identifies a device.
async function api(resource, token, body) {
    const headers = { Accept: 'application/json' };
    if (token) headers.Authorization = 'Bearer ' + token;
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const res = await fetch(new URL('/v1' + resource, base), {
        method: body === undefined ? 'GET' : 'POST', headers, redirect: 'manual',
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    const data = await res.json();
    if (data.accessToken) secrets.add(data.accessToken);
    if (data.refreshToken) secrets.add(data.refreshToken);
    return { status: res.status, data };
}

function expectStatus(result, status, errorCode) {
    assert.equal(result.status, status);
    if (errorCode) assert.equal(result.data.error?.code, errorCode);
    return result.data;
}

function redeem(offer) {
    return api('/device-pairings/' + offer.id + '/redeem', null, {
        oneTimeCode: offer.code, deviceLabel: '模拟 Edge', instanceNonce: randomUUID()
    });
}

async function clickForm(page, form, label) {
    const [response] = await Promise.all([
        page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/devices'),
        form.getByRole('button', { name: label, exact: true }).click()
    ]);
    return response.status();
}

async function issue(page, label, grants) {
    const form = await openDeviceAddition(page);
    await form.getByLabel('设备名称').fill(label);
    for (const [id, mode] of grants) await form.locator('[name="grants[' + id + ']"]').selectOption(mode);
    assert.equal(await clickForm(page, form, '生成配对码'), 200);
    const packed = await page.locator('#pairing-code').inputValue();
    secrets.add(packed);
    const [id, code] = packed.split('.');
    assert(id && code, 'Invalid pairing code format.');
    secrets.add(code);
    return { id, code };
}

function grantForm(page, deviceId, projectId) {
    return page.locator('form[data-device-id="' + deviceId + '"][data-project-id="' + projectId + '"]');
}

async function openGrant(page, deviceId, projectId) {
    await page.goto('/devices');
    const form = grantForm(page, deviceId, projectId);
    if (!await form.count()) await page.goto('/devices/'+deviceId);
    const picker = page.locator('#grant-project').filter({has: form});
    if (await picker.count() && !await picker.isVisible()) await page.locator('[data-open="grant-project"]').click();
    return form;
}

async function change(page, deviceId, projectId, mode) {
    const form = await openGrant(page, deviceId, projectId);
    await form.locator('[name=accessMode]').selectOption(mode);
    assert([200,302].includes(await clickForm(page, form, '保存权限')));
    const saved = await openGrant(page, deviceId, projectId);
    assert.equal(await saved.locator('[name=accessMode]').inputValue(), mode);
}

const workspacePath = (project, binding) => '/projects/' + project + '/bindings/' + binding + '/workspaces';
const registration = () => ({ workspaceId: randomUUID(), requestId: randomUUID(), repositoryIds: [] });

async function main() {
    const browser = await chromium.launch({ channel: windows ? 'msedge' : undefined, headless: true });
    try {
        const page = await browser.newPage({ baseURL: base.origin, locale: 'zh-CN', viewport: { width: 1280, height: 900 } });
        const testId = randomUUID().slice(0, 8);
        const password = randomBytes(32).toString('base64url');
        secrets.add(password);
        await visit(page, new URL('/devices', base));
        assert.equal(new URL(page.url()).pathname, '/login', 'Expected an initialized disposable Core.');
        await resetPassword(container, password);
        assert.equal(await login(page, owner, password), 302);
        const a = await projectId(page, '验收项目 A');
        const b = await projectId(page, '验收项目 B');
        const existingRows = await page.locator('tbody tr').count();
        pass('原 M1.1 管理员可登录，迁移后的网页保留项目 A 和 B');

        const cancelled = await issue(page, '已取消的邀请 ' + testId, []);
        await visit(page, new URL('/devices', base));
        assert.equal(await page.locator('#pairing-code').count(), 0);
        let pending = page.locator('[data-pairing-id="' + cancelled.id + '"]');
        const progress = pending.getByRole('button',{name:'查看进度'});
        if (await progress.count()) {
            await progress.click();
            pending = page.getByRole('dialog');
        }
        assert([200,302].includes(await clickForm(page, pending, '取消配对码')));
        expectStatus(await redeem(cancelled), 401, 'unauthenticated');
        pass('查看设备列表时不再显示配对码，取消尚未兑换的配对码后兑换被拒');

        const offer = await issue(page, '开发电脑 ' + testId, [[a, 'readOnly']]);
        const competing = await Promise.all([redeem(offer), redeem(offer)]);
        assert.deepEqual(competing.map(x => x.status).sort(), [201, 401]);
        const first = competing.find(x => x.status === 201).data;
        assert(first.deviceId && first.accessToken && first.refreshToken);
        const token = first.accessToken;
        expectStatus(await redeem(offer), 401, 'unauthenticated');
        pass('两个模拟 Edge 并发兑换同一码只签发一台设备，再次兑换仍被拒绝');

        const secondOffer = await issue(page, '运营电脑 ' + testId, [[b, 'readOnly']]);
        const second = expectStatus(await redeem(secondOffer), 201);
        assert.notEqual(first.deviceId, second.deviceId);
        assert(token !== second.accessToken, 'Expected distinct device credentials.');
        const list = expectStatus(await api('/projects', token), 200).items;
        assert.deepEqual(list.map(p => p.projectId), [a]);
        const readA = expectStatus(await api('/projects/' + a, token), 200);
        assert.equal(readA.accessMode, 'readOnly');
        const aWork = workspacePath(a, readA.bindingId);
        const beforeA = expectStatus(await api(aWork, token), 200).items;
        expectStatus(await api(aWork, token, registration()), 403, 'forbidden');
        assert.deepEqual(expectStatus(await api(aWork, token), 200).items, beforeA);
        pass('第一台设备只能列出 A，能读 A，写入返回 403，工作区记录没有变化');

        const secondB = expectStatus(await api('/projects/' + b, second.accessToken), 200);
        expectStatus(await api('/projects/' + b, token), 404, 'not_found');
        const otherB = workspacePath(b, secondB.bindingId);
        expectStatus(await api(otherB, token), 404, 'not_found');
        expectStatus(await api(otherB, token, registration()), 404, 'not_found');
        assert.deepEqual(expectStatus(await api(otherB, second.accessToken), 200).items, []);
        pass('使用另一设备实际获准的 B 和绑定 ID，未授权设备的读取和写入均返回 404');

        await visit(page, new URL('/projects', base));
        const cName = '验收项目 C ' + testId;
        const create = await openProjectCreation(page);
        await create.getByLabel('项目名称', { exact: true }).fill(cName);
        assert.equal(await submit(page, '创建项目', create), 302);
        const c = await projectId(page, cName);
        assert.equal(await page.locator('tbody tr').count(), existingRows + 1);
        assert.deepEqual(expectStatus(await api('/projects', token), 200).items.map(p => p.projectId), [a]);
        expectStatus(await api('/projects/' + c, token), 404, 'not_found');
        pass('管理员新建项目 C 后，已配对设备仍只可见 A');

        await visit(page, new URL('/devices', base));
        const stale = await (await openGrant(page, first.deviceId, b)).evaluate(f => Object.fromEntries(new FormData(f)));
        await change(page, first.deviceId, b, 'readWrite');
        const staleReply = await page.request.post('/devices?handler=ChangeGrant', {
            form: { ...stale, accessMode: 'readOnly' }, maxRedirects: 0
        });
        assert.equal(staleReply.status(), 409);
        const readB = expectStatus(await api('/projects/' + b, token), 200);
        assert.equal(readB.accessMode, 'readWrite');
        const bWork = workspacePath(b, readB.bindingId);
        const record = registration();
        expectStatus(await api(bWork, token, record), 201);
        expectStatus(await api(bWork, token, record), 200);
        assert.deepEqual(expectStatus(await api(bWork, token), 200).items.map(p => p.workspaceId), [record.workspaceId]);
        pass('网页追加 B 读写后，原 accessToken 可登记并读回持久记录；过时的网页表单返回 409');

        await change(page, first.deviceId, b, 'readOnly');
        expectStatus(await api(bWork, token, record), 403, 'forbidden');
        expectStatus(await api(bWork, token, registration()), 403, 'forbidden');
        assert.deepEqual(expectStatus(await api(bWork, token), 200).items.map(p => p.workspaceId), [record.workspaceId]);
        await change(page, first.deviceId, b, 'none');
        expectStatus(await api('/projects/' + b, token), 404, 'not_found');
        expectStatus(await api(bWork, token), 404, 'not_found');
        expectStatus(await api(bWork, token, record), 404, 'not_found');
        assert.deepEqual(expectStatus(await api('/projects', token), 200).items.map(p => p.projectId), [a]);
        const staleAdd = await page.request.post('/devices?handler=ChangeGrant', {
            form: { ...stale, accessMode: 'readWrite' }, maxRedirects: 0
        });
        assert.equal(staleAdd.status(), 409);
        expectStatus(await api('/projects/' + b, token), 404, 'not_found');
        pass('同一 accessToken 在降为只读后连成功过的写请求也被拒；移除 B 后读写均返回 404，旧新增授权表单不能恢复权限');

        await change(page, first.deviceId, b, 'readWrite');
        const rebound = expectStatus(await api('/projects/' + b, token), 200);
        assert.notEqual(rebound.bindingId, readB.bindingId);
        expectStatus(await api(bWork, token), 404, 'not_found');
        const newWork = workspacePath(b, rebound.bindingId);
        assert.deepEqual(expectStatus(await api(newWork, token), 200).items.map(p => p.workspaceId), [record.workspaceId]);
        pass('再次授予 B 后原设备凭证继续有效，新旧绑定 ID 分开，旧绑定失效，已登记的数据仍可读');

        const secondPassword = randomBytes(32).toString('base64url');
        secrets.add(secondPassword);
        await resetPassword(container, secondPassword);
        await visit(page, new URL('/devices', base));
        assert.equal(new URL(page.url()).pathname, '/login');
        expectStatus(await api('/projects/' + a, token), 200);
        expectStatus(await api('/projects/' + b, second.accessToken), 200);
        assert.equal(await login(page, owner, secondPassword), 302);
        await visit(page, new URL('/devices', base));
        pass('容器重设管理员密码后旧管理会话失效，两台设备凭证及授权继续有效');

        mkdirSync(evidenceDir, { recursive: true });
        assert.equal(await page.locator('#pairing-code').count(), 0);
        await page.screenshot({ path: path.join(evidenceDir, 'devices.png'), fullPage: true });
        let deviceCard = page.locator('article[data-device-id="' + first.deviceId + '"]');
        if (await deviceCard.count()) {
            await deviceCard.locator('summary').click();
        } else {
            await page.goto('/devices/'+first.deviceId);
            await page.locator('[data-open="revoke-device"]').click();
            deviceCard = page.getByRole('dialog');
        }
        assert([200,302].includes(await clickForm(page, deviceCard, '确认撤销设备')));
        expectStatus(await api('/projects', token), 401, 'unauthenticated');
        expectStatus(await api(newWork, token, record), 401, 'unauthenticated');
        expectStatus(await api('/tokens/refresh', null, { refreshToken: first.refreshToken }), 401, 'unauthenticated');
        expectStatus(await api('/projects/' + b, second.accessToken), 200);
        pass('网页撤销第一台设备后，读取、写入和刷新均返回 401；第二台设备继续访问 B');

        const renewed = expectStatus(await api('/tokens/refresh', null, { refreshToken: second.refreshToken }), 200);
        assert.equal(renewed.deviceId, second.deviceId);
        assert(renewed.accessToken !== second.accessToken && renewed.refreshToken !== second.refreshToken);
        expectStatus(await api('/projects', second.accessToken), 401, 'unauthenticated');
        expectStatus(await api('/projects/' + b, renewed.accessToken), 200);
        expectStatus(await api('/tokens/refresh', null, { refreshToken: second.refreshToken }), 401, 'unauthenticated');
        expectStatus(await api('/projects', renewed.accessToken), 401, 'unauthenticated');
        pass('设备续期轮换返回新凭证，旧 accessToken 失效；重放已使用的 refreshToken 后该凭证族失效');

        writeFileSync(path.join(evidenceDir, 'results.json'), JSON.stringify({
            checkedAtUtc: new Date().toISOString(), browser: browser.version(), container,
            projects: { a, b, c }, devices: [first.deviceId, second.deviceId], checks
        }, null, 2) + '\n');
    } finally {
        await browser.close();
    }
}

// Flush before exit because Windows ConPTY may keep its bridge thread alive.
main().then(() => process.stdout.write('', () => process.exit(0))).catch(error => {
    let message = error.stack || String(error);
    for (const secret of secrets) message = message.replaceAll(secret, '[redacted]');
    process.stderr.write(message + '\n', () => process.exit(1));
});
