// node tests/windows-project-init.cjs <Edge.exe beside bundled skills> <temporary paired state> <write project ID> <read-only project ID> <disposable Core container> <result.json>
// Prepare separate projects on a disposable loopback Core. The test creates/removes only its own workspace bindings.
const assert = require('node:assert/strict');
const {createHash, randomUUID} = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {lines, runExe, handshake, tool, binaryHashes} = require('./windows-mcp.cjs');

async function main() {
    const [cliArg, stateArg, writeId, readId, container, evidence] = process.argv.slice(2);
    const cli = path.resolve(cliArg), state = fs.realpathSync(stateArg);
    assert(state.startsWith(fs.realpathSync(os.tmpdir())+path.sep));
    const edge = (...args) => JSON.parse(runExe(cli,['--state',state,...args]));
    const core = new URL(edge('status').coreUrl);
    assert.equal(core.hostname,'127.0.0.1');
    assert(core.port && core.port !== '29480');
    assert.match(container,/^projecthub-init-test-[a-z0-9]+$/);
    const docker=(...args)=>runExe('wsl.exe',['-d','Ubuntu-26.04-LTS','-u','root','--exec','docker',...args]);
    assert.equal(docker('port',container,'8080/tcp'),core.host);
    const coreImage=docker('inspect','--format','{{.Image}}',container);
    assert.match(coreImage,/^sha256:[0-9a-f]{64}$/);
    const grants = edge('projects');
    assert(grants.some(p=>p.projectId===writeId && p.accessMode==='readWrite'));
    assert(grants.some(p=>p.projectId===readId && p.accessMode==='readOnly'));
    const skills = path.join(path.dirname(cli),'skills');
    const idScript = path.join(skills,'nodicarta-init','scripts','init-ids.cjs');
    const ids = (kind,key,version) => JSON.parse(runExe(process.execPath,[idScript,writeId,kind,key,String(version)]));
    const root = fs.mkdtempSync(path.join(os.tmpdir(),'projecthub-init-case-'));
    const workspace = path.join(root,'project'), read = path.join(root,'read'), outside = path.join(root,'unbound');
    const brand = path.join(workspace,'design'), clients = [], bindings = [], checks = [];
    const label = '便签 '+randomUUID().slice(0,8);
    const purpose = label+' 的项目自有标志，来源 design/mark.svg';
    const file = name => path.join(workspace,name);
    const sha = data => createHash('sha256').update(data).digest('hex');
    const record = message => { checks.push(message); console.log('PASS',message); };
    async function open(cwd) {
        const client = lines(cli,['mcp-stdio-global','--state',state],{cwd});
        clients.push(client);
        const names = await handshake(client);
        return {client,names};
    }
    try {
        for (const dir of [brand,file('src'),file('docs'),read,outside]) fs.mkdirSync(dir,{recursive:true});
        fs.writeFileSync(file('README.md'),'# 便签\n按标签整理本地便签。design/mark.svg 是本项目自有标志，供本项目共享。\n');
        fs.writeFileSync(file('docs/export.md'),'# 导出说明\n仅导出选中便签，采用 UTF-8。\n');
        fs.writeFileSync(file('src/export.js'),'export const format = "markdown";\n');
        fs.writeFileSync(file('.env'),'QA_OMIT=placeholder-never-publish\n');
        const svg = color => '<svg xmlns="http://www.w3.org/2000/svg" width="48" height="48"><circle cx="24" cy="24" r="18" fill="'+color+'"/></svg>';
        fs.writeFileSync(file('design/mark.svg'),svg('#397042'));
        for (const [id,dir] of [[writeId,workspace],[readId,read]]) {
            const binding = edge('bind','--project',id,'--path',dir);
            bindings.push([id,binding.workspaceId,dir]);
        }
        const {client:w,names} = await open(workspace);
        const {client:sub} = await open(brand);
        for (const name of ['get_project_snapshot','get_knowledge_history','list_assets','publish_asset','submit_knowledge_commit']) assert(names.includes(name));
        const project = (await tool(w,'list_projects')).items[0];
        const nested = (await tool(sub,'list_projects')).items[0];
        assert.equal(project.projectId,writeId);
        assert.equal(project.workspaceRelativeDirectory,'.');
        assert.equal(nested.workspaceRelativeDirectory,'design');
        const relativePath = path.posix.join(nested.workspaceRelativeDirectory,'mark.svg');
        const overview = {title:label+' / 项目目的',body:'按标签整理本地便签。',evidence:'README.md § 便签'};
        const index = {title:label+' / 导出说明',body:'docs/export.md 说明导出的范围、编码和格式。',evidence:'docs/export.md § 导出说明；src/export.js 的 format'};
        assert(fs.readFileSync(file('README.md'),'utf8').includes(overview.body));
        assert(fs.readFileSync(file('docs/export.md'),'utf8').includes('UTF-8'));
        const initial = await tool(w,'search_project_knowledge',{query:label});
        assert.deepEqual(initial.items,[]);
        assert.deepEqual((await tool(w,'list_assets',{query:label})).items,[]);
        const first = {...ids('asset',label+'/design/mark.svg',0),name:label+' / 标志',purpose,relativePath};
        const original = await tool(sub,'publish_asset',first);
        assert.equal(original.sha256,sha(fs.readFileSync(file(relativePath))));
        assert.equal(original.version,1);
        function formatEntry(asset) {
            const format = fs.readFileSync(file('src/export.js'),'utf8').match(/format = "(\w+)"/)[1];
            return {...index,body:index.body+' 当前格式 '+format+'。',evidence:index.evidence+'；项目标志 '+asset.assetId+' v'+asset.version};
        }
        const summaryInput = {...overview,...ids('knowledge',overview.title,0),baseVersion:0};
        const indexInput = {...formatEntry(original),...ids('knowledge',index.title,0),baseVersion:0};
        const summary = await tool(w,'submit_knowledge_commit',summaryInput);
        const entry = await tool(w,'submit_knowledge_commit',indexInput);
        assert.equal(summary.version,1);
        assert.equal(entry.version,1);
        record('首次从绑定根与其 design 子目录经全局 STDIO 发布项目标志，登记带 README、文档章节、代码符号以及真实素材版本的两条知识');

        const snapshot = () => tool(w,'get_project_snapshot');
        const before = (await snapshot()).items.filter(i=>i.title.startsWith(label));
        assert.equal(before.length,2);
        assert(before.some(i=>i.title===index.title && i.evidence.includes(original.assetId) && i.body.includes('markdown')));
        const assets = (await tool(sub,'list_assets',{query:label})).items;
        assert.equal(assets.length,1);
        assert.equal(assets[0].sha256,sha(fs.readFileSync(file(relativePath))));
        for(const planned of [summaryInput,indexInput]){
            const found=before.find(item=>item.title===planned.title);
            assert.equal(found.body,planned.body); assert.equal(found.evidence,planned.evidence);
        }
        assert.equal((await tool(w,'submit_knowledge_commit',indexInput)).commitId,entry.commitId);
        assert.deepEqual(await tool(sub,'publish_asset',first),original);
        assert.equal((await tool(w,'get_knowledge_history',{itemId:entry.itemId})).items.length,1);
        record('重新盘点时两条正文、证据及原件散列均相同；重放原知识与原素材请求仍是同一条记录、同一版本');

        fs.writeFileSync(file('src/export.js'),'export const format = "html";\n');
        fs.writeFileSync(file(relativePath),svg('#304C86'));
        const changed = await tool(sub,'publish_asset',{...first,...ids('asset',original.assetId,1),assetId:original.assetId,baseVersion:1});
        assert.equal(changed.assetId,original.assetId); assert.equal(changed.version,2);
        assert.equal(changed.sha256,sha(fs.readFileSync(file(relativePath))));
        const updated = await tool(w,'submit_knowledge_commit',{...formatEntry(changed),itemId:entry.itemId,
            requestId:ids('knowledge',entry.itemId,1).requestId,baseVersion:1});
        assert.equal(updated.itemId,entry.itemId); assert.equal(updated.version,2);
        const after=(await snapshot()).items.filter(i=>i.title.startsWith(label));
        assert.equal(after.length,2);
        assert.equal(after.find(i=>i.itemId===summary.itemId).version,1);
        assert(after.find(i=>i.itemId===entry.itemId).body.includes('html'));
        assert.deepEqual((await tool(w,'get_knowledge_history',{itemId:entry.itemId})).items.map(i=>i.version),[2,1]);
        assert.equal((await tool(sub,'list_assets',{query:label})).items.length,1);
        assert.equal((await tool(sub,'get_asset',{assetId:original.assetId,version:1})).sha256,original.sha256);
        assert(!JSON.stringify(after).includes('placeholder-never-publish'));
        record('更改导出格式和同一来源 SVG 后，仅对应知识与素材升为 v2，项目目的仍为 v1；旧版可读取，测试私有文件未进入回读记录');

        const {client:ro,names:roNames}=await open(read), {client:unbound,names:outsideNames}=await open(outside);
        assert(roNames.includes('get_project_snapshot') && !roNames.includes('submit_knowledge_commit') && !roNames.includes('publish_asset'));
        assert.deepEqual((await tool(ro,'search_project_knowledge',{query:label})).items,[]);
        assert.deepEqual(outsideNames,['get_edge_status']);
        assert.equal((await tool(unbound,'get_edge_status')).status,'unbound');
        for(const [client,name,args] of [[ro,'submit_knowledge_commit',indexInput],[ro,'publish_asset',first],[unbound,'get_project_snapshot',{}]]) {
            assert.equal((await client.call('tools/call',{name,arguments:args})).isError,true);
        }
        record('只读目录可盘点、自身搜索不含另一项目的记录，两类写入均被拒绝；未绑定目录只有诊断，直接请求快照也被拒绝');
        fs.writeFileSync(evidence,JSON.stringify({checkedAtUtc:new Date().toISOString(),mode:'按 Skill 步骤构造的真实 MCP 回归，未启动模型回合',coreImage,
            edgeBinaries:binaryHashes(cli),skillsVersion:fs.readFileSync(path.join(skills,'VERSION'),'utf8').trim(),
            checks,knowledgeVersions:[summary.version,updated.version],assetVersions:[original.version,changed.version]},null,2)+'\n');
    } finally {
        for (const client of clients) client.close();
        try { for (const [id,workspaceId,dir] of bindings) edge('unbind','--project',id,'--workspace',workspaceId,'--path',dir); }
        finally { assert.equal(path.dirname(root),os.tmpdir()); fs.rmSync(root,{recursive:true,force:true,maxRetries:10,retryDelay:250}); }
    }
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});
