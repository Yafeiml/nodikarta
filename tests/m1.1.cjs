// Run against a fresh, disposable Core and its own database. This test creates an
// owner, restarts the named container and resets its password through a real TTY.
// Usage: node tests/m1.1.cjs <loopback URL> <container> [evidence directory]
// Requires playwright-core, @lydell/node-pty and Chromium (Edge on Windows).
const assert = require('node:assert/strict');
const { randomBytes } = require('node:crypto');
const { mkdirSync, writeFileSync } = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright-core');
const { windows, docker, visit, submit, setup, login, projectId, openProjectCreation, resetPassword } = require('./core-fixture.cjs');

const [url, container, evidenceDir] = process.argv.slice(2);
if (!url || !container) throw new Error('Usage: node tests/m1.1.cjs <loopback URL> <disposable container> [evidence directory]');
const base = new URL(url);
assert(['localhost', '127.0.0.1', '[::1]'].includes(base.hostname), 'Use a loopback URL for this disposable acceptance test.');
const alias = new URL(base);
alias.hostname = base.hostname === 'localhost' ? '127.0.0.1' : 'localhost';
const results = [];
const secrets = [];

function pass(name) {
    results.push(name);
    console.log('PASS', name);
}

function codes() {
    const matches = [...docker('logs', container).matchAll(/首次安装码\s*[:：]\s*(\S+)/gu)].map(m => m[1]);
    assert(matches.length > 0, 'No first-install code in the container log.');
    secrets.push(...matches);
    return matches;
}

async function main() {
    const browser = await chromium.launch({ channel: windows ? 'msedge' : undefined, headless: true });
    try {
        const sites = [base, alias];
        const pages = await Promise.all(sites.map(async site => {
            const context = await browser.newContext({ baseURL: site.origin, locale: 'zh-CN', viewport: { width: 1280, height: 850 } });
            return context.newPage();
        }));
        await Promise.all(pages.map((page, i) => visit(page, sites[i])));
        assert(pages.every(page => new URL(page.url()).pathname.toLowerCase() === '/setup'), 'A fresh, uninitialized test database is required.');
        if (evidenceDir) {
            mkdirSync(evidenceDir, { recursive: true });
            await pages[0].setViewportSize({ width: 390, height: 844 });
            assert(await pages[0].evaluate(() => document.documentElement.scrollWidth <= innerWidth));
            await pages[0].screenshot({ path: path.join(evidenceDir, 'setup-mobile.png'), fullPage: true });
            await pages[0].setViewportSize({ width: 1280, height: 850 });
        }
        const oldCode = codes().at(-1);
        const password = randomBytes(24).toString('base64url');
        const newPassword = randomBytes(24).toString('base64url');
        secrets.push(password, newPassword);
        const users = ['m11-owner-a', 'm11-owner-b'];

        docker('restart', container);
        await Promise.all(pages.map((page, i) => visit(page, sites[i])));
        const freshCodes = codes();
        const code = freshCodes.at(-1);
        assert(code !== oldCode, 'The pre-install restart must rotate the install code.');
        assert.equal(await setup(pages[0], oldCode, users[0], password), 400);
        pass('首次启动与未初始化重启均从容器日志取码，旧码已失效');

        const attempts = await Promise.all(pages.map((page, i) => setup(page, code, users[i], password)));
        assert.deepEqual([...attempts].sort(), [302, 409]);
        const owner = users[attempts.indexOf(302)];
        pass('通过 IP 和 localhost 同时提交首装，只有一个管理员创建成功');

        for (const [i, page] of pages.entries()) {
            await visit(page, new URL('/setup', sites[i]));
            assert.equal(new URL(page.url()).pathname.toLowerCase(), '/login');
            if (i === 0 && evidenceDir) await page.screenshot({ path: path.join(evidenceDir, 'login.png') });
            const denied = await page.request.post('/projects', { form: { name: '匿名项目' }, maxRedirects: 0 });
            assert([302, 401, 403].includes(denied.status()), 'Anonymous creation must require login.');
            if (denied.status() === 302) assert.match(denied.headers().location, /\/login/i);
            assert.equal(await login(page, owner, password), 302);
            await page.waitForURL(/\/projects$/i);
        }
        pass('首装入口已关闭，匿名建项目受阻，两个独立浏览器会话分别登录');

        const names = ['验收项目 A', '验收项目 B'];
        const ids = [];
        for (const name of names) {
            const create = await openProjectCreation(pages[0]);
            await create.getByLabel('项目名称', { exact: true }).fill(name);
            const form = await create.evaluate(element => Object.fromEntries(new FormData(element)));
            assert.equal(await submit(pages[0], '创建项目', create), 302);
            const retry = await pages[0].request.post('/projects', { form, maxRedirects: 0 });
            assert.equal(retry.status(), 302);
            const conflict = await pages[0].request.post('/projects', { form: { ...form, name: name + ' 改名' }, maxRedirects: 0 });
            assert.equal(conflict.status(), 409);
            await pages[0].reload();
            ids.push(await projectId(pages[0], name));
        }
        assert.notEqual(ids[0], ids[1]);
        assert.equal(await pages[0].locator('tbody tr').count(), 2);
        pass('网页创建两个不同 ID 的项目，同一请求标识重放不重复建项，异名重放返回 409，列表仍有两条记录');

        assert.equal(await submit(pages[1], '退出登录'), 302);
        await visit(pages[1], new URL('/projects', alias));
        assert.equal(new URL(pages[1].url()).pathname.toLowerCase(), '/login');
        assert.equal(await login(pages[1], owner, password), 302);
        pass('主动退出后项目页面要求重新登录');

        docker('restart', container);
        for (const [i, page] of pages.entries()) {
            await visit(page, new URL('/projects', sites[i]));
            if (new URL(page.url()).pathname.toLowerCase() === '/login') assert.equal(await login(page, owner, password), 302);
            for (const [j, name] of names.entries()) assert.equal(await projectId(page, name), ids[j]);
        }
        assert.equal(codes().length, freshCodes.length, 'Restart after installation must not issue a new code.');
        pass('已初始化容器重启后不再发码，原管理员及两个项目 ID 保留');

        await resetPassword(container, newPassword, password);
        for (const [i, page] of pages.entries()) {
            await visit(page, new URL('/projects', sites[i]));
            assert.equal(await projectId(page, names[0]), ids[0]);
        }
        assert.equal(await submit(pages[1], '退出登录'), 302);
        assert.equal(await login(pages[1], owner, password), 302);
        pass('容器重设时两次输入不一致被拒，已有会话和原密码仍可用');

        await resetPassword(container, newPassword);
        for (const [i, page] of pages.entries()) {
            await visit(page, new URL('/projects', sites[i]));
            assert.equal(new URL(page.url()).pathname.toLowerCase(), '/login');
        }
        pass('真实容器交互终端无回显重设密码，先前的两份管理员会话均失效');
        assert.equal(await login(pages[0], owner, password), 401);
        assert.equal(await login(pages[0], owner, newPassword), 302);
        for (const [j, name] of names.entries()) assert.equal(await projectId(pages[0], name), ids[j]);
        pass('原密码被拒绝，新密码可登录，项目名称和 ID 均与重设前一致');
        if (evidenceDir) {
            await pages[0].screenshot({ path: path.join(evidenceDir, 'projects.png') });
            await pages[0].setViewportSize({ width: 360, height: 800 });
            assert(await pages[0].evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'Projects overflow the mobile viewport.');
            for (const [j, name] of names.entries()) assert.equal(await projectId(pages[0], name), ids[j]);
            pass('360px 宽的项目页无横向滚动，两条项目行仍对应原项目 ID');
            writeFileSync(path.join(evidenceDir, 'browser-results.json'), JSON.stringify({
                checkedAtUtc: new Date().toISOString(), browser: browser.version(), container, owner, checks: results, projects: ids
            }, null, 2) + '\n');
        }
    } finally {
        await browser.close();
    }
}

// Windows ConPTY can keep its bridge thread alive after the terminal has exited.
main().then(() => process.stdout.write('', () => process.exit(0))).catch(error => {
    let message = error.stack || String(error);
    for (const secret of secrets) message = message.replaceAll(secret, '[redacted]');
    process.stderr.write(message + '\n', () => process.exit(1));
});
