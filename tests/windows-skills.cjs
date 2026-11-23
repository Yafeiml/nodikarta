// node tests/windows-skills.cjs [path-to-codex.exe]
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {spawnSync} = require('node:child_process');
const {lines} = require('./windows-mcp.cjs');

async function run() {
    const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'projecthub-skills-'));
    const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'projecthub-skills-profile-'));
    let client;
    try {
        const fixture = spawnSync('dotnet', ['run', '--file', path.join(__dirname, 'EdgeAccessChecks.cs'), '--', '--skills-fixture', workspace],
            {encoding: 'utf8', windowsHide: true, timeout: 90000});
        assert.equal(fixture.status, 0, fixture.stderr || fixture.stdout);
        const git = spawnSync('git', ['init', '--quiet', workspace], {windowsHide: true});
        assert.equal(git.status, 0);
        fs.writeFileSync(path.join(profile, 'config.toml'), '[projects.' + JSON.stringify(workspace) + ']\ntrust_level = "trusted"\n');
        client = lines(process.argv[2] || 'codex.exe', ['app-server', '--listen', 'stdio://'],
            {cwd: workspace, env: {...process.env, CODEX_HOME: profile}});
        await client.call('initialize', {clientInfo: {name: 'projecthub_skills_qa', version: '1'}, capabilities: {experimentalApi: true}});
        client.notify({method: 'initialized'});
        const {data} = await client.call('skills/list', {cwds: [workspace], forceReload: true});
        const skillRoot = path.join(workspace, '.agents', 'skills');
        const discovered = data[0].skills.filter(s => s.enabled && path.resolve(s.path).toLowerCase().startsWith(skillRoot.toLowerCase() + path.sep));
        assert.deepEqual(discovered.map(s => s.name).sort(), ['nodicarta-assets', 'nodicarta-context', 'nodicarta-inbox', 'nodicarta-init', 'nodicarta-sync']);
        for (const skill of discovered) {
            assert.match(skill.description, /脉络/);
            assert.match(skill.description, /\bnodicarta\b/);
        }
        const helper = path.join(skillRoot, 'nodicarta-init', 'scripts', 'init-ids.cjs');
        const ids = (kind, key, version) => {
            const result = spawnSync(process.execPath, [helper, '44c9f8aa-903b-4910-8f28-c0263c75518f', kind, key, version], {encoding:'utf8', windowsHide:true});
            assert.equal(result.status, 0, result.stderr);
            return JSON.parse(result.stdout);
        };
        const first = ids('knowledge','杉径/导出','0');
        assert.deepEqual(first, {itemId:'e531e4f4-ed14-539d-85d8-3cd92cdefb7a',requestId:'ce995624-4d39-5737-aae4-7d28700ded74'});
        const next = ids('knowledge','杉径/导出','1');
        assert.equal(next.itemId,first.itemId);
        assert.notEqual(next.requestId,first.requestId);
        assert.deepEqual(ids('asset','brand/mark.svg','0'), {requestId:'29d505ba-7afb-5dee-9c1a-dc7db4f46eb0'});
        console.log('PASS: role-based install, existing edits preserved, complete helper, five local skills discovered by Codex.');
    } finally {
        client?.close();
        for (const dir of [workspace, profile]) fs.rmSync(dir, {recursive: true, force: true, maxRetries: 10, retryDelay: 200});
    }
}
run().catch(error => { console.error(error); process.exitCode = 1; });
