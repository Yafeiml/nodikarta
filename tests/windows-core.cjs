// Usage: node tests/windows-core.cjs <loopback URL> <disposable container> <evidence.json>
// Start with the two sample projects created by tests/m1.1.cjs. This test resets the fixture owner's password.
const assert = require('node:assert/strict');
const {randomUUID, randomBytes} = require('node:crypto');
const {mkdirSync, writeFileSync} = require('node:fs');
const path = require('node:path');
const {chromium} = require('playwright-core');
const {windows, login, resetPassword, projectId, openDeviceAddition} = require('./core-fixture.cjs');

const [address, container, evidence] = process.argv.slice(2);
const url = new URL(address);
assert(['localhost','127.0.0.1','[::1]'].includes(url.hostname));
const owner = process.env.PROJECTHUB_TEST_ADMIN;
assert(owner && container && evidence);
const checks = [];
const check = text => { checks.push(text); console.log('PASS', text); };

async function api(method, route, token, input, status) {
    const response = await fetch(new URL('/v1' + route, url), {
        method, headers: { ...(token && {Authorization: 'Bearer ' + token}), 'Content-Type': 'application/json' },
        body: input && JSON.stringify(input)
    });
    assert([status].flat().includes(response.status), method + ' ' + route + ' should return ' + status + ', got ' + response.status);
    return response.json();
}

async function pair(page, project, mode) {
    const form = await openDeviceAddition(page);
    await form.getByLabel('设备名称').fill('API 验收 ' + randomUUID());
    await form.locator('[name="grants[' + project + ']"]').selectOption(mode);
    await form.getByRole('button', {name:'生成配对码'}).click();
    const [id, secret] = (await page.locator('#pairing-code').inputValue()).split('.');
    const identity = await api('POST', '/device-pairings/' + id + '/redeem', null,
        {oneTimeCode: secret, instanceNonce: randomUUID()}, 201);
    const resource = await api('GET','/projects/' + project,identity.accessToken,null,200);
    const base = '/projects/' + project + '/bindings/' + resource.bindingId;
    return {
        get: (suffix, status = 200) => api('GET',base + suffix,identity.accessToken,null,status),
        post: (suffix, input, status = 201) => api('POST',base + suffix,identity.accessToken,input,status),
        identity, base
    };
}

async function run() {
    const browser = await chromium.launch({channel: windows ? 'msedge' : undefined, headless:true});
    try {
        const page = await browser.newPage({baseURL:url.origin, locale:'zh-CN'});
        const password = randomBytes(32).toString('base64url');
        await resetPassword(container, password);
        await page.goto('/login');
        assert.equal(await login(page, owner, password), 302);
        const project = await projectId(page,'验收项目 A');
        const other = await projectId(page,'验收项目 B');
        const a = await pair(page, project, 'readWrite');
        const b = await pair(page, project, 'readOnly');
        const c = await pair(page, project, 'readWrite');
        const outside = await pair(page, other, 'readWrite');
        const key = randomUUID();
        const input = {requestId: randomUUID(),title: '已验证 ' + key,body:'功能记录 v1',evidence:'repo:acceptance/sample'};
        const v1 = await a.post('/knowledge',input);
        assert.deepEqual(await a.post('/knowledge',input,200),v1);
        await a.post('/knowledge',{...input,body:'异参重试'},409);
        assert.equal((await b.get('/knowledge?query=' + key)).items[0].commitId,v1.commitId);
        await b.post('/knowledge',{...input,requestId:randomUUID()},403);
        assert.equal((await outside.get('/knowledge?query=' + key)).items.length,0);
        await api('GET',a.base + '/knowledge',outside.identity.accessToken,null,404);
        check('跨设备读取知识及证据；相同请求只提交一次，异参 409，只读写入 403，跨项目读取 404');

        const update = {...input, requestId:randomUUID(), itemId:v1.itemId, baseVersion:1,
            body:'功能记录 v2', evidence:'repo:acceptance/retest'};
        const v2 = await c.post('/knowledge',update);
        assert.equal(v2.itemId,v1.itemId);
        assert.notEqual(v2.commitId,v1.commitId);
        for (const [receipt, device, version, source] of [[v1,a,1,input],[v2,c,2,update]]) {
            assert.equal(receipt.deviceId,device.identity.deviceId);
            assert.equal(receipt.version,version);
            assert.equal(receipt.body,source.body);
            assert.equal(receipt.evidence,source.evidence);
            assert.ok(Number.isFinite(Date.parse(receipt.createdAt)));
        }
        await a.post('/knowledge',{...update,requestId:randomUUID(),body:'过期设备写入'},409);
        const history = '/knowledge/history?itemId=' + v1.itemId;
        assert.deepEqual((await b.get(history)).items,[v2,v1]);
        assert.deepEqual((await outside.get(history)).items,[]);
        for (const route of ['/knowledge', '/knowledge?query=' + key, '/knowledge?view=brief&query=' + key]) {
            const current = (await b.get(route)).items.filter(row=>row.itemId===v1.itemId);
            assert.deepEqual(current.map(row=>[row.commitId,row.body,row.evidence]),[[v2.commitId,update.body,update.evidence]]);
            assert(!(await outside.get(route)).items.some(row=>row.itemId===v1.itemId));
        }
        check('第二台设备修订同一条目，旧基线返回 409；历史完整保留两台设备的回执、时间及证据，快照、检索与接手摘要均只读到新版并隔离其他项目');

        const commit = (validity, fields = {}) => ({...input, requestId:randomUUID(), validity, ...fields});
        const policy = {kind:'decision',scope:'本次彩印物料'};
        const replaces = row => ({...policy,supersedesItemId:row.itemId,supersedesVersion:row.version});
        const oldDecision = await a.post('/knowledge',commit(policy,{itemId:v2.itemId,baseVersion:v2.version}));
        await outside.post('/knowledge',commit(replaces(oldDecision)),404);
        await a.post('/knowledge',commit({...replaces(oldDecision),scope:'不同的物料'}),400);
        const successor = await c.post('/knowledge',commit(replaces(oldDecision)));
        const contenders = await Promise.all([a,c].map(writer =>
            writer.post('/knowledge',commit(replaces(successor)),[201,409])));
        assert.equal(contenders.filter(row=>row.error?.code==='version_conflict').length,1);
        const winner = contenders.find(row=>row.commitId);
        const retired = (await b.get(history)).items[0].validity;
        assert.equal(retired.status,'superseded');
        assert.equal(retired.supersededByItemId,successor.itemId);
        assert.equal(retired.currentDecision.itemId,winner.itemId);
        await a.post('/knowledge',commit(replaces(winner),{itemId:oldDecision.itemId,baseVersion:oldDecision.version}),409);
        check('显式替代需同项目和范围；并发替代仅一项生效，旧决定可追至链尾且不能形成环');

        const observation = {kind:'observation',subject:'受检设备',observedAt:'2024-11-05T08:00:00+08:00'};
        const evidenceFile = await a.post('/assets',{requestId:randomUUID(),name:'现场示意',purpose:'实测依据',fileName:'proof.svg',
            content:Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>').toString('base64')});
        const evidenceRefs = [{role:'evidence',assetId:evidenceFile.assetId,version:evidenceFile.version}];
        const measured = await a.post('/knowledge',commit(observation,{assetRefs:evidenceRefs}));
        const revision = {itemId:measured.itemId,baseVersion:measured.version};
        await a.post('/knowledge',commit({...observation,observedAt:'2024-11-05T08:00:00'},revision),400);
        const future = new Date(Date.now()+86_400_000).toISOString();
        await a.post('/knowledge',commit({...observation,observedAt:future},revision),400);
        await a.post('/knowledge',commit({...observation,observedAt:'2024-11-04T08:00:00Z'},revision),409);
        const inherited = await c.post('/knowledge',commit(undefined,revision));
        assert.deepEqual(inherited.validity,{...observation,status:'lastObserved'});
        assert.deepEqual(inherited.assetRefs,evidenceRefs);
        check('现场观察需带时区并拒绝未来或倒退时间；兼容客户端的修订保留原实测设备和时间');

        const sameEvidence = await c.post('/knowledge',commit(undefined,{...revision,baseVersion:inherited.version,assetRefs:evidenceRefs}));
        const head = {itemId:measured.itemId,baseVersion:sameEvidence.version};
        for (const fields of [{title:'另一状态'},{body:'另一结果'},{evidence:'另一来源'},{assetRefs:[]}]) {
            const error = await c.post('/knowledge',commit(undefined,{...head,...fields}),400);
            assert.equal(error.error.code,'observation_confirmation_required');
        }
        const measureHistory = '/knowledge/history?itemId='+measured.itemId;
        assert.equal((await b.get(measureHistory)).items[0].commitId,sameEvidence.commitId);
        const resampled = await c.post('/knowledge',commit({...observation,observedAt:'2024-11-05T08:05:00+08:00'},
            {...head,title:'复测后的状态',body:'新的实测结果',evidence:'新的实测依据',assetRefs:[]}));
        assert.equal(resampled.version,sameEvidence.version+1);
        assert.deepEqual((await b.get(measureHistory)).items.at(-1).assetRefs,evidenceRefs);
        check('旧式修订只能继承相同的实测结论和依据；改标题、结果、依据或附件需要显式确认观察，原证据可追溯');

        const endpoint = await a.post('/endpoints',{requestId:randomUUID(),name:'开发验收 ' + key});
        assert((await c.get('/endpoints')).items.some(e=>e.endpointId === endpoint.endpointId));
        await outside.post('/feedback',{requestId:randomUUID(),content:'跨项目',targetEndpointId:endpoint.endpointId},404);
        const submit = {requestId:randomUUID(),content:'验收反馈 ' + key,targetEndpointId:endpoint.endpointId};
        const manual = await c.post('/feedback',submit);
        assert.equal(manual.observation,null);
        const data = {kind:'readOnlyWriteCount',expected:0,observed:1};
        for (const invalid of [{...data,observed:1000001},{...data,kind:'unsupported'},{kind:data.kind,observed:0}])
            await c.post('/feedback',{...submit,requestId:randomUUID(),observation:invalid},400);
        submit.requestId = randomUUID();
        submit.observation = data;
        const feedback = await c.post('/feedback',submit);
        assert.deepEqual(feedback.observation,data);
        assert(feedback.taskId);
        assert.deepEqual(await c.post('/feedback',submit,200),feedback);
        const claim = {requestId:randomUUID()};
        const route = '/tasks/' + feedback.taskId;
        assert((await a.get('/tasks')).items.some(t=>t.taskId===feedback.taskId && t.status==='pending'));
        assert(!(await c.get('/tasks')).items.some(t=>t.taskId===feedback.taskId));
        await c.post(route + '/claim',claim,404);
        await a.post(route + '/result',{requestId:randomUUID(),result:'提前回传'},409);
        const claimed = await a.post(route + '/claim',claim,200);
        assert.deepEqual(claimed.observation,data);
        assert.equal(claimed.status,'claimed');
        assert.deepEqual(await a.post(route + '/claim',claim,200),claimed);
        await a.post(route + '/claim',{requestId:randomUUID()},409);
        const result = {requestId:randomUUID(), result:'只读复现结果 ' + key};
        const finished = await a.post(route + '/result',result,200);
        assert.equal(finished.status,'completed');
        assert.deepEqual(finished.observation,data);
        assert.deepEqual(await a.post(route + '/result',result,200),finished);
        assert.equal((await b.get('/feedback')).items.find(f=>f.feedbackId===feedback.feedbackId).result,result.result);
        assert.equal((await a.post('/knowledge',{...input,requestId:randomUUID(),relatedTaskId:feedback.taskId})).relatedTaskId,
            feedback.taskId);
        await outside.post('/knowledge',{...input,requestId:randomUUID(),relatedTaskId:feedback.taskId},404);
        check('反馈与定向分析任务原子建立；其他设备不可认领，重复领取和结果提交可对账；其他获准设备读回结果与关联知识');
        check('三字段观测随反馈和任务传递，越界、未知类型或缺少计数返回 400；自由文本任务保留空观测');

        const oldBinding = a.base.split('/').pop();
        const outstanding = await c.post('/feedback',{...submit,requestId:randomUUID()});
        for (const mode of ['none','readWrite']) {
            await page.goto('/devices');
            const form = page.locator('form[data-device-id="' + a.identity.deviceId + '"][data-project-id="' + project + '"]');
            await form.locator('select').selectOption(mode);
            await form.getByRole('button',{name:'保存权限'}).click();
        }
        const regranted = await api('GET','/projects/' + project,a.identity.accessToken,null,200);
        assert.notEqual(regranted.bindingId,oldBinding);
        const freshBase = '/projects/' + project + '/bindings/' + regranted.bindingId;
        await a.get('/tasks',404);
        assert.equal((await api('GET',freshBase + '/tasks',a.identity.accessToken,null,200)).items.length,0);
        await api('POST',freshBase + '/tasks/' + outstanding.taskId + '/claim',a.identity.accessToken,{requestId:randomUUID()},404);
        assert(!(await c.get('/endpoints')).items.some(e=>e.endpointId===endpoint.endpointId));
        await c.post('/feedback',{...submit,requestId:randomUUID()},404);
        check('撤销后重新授权生成新绑定，旧目标从列表消失，待处理旧任务不会投递至新绑定');
        mkdirSync(path.dirname(evidence),{recursive:true});
        writeFileSync(evidence,JSON.stringify({checkedAtUtc:new Date().toISOString(),container,checks},null,2)+'\n');
    } finally {await browser.close();}
}
// ConPTY may keep a native thread alive after resetPassword's terminal has exited.
run().then(()=>process.stdout.write('',()=>process.exit(0))).catch(e=>
    process.stderr.write(e.message+'\n',()=>process.exit(1)));
