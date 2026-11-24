// node tests/windows-assets.cjs <Edge.exe> <disposable loopback URL> <projecthub-assets-test-... container> <evidence.json> [previous Edge.exe]
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {createHash, randomBytes, randomUUID} = require('node:crypto');
const {deflateSync} = require('node:zlib');
const {chromium} = require('playwright-core');
const {docker, visit, setup, login, submit, projectId, openProjectCreation, openDeviceAddition, resetPassword} = require('./core-fixture.cjs');
const {lines, httpRpc, runExe, handshake, tool, binaryHashes} = require('./windows-mcp.cjs');
const {publishPosterCase,readPosterCase} = require('./poster-decisions.cjs');
const {checkProjectRecall,checkDecisionVersions} = require('./project-recall.cjs');

function png(red) {
    function chunk(name, data) {
        const bytes = Buffer.concat([Buffer.from(name), data]);
        let crc = 0xffffffff;
        for (const b of bytes) {
            crc ^= b;
            for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ (crc & 1 ? 0xedb88320 : 0);
        }
        const length = Buffer.alloc(4), checksum = Buffer.alloc(4);
        length.writeUInt32BE(data.length);
        checksum.writeUInt32BE((crc ^ 0xffffffff) >>> 0);
        return Buffer.concat([length, bytes, checksum]);
    }
    const ihdr = Buffer.from([0,0,0,1, 0,0,0,1, 8,2,0,0,0]);
    return Buffer.concat([Buffer.from('89504e470d0a1a0a','hex'), chunk('IHDR',ihdr),
        chunk('IDAT',deflateSync(Buffer.from([0,red,32,98]))), chunk('IEND',Buffer.alloc(0))]);
}

async function main() {
    const [binary, url, container, evidence, previousEdge] = process.argv.slice(2);
    const exe = path.resolve(binary), origin = new URL(url);
    assert.equal(origin.hostname, '127.0.0.1');
    assert.match(container, /^projecthub-assets-test-[a-z0-9]+$/);
    assert.equal(docker('port',container,'8080/tcp').trim(), '127.0.0.1:'+origin.port);
    const root = fs.mkdtempSync(path.join(os.tmpdir(),'projecthub-assets-'));
    const browser = await chromium.launch({channel:'msedge',headless:true});
    const clients = new Set(), checks = [];
    const pass = description => { checks.push(description); console.log('PASS',description); };
    const hash = data => createHash('sha256').update(data).digest('hex');
    async function denied(client, name, args) {
        assert.equal((await client.call('tools/call',{name,arguments:args})).isError,true,name+' must fail');
    }
    try {
        const page = await browser.newPage({baseURL:origin.origin, locale:'zh-CN'});
        const admin = 'assets-qa', password = randomBytes(24).toString('hex');
        await visit(page,origin);
        if (new URL(page.url()).pathname.toLowerCase() === '/setup') {
            const code = [...docker('logs',container).matchAll(/首次安装码\s*[:：]\s*(\S+)/gu)].at(-1)?.[1];
            assert.ok(code,'Expected initial setup code for disposable Core');
            assert.equal(await setup(page,code,admin,password),302);
        } else { await resetPassword(container,password); }
        await page.goto('/login');
        assert.equal(await login(page,admin,password),302);
        async function project(label) {
            const name = label+' '+randomUUID().slice(0,8);
            const form = await openProjectCreation(page);
            await form.getByLabel('项目名称',{exact:true}).fill(name);
            assert.equal(await submit(page,'创建项目',form),302);
            return projectId(page,name);
        }
        const primary = await project('共享素材'), other = await project('隔离项目');
        async function pair(label,id,mode) {
            const form = await openDeviceAddition(page);
            await form.getByLabel('设备名称').fill(label);
            await form.locator('[name="grants['+id+']"]').selectOption(mode);
            assert.equal(await submit(page,'生成配对码',form),200);
            const code = await page.getByRole('dialog').locator('#pairing-code').inputValue();
            const state = path.join(root,label+'-state'), workspace = path.join(root,label+'-files');
            fs.mkdirSync(workspace);
            const device = JSON.parse(runExe(exe,['--state',state,'pair','--core',origin.origin],{input:code+'\n'}));
            const binding = JSON.parse(runExe(exe,['--state',state,'bind','--project',id,'--path',workspace]));
            return {workspace,deviceId:device.deviceId,args:['--state',state,'--project',id,'--binding',binding.bindingId,
                '--workspace',binding.workspaceId,'--mode','readWrite']};
        }
        const publisher = await pair('design',primary,'readWrite');
        const reader = await pair('poster',primary,'readOnly');
        const outsider = await pair('other',other,'readWrite');
        async function connect(device,http=false,edge=exe) {
            const args = [http?'mcp-http':'mcp-stdio',...device.args];
            const client = http ? await httpRpc(edge,args) : lines(edge,args);
            clients.add(client);
            const names = await handshake(client);
            for (const name of ['publish_asset','list_assets','get_asset','download_asset']) assert.ok(names.includes(name));
            return client;
        }
        let a = await connect(publisher);
        const b = await connect(reader,true), c = await connect(outsider);
        const data = png(245), input = {requestId:randomUUID(),relativePath:'logo.png',name:'星河 Logo',purpose:'海报排版',tags:['品牌','星河']};
        const source = path.join(publisher.workspace,input.relativePath);
        fs.writeFileSync(source,data);
        const v1 = await tool(a,'publish_asset',input);
        assert.equal(v1.version,1); assert.equal(v1.sha256,hash(data));
        const retry = await tool(a,'publish_asset',input);
        assert.equal(retry.assetId,v1.assetId); assert.equal(retry.version,1);
        await denied(a,'publish_asset',{...input,name:'冲突的内容'});
        a.close(); clients.delete(a); fs.unlinkSync(source);
        assert.equal((await tool(b,'list_assets',{query:'品牌'})).items[0].assetId,v1.assetId);
        const id = {assetId:v1.assetId,version:1};
        const preview = await b.call('tools/call',{name:'get_asset',arguments:{...id,includePreview:true}});
        assert.ok(preview.content.some(x=>x.type==='image' && x.mimeType==='image/png' && x.data.length));
        const download = {...id,destinationPath:'收取的原件.png'};
        const received = await tool(b,'download_asset',download);
        assert.equal(received.sha256,hash(data));
        const local = path.join(reader.workspace,download.destinationPath);
        assert.deepEqual(fs.readFileSync(local),data);
        pass('独立设备经 STDIO 发布，发布者断开并移除源文件后，只读设备经 HTTP 搜索、预览并下载相同原件');
        pass('同一发布请求重试保留版本和 ID，变更内容复用请求 ID 被拒绝');
        fs.writeFileSync(local,'保留本地内容');
        await denied(b,'download_asset',download);
        assert.equal(fs.readFileSync(local,'utf8'),'保留本地内容');
        await denied(b,'download_asset',{...id,destinationPath:'../超出目录.png'});
        assert.equal(fs.existsSync(path.join(root,'超出目录.png')),false);
        pass('同名文件受保护，下载只能写入当前绑定目录');
        a = await connect(publisher);
        fs.writeFileSync(source,png(18));
        const update = {...input,requestId:randomUUID(),assetId:id.assetId,baseVersion:1};
        const v2 = await tool(a,'publish_asset',update);
        assert.equal(v2.version,2);
        await denied(a,'publish_asset',{...update,requestId:randomUUID()});
        assert.equal((await tool(b,'get_asset',{assetId:id.assetId})).version,2);
        assert.equal((await tool(b,'list_assets',{query:'海报'})).items[0].version,2);
        assert.equal((await tool(b,'download_asset',{...id,destinationPath:'旧版.png'})).sha256,hash(data));
        assert.equal((await tool(b,'download_asset',{...id,version:2,destinationPath:'新版.png'})).sha256,hash(png(18)));
        pass('更新需匹配基础版本，最新搜索返回第二版，两版原件皆可独立取回');
        fs.writeFileSync(path.join(reader.workspace,'logo.png'),data);
        await denied(b,'publish_asset',{...input,requestId:randomUUID()});
        assert.deepEqual((await tool(c,'list_assets',{query:'星河'})).items,[]);
        await denied(c,'get_asset',id);
        await denied(c,'download_asset',{...id,destinationPath:'拒绝.png'});
        const fixture = await publishPosterCase(a,publisher.workspace,'印刷验收 '+randomUUID().slice(0,8));
        const lookup = await readPosterCase(b,reader.workspace,fixture);
        const recall = await checkProjectRecall(a,b,c,publisher.workspace,fixture);
        pass('中文接手、别称、固定版本、待定或否决依据、同类候选、技术标识、摘要展开与项目隔离');
        if (previousEdge) {
            const previous = await connect(reader,false,path.resolve(previousEdge));
            const found = await tool(previous,'search_project_knowledge',{query:'继续做上次那张宣传画'});
            const known = found.items.find(item=>item.commitId===fixture.decision.commitId);
            assert.ok(known);
            assert.equal(known.assetRefs.find(ref=>ref.role==='used').version,1);
            assert.equal(known.linkedAssets.find(ref=>ref.role==='used').latestPublishedVersion,2);
            const priorUse = await checkDecisionVersions(previous,fixture);
            assert.equal(priorUse.currentAdoption,'requiresEvidence');
            assert.ok((await tool(previous,'get_project_snapshot')).items.some(item=>item.commitId===known.commitId));
            recall.previousEdgeBinaries = binaryHashes(previousEdge);
            pass('旧版 Edge 支持中文别称搜索，交付物反查保留原引用，并提示历史用料、文件最新版与新交付的采纳依据；无参快照仍可读取全文');
        }
        await denied(c,'get_asset_decisions',lookup);
        await denied(b,'submit_knowledge_commit',{...fixture.input,requestId:randomUUID()});
        const foreign = await tool(c,'submit_knowledge_commit',{requestId:randomUUID(),title:'对照项目',body:'对照正文',evidence:'对照来源'});
        await denied(c,'submit_knowledge_commit',{...fixture.input,itemId:foreign.itemId,baseVersion:1,requestId:randomUUID()});
        assert.equal((await tool(c,'get_knowledge_history',{itemId:foreign.itemId})).items.length,1);
        const revised = {...fixture.input,itemId:fixture.decision.itemId,baseVersion:1,requestId:randomUUID(),body:'更正了印样说明文字。'};
        delete revised.assetRefs;
        const inherited = await tool(a,'submit_knowledge_commit',revised);
        assert.deepEqual(inherited.assetRefs,fixture.decision.assetRefs);
        await denied(a,'submit_knowledge_commit',{...revised,assetRefs:[]});
        const cleared = await tool(a,'submit_knowledge_commit',{...revised,baseVersion:2,requestId:randomUUID(),assetRefs:[]});
        assert.deepEqual(cleared.assetRefs,[]);
        const historical = (await tool(b,'get_asset_decisions',lookup)).items;
        assert.deepEqual(historical.map(row=>row.version).sort(),[1,2]);
        assert(historical.every(row=>row.isCurrent===false));
        pass('海报关联固定 Logo 与印样单；Logo 更新后仍能按决定下载旧原件，落盘字节匹配发布端');
        pass('项目隔离、只读、继承或显式清空引用、重放冲突与历史查询通过');
        await page.goto('/devices/'+reader.deviceId);
        const form = page.locator('form[data-device-id="'+reader.deviceId+'"][data-project-id="'+primary+'"]');
        await form.locator('[name=accessMode]').selectOption('none');
        assert.equal(await submit(page,'保存权限',form),200);
        await denied(b,'list_assets',{});
        await denied(b,'get_project_snapshot',{task:'继续做上次那张宣传画'});
        await denied(b,'search_project_knowledge',{query:'宣传画'});
        await denied(b,'get_asset_decisions',lookup);
        await denied(b,'get_asset',id);
        await denied(b,'download_asset',{...id,destinationPath:'撤销后.png'});
        pass('只读发布被拒绝，另一项目无法访问，网页撤权后已连接客户端随即失去读取和下载权限');
        fs.writeFileSync(evidence,JSON.stringify({checkedAtUtc:new Date().toISOString(),coreImage:docker('inspect','--format','{{.Image}}',container).trim(),
            edgeBinaries:binaryHashes(exe),checks,recall},null,2)+'\n');
    } finally {
        for (const client of clients) client.close();
        await browser.close();
        fs.rmSync(root,{recursive:true,force:true,maxRetries:8,retryDelay:110});
    }
}
// node-pty retains a Windows handle after the optional disposable admin reset.
main().then(()=>process.exit(0),error=>{console.error(error.message);process.exit(1);});
