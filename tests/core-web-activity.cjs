// Run only on the dedicated disposable Core. Screenshots and credentials stay outside the repository.
// node tests/core-web-activity.cjs [--offer <existing project name> <new temporary file>]
const assert = require('node:assert/strict');
const {randomUUID, randomBytes, createHash} = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {chromium} = require('playwright-core');
const {docker, setup, login, openDeviceAddition, openProjectCreation, projectId, resetPassword, submit} = require('./core-fixture.cjs');

const base = 'http://127.0.0.1:29586';
const container = 'nodicarta-core-adminqa';
const owner = 'core-admin-qa';
const secrets = new Set();
const checks = [];
const proof = label => { checks.push(label); console.log('PASS', label); };
const hash = bytes => createHash('sha256').update(bytes).digest('hex');

async function signIn(page) {
    assert.equal(docker('port', container, '8080/tcp').trim(), new URL(base).host);
    const password = randomBytes(30).toString('base64url');
    secrets.add(password);
    await page.goto('/');
    if (new URL(page.url()).pathname === '/setup') {
        const code = [...docker('logs', container).matchAll(/首次安装码\s*[:：]\s*(\S+)/gu)].at(-1)?.[1];
        assert(code, 'The disposable container needs a valid installation code.');
        secrets.add(code);
        assert.equal(await setup(page, code, owner, password), 302);
    } else {
        await resetPassword(container, password);
    }
    await page.goto('/login');
    assert.equal(await login(page, owner, password), 302);
    await page.waitForURL('**/projects');
}

async function createProject(page, name) {
    const form = await openProjectCreation(page);
    await form.getByLabel('项目名称', {exact: true}).fill(name);
    assert.equal(await submit(page, '创建项目', form), 302);
    return projectId(page, name);
}

async function issue(page, label, project, mode) {
    const form = await openDeviceAddition(page);
    await form.locator('#device-label').fill(label);
    await form.locator('[name="grants[' + project + ']"]').selectOption(mode);
    assert.equal(await submit(page, '生成配对码', form), 200);
    const code = await page.getByRole('dialog').locator('#pairing-code').inputValue();
    secrets.add(code);
    return code;
}

async function api(route, token, data, expected, source = {}) {
    const r = await fetch(base + '/v1' + route, {method: data === undefined ? 'GET' : 'POST',
        headers: {...source, ...(token && {Authorization: 'Bearer ' + token}), ...(data && {'Content-Type':'application/json'})},
        body: data === undefined ? undefined : JSON.stringify(data), redirect: 'error'});
    assert.equal(r.status, expected, route.split('?')[0] + ' returned ' + r.status);
    const json = await r.json();
    if (json.accessToken) secrets.add(json.accessToken);
    if (json.refreshToken) secrets.add(json.refreshToken);
    return json;
}

async function device(page, name, project, mode) {
    const [id, oneTimeCode] = (await issue(page, name, project, mode)).split('.');
    const {accessToken, deviceId} = await api('/device-pairings/' + id + '/redeem', null,
        {oneTimeCode, instanceNonce: randomUUID(), deviceLabel:name}, 201);
    const {bindingId} = await api('/projects/' + project, accessToken, undefined, 200);
    return {accessToken, deviceId, scope: '/projects/' + project + '/bindings/' + bindingId};
}

async function assertCounts(page, project, expected) {
    const row = page.locator('tr[data-project-id="' + project + '"]');
    for (const [field, value] of Object.entries(expected))
        assert.equal((await row.locator('[data-count="' + field + '"]').innerText()).trim(), String(value));
}

async function screenshot(page, dir, name, width) {
    assert.equal(await page.locator('#pairing-code:visible').count(), 0);
    await page.setViewportSize({width, height: width < 600 ? 844 : 900});
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), name + ' overflows');
    await page.screenshot({path: path.join(dir, name + '.png')});
}

async function checkAdmin(page, shots) {
    const run = randomUUID().slice(0,8);
    const primaryName = '留白设计 · ' + run;
    const project = await createProject(page, primaryName);
    const other = await createProject(page, '品牌资料库 · ' + run);
    const desktop = await device(page, '设计工作站 ' + run, project, 'readWrite');
    const proofreader = await device(page, '审阅设备 ' + run, project, 'readOnly');
    const codex = {'X-Nodicarta-Client':'codex-mcp-client', 'X-Nodicarta-Client-Version':'1.0', 'X-Nodicarta-Entry':'mcp'};
    const claude = {...codex, 'X-Nodicarta-Client':'claude-code'};
    const call = (suffix, data, status, source = codex) => api(desktop.scope + suffix, desktop.accessToken, data, status, source);
    const v1 = Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32"><rect fill="#173630" width="16" height="16"/></svg>');
    const v2 = Buffer.from(v1.toString().replace('#173630','#cc7056'));
    const logo = {name:'双折线标志', purpose:'海报与展示屏', tags:['品牌'], fileName:'logo.svg'};
    const asset = await call('/assets', {...logo,requestId:randomUUID(),content:v1.toString('base64')}, 201);
    const old = {requestId:randomUUID(),title:'旧海报的单色印样',body:'小尺寸仍能看清两折线间的留白',
        evidence:'印样单 LB-1121',assetRefs:[{role:'used',assetId:asset.assetId,version:1}],validity:{kind:'decision',scope:'纸质海报'}};
    const decision = await call('/knowledge', old, 201);
    for (const revision of [2,3]) await call('/knowledge',{...old,itemId:decision.itemId,requestId:randomUUID(),baseVersion:revision-1},201);
    await call('/assets', {...logo,assetId:asset.assetId,baseVersion:1,requestId:randomUUID(),content:v2.toString('base64')}, 201);
    const current = {...old,requestId:randomUUID(),title:'橱窗屏的现行用标',body:'彩色标记提升轮播辨识度',evidence:'定稿样张 LB-C27',
        assetRefs:[{role:'used',assetId:asset.assetId,version:2}],validity:{kind:'decision',scope:'橱窗屏'}};
    const submittedAt = new Date(Date.now() - 120000).toISOString();
    await call('/knowledge', current,201,{...claude,'X-Nodicarta-Origin-At':submittedAt,'X-Nodicarta-Replay':'1'});
    await call('/knowledge', current,200,claude);
    await call('/knowledge?query=no-result-private-search',undefined,200,{...claude, 'X-Forwarded-For':'203.0.113.244'});
    await call('/knowledge/history?itemId=' + decision.itemId,undefined,200,{});
    await api(proofreader.scope+'/knowledge',proofreader.accessToken,{...current,requestId:randomUUID()},403,codex);
    await api('/projects/'+other+'/bindings/'+randomUUID()+'/knowledge',desktop.accessToken,undefined,404,claude);
    await page.goto('/projects');
    assert.equal(await page.locator('dialog[open]').count(),0,'Project list opened a dialog without a user action');
    await assertCounts(page,project,{knowledge:2,assets:1,devices:2});
    await assertCounts(page,other,{knowledge:0,assets:0,devices:0});
    const recentChange = page.locator('tr[data-project-id="'+project+'"] time');
    const beforeGrant = await recentChange.getAttribute('datetime');
    await screenshot(page,shots,'projects-wide',1440);
    await screenshot(page,shots,'projects-narrow',390);
    proof('跨项目汇总、知识修订及附件版本计数，宽屏和窄屏无横向溢出');
    await page.setViewportSize({width:1440,height:900});

    const detail = '/projects/'+project;
    await page.goto(detail+'?tab=knowledge&objectId='+decision.itemId+'&version=1');
    assert((await page.locator('main').innerText()).includes(old.evidence));
    await page.locator('a[href*="objectId='+asset.assetId+'"][href*="version=1" i]').first().click();
    assert((await page.locator('main').innerText()).includes(old.title));
    const download = page.getByRole('link',{name:'下载原件'});
    const url = await download.getAttribute('href');
    const original = await page.request.get(url);
    assert.equal(original.status(),200);
    assert.equal(hash(await original.body()),hash(v1));
    assert.notEqual(hash(v1),hash(v2));
    const cross = await page.request.get(url.replace(project,other));
    assert.equal(cross.status(),404);
    const anonymous = await fetch(new URL(url,base),{redirect:'manual'});
    assert([302,401,403].includes(anonymous.status));
    proof('旧海报依据、固定版本原件哈希、反向引用及管理员下载归属');

    const activities = page.locator('tr[data-activity-id]');
    await page.goto(detail+'?tab=activity&client=Claude%20Code&action=knowledge.commit');
    await activities.first().waitFor();
    assert.equal(await activities.count(),1,'Lost response retried with same business key created two events');
    await activities.locator('[data-disclose]').click();
    const info = activities.locator('xpath=following-sibling::tr[1]');
    assert((await activities.innerText()).includes('续传'));
    assert((await info.innerText()).includes(current.requestId));
    const times = await info.locator('time').evaluateAll(nodes=>nodes.map(node=>Date.parse(node.dateTime)));
    assert(times.includes(Date.parse(submittedAt)), 'Offline source timestamp was not retained');
    assert((await page.locator('main').innerText()).includes('Claude Code'));
    await page.goto(detail+'?tab=activity&view=reads&client=Claude%20Code&action=knowledge.search');
    assert.equal(await activities.count(),1);
    await activities.locator('[data-disclose]').click();
    assert((await page.locator('main').innerText()).match(/0\s*条/));
    assert(!(await page.locator('main').textContent()).includes('no-result-private-search'));
    assert(!(await page.locator('main').textContent()).includes('203.0.113.244'));
    await page.goto(detail+'?tab=activity&view=reads&action=knowledge.history&device='+desktop.deviceId);
    assert((await activities.innerText()).includes('未上报'));
    await page.goto(detail+'?tab=activity&view=errors');
    assert.equal(await activities.count(),1);
    assert((await activities.innerText()).includes('Codex'));
    await page.goto('/projects/'+other+'?tab=activity&view=errors');
    assert.equal(await activities.count(),0);
    assert.equal(await page.locator('.filter-options input[name=client][value=Codex]').count(),0,'Another project exposed client choices');
    await page.goto('/devices?view=security&client=Claude%20Code&action=knowledge.snapshot');
    assert((await activities.count())>=1,'Unscoped failure missing from security view');
    await page.goto(detail+'?tab=activity&view=all');
    await screenshot(page,shots,'activity-wide',1440);
    await screenshot(page,shots,'activity-narrow',390);
    await page.goto(detail+'?tab=activity');
    const filters=page.locator('.filter-bar');
    assert.equal(await filters.locator('select[name=view]').inputValue(),'all');
    for (const name of ['client','action','result','since','until']) {
        assert(await filters.locator(name==='since'||name==='until'?'[name='+name+']':'#label-'+name).isVisible());
    }
    async function choose(key, values) {
        const menu=filters.locator('.filter-menu').filter({has:page.locator('input[name='+key+']')});
        await menu.locator('summary').click();
        const bounds=await menu.locator('.filter-options').boundingBox();
        assert(bounds && bounds.x>=0 && bounds.x+bounds.width <= page.viewportSize().width,'Dropdown overflows the viewport');
        for(const value of values) await menu.locator('input[value="'+value+'"]').check();
        await page.keyboard.press('Escape');
        assert.equal(await menu.getAttribute('open'),null);
    }
    await choose('client',['Codex','Claude Code']);
    await choose('action',['knowledge.commit','asset.publish']);
    await choose('result',['success','forbidden']);
    await filters.getByRole('button',{name:'筛选'}).click();
    assert.equal(await activities.count(),7,'Client/action/result OR selections should intersect across fields');
    assert.equal(await filters.locator('input:checked').count(),6);
    await filters.getByRole('link',{name:'清除筛选'}).click();
    assert.equal(await filters.locator('input:checked').count(),0);
    // Force a second page containing both clients with real API reads. Other tests above use their exact earlier counts.
    for(let i=0;i<26;i++) await call('/knowledge?query=qa',undefined,200,i%2===0?codex:claude);
    await page.goto(detail+'?tab=activity&client=Codex&client=Claude%20Code&action=knowledge.search&action=knowledge.commit&result=success&result=forbidden');
    assert.equal(await activities.count(),25);
    const firstPageIds=await activities.evaluateAll(rows=>rows.map(r=>r.dataset.activityId));
    await page.getByRole('link',{name:'更早的活动'}).click();
    const params=new URL(page.url()).searchParams;
    assert.deepEqual(params.getAll('client'),['Codex','Claude Code']);
    assert.deepEqual(params.getAll('action'),['knowledge.search','knowledge.commit']);
    assert.deepEqual(params.getAll('result'),['success','forbidden']);
    const olderIds=await activities.evaluateAll(rows=>rows.map(r=>r.dataset.activityId));
    assert(olderIds.length>0 && olderIds.every(id=>!firstPageIds.includes(id)));
    await page.getByRole('link',{name:'返回最新'}).click();
    assert.equal(new URL(page.url()).searchParams.has('before'),false);
    assert.equal(await filters.locator('input:checked').count(),6);
    proof('全部条件常显、三个下拉复选同组取并集且跨组取交集、跨项目选项隔离、翻页不丢选项、可清除');
    proof('同机客户端与旧版来源、离线重放只记一次、零命中与拒绝分开，越权落在访问异常，地址不采用任意代理头');

    await page.setViewportSize({width:1440,height:900});
    const unknown = 'antigravity-client-preview';
    // Preserve the spelling and version of each report, even when several reports belong to one product.
    const agyReports = ['Antigravity','AnTiGrAvItY-ClIeNt','Google Antigravity'];
    for (const name of ['Codex','Claude Code',...agyReports,unknown])
        await call('/knowledge?query=qa',undefined,200,{...codex,'X-Nodicarta-Client':name,'X-Nodicarta-Client-Version':'2.34.0'});
    await page.goto(detail+'?tab=activity');
    const clients = filters.locator('#label-client + details');
    const options = await clients.locator('label span').allTextContents();
    for (const label of ['Codex','Claude Code','Antigravity',unknown,'Core 后台','未上报'])
        assert.equal(options.filter(o=>o===label).length,1,'Expected one client option for '+label);
    assert(!options.some(o=>['codex-mcp-client','claude-code','antigravity-client','google antigravity'].includes(o.toLowerCase())));
    const clientCells = activities.locator('td:nth-child(3)');
    for (const [product,alias] of [['Codex','codex-mcp-client'],['Claude Code','claude-code'],['Antigravity','antigravity-client']]) {
        const filteredIds = [];
        for (const name of [product,alias]) {
            await page.goto(detail+'?tab=activity&client='+encodeURIComponent(name));
            const labels = await clientCells.allTextContents();
            assert(labels.length>=2 && labels.every(label=>label.trim()===product),'Unexpected rows for '+name);
            assert.equal(await clients.locator('input:checked').count(),1);
            assert.equal(await clients.locator('input:checked').inputValue(),product);
            filteredIds.push(await activities.evaluateAll(rows=>rows.map(row=>row.dataset.activityId)));
        }
        assert.deepEqual(filteredIds[0],filteredIds[1],product+' and old filter URL returned different rows');
    }
    const originals = [];
    for (const row of await activities.all()) {
        await row.locator('[data-disclose]').click();
        const details = row.locator('xpath=following-sibling::tr[1]');
        const readFact = key => details.getByText(key,{exact:true}).locator('xpath=following-sibling::dd[1]').innerText();
        originals.push(await readFact('原始客户端名称'));
        assert.equal(await readFact('客户端版本'),'2.34.0');
    }
    assert.deepEqual(originals.sort(),agyReports.sort());
    await page.goto(detail+'?tab=activity&client='+encodeURIComponent(unknown));
    assert.deepEqual((await clientCells.allTextContents()).map(s=>s.trim()),[unknown]);
    await page.goto(detail+'?tab=activity&client='+encodeURIComponent('Core 后台'));
    const web = await clientCells.allTextContents();
    assert(web.length && web.every(cell=>cell.trim()==='Core 后台'));
    proof('客户端产品名显示与去重、旧筛选链接与产品一致、明细原始名称和版本、未知名称及网页来源');
    await page.goto(detail+'?tab=grants');
    const grant = page.locator('form[data-device-id="'+desktop.deviceId+'"][data-project-id="'+project+'"]');
    await grant.locator('select[name=accessMode]').selectOption('readOnly');
    const stale = await grant.evaluate(f=>Object.fromEntries(new FormData(f)));
    assert.equal(await submit(page,'保存权限',grant),200);
    await call('/knowledge',{...old,requestId:randomUUID()},403);
    const conflict = await page.request.post('/devices?handler=ChangeGrant',{form:{...stale,accessMode:'readWrite'},maxRedirects:0});
    assert.equal(conflict.status(),409);
    await page.goto('/projects');
    assert.equal(await recentChange.getAttribute('datetime'),beforeGrant);
    proof('项目侧当行改权限、旧表单并发冲突、同一凭据立即只读；最近资料变更保持原时间');

    await page.goto('/devices');
    const trigger = page.locator('[data-open="update-'+desktop.deviceId+'"]');
    await trigger.click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('button',{name:'生成更新码'}).waitFor();
    await screenshot(page,shots,'update-before-issue',1440);
    await submit(page,'生成更新码',dialog);
    const packed = await dialog.locator('#pairing-code').inputValue();
    secrets.add(packed);
    const [type, invitation, secret] = packed.split('.');
    assert.equal(type,'U1');
    await page.keyboard.press('Escape');
    await dialog.waitFor({state:'hidden'});
    await page.locator('#pairing-code').waitFor({state:'detached'});
    await page.waitForFunction(id=>document.activeElement?.dataset.open==='update-'+id,desktop.deviceId,{timeout:4000});
    await page.reload();
    assert(!(await page.content()).includes(secret));
    const key = randomBytes(32).toString('hex');
    await api('/device-pairings/'+invitation+'/claim',null,{oneTimeCode:secret,instanceNonce:randomUUID(),attemptSecret:key},200);
    await api('/projects/'+project,desktop.accessToken,undefined,200);
    const replacement = await api('/device-pairings/'+invitation+'/confirm',null,{attemptSecret:key},200);
    await api('/projects/'+project,desktop.accessToken,undefined,401);
    const inherited = await api('/projects/'+project,replacement.accessToken,undefined,200);
    assert.equal(inherited.accessMode,'readOnly');
    await page.goto('/devices/'+desktop.deviceId);
    assert((await page.locator('main').innerText()).includes('已被接替'));
    await page.goto(detail+'?tab=activity');
    assert((await page.locator('main').textContent()).includes('接替'));
    proof('更新弹窗签发和焦点归位、明文不重现、旧身份在确认后失效、历史可查');
    return {project,primaryName,deviceId:desktop.deviceId,assetId:asset.assetId,oldOriginalSha256:hash(v1)};
}

async function checkPageWidths(page, project) {
    const measurements = [];
    for (const width of [1440, 1120, 390]) {
        await page.setViewportSize({width, height: 800});
        let baseline;
        for (const url of ['/projects', ...['knowledge','assets','activity','grants'].map(tab =>
            '/projects/'+project+'?tab='+tab), '/devices', '/']) {
            await page.goto(url);
            assert(await page.locator('.workspace').isVisible(), url);
            for (const tall of [false, true]) {
                const edges = await page.evaluate(tall => {
                    const main = document.querySelector('main');
                    main.style.minHeight = tall ? '150vh' : '';
                    const bounds = selector => {
                        const rect = document.querySelector(selector).getBoundingClientRect();
                        return [rect.left, rect.right];
                    };
                    return {workspace:bounds('.workspace'), content:bounds('.panel'), header:bounds('.bar-inner'),
                        footer:bounds('.app-footer'), rootOverflow:document.documentElement.scrollWidth > innerWidth};
                }, tall);
                assert.equal(edges.rootOverflow, false, url+' overflows at '+width);
                assert.deepEqual(edges.workspace, edges.header, 'Header width at '+url);
                assert.deepEqual(edges.workspace, edges.footer, 'Footer width at '+url);
                baseline ??= edges;
                assert.deepEqual(edges, baseline, 'Shell shifted at '+width+' on '+url);
            }
        }
        measurements.push({viewport:width, contentLeft:baseline.content[0], contentRight:baseline.content[1]});
    }
    proof('实测首页、四个项目标签、设备页与返回首页的左右边缘，含强制滚动条和窄屏');
    return measurements;
}

async function checkLanguages(browser, page, project, shots) {
    for (const locale of ['en-US','fr-FR','zh-TW']) {
        const fresh = await browser.newContext({baseURL:base, locale});
        try {
            const loginPage = await fresh.newPage();
            await loginPage.goto('/login');
            const chinese = locale.startsWith('zh');
            assert.equal(await loginPage.locator('html').getAttribute('lang'), chinese ? 'zh-CN' : 'en', locale);
            assert.equal(await loginPage.locator('h1').innerText(), chinese ? '管理员登录' : 'Administrator sign-in', locale);
            assert.equal(await loginPage.locator('#captcha').getAttribute('maxlength'), '4');
            assert.match(await loginPage.locator('.app-footer').innerText(), /Core v\d+\.\d+\.\d+/);
        } finally { await fresh.close(); }
    }
    const switchLanguage = async choice => {
        const picker = page.locator('.language-picker');
        await picker.locator('summary').click();
        await Promise.all([page.waitForNavigation(), picker.locator('button[value="'+choice+'"]').click()]);
    };
    await page.goto('/projects/'+project+'?tab=activity&view=all');
    const original = page.url();
    await switchLanguage('en');
    assert.equal(page.url(), original);
    assert.equal(await page.locator('html').getAttribute('lang'), 'en');
    assert.equal(await page.locator('.tabs a[aria-current]').innerText(), 'Activity log');
    await screenshot(page,shots,'activity-english-narrow',390);
    await screenshot(page,shots,'activity-english-wide',1440);
    for (const url of ['/projects', '/projects/'+project+'?tab=assets', '/devices', '/account/password']) {
        await page.goto(url);
        assert.equal(await page.locator('html').getAttribute('lang'), 'en', url);
        assert(!/[\u3400-\u9fff]/u.test(await page.locator('header.topbar').innerText()), url+' header untranslated');
    }
    await switchLanguage('zh');
    assert.equal(await page.locator('h1').innerText(), '修改密码');
    await switchLanguage('auto');
    assert.equal(await page.locator('.language-picker button[value="auto"]').getAttribute('aria-pressed'), 'true');
    proof('英文与法文浏览器自动显示英文，繁体中文浏览器显示中文；手动英文保留原筛选并跨页面生效，可恢复自动');
}

async function main() {
    const browser = await chromium.launch({channel:'msedge',headless:true});
    try {
        const page = await browser.newPage({baseURL:base,locale:'zh-CN',viewport:{width:1440,height:900}});
        page.setDefaultTimeout(8000);
        await signIn(page);
        if (process.argv[2] === '--offer') {
            const [projectName,destination] = process.argv.slice(3);
            const withinTemp = path.relative(fs.realpathSync(os.tmpdir()),path.join(fs.realpathSync(path.dirname(destination)),path.basename(destination)));
            assert(withinTemp && !withinTemp.startsWith('..') && !path.isAbsolute(withinTemp));
            const id = await projectId(page,projectName);
            const packed = await issue(page,'Edge 联测 '+randomUUID().slice(0,8),id,'readWrite');
            fs.writeFileSync(destination,packed,{flag:'wx',mode:0o600});
            console.log('联测码有效期十分钟，已写入 '+destination);
            return;
        }
        const shots = fs.mkdtempSync(path.join(os.tmpdir(),'nodicarta-core-admin-'));
        const result = await checkAdmin(page,shots);
        result.layout = await checkPageWidths(page,result.project);
        await checkLanguages(browser,page,result.project,shots);
        fs.writeFileSync(path.join(shots,'results.json'),JSON.stringify({checkedAt:new Date().toISOString(),browser:browser.version(),checks,...result},null,2));
        console.log('验收产物 '+shots);
    } finally {await browser.close();}
}
main().then(()=>process.stdout.write('',()=>process.exit(0)),e=>{
    let message = e.stack || e.message;
    for (const secret of secrets) message=message.replaceAll(secret,'[redacted]');
    process.stderr.write(message+'\n',()=>process.exit(1));
});
