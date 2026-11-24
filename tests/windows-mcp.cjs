// node tests/windows-mcp.cjs <Edge.exe> <fixture.json> <output.json> [official codex.exe]
const assert = require('node:assert/strict');
const {spawn, spawnSync} = require('node:child_process');
const {createHash, randomBytes, randomUUID} = require('node:crypto');
const fs = require('node:fs');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');
const readline = require('node:readline');
const {setTimeout: delay} = require('node:timers/promises');

const version = '2025-11-25';
const init = {protocolVersion:version,capabilities:{},clientInfo:{name:'projecthub-windows-qa',version:'1'}};
const initialized = {jsonrpc:'2.0',method:'notifications/initialized'};
function runExe(exe,args,options={}) {
    const r = spawnSync(exe,args,{windowsHide:true,encoding:'utf8',timeout:45000,...options});
    assert.equal(r.status,0,path.basename(exe)+' '+args[0]+' failed');
    return r.stdout.trim();
}
function lines(exe,args,{roots, onToolListChanged, ...options}={}) {
    const ps = spawn(exe,args,{windowsHide:true,...options});
    const pending = new Map();
    let id = 0;
    ps.stderr.resume();
    const reader = readline.createInterface({input:ps.stdout});
    function finish(error) {
        for (const {reject} of pending.values()) reject(error);
        pending.clear();
    }
    ps.on('error',finish);
    ps.on('close',()=>finish(new Error(path.basename(exe)+' closed before response')));
    reader.on('line',line=>{
        let message;
        try {message=JSON.parse(line);} catch {return finish(new Error('invalid JSON-RPC output'));}
        if (roots && message.method==='roots/list')
            return send({jsonrpc:'2.0',id:message.id,result:{roots:roots().map(uri=>({uri}))}});
        if (message.method==='notifications/tools/list_changed') return onToolListChanged?.();
        const item = pending.get(message.id);
        if (!item || !('result' in message || 'error' in message)) return;
        pending.delete(message.id);
        if (message.error) item.reject(new Error(item.method+': '+message.error.message.slice(0,300)));
        else item.resolve(message.result);
    });
    const send = message=>ps.stdin.write(JSON.stringify(message)+'\n');
    return {
        call(method,params) {
            const key=++id;
            return new Promise((resolve,reject)=>{
                const timer=setTimeout(()=>{pending.delete(key);reject(new Error('timed out: '+method));},45000);
                pending.set(key,{method,resolve:value=>{clearTimeout(timer);resolve(value);},reject:e=>{clearTimeout(timer);reject(e);}});
                send({jsonrpc:'2.0',id:key,method,params});
            });
        },
        notify:send,
        close(){ps.stdin.end();ps.kill();}
    };
}
function argsFor(device,project,transport,mode) {
    const binding = device.projects.find(p=>p.projectId===project).bindingId;
    return ['--state',device.state,transport,'--project',project,'--binding',binding,'--mode',mode];
}
async function openPort() {
    const socket = net.createServer();
    await new Promise((resolve,reject)=>socket.once('error',reject).listen(0,'127.0.0.1',resolve));
    const port=socket.address().port;
    await new Promise(resolve=>socket.close(resolve));return port;
}
async function httpRpc(exe,args,env=process.env) {
    const token=randomBytes(32).toString('hex'), port=await openPort();
    const ps=spawn(exe,[...args,'--port',String(port)],{windowsHide:true,env:{...env,PROJECTHUB_MCP_TOKEN:token},stdio:'ignore'});
    const url='http://127.0.0.1:'+port+'/mcp';
    const request=(body,auth,headers={},method='POST')=>fetch(url,{method,signal:AbortSignal.timeout(15000),
        headers:{'Content-Type':'application/json',Accept:'application/json, text/event-stream','MCP-Protocol-Version':version,
            ...(auth && {Authorization:'Bearer '+auth}),...headers},body:JSON.stringify(body)});
    try {
        let ready=false;
        for (let i=0;i<75;i++) {
            try {ready=(await request(initialized)).status===401;}catch{}
            if(ready) break; await delay(150);
        }
        assert(ready,'HTTP MCP listener was not ready');
        assert.equal((await request(initialized,token,{Origin:'http://example.invalid'})).status,403);
    } catch(e) {ps.kill();throw e;}
    function client() {
        let id=0, sessionHeaders={};
        return {async notify(message){assert.equal((await request(message,token,sessionHeaders)).status,202);},
        async call(method,params,headers={}) {
            const key=++id;
            const res=await request({jsonrpc:'2.0',id:key,method,params},token,{...sessionHeaders,...headers});
            if (method==='initialize' && res.headers.has('Mcp-Session-Id'))
                sessionHeaders={'Mcp-Session-Id':res.headers.get('Mcp-Session-Id')};
            assert.equal(res.status,200,method);
            const raw=await res.text();
            const answer=res.headers.get('content-type')?.includes('text/event-stream')
                ? raw.split(/\r?\n/).filter(l=>l.startsWith('data:')).map(l=>JSON.parse(l.slice(5))).find(x=>x.id===key)
                : JSON.parse(raw);
            if (answer?.error) throw new Error(method+': '+answer.error.message.slice(0,300));
            assert(answer?.result,method+' missing result');
            return answer.result;
        }};
    }
    return {...client(), peer:client, raw:(body,headers,method)=>request(body,token,headers,method),close(){ps.kill();}};
}
function unpack(response) {
    assert(!response.isError,'MCP tool returned an error');
    return response.structuredContent ?? JSON.parse(response.content.find(x=>x.type==='text').text);
}
async function handshake(connection, capabilities=init.capabilities, clientInfo=init.clientInfo) {
    const result=await connection.call('initialize',{...init,capabilities,clientInfo});
    assert.equal(result.protocolVersion,version);
    assert(result.capabilities.tools);
    await connection.notify(initialized);
    const list=await connection.call('tools/list',{});
    return list.tools.map(t=>t.name).sort();
}
const tool=(c,name,args={})=>c.call('tools/call',{name,arguments:args}).then(unpack);
async function verifyCodex(codex,exe,device,project) {
    const temp=fs.mkdtempSync(path.join(os.tmpdir(),'projecthub-mcp-client-'));
    const profile=path.join(temp,'profile');fs.mkdirSync(profile);
    const server='projecthub_fixture', mcpArgs=argsFor(device,project,'mcp-stdio','readOnly');
    // Start a separate client with a throwaway profile. No desktop thread, user config or model turn is used.
    const cliArgs=['app-server','--listen','stdio://','-c','mcp_servers.'+server+'.command='+JSON.stringify(exe),
        '-c','mcp_servers.'+server+'.args='+JSON.stringify(mcpArgs)];
    const codexRpc=lines(codex,cliArgs,{cwd:temp,env:{...process.env,CODEX_HOME:profile}});
    try{
        const info=await codexRpc.call('initialize',{clientInfo:{name:'projecthub_mcp_fixture',version:'1'},capabilities:{experimentalApi:true}});
        codexRpc.notify({method:'initialized'});
        // The public direct tool-call RPC requires a thread ID. This ephemeral thread never starts a turn.
        const thread=await codexRpc.call('thread/start',{ephemeral:true,cwd:temp,sandbox:'read-only',approvalPolicy:'never'});
        const id=thread.thread.id;
        const inventory=await codexRpc.call('mcpServerStatus/list',{threadId:id,serverName:server});
        const registered=inventory.data.find(i=>i.name===server);
        assert(registered && Object.keys(registered.tools).includes('list_projects'),'Codex MCP inventory missing list_projects');
        const result=unpack(await codexRpc.call('mcpServer/tool/call',{threadId:id,server,tool:'list_projects',arguments:{}}));
        assert.equal(result.items[0].projectId,project);
        assert.equal(result.items[0].accessMode,'readOnly');
        return {client:runExe(codex,['--version']),initialized:!!info,toolCount:Object.keys(registered.tools).length,
            tool:'list_projects',readProjectMatched:true,ephemeral:true,modelTurns:0};
    }finally{codexRpc.close();}
}
async function run(){
    const [exeFile,fixtureFile,output,codex]=process.argv.slice(2);
    const exe=path.resolve(exeFile),fixture=JSON.parse(fs.readFileSync(fixtureFile));
    const develop=fixture.devices.find(d=>d.role==='development'), feedback=fixture.devices.find(d=>d.role==='feedback');
    const primary=fixture.project, secondary=fixture.secondaryProject;
    const checks=[], clients=[];
    const pass=what=>{checks.push(what);console.log('PASS',what);};
    const stdio=(device,p,mode)=>{const c=lines(exe,argsFor(device,p,'mcp-stdio',mode));clients.push(c);return c;};
    try{
        const writer=stdio(develop,primary,'readWrite');
        const names=await handshake(writer);
        const readerArgs=argsFor(feedback,primary,'mcp-http','readOnly');
        const reader=await httpRpc(exe,readerArgs);clients.push(reader);
        assert.deepEqual(await handshake(reader),names);
        const peer=stdio(feedback,primary,'readOnly');assert.deepEqual(await handshake(peer),names);
        assert.deepEqual(await tool(reader,'list_projects'),await tool(peer,'list_projects'));
        assert.deepEqual((await tool(reader,'list_projects')).items.map(p=>[p.projectId,p.accessMode]),[[primary,'readOnly']]);
        pass('STDIO 与 Streamable HTTP 协商同一协议和工具；同一客户端绑定仅返回获准项目与有效权限，HTTP 缺少凭证或带浏览器 Origin 被拒');
        const tag=randomUUID(), data={requestId:randomUUID(),title:'MCP verification '+tag,body:'First observed value.',evidence:'local:test/'+tag};
        const v1=await tool(writer,'submit_knowledge_commit',data);
        assert.deepEqual(await tool(writer,'submit_knowledge_commit',data),v1);
        const v2=await tool(writer,'submit_knowledge_commit',{...data,requestId:randomUUID(),baseVersion:1,itemId:v1.itemId,body:'Updated observation.'});
        assert.deepEqual((await tool(reader,'search_project_knowledge',{query:tag})).items.map(v=>v.commitId),[v2.commitId]);
        const snapshot=await tool(reader,'get_project_snapshot');
        assert(snapshot.items.some(v=>v.commitId===v2.commitId && v.evidence===data.evidence));
        assert(!snapshot.items.some(v=>v.commitId===v1.commitId));
        assert.deepEqual((await tool(peer,'get_knowledge_history',{itemId:v1.itemId})).items.map(v=>v.version),[2,1]);
        pass('独立 Windows 开发端经 STDIO 提交带证据的知识，反馈端经 HTTP/STDIO 读取最新快照、关键词和两版历史；重试同一提交只产生一版');
        assert((await reader.call('tools/call',{name:'submit_knowledge_commit',arguments:{...data,requestId:randomUUID()}})).isError);
        const secondaryDir=path.join(path.dirname(develop.workspace),'readonly-workspace');
        fs.mkdirSync(secondaryDir,{recursive:true});
        runExe(exe,['--state',develop.state,'bind','--project',secondary,'--path',secondaryDir]);
        const scoped=stdio(develop,secondary,'readWrite');await handshake(scoped);
        assert.equal((await tool(scoped,'list_projects')).items[0].accessMode,'readOnly');
        assert.deepEqual((await tool(scoped,'search_project_knowledge',{query:tag})).items,[]);
        assert((await scoped.call('tools/call',{name:'submit_feedback',arguments:{requestId:randomUUID(),content:'scope check'}})).isError);
        pass('当前客户端只读和设备项目只读分别拒绝写入，另一项目不能搜索到本次知识');
        const official=codex?await verifyCodex(path.resolve(codex),exe,feedback,primary):null;
        if(official) pass('官方 Codex app-server 在临时进程配置下发现并直接调用 ProjectHub MCP 工具，未发起模型回合');
        fs.mkdirSync(path.dirname(output),{recursive:true});
        fs.writeFileSync(output,JSON.stringify({checkedAtUtc:new Date().toISOString(),protocolVersion:version,
            transports:['stdio','streamable-http'],tools:names,official,checks},null,2)+'\n');
    }finally{for(const c of clients)c.close();}
}
function binaryHashes(executable) {
    const stem = path.basename(executable, path.extname(executable));
    const adjacent = [stem + '.dll'];
    if (stem === 'ProjectHub.Edge') adjacent.push('NodicartaEdgeCli.dll');
    const files = [executable, ...adjacent.map(name => path.join(path.dirname(executable), name)).filter(fs.existsSync)];
    return Object.fromEntries(files.map(file => [path.basename(file), createHash('sha256').update(fs.readFileSync(file)).digest('hex')]));
}
module.exports={lines,runExe,handshake,tool,argsFor,httpRpc,binaryHashes};
if(require.main===module)run().catch(e=>{console.error(e.message);process.exitCode=1;});
