// Dedicated loopback fixture: database nodicarta_admin_scale, container nodicarta-core-scale.
// Run with the same NODE_PATH and Windows browser as tests/core-web-activity.cjs.
const assert = require('node:assert/strict');
const { randomBytes } = require('node:crypto');
const { performance } = require('node:perf_hooks');
const { chromium } = require('playwright-core');
const { docker, setup, login, resetPassword, visit } = require('./core-fixture.cjs');

const container = 'nodicarta-core-scale';
const base = 'http://127.0.0.1:29588';
const database = 'nodicarta_admin_scale';
const owner = 'core-scale-qa';

function sql(statement) {
    return docker('exec','dev-postgresql','psql','-U','postgres','-d',database,'-XAtq','-v','ON_ERROR_STOP=1','-c',statement).trim();
}

async function run() {
    assert.equal(docker('port',container,'8080/tcp').trim(),new URL(base).host);
    assert.equal(sql('select current_database()'),database);
    const browser = await chromium.launch({ channel:'msedge', headless:true });
    try {
        const page = await browser.newPage({baseURL:base,locale:'zh-CN'});
        const password = randomBytes(30).toString('base64url');
        await visit(page,new URL(base));
        if (new URL(page.url()).pathname === '/setup') {
            const code = [...docker('logs',container).matchAll(/首次安装码\s*[:：]\s*(\S+)/gu)].at(-1)?.[1];
            assert(code, 'Missing fixture bootstrap code');
            assert.equal(await setup(page,code,owner,password),302);
        } else {
            assert.equal(sql('select username from projecthub.owner_account'),owner);
            await resetPassword(container,password);
        }
        await page.goto('/login');
        if (new URL(page.url()).pathname === '/login') assert.equal(await login(page,owner,password),302);
        const prefix = 'volume-' + randomBytes(5).toString('hex') + '-';
        sql(`
            WITH projects AS (
                INSERT INTO projecthub.projects(id,owner_id,name,request_id)
                SELECT gen_random_uuid(),a.id,'${prefix}'||lpad(n::text,3,'0'),gen_random_uuid()
                FROM projecthub.owner_account a CROSS JOIN generate_series(1,100) n
                RETURNING id,owner_id
            )
            INSERT INTO projecthub.audit_log(occurred_at,actor_id,action,project_id)
            SELECT statement_timestamp(),p.owner_id,'project.created',p.id
            FROM projects p CROSS JOIN generate_series(1,1000);
            ANALYZE projecthub.projects; ANALYZE projecthub.audit_log`);
        const project = sql("select id from projecthub.projects where name='" + prefix + "001'");
        assert.match(project,/^[a-f\d-]{36}$/);
        assert.equal(sql("select count(*) from projecthub.audit_log a join projecthub.projects p on p.id=a.project_id where p.name like '" + prefix + "%'"),'100000');

        const measured = {};
        async function navigate(url,label) {
            const start = performance.now();
            const response = await page.goto(url);
            assert.equal(response.status(),200,label);
            measured[label] = Math.round(performance.now()-start);
        }
        await navigate('/projects?q=' + prefix,'projectPageMs');
        const projectRows = page.locator('tr[data-project-id]');
        const pageOne = await projectRows.evaluateAll(rs => rs.map(r => r.dataset.projectId));
        assert.equal(pageOne.length,25);
        await navigate('/projects?q=' + prefix + '&number=2','nextProjectPageMs');
        const pageTwo = await projectRows.evaluateAll(rs => rs.map(r => r.dataset.projectId));
        assert.equal(new Set([...pageOne,...pageTwo]).size,50);

        const activity = '/projects/' + project + '?tab=activity&view=important';
        const rows = page.locator('[data-activity-id]');
        async function ids() { return rows.evaluateAll(rs => rs.map(r => r.dataset.activityId)); }
        await navigate(activity,'activityPageMs');
        const first = await ids();
        assert.equal(first.length,25);
        await navigate(activity + '&before=' + first.at(-1),'nextActivityPageMs');
        const second = await ids();
        assert.equal(second.length,25);
        const expected = sql("select id from projecthub.audit_log where project_id='" + project + "' order by occurred_at desc,id desc limit 50").split(/\s+/);
        assert.deepEqual([...first,...second],expected);
        const plan = sql("explain (analyze,buffers) select id from projecthub.audit_log where project_id='" + project + "' order by occurred_at desc,id desc limit 26");
        assert.match(plan,/activity_by_project/);
        console.log(JSON.stringify({projects:100,events:100000,tiedEventsPerProject:1000,matchedActivityIds:50,
            index:'activity_by_project',...measured}));
    } finally { await browser.close(); }
}

run().then(
    () => process.stdout.write('', () => process.exit(0)),
    err => process.stderr.write(err.message + '\n', () => process.exit(1))
);
