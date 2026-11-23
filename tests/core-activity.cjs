// Windows: build Core Release, then run node tests/core-activity.cjs. Uses a disposable DB on the local development PostgreSQL.
const assert = require('node:assert/strict');
const { spawn, spawnSync } = require('node:child_process');
const { randomBytes, randomUUID, createHash } = require('node:crypto');
const { once } = require('node:events');
const { createServer } = require('node:net');
const { request: httpRequest } = require('node:http');
const { resolve, join } = require('node:path');
const { tmpdir } = require('node:os');
const { mkdtempSync, rmSync } = require('node:fs');
const {fixtureCaptcha} = require('./login-challenge-fixture.cjs');

const distro = process.env.PROJECTHUB_WSL_DISTRO || 'Ubuntu-26.04-LTS';
const database = 'nodicarta_activity_qa_' + randomBytes(5).toString('hex');
const password = randomBytes(24).toString('hex');
const sql = (target, input) => {
    const command = spawnSync('wsl.exe', ['-d', distro, '-u', 'root', '--', 'docker', 'exec', '-i', 'dev-postgresql',
        'psql', '-qXAt', '-v', 'ON_ERROR_STOP=1', '-U', 'postgres', '-d', target], {input, encoding:'utf8', windowsHide:true, timeout:20000});
    if (command.status !== 0) throw Error('Fixture SQL failed: ' + (command.stderr || '').split('\n')[0].slice(0, 160));
    return command.stdout.trim();
};
const data = query => sql(database, 'SET search_path TO projecthub,public;\n' + query);
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function eventually(check, label, seconds=12) {
    for (let end=Date.now()+seconds*1000; Date.now()<end;) {
        if (await check()) return;
        await sleep(250);
    }
    throw Error('Timed out: ' + label);
}
async function main() {
    assert.match(database, /^nodicarta_activity_qa_[a-f0-9]{10}$/);
    const folder = mkdtempSync(join(tmpdir(),'nodicarta-activity-qa-'));
    const dll = resolve(process.argv[2] || 'src/ProjectHub.Core/bin/Release/net10.0/ProjectHub.Core.dll');
    const server = createServer();
    server.listen(0, '127.0.0.1');
    await once(server, 'listening');
    const origin = 'http://127.0.0.1:' + server.address().port;
    await new Promise(resolve => server.close(resolve));
    let proc, closed, log = '', roleCreated = false, dbCreated = false;
    const stop = async () => {
        if (proc && proc.exitCode === null) proc.kill();
        if (closed) await closed;
    };
    const start = async (trustedProxy='') => {
        proc = spawn('dotnet',[dll],{cwd:folder,windowsHide:true,stdio:['ignore','pipe','pipe'],env:{...process.env,
            ASPNETCORE_URLS:origin, ConnectionStrings__Core:`Host=127.0.0.1;Port=5432;Database=${database};Username=${database};Password=${password}`,
            ProjectHub__LocalHttp:'true', ProjectHub__KeyDirectory:folder, ProjectHub__TrustedProxy:trustedProxy}});
        closed = once(proc,'close');
        for(const stream of [proc.stdout,proc.stderr]) stream.on('data',chunk => {log += chunk.toString();});
        await eventually(async () => {
            if (proc.exitCode !== null) throw Error('Fixture Core exited before readiness');
            return fetch(origin+'/login').then(r => r.status===200, () => false);
        },'Core startup');
    };
    const send = (route,token,headers={},body) => fetch(origin+'/v1'+route,{method:body ? 'POST' : 'GET',
        headers:{...(token ? {Authorization:'Bearer '+token}:{}), ...headers, ...(body ? {'Content-Type':'application/json'}:{})},
        body:body ? JSON.stringify(body):undefined});
    const cookies = new Map();
    async function admin(path, options={}) {
        const cookie = Array.from(cookies, ([k,v])=>k+'='+v).join('; ');
        const reply = await fetch(origin+path,{...options,redirect:'manual',headers:{...options.headers,Cookie:cookie}});
        for (const line of reply.headers.getSetCookie()) {
            const pair=line.split(';')[0], split=pair.indexOf('=');
            cookies.set(pair.slice(0,split),pair.slice(split+1));
        }
        return reply;
    }
    async function submitForm(path, fields) {
        const form=await admin(path);
        const html=await form.text();
        const token=html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/);
        assert.ok(token,'CSRF token missing from '+path);
        if (path === '/login') {
            fields.challengeId = html.match(/name="challengeId" value="([^"]+)"/)[1];
            fields.captcha = fixtureCaptcha(database, fields.challengeId, data);
        }
        const result=await admin(path,{method:'POST',body:new URLSearchParams({...fields,__RequestVerificationToken:token[1]})});
        assert.equal(result.status,302);
    }
    try {
        sql('postgres',`CREATE ROLE ${database} LOGIN PASSWORD '${password}';`);
        roleCreated = true;
        sql('postgres',`CREATE DATABASE ${database} OWNER ${database};`);
        dbCreated = true;
        await start();
        const setup=log.match(/:\s*([0-9A-F]{32})\b/);
        assert.ok(setup,'Missing bootstrap code in disposable service output');
        await submitForm('/setup',{code:setup[1],username:'fixture',password,confirmPassword:password});
        await submitForm('/login',{username:'fixture',password});
        const owner=data("SELECT id FROM owner_account"), project=randomUUID(), other=randomUUID(), device=randomUUID(), binding=randomUUID();
        const token=randomBytes(32).toString('hex'), digest=createHash('sha256').update(token).digest('hex');
        data(`INSERT INTO projects(id,owner_id,name,request_id) VALUES('${project}','${owner}','One',gen_random_uuid()),('${other}','${owner}','Two',gen_random_uuid());
            INSERT INTO devices(id,edge_id,owner_id,label,reported_label,instance_nonce,access_digest,access_expires_at)
            VALUES('${device}',gen_random_uuid(),'${owner}','Fixture','Fixture','test',decode('${digest}','hex'),now()+interval '1 hour');
            INSERT INTO device_grants(binding_id,device_id,project_id,access_mode) VALUES('${binding}','${device}','${project}','readWrite');`);
        const base=`/projects/${project}/bindings/${binding}`, otherBase=`/projects/${other}/bindings/${binding}`;
        const stamp = '2026-11-20T12:34:56.123Z';
        const headers={'X-Nodicarta-Entry':'mcp','X-Nodicarta-Client':'Codex','X-Nodicarta-Client-Version':'1.4.3',
            'X-Nodicarta-Origin-At':stamp,'X-Nodicarta-Replay':'1','X-Forwarded-For':'192.0.2.199'};
        const body={requestId:randomUUID(),title:'Activity fixture',body:'Private body',evidence:'Private evidence'};
        const first=await send(base+'/knowledge',token,headers,body);
        assert.equal(first.status,201);
        const item=(await first.json()).itemId;
        assert.equal((await send(base+'/knowledge',token,{'X-Nodicarta-Client':'Other'},body)).status,200);
        const wrote=data(`SELECT count(*),min(object_id::text),min(object_version),min(client_name),min(client_version),min(entry),
            bool_and(origin_at='${stamp}'::timestamptz AND is_replay AND host(source_ip)='127.0.0.1')
            FROM audit_log WHERE action='knowledge.commit' AND request_id='${body.requestId}'`);
        assert.equal(wrote,`1|${item}|1|Codex|1.4.3|mcp|t`);
        const marker='private_search_'+randomBytes(8).toString('hex');
        const replies = await Promise.all(['Claude Code','Codex',null].map(client => send(base+'/knowledge?query='+marker,token,
            client ? {'X-Nodicarta-Entry':'mcp','X-Nodicarta-Client':client}:{})));
        for(const reply of replies) assert.equal(reply.status,200);
        await eventually(()=>data(`SELECT count(*) FROM audit_log WHERE project_id='${project}' AND action='knowledge.search' AND match_count=0`)==='3','read telemetry');
        assert.equal(data("SELECT string_agg(coalesce(client_name,'unknown'),',' ORDER BY client_name NULLS LAST) FROM audit_log WHERE action='knowledge.search'"),'Claude Code,Codex,unknown');
        const spelling='Claude Code (CLI) & tools', reportedVersion='2.4 (Win/x64)';
        assert.equal((await send(base+'/assets',token,{'X-Nodicarta-Client':spelling,'X-Nodicarta-Client-Version':reportedVersion,
            'X-Nodicarta-Entry':'mcp (CLI)','X-Nodicarta-Origin-At':'2026-11-22','X-Nodicarta-Replay':'yes'})).status,200);
        // Send separate wire fields: Fetch combines repeated headers into a single value before transmission.
        async function rawAssetRead(extra) {
            await new Promise((resolve,reject) => {
                const req=httpRequest(origin+'/v1'+base+'/assets', {headers:{Authorization:'Bearer '+token,...extra}}, res=>{
                    if(res.statusCode!==200) return reject(Error('Raw header fixture got '+res.statusCode));
                    res.resume();
                    res.on('end',resolve);
                });
                req.on('error',reject);
                req.end();
            });
        }
        await rawAssetRead({'X-Nodicarta-Client':['First','Second'],'X-Nodicarta-Client-Version':['One','Two']});
        await rawAssetRead({'X-Nodicarta-Client':'a'.repeat(65),'X-Nodicarta-Client-Version':'b'.repeat(49)});
        await rawAssetRead({'X-Nodicarta-Client':'Bad\tTab','X-Nodicarta-Client-Version':'Bad\tTab'});
        await eventually(()=>data("SELECT count(*) FROM audit_log WHERE action='asset.search'")==='4','printable and invalid headers');
        assert.equal(data("SELECT client_name||'|'||client_version FROM audit_log WHERE action='asset.search' AND client_name IS NOT NULL AND entry IS NULL AND origin_at IS NULL AND is_replay=false"),spelling+'|'+reportedVersion);
        assert.equal(data("SELECT count(*) FROM audit_log WHERE action='asset.search' AND client_name IS NULL AND client_version IS NULL"),'3');
        const filter='/projects/'+project+'?tab=activity&view=reads&client='+encodeURIComponent(spelling);
        const displayed=await admin(filter);
        assert.equal(displayed.status,200);
        assert.ok((await displayed.text()).includes('Claude Code (CLI) &amp; tools'),'Display name should be escaped in HTML');
        console.log('PASS exact printable client name/version, Razor display escaping, repeated/oversize/control headers omitted, strict origin/entry/replay');
        assert.equal((await send(otherBase+'/knowledge?query='+marker,token,headers)).status,404);
        assert.equal((await send(otherBase+'/knowledge',null,headers)).status,401);
        data("UPDATE device_grants SET access_mode='readOnly'");
        assert.equal((await send(base+'/knowledge',token,headers,{...body,requestId:randomUUID()})).status,403);
        await eventually(()=>data("SELECT count(*) FROM audit_log WHERE result<>'success'")==='3','rejections');
        assert.equal(data(`SELECT count(*) FROM audit_log WHERE project_id='${other}'`),'0');
        assert.equal(data(`SELECT count(*) FROM audit_log WHERE device_id='${device}' AND project_id IS NULL AND result='not_found'`),'1');
        assert.equal(data("SELECT count(*) FROM audit_log WHERE actor_id IS NULL AND project_id IS NULL AND result='unauthenticated'"),'1');
        assert.equal(data(`SELECT detail FROM audit_log WHERE project_id='${project}' AND result='forbidden'`),'readOnly');
        assert.equal(data("SELECT COUNT(*) FROM devices WHERE last_contact_at IS NOT NULL"),'1');
        assert(!log.includes(marker),'Raw search appeared in Core stdout');
        assert.equal(data(`SELECT count(*) FROM audit_log a WHERE to_jsonb(a)::text LIKE '%${marker}%'`),'0');
        console.log('PASS source, retry deduplication, read attribution, rejection isolation, trusted IP boundary, search privacy');

        data(`CREATE FUNCTION test_block_diagnostic() RETURNS trigger LANGUAGE plpgsql AS $f$
            BEGIN IF NEW.is_diagnostic THEN RAISE EXCEPTION 'intentional telemetry outage'; END IF; RETURN NEW; END $f$;
            CREATE TRIGGER test_diag BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION test_block_diagnostic();`);
        assert.equal((await send(base+'/knowledge/history?itemId='+item,token,headers)).status,200);
        await eventually(()=>data("SELECT missed_count>0 AND first_missed_at<=last_missed_at FROM activity_coverage")==='t','coverage recovery',25);
        assert.equal(data("SELECT count(*) FROM audit_log WHERE action='knowledge.history'"),'0');
        data("DROP TRIGGER test_diag ON audit_log; DROP FUNCTION test_block_diagnostic();");
        assert.equal((await send(base+'/knowledge/history?itemId='+item,token,headers)).status,200);
        await eventually(()=>data(`SELECT count(*) FROM audit_log WHERE action='knowledge.history' AND object_id='${item}'`)==='1','recovery');
        console.log('PASS read remains available during diagnostic sink failure, gap persists, capture resumes');

        data("UPDATE audit_log SET occurred_at=now()-interval '31 days' WHERE action='knowledge.search'; UPDATE audit_log SET occurred_at=now()-interval '8 days' WHERE action='knowledge.commit' AND result='success';");
        await stop(); await start('127.0.0.1');
        await eventually(()=>data("SELECT count(*) FROM audit_log WHERE action='knowledge.search'")==='0','diagnostic expiry');
        assert.equal(data("SELECT count(*) FROM audit_log WHERE action='knowledge.commit' AND result='success' AND source_ip IS NULL AND client_name='Codex'"),'1');
        assert.equal(data("SELECT count(*) FROM knowledge_versions"),'1');
        assert.equal(data("SELECT count(*) FROM audit_log WHERE action='knowledge.history' AND source_ip IS NOT NULL"),'1');
        assert.equal((await send(base+'/knowledge?query=fixture',token,headers)).status,200);
        await eventually(()=>data("SELECT count(*) FROM audit_log WHERE action='knowledge.search' AND host(source_ip)='192.0.2.199'")==='1','configured trusted proxy');
        console.log('PASS retention: expired reads removed, old IP cleared, knowledge and write receipt preserved');

        const invitation=randomUUID(), code=randomBytes(16).toString('hex').toUpperCase();
        const codeHash=createHash('sha256').update(code).digest('hex');
        data(`INSERT INTO device_pairings(id,request_id,owner_id,label,code_digest,expires_at)
            VALUES('${invitation}',gen_random_uuid(),'${owner}','Reuse label',decode('${codeHash}','hex'),now()+interval '10 minutes');
            INSERT INTO pairing_grants(pairing_id,project_id,access_mode) VALUES('${invitation}','${project}','readWrite');`);
        const activation=await send('/device-pairings/'+invitation+'/redeem',null,{}, {oneTimeCode:code,deviceLabel:'Reuse label',instanceNonce:'qa.first'});
        assert.equal(activation.status,201);
        const old=await activation.json();
        assert.equal(data(`SELECT detail FROM audit_log WHERE action='grant.activated' AND project_id='${project}' AND device_id='${old.deviceId}'`),'readWrite');
        const update=randomUUID(), secret=randomBytes(32).toString('hex').toUpperCase();
        data(`INSERT INTO device_pairings(id,request_id,owner_id,label,code_digest,expires_at,target_device_id)
            VALUES('${update}',gen_random_uuid(),'${owner}','Reuse label',decode('${codeHash}','hex'),now()+interval '10 minutes','${old.deviceId}');
            INSERT INTO pairing_grants(pairing_id,project_id,access_mode,source_binding_id)
            SELECT '${update}',project_id,access_mode,binding_id FROM device_grants WHERE device_id='${old.deviceId}';
            UPDATE device_grants SET access_mode='readOnly' WHERE device_id='${old.deviceId}';`);
        assert.equal((await send('/device-pairings/'+update+'/claim',null,{}, {oneTimeCode:code,deviceLabel:'Reuse label',instanceNonce:'qa.second',attemptSecret:secret})).status,200);
        const activated=await send('/device-pairings/'+update+'/confirm',null,{}, {attemptSecret:secret});
        assert.equal(activated.status,200);
        const current=await activated.json();
        assert.equal(data(`SELECT detail FROM audit_log WHERE action='grant.transferred' AND project_id='${project}' AND device_id='${current.deviceId}' AND object_id='${old.deviceId}'`),'readOnly');
        assert.equal(data(`SELECT count(*) FROM audit_log WHERE action='grant.activated' AND device_id='${old.deviceId}'`),'1');
        assert.equal((await send('/projects',old.accessToken)).status,401);
        console.log('PASS initial project grant preserved on old identity, actual reduced grant captured on successor, old credentials rejected');

        const svg=Buffer.from('<svg xmlns="http://www.w3.org/2000/svg"><path d="M0 0"/></svg>');
        data(`UPDATE device_grants SET access_mode='readWrite' WHERE device_id='${device}'`);
        const published=await send(base+'/assets',token,headers,{requestId:randomUUID(),name:'Audit logo',purpose:'Fixture SVG',
            fileName:'logo.svg',content:svg.toString('base64')});
        assert.equal(published.status,201);
        const asset=(await published.json()).assetId;
        const projectPage='/projects/'+project;
        assert.equal((await admin(projectPage+'?tab=knowledge&objectId='+item+'&version=1')).status,200);
        assert.equal((await admin(projectPage+'?tab=assets&objectId='+asset+'&version=1')).status,200);
        const file=await admin(projectPage+'?handler=Download&assetId='+asset+'&version=1');
        assert.equal(file.status,200);
        assert.deepEqual(Buffer.from(await file.arrayBuffer()),svg);
        await eventually(()=>data(`SELECT count(*) FROM audit_log WHERE project_id='${project}' AND entry='admin'
            AND actor_id='${owner}' AND device_id IS NULL AND object_version=1 AND action IN ('knowledge.history','asset.get','asset.download')`)==='3','administrator reads');
        const count=data("SELECT count(*) FROM audit_log");
        const activityPage=await admin(projectPage+'?tab=activity&view=all');
        assert.equal(activityPage.status,200);
        assert.ok((await activityPage.text()).includes('data-activity-id='),'Activity list failed to render persisted rows');
        assert.equal(data("SELECT count(*) FROM audit_log"),count,'Activity page should not audit itself');
        console.log('PASS authenticated administrator reads and exact original download attributed to admin, activity view creates no event');
        await require('./core-admin-auth.cjs')(origin, database, data, password, async () => { await stop(); await start(); });
    } finally {
        await stop();
        if(dbCreated) sql('postgres',`DROP DATABASE ${database};`);
        if(roleCreated) sql('postgres',`DROP ROLE ${database};`);
        assert.equal(resolve(folder,'..'),resolve(tmpdir()));
        rmSync(folder,{recursive:true,force:true});
    }
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});
