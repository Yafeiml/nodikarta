// node tests/core-assets.cjs <disposable loopback URL> <projecthub-assets-test-... container> <evidence.json>
const assert = require('node:assert/strict');
const {createHash, randomBytes, randomUUID} = require('node:crypto');
const {writeFileSync} = require('node:fs');
const http = require('node:http');
const {chromium} = require('playwright-core');
const {docker, login, projectId, openProjectCreation, openDeviceAddition, resetPassword, submit} = require('./core-fixture.cjs');

async function run() {
    const [address, container, evidence] = process.argv.slice(2);
    const origin = new URL(address);
    assert.equal(origin.hostname, '127.0.0.1');
    assert.notEqual(origin.port, '29480');
    assert.match(container, /^projecthub-assets-test-[a-z0-9]+$/);
    assert.equal(docker('port', container, '8080/tcp').trim(), '127.0.0.1:' + origin.port);
    const checks = [];
    const pass = text => { checks.push(text); console.log('PASS', text); };
    const browser = await chromium.launch({channel: 'msedge', headless: true});
    async function send(route, token, body) {
        const headers = token ? {Authorization: 'Bearer ' + token} : {};
        if (body !== undefined) headers['Content-Type'] = 'application/json';
        return fetch(new URL('/v1' + route, origin), {method: body === undefined ? 'GET' : 'POST',
            headers, body: body === undefined ? undefined : JSON.stringify(body), redirect: 'error'});
    }
    async function expect(reply, status, error) {
        const response = await reply;
        assert.equal(response.status, status);
        const json = await response.json();
        if (error) assert.equal(json.error.code, error);
        return json;
    }
    try {
        const page = await browser.newPage({baseURL: origin.origin,locale:'zh-CN'});
        const password = randomBytes(24).toString('hex');
        await resetPassword(container, password);
        await page.goto('/login');
        assert.equal(await login(page, 'assets-qa', password), 302);
        async function project() {
            const name = '素材 Core 验收 ' + randomUUID().slice(0, 8);
            const form = await openProjectCreation(page);
            await form.getByLabel('项目名称', {exact: true}).fill(name);
            assert.equal(await submit(page, '创建项目', form), 302);
            return projectId(page, name);
        }
        async function device(project, mode) {
            const form = await openDeviceAddition(page);
            await form.getByLabel('设备名称').fill('Core 素材 ' + mode);
            await form.locator('[name="grants[' + project + ']"]').selectOption(mode);
            assert.equal(await submit(page,'生成配对码', form), 200);
            const [id, oneTimeCode] = (await page.locator('#pairing-code').inputValue()).split('.');
            const {accessToken} = await expect(send('/device-pairings/' + id + '/redeem', null,
                {oneTimeCode, deviceLabel: 'Core API QA', instanceNonce: randomUUID()}), 201);
            const {bindingId} = await expect(send('/projects/' + project, accessToken), 200);
            const route = '/projects/' + project + '/bindings/' + bindingId + '/assets';
            return {route, token: accessToken, request: (suffix = '', body) => send(route + suffix, accessToken, body)};
        }
        const alpha = await project(), beta = await project();
        const writer = await device(alpha, 'readWrite'), reader = await device(alpha, 'readOnly'), other = await device(beta, 'readWrite');
        const seed = {name: 'Shared Logo', purpose: 'catalog', fileName: 'brand.svg', tags: ['原图'],
            content: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>').toString('base64')};
        const interrupted = {...seed, tags: [], requestId: randomUUID(), name: 'Interrupted upload test'};
        const body = Buffer.from(JSON.stringify(interrupted));
        await new Promise((resolve, reject) => {
            let aborted = false;
            const request = http.request(new URL('/v1' + writer.route, origin), {method: 'POST', headers: {
                Authorization: 'Bearer ' + writer.token, 'Content-Type': 'application/json',
                'Content-Length': body.length, Expect: '100-continue'}});
            request.setTimeout(10000, () => request.destroy(new Error('Core did not start reading the upload')));
            request.on('response', () => reject(new Error('Unexpected final response to the interrupted upload')));
            request.on('error', error => { if (!aborted || error.code !== 'ECONNRESET') reject(error); });
            request.on('close', () => aborted ? resolve() : reject(new Error('Core closed before upload started')));
            request.once('continue', () => request.write(body.subarray(0, body.length - 8), () => {
                aborted = true;
                request.destroy();
            }));
            request.flushHeaders();
        });
        assert.deepEqual((await expect(reader.request('?query=Interrupted%20upload%20test'), 200)).items, []);
        const recovered = await expect(writer.request('', interrupted), 201);
        assert.equal(recovered.version, 1);
        assert.deepEqual(await expect(writer.request('', interrupted), 200), recovered);
        assert.deepEqual((await expect(reader.request('?query=Interrupted%20upload%20test'), 200)).items, [recovered]);
        pass('Core 开始读取后主动中断未传完的上传，列表中无半成品，同号同内容完整重试两次仅出现一个素材和第 1 版');
        const create = {...seed, requestId: randomUUID()};
        const first = await Promise.all([writer.request('', create), writer.request('', create)]);
        assert.deepEqual(first.map(r => r.status).sort(), [200, 201]);
        const [original, duplicate] = await Promise.all(first.map(r => r.json()));
        assert.deepEqual(original, duplicate);
        await expect(writer.request('', {...create, content: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"/>').toString('base64')}),
            409, 'idempotency_conflict');
        const revise = {assetId: original.assetId, baseVersion: 1};
        const racers = [1, 2].map(n => ({...seed, ...revise, requestId: randomUUID(), name: 'Winner ' + n}));
        const raced = await Promise.all(racers.map(payload => writer.request('', payload)));
        assert.deepEqual(raced.map(r => r.status).sort(), [201,409]);
        const winner = raced.findIndex(r => r.status === 201);
        assert.equal((await raced[1 - winner].json()).error.code, 'version_conflict');
        assert.equal((await raced[winner].json()).version, 2);
        assert.equal((await expect(writer.request('', racers[winner]), 200)).version, 2);
        const old = await expect(reader.request('/' + original.assetId + '?version=1'), 200);
        assert.equal(old.name, seed.name);
        pass('并发同号首发只产生一个版本；异参重用返回 409；同一基础版本的并发更新只有一个成功，重试可读回原回执');

        for (let n = 0; n < 3; n++) await expect(writer.request('', {...seed, requestId: randomUUID(), name: 'Entry ' + n}), 201);
        const list = after => reader.request('?query=' + encodeURIComponent('原图') + '&limit=2' + (after ? '&after=' + after : ''));
        const firstPage = await expect(list(),200), secondPage = await expect(list(firstPage.nextCursor),200);
        assert.equal(firstPage.items.length,2);
        assert.equal(secondPage.items.length,2);
        assert.equal(secondPage.nextCursor,null);
        const merged = [...firstPage.items, ...secondPage.items];
        assert.equal(new Set(merged.map(x => x.assetId)).size,4);
        assert.equal(merged.find(x => x.assetId === original.assetId).version,2);
        assert.equal((await expect(reader.request('?query=Shared%20Logo'),200)).items.length,0);
        await expect(reader.request('?limit=101'),400,'invalid_argument');
        pass('按标签分页返回四条不同素材；摘要只显示最新版本，旧名称不再命中，越界页大小被拒绝');

        for (const suffix of ['.png', '.jpeg', '.webp']) {
            const mime = 'image/' + suffix.slice(1);
            const content = await page.evaluate(type => document.createElement('canvas').toDataURL(type).split(',')[1], mime);
            const uploaded = await expect(writer.request('', {...seed, requestId: randomUUID(), fileName: 'image' + suffix, content}), 201);
            assert.equal(uploaded.contentType, mime);
        }
        const bounded = Buffer.alloc(8 * 1024 * 1024, 32);
        bounded.write('%PDF-1.7\n');
        bounded.write('\n%%EOF', bounded.length - 6);
        const pdf = {...seed, fileName: 'poster.pdf', requestId: randomUUID(), content: bounded.toString('base64')};
        const full = await expect(writer.request('', pdf),201);
        assert.equal(full.size,bounded.length);
        assert.equal(full.sha256,createHash('sha256').update(bounded).digest('hex'));
        assert.equal(Buffer.compare(Buffer.from(await (await reader.request('/' + full.assetId + '/content?version=1')).arrayBuffer()),bounded),0);
        await expect(writer.request('', {...pdf, requestId: randomUUID(), content: Buffer.concat([bounded,Buffer.from('!')]).toString('base64')}),
            413, 'asset_too_large');
        for (const invalid of [{fileName: 'not-png.png'}, {fileName: 'unknown.mp4'},
            {content: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg">').toString('base64')}]) {
            await expect(writer.request('', {...seed, requestId: randomUUID(), ...invalid}),400,'invalid_asset_format');
        }
        pass('PNG、JPEG、WebP、SVG、PDF 正确识别；8 MiB 原件逐字节取回，超限一字节返回 413，错扩展名和无效 SVG 被拒绝');

        await expect(reader.request('', {...seed, requestId: randomUUID()}),403,'forbidden');
        await expect(other.request('/' + original.assetId),404,'not_found');
        await expect(other.request('/' + original.assetId + '/content?version=1'),404,'not_found');
        for (const suffix of ['', '/' + original.assetId, '/' + original.assetId + '/content?version=1']) {
            await expect(send(writer.route + suffix, other.token),404,'not_found');
            await expect(send(writer.route + suffix, null),401,'unauthenticated');
        }
        await expect(send(writer.route, other.token, {...seed, requestId: randomUUID()}),404,'not_found');
        pass('绕过 Edge 直接调用 Core：只读发布 403；跨项目 ID、跨设备绑定及受限文件读取 404；无凭证 401');
        writeFileSync(evidence, JSON.stringify({checkedAtUtc: new Date().toISOString(),
            imageId: docker('inspect','--format','{{.Image}}',container).trim(), checks}, null, 2) + '\n');
    } finally { await browser.close(); }
}
run().then(() => process.stdout.write('', () => process.exit(0)),
    error => process.stderr.write(error.message + '\n', () => process.exit(1)));
