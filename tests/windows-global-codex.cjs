// node tests/windows-global-codex.cjs <desktop.exe> <disposable loopback Core> <codex.exe>
// Supply a pairing code on stdin for a disposable device with one read/write and one read-only project.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {randomUUID} = require('node:crypto');
const {spawnSync} = require('node:child_process');
const {lines} = require('./windows-mcp.cjs');
const server = 'nodicarta';

async function verify() {
    const [desktop, address, codex] = process.argv.slice(2);
    const core = new URL(address);
    assert(['localhost','127.0.0.1'].includes(core.hostname) && core.port && core.port !== '29480', 'Use a disposable Core');
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'projecthub-global-codex-'));
    const state = path.join(root, 'state');
    const user = path.join(root, 'user');
    // Codex on Windows also discovers CODEX_HOME/skills. Point that user-scope
    // root at our throwaway profile so the real Windows account stays untouched.
    const profile = path.join(user, '.agents');
    const cli = path.join(path.dirname(desktop), 'ProjectHub.Edge.exe');
    const env = {...process.env, CODEX_HOME:profile, USERPROFILE:user, HOME:user};
    let client;
    const run = (file, args, input, cwd = root) => {
        const result = spawnSync(file, args, {cwd, input, env, windowsHide:true, encoding:'utf8', timeout:60000});
        assert.equal(result.status, 0, result.error?.message || result.stderr || 'Command failed');
        return result.stdout;
    };
    const edge = (...args) => JSON.parse(run(cli, ['--state',state,...args]));
    try {
        for (const dir of [user, profile]) fs.mkdirSync(dir);
        const a = path.join(root,'A');
        const b = path.join(root,'B');
        const outside = path.join(root,'A-other');
        for (const dir of [path.join(a,'src'),b,outside]) fs.mkdirSync(dir,{recursive:true});
        fs.writeFileSync(path.join(profile,'config.toml'),'# Preserve user configuration\n');
        run(cli, ['--state',state,'pair','--core',core.origin], fs.readFileSync(0,'utf8').trim()+'\n');
        const projects = edge('projects');
        const write = projects.find(p => p.accessMode === 'readWrite');
        const read = projects.find(p => p.accessMode === 'readOnly');
        assert(write && read && write.projectId !== read.projectId);
        edge('bind','--project',write.projectId,'--path',a);
        edge('bind','--project',read.projectId,'--path',b);
        run(desktop, ['--connect-codex','--state',state]);
        const config = fs.readFileSync(path.join(profile,'config.toml'),'utf8');
        run(desktop, ['--connect-codex','--state',state]);
        assert.equal(fs.readFileSync(path.join(profile,'config.toml'),'utf8'), config);
        assert(config.includes(`[mcp_servers.${server}]`) && !config.includes('[mcp_servers.projecthub]'));
        assert(!config.includes(write.projectId) && !config.includes(read.projectId));
        for (const dir of [a,b]) {
            assert(!fs.existsSync(path.join(dir,'.codex','config.toml')));
            assert(!fs.existsSync(path.join(dir,'.agents','skills')));
        }
        client = lines(codex, ['app-server','--listen','stdio://'], {cwd:root, env});
        await client.call('initialize', {clientInfo:{name:'edge_global_qa',version:'1'},capabilities:{experimentalApi:true}});
        client.notify({method:'initialized'});
        const directories = [path.join(a,'src'),b,outside];
        const threads = [];
        for (const cwd of directories) {
            const {thread} = await client.call('thread/start',{ephemeral:true,cwd,sandbox:'read-only',approvalPolicy:'never'});
            threads.push(thread.id);
        }
        const call = async (index,tool,args={}) => {
            const data = await client.call('mcpServer/tool/call',{threadId:threads[index],server,tool,arguments:args});
            assert(!data.isError, data.content?.[0]?.text);
            return data.structuredContent ?? JSON.parse(data.content.find(c=>c.type==='text').text);
        };
        for (const index of [0,1,0]) {
            const {items} = await call(index,'list_projects');
            assert.deepEqual(items.map(p=>p.projectId),[index === 0 ? write.projectId : read.projectId]);
            assert.equal(items[0].workspaceRelativeDirectory, index === 0 ? 'src' : '.');
        }
        const inventory = async index => {
            const result = await client.call('mcpServerStatus/list',{threadId:threads[index],serverName:server});
            return Object.keys(result.data.find(s=>s.name===server).tools);
        };
        assert(!(await inventory(1)).includes('submit_knowledge_commit'));
        assert.deepEqual(await inventory(2),['get_edge_status']);
        const diagnostic = await call(2,'get_edge_status');
        assert(diagnostic.message && !JSON.stringify(diagnostic).includes(write.projectId));
        const title = 'global-codex-qa-'+randomUUID();
        const denied = await client.call('mcpServer/tool/call',{threadId:threads[1],server,
            tool:'submit_knowledge_commit',arguments:{requestId:randomUUID(),title,body:'Read-only write check',evidence:'isolated Windows acceptance'}});
        assert.equal(denied.isError,true,'Read-only directory accepted a write');
        await call(0,'submit_knowledge_commit',{requestId:randomUUID(),title,body:'Temporary acceptance data',evidence:'isolated Windows acceptance'});
        assert((await call(0,'search_project_knowledge',{query:title})).items.length);
        assert.deepEqual((await call(1,'search_project_knowledge',{query:title})).items,[]);
        const expected = ['nodicarta-assets','nodicarta-context','nodicarta-inbox','nodicarta-init','nodicarta-sync'];
        const skillRoot = path.join(user,'.agents','skills').toLowerCase()+path.sep;
        const {data} = await client.call('skills/list',{cwds:directories.slice(0,2),forceReload:true});
        assert.equal(data.length,2);
        for (const row of data)
            assert.deepEqual(row.skills.filter(s=>s.enabled && s.name.startsWith('nodicarta-') &&
                path.resolve(s.path).toLowerCase().startsWith(skillRoot)).map(s=>s.name).sort(),expected);
        const helper = path.join(user,'.agents','skills','nodicarta-sync','scripts','call-knowledge.cjs');
        assert.equal(JSON.parse(run(process.execPath,[helper,'list_projects'],'{}',b)).items[0].projectId,read.projectId);

        const workspace = edge('bindings').find(row => row.projectId === write.projectId && row.localPath === a);
        const file = path.join(a,'src','keep.txt');
        fs.writeFileSync(file,'Keep my work');
        run(cli,['--state',state,'unbind','--project',write.projectId,'--workspace',workspace.workspaceId,'--path',a]);
        assert.deepEqual(edge('bindings').map(row=>row.localPath),[b]);
        const oldSessionDenied = () => client.call('mcpServer/tool/call',{threadId:threads[0],server,
            tool:'get_project_snapshot',arguments:{}});
        assert.equal((await oldSessionDenied()).isError,true);
        assert.equal((await call(1,'list_projects')).items[0].projectId,read.projectId);
        const moved = edge('bind','--project',read.projectId,'--path',a);
        assert.notEqual(moved.workspaceId,workspace.workspaceId);
        assert.equal((await oldSessionDenied()).isError,true);
        const {thread:newThread} = await client.call('thread/start',{ephemeral:true,cwd:directories[0],sandbox:'read-only',approvalPolicy:'never'});
        threads.push(newThread.id);
        assert.deepEqual((await call(threads.length-1,'list_projects')).items.map(row=>row.projectId),[read.projectId]);
        assert.equal(fs.readFileSync(file,'utf8'),'Keep my work');
        assert.equal(fs.readFileSync(path.join(profile,'config.toml'),'utf8'),config);
        run(desktop,['--disconnect-codex','--state',state]);
        const disconnected=fs.readFileSync(path.join(profile,'config.toml'),'utf8');
        assert(disconnected.includes('# Preserve user configuration') && !disconnected.includes(`[mcp_servers.${server}]`));
        assert(expected.every(name=>!fs.existsSync(path.join(user,'.agents','skills',name))));
        assert.equal(edge('bindings').length,2);
        console.log('PASS: real Codex with five global skills, two isolated projects, bound subdirectory, read-only scope, unbound directory, live chat loses access on unbind, fresh chat resolves reassigned directory, files and global config preserved.');
    } finally {
        client?.close();
        assert.equal(path.dirname(root), os.tmpdir());
        fs.rmSync(root,{recursive:true,force:true,maxRetries:10,retryDelay:250});
    }
}
verify().catch(error=>{console.error(error.message);process.exitCode=1;});
