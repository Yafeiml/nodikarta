// node tests/windows-unbind-dispatch.cjs <Edge.exe>
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const {randomUUID, randomBytes} = require('node:crypto');
const {spawn} = require('node:child_process');
const {once} = require('node:events');
const {DatabaseSync} = require('node:sqlite');
const {setTimeout: delay} = require('node:timers/promises');

async function main() {
    const exe = path.resolve(process.argv[2]);
    const root = fs.mkdtempSync(path.join(os.tmpdir(),'projecthub-dispatch-test-'));
    const state = path.join(root,'state');
    const codex = path.join(root,'codex.exe');
    const deviceId = randomUUID(), token = randomBytes(32).toString('hex');
    const project = {projectId:randomUUID(),bindingId:randomUUID(),accessMode:'readWrite',name:'fixture'};
    const tasks = [], claims = [];
    let delayRegistration;
    const server = http.createServer((req,res) => {
        const send = data => { res.setHeader('Content-Type','application/json'); res.end(JSON.stringify(data)); };
        if (req.url.endsWith('/redeem')) return send({deviceId,edgeId:randomUUID(),accessToken:token,
            refreshToken:randomBytes(32).toString('hex'),expiresAt:new Date(Date.now()+3600000).toISOString()});
        if (req.headers.authorization !== 'Bearer '+token) { res.writeHead(401); return res.end(); }
        if (req.url === '/v1/projects') return send({items:[project]});
        if (req.url.endsWith('/endpoints')) {
            const respond = () => send({endpointId:randomUUID()});
            return delayRegistration ? delayRegistration(respond) : respond();
        }
        if (req.url.endsWith('/tasks')) return send({items:tasks});
        if (req.url.endsWith('/claim')) { claims.push(req.url.split('/').at(-2)); return send({}); }
        res.writeHead(404); res.end();
    });
    const children = new Set();
    async function cli(args,input) {
        const child = spawn(exe,['--state',state,...args],{windowsHide:true,timeout:25000});
        children.add(child);
        let out='', err='';
        child.stdout.on('data',chunk=>out+=chunk); child.stderr.on('data',chunk=>err+=chunk);
        child.stdin.end(input);
        const [code] = await once(child,'close'); children.delete(child);
        assert.equal(code,0,err);
        return out.trim() && JSON.parse(out);
    }
    function workspace(name) {
        const dir = path.join(root,name);
        fs.mkdirSync(dir);
        fs.writeFileSync(path.join(dir,'queue'),"require('node:fs').appendFileSync('queued','one\\n');");
        // Hold the post-claim conversation check until the test has unbound this workspace.
        fs.writeFileSync(path.join(dir,'app-server'),`
            const fs = require('node:fs');
            require('node:readline').createInterface({input:process.stdin}).on('line',async line => {
                const {id,method,params} = JSON.parse(line);
                if (id == null) return;
                let result = {};
                if (method === 'thread/read') {
                    if (fs.existsSync('pause')) {
                        fs.writeFileSync('probing','');
                        while (fs.existsSync('pause')) await new Promise(r=>setTimeout(r,20));
                    }
                    result = {thread:{id:params.threadId,cwd:process.cwd()}};
                }
                if (method === 'thread/turns/list') result = {data:[],nextCursor:null};
                console.log(JSON.stringify({jsonrpc:'2.0',id,result}));
            });`);
        return dir;
    }
    let routes;
    try {
        server.listen(0,'127.0.0.1'); await once(server,'listening');
        fs.copyFileSync(process.execPath,codex);
        await cli(['pair','--core','http://127.0.0.1:'+server.address().port],randomUUID()+'.'+randomBytes(16).toString('hex')+'\n');
        const a = workspace('alpha'), b = workspace('beta');
        const projectArg = ['--project',project.projectId];
        const original = await cli(['bind',...projectArg,'--path',a]);
        await cli(['bind',...projectArg,'--path',b]);
        for (const dir of [a,b]) {
            const {endpointId} = await cli(['codex-link',...projectArg,'--path',dir,'--codex',codex,
                '--thread',randomUUID(),'--name',path.basename(dir)]);
            tasks.push({taskId:randomUUID(),targetEndpointId:endpointId,status:'pending',
                observation:{kind:'duplicateCommitCount',expected:1,observed:1}});
        }
        routes = new DatabaseSync(path.join(state,'mvp.sqlite3'));
        // Existing releases tracked only project and path.
        routes.prepare('UPDATE route SET workspace=NULL WHERE endpoint=?').run(tasks[0].targetEndpointId);
        fs.writeFileSync(path.join(a,'pause'),'');
        const tick = cli(['dispatch']);
        try {
            for (let i=0;i<240 && !fs.existsSync(path.join(a,'probing'));i++) await delay(50);
            assert(fs.existsSync(path.join(a,'probing')) && claims.includes(tasks[0].taskId),'reached post-claim check');
            await cli(['unbind',...projectArg,'--workspace',original.workspaceId,'--path',a]);
        } finally { fs.rmSync(path.join(a,'pause'),{force:true}); await tick; }
        assert(!fs.existsSync(path.join(a,'queued')));
        assert.equal(fs.readFileSync(path.join(b,'queued'),'utf8'),'one\n');
        assert.deepEqual(routes.prepare('SELECT endpoint FROM route').all().map(r=>r.endpoint),[tasks[1].targetEndpointId]);
        assert.equal(routes.prepare('SELECT COUNT(*) n FROM attempt').get().n,2);
        const successor = await cli(['bind',...projectArg,'--path',a]);
        assert.notEqual(successor.workspaceId,original.workspaceId);
        await cli(['dispatch']);
        assert(!fs.existsSync(path.join(a,'queued')) && claims.filter(id=>id===tasks[0].taskId).length===1);
        const registration = new Promise(resolve=>{ delayRegistration=resolve; });
        const linking = cli(['codex-link',...projectArg,'--path',a,'--codex',codex,'--thread',randomUUID(),'--name','race']);
        const respond = await registration;
        try {
            await cli(['unbind',...projectArg,'--workspace',successor.workspaceId,'--path',a]);
            await cli(['bind',...projectArg,'--path',a]);
        } finally { respond(); }
        await assert.rejects(linking,/目录绑定已变化/);
        assert.deepEqual(routes.prepare('SELECT endpoint FROM route').all().map(r=>r.endpoint),[tasks[1].targetEndpointId]);
        console.log('PASS: unbinding after claim and before queue blocks the old chat, retains attempts and sibling dispatch; concurrent linking and rebinding cannot resurrect an endpoint.');
    } finally {
        for (const child of children) child.kill();
        routes?.close(); server.closeAllConnections(); server.close();
        assert.equal(path.dirname(root), os.tmpdir());
        fs.rmSync(root,{recursive:true,force:true,maxRetries:10,retryDelay:150});
    }
}
main().catch(e=>{console.error(e);process.exitCode=1;});
