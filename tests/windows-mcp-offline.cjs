// Use a distinct disposable container created from the M1.1 fixture. This test stops/restarts ONLY that container.
// node tests/windows-mcp-offline.cjs <Edge.exe> <loopback URL> <disposable core container> <evidence.json>
const assert = require('node:assert/strict');
const {createHash,randomUUID,randomBytes} = require('node:crypto');
const {existsSync,mkdtempSync,mkdirSync,readFileSync,writeFileSync} = require('node:fs');
const {spawn,spawnSync} = require('node:child_process');
const {once} = require('node:events');
const {setTimeout:delay} = require('node:timers/promises');
const os = require('node:os');
const path = require('node:path');
const {chromium} = require('playwright-core');
const {windows,docker,login,resetPassword,projectId,visit,openDeviceAddition,submit} = require('./core-fixture.cjs');
const {runExe,lines,tool,handshake} = require('./windows-mcp.cjs');

async function run() {
    const [exeFile,base,container,output,archive]=process.argv.slice(2);
    const url=new URL(base);
    assert(windows && ['localhost','127.0.0.1'].includes(url.hostname));
    assert(container && container.includes('offline'),'Use an isolated container with "offline" in its name');
    const exe=path.resolve(exeFile);
    const temp=mkdtempSync(path.join(os.tmpdir(),'projecthub-offline-'));
    const state=path.join(temp,'state'),workspace=path.join(temp,'workspace');
    mkdirSync(workspace);
    const browser=await chromium.launch({channel:'msedge',headless:true});
    let rpc, worker, stopped=false;
    try {
        const page=await browser.newPage({baseURL:base, locale:'zh-CN'});
        const password=randomBytes(32).toString('base64url');
        await resetPassword(container,password);
        await page.goto('/login');assert.equal(await login(page,process.env.PROJECTHUB_TEST_ADMIN,password),302);
        const project=await projectId(page,'验收项目 A');
        const form=await openDeviceAddition(page);
        await form.getByLabel('设备名称').fill('离线验收 '+randomUUID().slice(0,8));
        await form.locator('[name="grants['+project+']"]').selectOption('readWrite');
        assert.equal(await submit(page,'生成配对码',form),200);
        const offer=await page.getByRole('dialog').locator('#pairing-code').inputValue();
        const device=JSON.parse(runExe(exe,['--state',state,'pair','--core',base],{input:offer+'\n'}));
        const bound=JSON.parse(runExe(exe,['--state',state,'bind','--project',project,'--path',workspace]));
        const options=['--project',project,'--binding',bound.bindingId,'--mode','readWrite'];
        const start=()=>lines(exe,['--state',state,'mcp-stdio',...options]);
        rpc=start();await handshake(rpc);
        const requestId=randomUUID(), tag=randomUUID();
        const data={requestId,title:'Offline '+tag,body:'Offline draft round trip.',evidence:'fixture:'+tag};
        const restore=async()=>{docker('start',container);stopped=false;await visit(page,new URL('/login',base));};
        docker('stop',container);stopped=true;
        assert.deepEqual(await tool(rpc,'submit_knowledge_commit',data),{outcome:'queuedLocally',requestId});
        assert.deepEqual(await tool(rpc,'submit_knowledge_commit',data),{outcome:'queuedLocally',requestId});
        assert((await rpc.call('tools/call',{name:'get_project_snapshot',arguments:{}})).isError);
        rpc.close();rpc=start();await handshake(rpc);
        await restore();
        runExe(exe,['--state',state,'sync',...options]);
        runExe(exe,['--state',state,'sync',...options]);
        const committed=(await tool(rpc,'search_project_knowledge',{query:tag})).items;
        assert.equal(committed.length,1);
        assert.deepEqual((await tool(rpc,'get_knowledge_history',{itemId:committed[0].itemId})).items.map(x=>x.version),[1]);

        const autoTag=randomUUID(),automatic={...data,requestId:randomUUID(),title:'Background '+autoTag};
        docker('stop',container);stopped=true;
        assert.equal((await tool(rpc,'submit_knowledge_commit',automatic)).outcome,'queuedLocally');
        worker=spawn(exe,['--state',state,'run'],{windowsHide:true,stdio:'ignore'});
        const statusFile=path.join(state,'connection.json');
        const workerOffline=()=>existsSync(statusFile) && JSON.parse(readFileSync(statusFile)).state==='offline';
        const firstCheck=Date.now()+25000;
        while(!workerOffline() && Date.now()<firstCheck) await delay(200);
        assert(workerOffline(),'waiting for offline background check');
        await restore();
        const resumed=Date.now(),deadline=resumed+90000;
        let visible=[];
        while(Date.now()<deadline){
            visible=(await tool(rpc,'search_project_knowledge',{query:autoTag})).items;
            if(visible.length)break;
            await delay(1500);
        }
        assert.equal(visible.length,1,'run did not upload the offline draft');
        const waitSeconds=Math.round((Date.now()-resumed)/1000);
        assert.deepEqual((await tool(rpc,'get_knowledge_history',{itemId:visible[0].itemId})).items.map(x=>x.version),[1]);
        const exited=once(worker,'close');worker.kill();await exited;worker=null;
        const autoSync={mode:'run',queuedWhileOffline:true,observedOfflineBeforeReconnect:true,
            manualSyncCount:0,secondsAfterReconnect:waitSeconds,visibleVersions:1,commitId:visible[0].commitId};
        console.log('PASS 在离线时已启动的 run 进程重连后自行上传，未执行 sync，读回一条记录和一个版本');

        const nextTag=randomUUID(),feedback={requestId:randomUUID(),content:'Revoked offline '+nextTag};
        docker('stop',container);stopped=true;
        assert.equal((await tool(rpc,'submit_feedback',feedback)).outcome,'queuedLocally');
        await restore();
        await page.goto('/devices/'+device.deviceId);
        const action=page.locator('form[data-project-id="'+project+'"]').filter({has:page.locator('input[value="'+bound.bindingId+'"]')});
        assert.equal(await action.count(),1);
        await action.locator('select').selectOption('none');
        assert.equal(await submit(page,'保存权限',action),200);
        // The stale binding's draft is paused by the first sync; later retries cannot post it.
        const rejected=spawnSync(exe,['--state',state,'sync',...options],{encoding:'utf8',windowsHide:true});
        assert.notEqual(rejected.status,0);
        runExe(exe,['--state',state,'sync',...options]);
        const {DatabaseSync}=require('node:sqlite');
        const drafts=new DatabaseSync(path.join(state,'mvp.sqlite3'),{readOnly:true});
        try {assert.deepEqual(drafts.prepare('SELECT paused FROM draft WHERE request=?').get(feedback.requestId)?.paused,1);}
        finally{drafts.close();}
        const checks=['隔离 Core 停止后，只写入本地草稿，重复提交同一请求仍待同步，离线读取失败',
            '重启 MCP 进程并恢复 Core，两次同步后只有一个提交、一个版本',
            '第二次离线提交后，在重连同步前网页撤销项目授权，原草稿暂停，后续同步未重新投递'];
        mkdirSync(path.dirname(output),{recursive:true});
        const packageInfo=archive?{name:path.basename(archive),sha256:createHash('sha256').update(readFileSync(archive)).digest('hex'),
            executableSha256:createHash('sha256').update(readFileSync(exe)).digest('hex')}:null;
        writeFileSync(output,JSON.stringify({checkedAtUtc:new Date().toISOString(),container,checks,autoSync,package:packageInfo},null,2)+'\n');
        checks.forEach(c=>console.log('PASS',c));
    } finally {
        worker?.kill();
        if(stopped)docker('start',container);
        rpc?.close();await browser.close();
    }
}
run().then(()=>process.stdout.write('',()=>process.exit(0))).catch(e=>process.stderr.write(e.message+'\n',()=>process.exit(1)));
