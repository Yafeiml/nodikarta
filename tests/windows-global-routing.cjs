// node tests/windows-global-routing.cjs <Edge.exe>
const assert = require('node:assert/strict');
const {spawn, execFileSync} = require('node:child_process');
const {randomBytes, randomUUID} = require('node:crypto');
const {once} = require('node:events');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const {DatabaseSync} = require('node:sqlite');
const {lines, handshake, tool, httpRpc} = require('./windows-mcp.cjs');

async function run() {
    const exe = path.resolve(process.argv[2]);
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'projecthub-global-routing-'));
    const state = path.join(root, 'state');
    const clients = [];
    const deviceId = randomUUID(), code = randomBytes(16).toString('hex');
    const token = randomBytes(32).toString('hex');
    const project = (name, accessMode) => ({name, accessMode, projectId:randomUUID(), bindingId:randomUUID()});
    const a = project('A', 'readWrite'), b = project('B', 'readOnly');
    let grants = [a, b], offline = false;
    const endpoints = [randomUUID(),randomUUID()];
    const grantSources = [];
    const server = http.createServer((req, res) => {
        const send = (status, data) => { res.writeHead(status, {'Content-Type':'application/json'}); res.end(JSON.stringify(data)); };
        const source = [req.headers['x-nodicarta-entry'],req.headers['x-nodicarta-client'] ?? null,req.headers['x-nodicarta-client-version'] ?? null];
        if (req.url.endsWith('/redeem')) return send(201, {deviceId, edgeId:randomUUID(), accessToken:token,
            refreshToken:randomBytes(32).toString('hex'), expiresAt:new Date(Date.now()+3600000).toISOString()});
        if (req.headers.authorization !== 'Bearer '+token) return send(401, {});
        if (req.url === '/v1/projects') {
            grantSources.push(source);
            return offline ? send(503,{}) : send(200, {items:grants});
        }
        if (req.url.endsWith('/endpoints')) return send(200, {items:endpoints.map(endpointId => ({deviceId,endpointId,name:'fixture'}))});
        if (req.url.endsWith('/tasks')) return send(200, {items:endpoints.map(targetEndpointId => ({targetEndpointId}))});
        const url = new URL(req.url,'http://localhost');
        if (grants.some(p => url.pathname === '/v1/projects/'+p.projectId+'/bindings/'+p.bindingId+'/knowledge')) {
            return send(200, {ok:true, filters:Object.fromEntries(url.searchParams),source});
        }
        send(404, {});
    });
    async function cli(args, input) {
        const child = spawn(exe, ['--state',state,...args], {windowsHide:true});
        let stdout = '', stderr = '';
        child.stdout.on('data', chunk => stdout+=chunk);
        child.stderr.on('data', chunk => stderr+=chunk);
        child.stdin.end(input);
        if ((await once(child,'exit'))[0]) throw Error(stderr.trim());
        return JSON.parse(stdout);
    }
    function connect(cwd, workspaceVariable, value) {
        const args = ['mcp-stdio-global','--state',state];
        const env = {...process.env};
        if (workspaceVariable) {
            args.push('--workspace-env',workspaceVariable);
            if (value === undefined) delete env[workspaceVariable];
            else env[workspaceVariable] = value;
        }
        const client = lines(exe,args,{cwd,env});
        clients.push(client);
        return client;
    }
    async function toolNames(client) {
        return (await client.call('tools/list',{})).tools.map(t => t.name).sort();
    }
    async function expectDiagnostic(client, status, message) {
        assert.deepEqual(await toolNames(client), ['get_edge_status']);
        const diagnostic = await tool(client, 'get_edge_status');
        assert.equal(diagnostic.status, status);
        if (message) assert.match(diagnostic.message, message);
        assert.equal((await client.call('tools/call',{name:'get_project_snapshot',arguments:{}})).isError, true);
    }
    let db;
    try {
        server.listen(0,'127.0.0.1'); await once(server,'listening');
        await cli(['pair','--core','http://127.0.0.1:'+server.address().port],randomUUID()+'.'+code+'\n');
        const folders = Object.fromEntries(['alpha','alpha/nested','alpha/sub','alpha2','beta','beta/测试hub','unknown'].map(d => {
            const full = path.join(root,d); fs.mkdirSync(full,{recursive:true}); return [d,full];
        }));
        const bind = (p, dir) => cli(['bind','--project',p.projectId,'--path',dir]);
        const workspaceA = await bind(a, folders.alpha);
        const nested = await bind(a, folders['alpha/nested']);
        await bind(b, folders.beta);
        await assert.rejects(bind(b, folders.alpha));
        await assert.rejects(bind(b, folders['alpha/sub']));
        await assert.rejects(bind(b, root));
        db = new DatabaseSync(path.join(state,'edge.sqlite3'));
        const setPolicy = db.prepare('INSERT OR REPLACE INTO workspace_access(workspace_id, tools, endpoint) VALUES (?, ?, NULL)');
        setPolicy.run(nested.workspaceId,JSON.stringify(['get_project_snapshot']));
        const A = connect(folders['alpha/sub']), B = connect(folders.beta), sub = connect(folders['alpha/nested']);
        const [toolsA,toolsB,toolsSub] = await Promise.all([A,B,sub].map(c=>handshake(c)));
        assert.ok(toolsA.includes('submit_feedback') && !toolsA.includes('list_tasks'));
        assert.ok(!toolsB.includes('submit_feedback'));
        assert.deepEqual(toolsSub,['get_project_snapshot']);
        assert.equal((await tool(A,'list_projects')).items[0].projectId,a.projectId);
        assert.equal((await tool(B,'list_projects')).items[0].projectId,b.projectId);
        for (const variable of ['CLAUDE_PROJECT_DIR','PROJECTHUB_WORKSPACE']) {
            const routed = connect(folders.alpha,variable,folders.beta);
            await handshake(routed);
            assert.equal((await tool(routed,'list_projects')).items[0].projectId,b.projectId);
        }
        for (const value of [undefined,'.','${workspaceFolder}']) {
            const missing = connect(folders.alpha,'PROJECTHUB_WORKSPACE',value);
            await handshake(missing,{}, {name:'Cursor',version:'1.0.0'});
            await expectDiagnostic(missing,value === '${workspaceFolder}' ? 'unbound' : 'ambiguous');
        }
        await assert.rejects(cli(['mcp-stdio-global','--workspace-env','TEMP']));
        const noRoots = lines(exe,['mcp-stdio-global','--state',state,'--workspace-roots'],{cwd:folders.alpha});
        clients.push(noRoots);
        await handshake(noRoots,{});
        await expectDiagnostic(noRoots,'unbound',/没有声明支持/);
        let roots = [], notifyToolsChanged;
        const rootClient = lines(exe,['mcp-stdio-global','--state',state,'--workspace-roots'],{
            cwd:folders.alpha, roots:()=>roots, onToolListChanged:()=>notifyToolsChanged?.()
        });
        clients.push(rootClient);
        assert.deepEqual(await handshake(rootClient,{roots:{listChanged:true}},{name:'roots-client',version:'2.5'}),['get_edge_status']);
        await expectDiagnostic(rootClient,'unbound',/尚未提供工作目录/);
        async function changeRoots(uris) {
            roots = uris;
            const refreshed = new Promise(resolve=>notifyToolsChanged=resolve);
            rootClient.notify({jsonrpc:'2.0',method:'notifications/roots/list_changed'});
            await Promise.race([refreshed, new Promise((_,reject)=>setTimeout(()=>reject(Error('tools/list_changed missing')),5000).unref())]);
        }
        const unknownUri = pathToFileURL(folders.unknown).href;
        const desktopUri = pathToFileURL(folders['beta/测试hub']).href;
        const encodedDrive = uri => uri.replace(/^file:\/\/\/([A-Za-z]):\//, (_, drive) => 'file:///'+drive.toLowerCase()+'%3A/');
        assert.notEqual(encodedDrive(desktopUri), desktopUri);
        await changeRoots([encodedDrive(unknownUri)]);
        await expectDiagnostic(rootClient,'unbound',/工作目录没有绑定/);
        await changeRoots([unknownUri,desktopUri]);
        await expectDiagnostic(rootClient,'unbound',/多个不同的工作目录/);
        for (const invalid of ['file://server/share','file:///%2F%2Fserver/share','file:///d%3Arelative',
                'file:///d%253A/archive','https://example.invalid/path',desktopUri+'?query',desktopUri+'#fragment']) {
            await changeRoots([invalid]);
            await expectDiagnostic(rootClient,'unbound',/地址不受支持/);
        }
        await changeRoots([encodedDrive(desktopUri)]);
        grantSources.length = 0;
        assert.deepEqual((await tool(rootClient,'get_project_snapshot')).source,['mcp','roots-client','2.5']);
        assert(grantSources.length && grantSources.every(row=>row.join('|')==='mcp|roots-client|2.5'));
        const routedProject = (await tool(rootClient,'list_projects')).items[0];
        assert.equal(routedProject.projectId,b.projectId);
        assert.equal(routedProject.workspaceRelativeDirectory,'测试hub');
        await changeRoots([desktopUri,encodedDrive(desktopUri)+'/']);
        assert.equal((await tool(rootClient,'list_projects')).items[0].projectId,b.projectId);
        roots.push(pathToFileURL(folders.alpha).href);
        assert.equal((await rootClient.call('tools/call',{name:'list_projects',arguments:{}})).isError,true);
        await expectDiagnostic(rootClient,'changed');
        roots = [desktopUri];
        await expectDiagnostic(rootClient,'changed');
        assert.deepEqual((await tool(sub,'get_project_snapshot')).filters,{});
        assert.deepEqual((await tool(sub,'get_project_snapshot',{task:''})).filters,{view:'brief'});
        const task = '接着处理上次的海报 & 黑白印刷 #2';
        assert.deepEqual((await tool(sub,'get_project_snapshot',{task})).filters,{view:'brief',query:task});
        const caseAlias = connect(folders.beta.toUpperCase());
        await handshake(caseAlias);
        assert.equal((await tool(caseAlias,'list_projects')).items[0].projectId,b.projectId);
        const routes = new DatabaseSync(path.join(state,'mvp.sqlite3'));
        try {
            routes.exec('CREATE TABLE route(endpoint TEXT PRIMARY KEY, project TEXT, binding TEXT, directory TEXT, executable TEXT, thread TEXT, owner_sid TEXT)');
            const insert = routes.prepare('INSERT INTO route VALUES(?,?,?,?,?,?,?)');
            const whoami = path.join(process.env.SystemRoot,'System32','whoami.exe');
            const sid = execFileSync(whoami,['/user','/fo','csv','/nh'],{encoding:'utf8',windowsHide:true}).match(/S-1-[0-9-]+/)[0];
            insert.run(endpoints[0],a.projectId,a.bindingId,folders.alpha,'fixture',randomUUID(),sid);
            assert.deepEqual((await tool(A,'list_tasks')).items.map(t=>t.targetEndpointId),[endpoints[0]]);
            insert.run(endpoints[1],a.projectId,a.bindingId,folders.alpha,'fixture',randomUUID(),sid);
            assert.ok(!(await toolNames(A)).includes('list_tasks'));
            db.prepare('INSERT INTO workspace_access(workspace_id,endpoint) VALUES(?,?)').run(workspaceA.workspaceId,endpoints[1]);
            assert.deepEqual((await tool(A,'list_tasks')).items.map(t=>t.targetEndpointId),[endpoints[1]]);
            assert.deepEqual(await toolNames(sub), ['get_project_snapshot']);
        } finally { routes.close(); }
        for (const dir of [folders.unknown, folders.alpha2, root]) {
            const unmatched = connect(dir); await handshake(unmatched); await expectDiagnostic(unmatched,'unbound');
        }
        const junction = path.join(root,'alias');
        fs.symlinkSync(folders.alpha,junction,'junction');
        const linked = connect(junction); await handshake(linked); await expectDiagnostic(linked,'ambiguous');
        await assert.rejects(bind(b,junction));
        setPolicy.run(workspaceA.workspaceId,JSON.stringify(['list_projects']));
        assert.deepEqual(await toolNames(A),['list_projects']);
        assert.equal((await A.call('tools/call',{name:'submit_feedback',arguments:{requestId:randomUUID(),content:'denied'}})).isError,true);
        setPolicy.run(workspaceA.workspaceId, null);
        a.accessMode='readOnly';
        assert.ok(!(await toolNames(A)).includes('submit_feedback'));
        grants=[b];
        await expectDiagnostic(A, 'unauthorized');
        const conflict = randomUUID();
        db.prepare('INSERT INTO workspaces(device_id,project_id,local_path,workspace_id) VALUES(?,?,?,?)')
            .run(deviceId,b.projectId,folders['alpha/nested'],conflict);
        const duplicate = connect(folders['alpha/nested']);
        await handshake(duplicate); await expectDiagnostic(duplicate,'ambiguous');
        await expectDiagnostic(A,'unavailable');
        assert.equal((await tool(B,'list_projects')).items[0].name,'B');
        b.bindingId=randomUUID();
        await expectDiagnostic(B,'changed');
        a.accessMode = 'readWrite';
        grants = [a,b];
        const folder = folders.unknown;
        const document = path.join(folder,'keep.txt');
        fs.writeFileSync(document,'keep this file');
        // The desktop install button sets this preference. Exercise a real CLI bind using that state.
        db.prepare('INSERT INTO antigravity_access(singleton,command) VALUES(1,?)').run(exe);
        const old = await bind(a,folder);
        const unbind = (row, dir = row.localPath) => cli(['unbind','--project',row.projectId,'--workspace',row.workspaceId,'--path',dir]);
        const deletedFolder = fs.mkdtempSync(path.join(root,'deleted-workspace-'));
        const deleted = await bind(a,deletedFolder);
        const receipt = path.join(state,'antigravity-project-plugins',deleted.workspaceId+'.nodicarta.json');
        assert(fs.existsSync(receipt));
        fs.rmSync(deletedFolder,{recursive:true});
        assert.equal((await unbind(deleted)).removed,true);
        assert(!(await cli(['bindings','--local'])).some(row => row.workspaceId === deleted.workspaceId));
        assert(!fs.existsSync(deletedFolder) && !fs.existsSync(receipt));
        assert.equal(db.prepare('SELECT count(*) AS n FROM antigravity_pending WHERE workspace_id=?').get(deleted.workspaceId).n,0);
        const global = connect(folder);
        const pluginAt = dir => path.join(dir,'.agents','plugins','nodicarta');
        const serverKey = 'kb_' + old.workspaceId.replaceAll('-','');
        const serverAt = plugin => JSON.parse(fs.readFileSync(path.join(plugin,'mcp_config.json'),'utf8')).mcpServers[serverKey];
        const installed = pluginAt(folder);
        const registration = serverAt(installed);
        function pinned(plugin, entry) {
            assert.equal(entry.cwd,'${PLUGIN_ROOT}/../../..');
            const cwd = path.resolve(entry.cwd.replace('${PLUGIN_ROOT}',plugin));
            const client = lines(entry.command,entry.args,{cwd});
            clients.push(client);
            return client;
        }
        const desktop = pinned(installed,registration);
        await handshake(desktop);
        assert.equal((await tool(desktop,'list_projects')).items[0].projectId,a.projectId);
        const copiedPlugin = pluginAt(folders.beta);
        fs.cpSync(installed,copiedPlugin,{recursive:true});
        const copied = pinned(copiedPlugin,serverAt(copiedPlugin));
        assert.deepEqual(await handshake(copied),['get_edge_status']);
        assert.equal((await copied.call('tools/call',{name:'list_projects',arguments:{}})).isError,true);
        const options = ['--state',state,'--project',a.projectId,'--binding',a.bindingId,'--mode','readWrite'];
        const legacy = lines(exe,['mcp-stdio',...options],{cwd:folder});
        grantSources.length = 0;
        const fixed = await httpRpc(exe,['mcp-http','--state',state,'--project',a.projectId,'--binding',a.bindingId,'--mode','auto','--workspace',old.workspaceId]);
        assert(grantSources.length && grantSources.every(row=>row[0]==='mcp' && row[1]===null && row[2]===null),'pre-handshake grants only know the MCP entry');
        clients.push(legacy,fixed);
        const peer = fixed.peer(), anonymous = fixed.peer(), modern = fixed.peer();
        const tagged = [[global,'global','1'],[legacy,'scoped','2'],[fixed,'http-first','3'],[peer,'http-second','4']];
        await Promise.all(tagged.map(([c,name,version])=>handshake(c,{},{name,version})));
        const modernCall = info => modern.call('tools/call',{name:'get_project_snapshot',arguments:{},_meta:{
            'io.modelcontextprotocol/protocolVersion':'2026-07-28','io.modelcontextprotocol/clientCapabilities':{},
            ...(info && {'io.modelcontextprotocol/clientInfo':info})}},{
            'MCP-Protocol-Version':'2026-07-28','Mcp-Method':'tools/call','Mcp-Name':'get_project_snapshot'}).then(unpackSource);
        function unpackSource(result) { assert(!result.isError); return (result.structuredContent ?? JSON.parse(result.content[0].text)).source; }
        for (let round=0; round<2; round++) {
            const inputs = [...tagged,[anonymous,null,null]];
            const current = {name:'request-'+round,version:'modern'};
            const replies = await Promise.all([...inputs.map(([c])=>tool(c,'get_project_snapshot').then(x=>x.source)),modernCall(current),modernCall(null)]);
            assert.deepEqual(replies,[...inputs.map(([,n,v])=>['mcp',n,v]),['mcp',current.name,current.version],['mcp',null,null]]);
        }
        const opaque = {name:'get_project_snapshot',arguments:{},_meta:{'io.modelcontextprotocol/clientInfo':{name:'other',version:'9'}}};
        assert.deepEqual(unpackSource(await fixed.call('tools/call',opaque)),['mcp','http-first','3']);
        assert.deepEqual(unpackSource(await anonymous.call('tools/call',opaque)),['mcp',null,null]);
        const list = {jsonrpc:'2.0',id:1,method:'tools/list',params:{}};
        assert.equal((await fixed.raw(list,{'Mcp-Session-Id':randomUUID()})).status,404);
        const create = await fixed.raw({jsonrpc:'2.0',id:2,method:'initialize',params:{protocolVersion:'2025-11-25',capabilities:{},clientInfo:{name:'ending',version:'1'}}});
        assert.equal(create.status,200);
        await create.text();
        const ended = {'Mcp-Session-Id':create.headers.get('Mcp-Session-Id')};
        assert(ended['Mcp-Session-Id']);
        assert.equal((await fixed.raw(undefined,ended,'DELETE')).status,200);
        assert.equal((await fixed.raw(list,ended)).status,404);
        console.log('PASS global and scoped STDIO, roots, preflight, concurrent HTTP sessions, anonymous legacy calls, per-request metadata, and invalid/closed session rejection');
        for (const connection of [global,legacy,fixed])
            assert.equal((await tool(connection,'list_projects')).items[0].projectId,a.projectId);
        setPolicy.run(old.workspaceId,JSON.stringify(['list_projects']));
        assert.deepEqual(await toolNames(fixed),['list_projects']);
        assert.equal((await fixed.call('tools/call',{name:'get_project_snapshot',arguments:{}})).isError,true);
        grants = [b];
        await expectDiagnostic(desktop,'unauthorized');
        assert(!(await cli(['bindings'])).some(row => row.workspaceId === old.workspaceId));
        offline = true;
        const listed = (await cli(['bindings','--local'])).find(row => row.workspaceId === old.workspaceId);
        assert.deepEqual(listed,{projectId:a.projectId,workspaceId:old.workspaceId,localPath:folder});
        await assert.rejects(unbind(old,folders.alpha2));
        await assert.rejects(unbind({...old,projectId:b.projectId}));
        assert.equal((await unbind(old)).removed,true);
        assert.equal(db.prepare('SELECT count(*) AS n FROM workspace_access WHERE workspace_id=?').get(old.workspaceId).n,0);
        assert.equal(db.prepare('SELECT count(*) AS n FROM workspace_access WHERE workspace_id=?').get(nested.workspaceId).n,1);
        await expectDiagnostic(global,'changed');
        assert.deepEqual(await toolNames(desktop),['get_edge_status']);
        assert.equal((await desktop.call('tools/call',{name:'get_project_snapshot',arguments:{}})).isError,true);
        for (const connection of [legacy,fixed]) {
            assert.deepEqual(await toolNames(connection),[]);
            assert.equal((await connection.call('tools/call',{name:'submit_feedback',arguments:{requestId:randomUUID(),content:'stale'}})).isError,true);
        }
        const unbound = connect(folder);
        await handshake(unbound); await expectDiagnostic(unbound,'unbound');
        offline = false;
        const rebound = await bind(b,folder);
        assert.notEqual(rebound.workspaceId,old.workspaceId);
        const staleConfig = pinned(installed,registration);
        assert.deepEqual(await handshake(staleConfig),['get_edge_status']);
        assert.equal((await staleConfig.call('tools/call',{name:'get_project_snapshot',arguments:{}})).isError,true);
        await assert.rejects(unbind(old));
        const fresh = connect(folder);
        await handshake(fresh);
        assert.equal((await tool(fresh,'list_projects')).items[0].projectId,b.projectId);
        await unbind(rebound);
        grants = [a,b];
        await bind(a,folder);
        await expectDiagnostic(global,'changed');
        assert.deepEqual(await toolNames(legacy),[]);
        assert.deepEqual(await toolNames(fixed),[]);
        assert.equal(fs.readFileSync(document,'utf8'),'keep this file');
        console.log('PASS: exact offline unbind, revoked local records, real files kept, scoped policy removal, cross-project rebind and stale global, fixed HTTP and legacy STDIO clients denied after same-project rebind.');
        console.log('PASS: simultaneous project sessions, Windows path boundaries and aliases, conflict isolation, live directory policy, workspace endpoints, permission downgrade, revocation and regrant.');
        console.log('PASS: Antigravity encoded drive and Chinese folder, equivalent roots, distinct missing, multiple, unsupported and unbound diagnostics, pinned session isolation.');
        console.log('PASS: Antigravity Desktop scoped STDIO query, copied configuration denied, open connection revoked, stale registration denied after rebinding.');
    } finally {
        for (const client of clients) client.close();
        db?.close();
        server.closeAllConnections(); server.close();
        fs.rmSync(root,{recursive:true,force:true,maxRetries:8,retryDelay:150});
    }
}
run().catch(error=>{console.error(error);process.exitCode=1;});
