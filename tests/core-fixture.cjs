const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const { stripVTControlCharacters } = require('node:util');
const pty = require('@lydell/node-pty');
const {fixtureCaptcha} = require('./login-challenge-fixture.cjs');

const windows = process.platform === 'win32';
const engine = windows ? 'wsl.exe' : 'docker';
const prefix = windows ? ['-d', process.env.PROJECTHUB_WSL_DISTRO || 'Ubuntu-26.04-LTS', '-u', 'root', '--', 'docker'] : [];

function docker(...args) {
    const result = spawnSync(engine, [...prefix, ...args], { encoding: 'utf8', timeout: 45000, windowsHide: true });
    if (result.status !== 0) throw new Error('Container command failed: ' + args[0]);
    return result.stdout + result.stderr;
}

async function visit(page, address) {
    // A restart may leave the port listening while the worker is still starting.
    const expires = Date.now() + 30000;
    while (true) {
        try {
            const response = await page.goto(address.toString(), { timeout: 4000 });
            if (response && response.status() < 500) return;
        } catch (error) {
            if (Date.now() >= expires) throw error;
        }
        if (Date.now() >= expires) throw new Error('Core did not become ready.');
        await new Promise(resolve => setTimeout(resolve, 300));
    }
}

async function submit(page, label, scope = page) {
    const [response] = await Promise.all([
        page.waitForResponse(response => response.request().method() === 'POST'),
        scope.getByRole('button', { name: label, exact: true }).click()
    ]);
    return response.status();
}

async function setup(page, code, username, password) {
    await page.getByLabel('首次安装码', { exact: true }).fill(code);
    await page.getByLabel('管理员账号', { exact: true }).fill(username);
    await page.getByLabel('密码', { exact: true }).fill(password);
    await page.getByLabel('确认密码', { exact: true }).fill(password);
    return submit(page, '创建管理员账号');
}

async function login(page, username, password) {
    const address = new URL(page.url());
    assert(['127.0.0.1','localhost','[::1]'].includes(address.hostname), 'Use a loopback QA instance.');
    let database = process.env.PROJECTHUB_TEST_DATABASE;
    if (!database) {
        const containers = docker('ps','--format','{{.Names}} {{.Ports}}').split(/\r?\n/);
        const matched = containers.filter(line => line.includes(':' + address.port + '->8080/tcp'));
        assert.equal(matched.length,1,'Specify PROJECTHUB_TEST_DATABASE for the disposable Core.');
        const env = JSON.parse(docker('inspect','--format','{{json .Config.Env}}',matched[0].split(' ')[0]));
        const connection = env.find(line => line.startsWith('ConnectionStrings__Core='));
        database = connection?.match(/(?:=|;)Database=([^;]+)/i)?.[1];
    }
    const id = await page.locator('input[name="challengeId"]').first().inputValue();
    const answer = fixtureCaptcha(database, id, query => docker('exec',process.env.PROJECTHUB_TEST_POSTGRES_CONTAINER || 'dev-postgresql',
        'psql','-U','postgres','-d',database,'-XAtq','-v','ON_ERROR_STOP=1','-c',query));
    await page.locator('#username').fill(username);
    await page.locator('#password').fill(password);
    await page.locator('#captcha').fill(answer);
    const [response] = await Promise.all([page.waitForResponse(res => res.request().method() === 'POST'),page.locator('#login-submit').click()]);
    return response.status();
}

async function projectId(page, name) {
    if (new URL(page.url()).pathname !== '/projects') await page.goto('/projects');
    const row = page.getByRole('row').filter({has: page.getByText(name, {exact: true})});
    const id = await row.getAttribute('data-project-id') ?? await row.locator('code').innerText();
    assert.match(id, /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i);
    return id;
}

async function openProjectCreation(page) {
    await page.goto('/projects');
    const button = page.locator('.page-head [data-open="create-project"]');
    if (await button.count() && !await page.locator('#create-project').isVisible()) await button.click();
    return page.locator('form').filter({has: page.locator('#name')});
}

async function openDeviceAddition(page) {
    await page.goto('/devices');
    const button = page.locator('[data-open="add-device"]');
    if (await button.count()) await button.click();
    return page.locator('form').filter({has: page.locator('#device-label')});
}

function resetPassword(container, password, confirmation = password) {
    return new Promise((resolve, reject) => {
        const terminal = pty.spawn(engine, [...prefix, 'exec', '-it', container, 'dotnet', 'ProjectHub.Core.dll', 'reset-password'], { cols: 120, rows: 30 });
        const prompts = ['新密码（12–128 个字符）：', '确认新密码：'];
        let output = '';
        let sent = 0;
        const timer = setTimeout(() => {
            terminal.kill();
            reject(new Error('Timed out waiting for interactive reset (prompts answered: ' + sent + ').'));
        }, 20000);
        terminal.onData(data => {
            output += data;
            if (sent < prompts.length && stripVTControlCharacters(output).includes(prompts[sent])) {
                sent++;
                terminal.write((sent === 1 ? password : confirmation) + '\r');
            }
        });
        terminal.onExit(({ exitCode }) => {
            clearTimeout(timer);
            const transcript = stripVTControlCharacters(output);
            const matched = confirmation === password;
            if (exitCode !== (matched ? 0 : 2) || sent !== 2) return reject(new Error('Unexpected reset outcome (exit ' + exitCode + ', prompts answered ' + sent + ').'));
            if (!transcript.includes(matched ? '管理员密码已重设' : '两次输入不匹配')) return reject(new Error('Missing reset outcome message.'));
            if ([password, confirmation].some(secret => transcript.includes(secret))) return reject(new Error('Reset echoed the password in the terminal.'));
            resolve();
        });
    });
}

module.exports = { windows, docker, visit, submit, setup, login, projectId, openProjectCreation, openDeviceAddition, resetPassword };
