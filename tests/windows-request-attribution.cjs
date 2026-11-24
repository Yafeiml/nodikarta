// node tests/windows-request-attribution.cjs <desktop.exe> <pre-attribution Edge.exe> <checks.json> [release.zip]
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const {randomBytes, randomUUID, createHash} = require('node:crypto');
const {once} = require('node:events');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const {DatabaseSync} = require('node:sqlite');
const {setTimeout: delay} = require('node:timers/promises');
const {lines, handshake, tool, httpRpc, binaryHashes} = require('./windows-mcp.cjs');

async function until(check, reason) {
    const end = Date.now() + 15000;
    while (!check()) {
        assert(Date.now() < end, reason);
        await delay(80);
    }
}
async function run() {
    const [desktop, legacy, output, archive] = process.argv.slice(2).map(p => path.resolve(p));
    const cli = path.join(path.dirname(desktop), 'ProjectHub.Edge.exe');
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'nodicarta-origin-'));
    const state = path.join(root, 'state'), workspace = path.join(root, 'workspace');
    fs.mkdirSync(workspace);
    const profile = path.join(root, 'profile'); fs.mkdirSync(profile);
    const env = {...process.env, USERPROFILE:profile, HOME:profile, CODEX_HOME:path.join(profile,'.codex'),
        APPDATA:path.join(profile,'roaming'), LOCALAPPDATA:path.join(profile,'local'),
        XDG_CONFIG_HOME:path.join(profile,'.config'), NODICARTA_EDGE_STATE:state, PROJECTHUB_EDGE_STATE:state};
    for (const key of ['OPENCODE_CONFIG','OPENCODE_CONFIG_DIR','OPENCODE_CONFIG_CONTENT','CLAUDE_CONFIG_DIR','DSH_HOME','PI_CODING_AGENT_DIR']) delete env[key];
    const project = randomUUID(), binding = randomUUID(), offer = randomUUID(), code = randomBytes(16).toString('hex');
    const deviceId = randomUUID(), edgeId = randomUUID(), accessToken = randomBytes(32).toString('hex');
    const alpha = {name:'Claude Code',version:'2.1.13'}, beta = {name:'Cursor',version:'3.4.1'};
    const parallelIds = [randomUUID(),randomUUID()], httpIds = [randomUUID(),randomUUID()];
    const barriers = [parallelIds,httpIds].map(ids=>({ids,active:0,peak:0}));
    const legacyId = randomUUID(), retryId = randomUUID();
    const started = Date.now(), commits = new Map(), requests = [], clients = [], processes = new Set();
    let phase = 'pair', offline = true;
    const prefix = '/v1/projects/'+project+'/bindings/'+binding+'/';
    const headerNames = ['client','client-version','entry','origin-at','replay'];
    const server = http.createServer(async (req,res) => {
        try {
            let body = ''; for await (const chunk of req) body += chunk;
            const data = body ? JSON.parse(body) : {};
            const headers = Object.fromEntries(headerNames.filter(key => 'x-nodicarta-'+key in req.headers)
                .map(key => [key,req.headers['x-nodicarta-'+key]]));
            requests.push({phase,url:req.url,method:req.method,requestId:data.requestId,headers,
                bodyHash:data.requestId && createHash('sha256').update(body).digest('hex')});
            function send(status,value) {res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value));}
            if (req.url === '/v1/device-pairings/'+offer+'/redeem' && data.oneTimeCode === code)
                return send(201,{deviceId,edgeId,accessToken,refreshToken:randomBytes(32).toString('hex'),expiresAt:new Date(Date.now()+3600000)});
            if (req.headers.authorization !== 'Bearer '+accessToken) return send(401,{});
            if (req.url === '/v1/projects') return send(200,{items:[{name:'Attribution QA',projectId:project,bindingId:binding,accessMode:'readWrite'}]});
            if (req.url === prefix+'feedback' && req.method === 'POST') {
                const barrier=barriers.find(pair=>pair.ids.includes(data.requestId));
                if (barrier) {
                    barrier.active++; barrier.peak=Math.max(barrier.active,barrier.peak);
                    try { await until(() => barrier.peak >= 2,'the two MCP writes never overlapped at Core'); }
                    finally { barrier.active--; }
                }
                if (offline && data.requestId === legacyId) return send(503,{});
                const previous = commits.get(data.requestId);
                if (previous && previous.body !== body) return send(409,{});
                if (!previous) commits.set(data.requestId,{body,ack:{feedbackId:randomUUID()}});
                // The write completed; the client receives no successful acknowledgement.
                if (offline && data.requestId === retryId) return send(503,{});
                return send(200,commits.get(data.requestId).ack);
            }
            if (req.url === prefix+'feedback' && req.method === 'GET') return send(200,{items:[]});
            return send(404,{});
        } catch (e) { res.destroy(e); }
    });
    function launch(exe,args) {
        const p = spawn(exe,['--state',state,...args],{windowsHide:true,cwd:workspace,env,stdio:['pipe','ignore','pipe']});
        processes.add(p); p.stderr.resume(); p.once('exit',()=>processes.delete(p)); return p;
    }
    async function command(args,input) {
        const p = launch(cli,args), done = once(p,'exit');
        p.stdin.end(input);
        assert.equal((await done)[0],0,'CLI command failed: '+args[0]);
    }
    async function stop(p) {
        if (!processes.has(p)) return;
        const stopped=once(p,'exit'); p.kill(); await stopped;
    }
    const scope = ['--project',project,'--binding',binding,'--mode','readWrite'];
    async function connect(exe,client,global=false) {
        const rpc = lines(exe,['--state',state,...(global?['mcp-stdio-global']:['mcp-stdio',...scope])],{cwd:workspace,env});
        clients.push(rpc);
        await handshake(rpc,{},client); return rpc;
    }
    const feedback = (rpc,requestId) => tool(rpc,'submit_feedback',{requestId,content:'Fixture record '+requestId});
    const draftFile = path.join(state,'mvp.sqlite3');
    function pending() {
        const db = new DatabaseSync(draftFile,{readOnly:true});
        try { return db.prepare('select count(*) n from draft').get().n; }
        finally { db.close(); }
    }
    try {
        server.listen(0,'127.0.0.1'); await once(server,'listening');
        await command(['pair','--core','http://127.0.0.1:'+server.address().port],offer+'.'+code+'\n');
        phase='cli'; await command(['projects']); await command(['bind','--project',project,'--path',workspace]);
        phase='gui';
        const app=launch(desktop,[]);
        await until(()=>requests.filter(r=>r.phase==='gui' && r.url==='/v1/projects').length >= 2,'no GUI refresh/heartbeat');
        await stop(app);
        phase='legacy';
        const older = await connect(legacy,{name:'Legacy fixture',version:'1'});
        assert.equal((await feedback(older,legacyId)).outcome,'queuedLocally');
        older.close(); clients.splice(clients.indexOf(older),1);
        assert.equal(pending(),1);
        const oldDb=new DatabaseSync(draftFile,{readOnly:true});
        try {
            const columns=oldDb.prepare('PRAGMA table_info(draft)').all().map(row=>row.name);
            assert.deepEqual(columns,['binding','request','resource','body','paused'],'fixture must originate from the legacy schema');
        } finally { oldDb.close(); }
        phase='parallel';
        const [a,b]=await Promise.all([connect(cli,alpha),connect(cli,beta,true)]);
        await Promise.all([feedback(a,parallelIds[0]),feedback(b,parallelIds[1])]);
        phase='known-reads';
        await Promise.all([tool(a,'list_feedback'),tool(b,'list_feedback')]);
        phase='http-sessions';
        const listener=await httpRpc(cli,['--state',state,'mcp-http',...scope],env);
        clients.push(listener);
        const second=listener.peer();
        await Promise.all([handshake(listener,{},alpha),handshake(second,{},beta)]);
        await Promise.all([feedback(listener,httpIds[0]),feedback(second,httpIds[1])]);
        phase='unknown-http';
        // Older HTTP callers can invoke tools without an initialized session.
        await tool(listener.peer(),'list_feedback');
        phase='queued';
        assert.equal((await feedback(a,retryId)).outcome,'queuedLocally');
        await delay(180);
        assert.equal((await feedback(b,retryId)).outcome,'queuedLocally');
        const conflict=await b.call('tools/call',{name:'submit_feedback',arguments:{requestId:retryId,content:'different fixture body'}});
        assert.equal(conflict.isError,true);
        assert.equal(pending(),2);
        for (const rpc of clients.splice(0)) rpc.close();
        offline=false; phase='after-restart';
        const worker=launch(cli,['run']);
        await until(()=>pending()===0,'worker did not finish replaying both persisted drafts');
        await stop(worker);
        phase='second-sync'; await command(['sync',...scope]); assert.equal(pending(),0);
        const reads=p=>requests.filter(r=>r.phase===p && r.url==='/v1/projects');
        const plain=entry=>({entry});
        assert.deepEqual(requests.find(r=>r.phase==='pair').headers,plain('cli'));
        for (const r of reads('cli')) assert.deepEqual(r.headers,plain('cli'));
        assert(reads('gui').some(r=>r.headers.entry==='gui'),'no GUI-attributed request');
        assert(reads('gui').some(r=>r.headers.entry==='background'),'no background-attributed request');
        for (const r of reads('gui')) {
            assert(['gui','background'].includes(r.headers.entry));assert.deepEqual(r.headers,plain(r.headers.entry));
        }
        assert(reads('after-restart').some(r=>r.headers.entry==='background'));
        const namedReads=requests.filter(r=>r.phase==='known-reads' && r.url===prefix+'feedback');
        assert.equal(namedReads.length,2);
        for(const actor of [alpha,beta]) assert.deepEqual(namedReads.find(r=>r.headers.client===actor.name)?.headers,
            {client:actor.name,'client-version':actor.version,entry:'mcp'});
        const unknown=requests.find(r=>r.phase==='unknown-http' && r.url===prefix+'feedback');
        assert.deepEqual(unknown?.headers,plain('mcp'));
        const writes=id=>requests.filter(r=>r.requestId===id && r.url===prefix+'feedback');
        function original(h,actor) {
            assert.match(h['origin-at'] || '',/Z$|\+00:00$/);
            assert(Date.parse(h['origin-at']) >= started - 1000 && Date.parse(h['origin-at']) <= Date.now() + 1000);
            assert.deepEqual(h,{client:actor.name,'client-version':actor.version,entry:'mcp','origin-at':h['origin-at']});
        }
        for (const ids of [parallelIds,httpIds]) for (const [i,id] of ids.entries()) {
            assert.equal(writes(id).length,1);original(writes(id)[0].headers,[alpha,beta][i]);
        }
        const recovered=writes(retryId); assert.equal(recovered.length,3);
        original(recovered[0].headers,alpha);
        for (const r of recovered.slice(1)) assert.deepEqual(r.headers,{...recovered[0].headers,replay:'1'});
        assert.equal(recovered.at(-1).phase,'after-restart');
        assert.deepEqual(writes(legacyId).map(r=>r.headers),[{}, {replay:'1'}]);
        assert.equal(writes(legacyId)[1].phase,'after-restart');
        for(const id of [legacyId,retryId]) assert.equal(new Set(writes(id).map(r=>r.bodyHash)).size,1,'replay changed request body');
        assert.equal(commits.size,6);
        assert(!requests.some(r=>r.phase==='second-sync' && r.method==='POST'),'drained outbox was submitted again');
        const hash=file=>createHash('sha256').update(fs.readFileSync(file)).digest('hex');
        const checks={checkedAtUtc:new Date().toISOString(),...(archive && {archive:path.basename(archive),zipSha256:hash(archive)}),
            desktopBinaries:binaryHashes(desktop),
            edgeBinaries:binaryHashes(cli),legacyEdgeBinaries:binaryHashes(legacy),
            plainReads:{gui:reads('gui').find(r=>r.headers.entry==='gui').headers,cli:reads('cli')[0].headers,
                background:reads('after-restart').find(r=>r.headers.entry==='background').headers},
            simultaneousWritesAtCore:{stdio:barriers[0].peak,sharedHttpListener:barriers[1].peak},parallelClients:[alpha,beta],
            parallelStdioWrites:parallelIds.map(id=>writes(id)[0].headers),sharedHttpWrites:httpIds.map(id=>writes(id)[0].headers),
            unknownStatelessClient:unknown.headers,retryAfterRestart:recovered.map(r=>r.headers),legacyReplay:writes(legacyId)[1].headers,
            originalRequestBodiesPreserved:true,conflictingRequestIdRejected:true,committedRequestIds:commits.size,pendingAfterSecondSync:0};
        fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(checks,null,2)+'\n');
        console.log('PASS four entry points, overlapping MCP clients, unknown stateless identity, original and legacy outbox replay');
    } finally {
        for(const rpc of clients) rpc.close();
        await Promise.all([...processes].map(stop));
        server.closeAllConnections(); await new Promise(resolve=>server.close(resolve));
        if (path.dirname(root)===os.tmpdir() && path.basename(root).startsWith('nodicarta-origin-'))
            fs.rmSync(root,{recursive:true,force:true,maxRetries:5,retryDelay:100});
    }
}
run().catch(e=>{console.error(e.stack);process.exitCode=1;});
