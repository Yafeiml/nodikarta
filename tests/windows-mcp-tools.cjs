// node tests/windows-mcp-tools.cjs <Edge.exe>
const assert = require('node:assert/strict');
const {spawnSync} = require('node:child_process');
const {randomUUID} = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {lines, handshake, httpRpc} = require('./windows-mcp.cjs');

async function run() {
    const exe = path.resolve(process.argv[2]);
    const state = path.join(os.tmpdir(), 'projecthub-tools-' + randomUUID());
    const scope = ['--state', state, '--project', randomUUID(), '--binding', randomUUID()];
    const selected = ['get_project_snapshot', 'list_projects'];
    try {
        const unrestricted = lines(exe, ['mcp-stdio', ...scope]);
        try {
            assert.ok((await handshake(unrestricted)).includes('submit_feedback'));
            const {tools} = await unrestricted.call('tools/list',{});
            const reads = tools.filter(t => t.annotations?.readOnlyHint === true).map(t => t.name).sort();
            assert.deepEqual(reads,['get_asset','get_asset_decisions','get_feedback','get_knowledge_history','get_project_snapshot',
                'list_agent_endpoints','list_assets','list_feedback','list_projects','list_tasks','search_project_knowledge']);
            assert(tools.some(t => t.name === 'download_asset' && !t.annotations?.readOnlyHint));
        }
        finally { unrestricted.close(); }
        for (const transport of ['mcp-stdio', 'mcp-http']) {
            const args = [transport, ...scope, '--tools', selected.join(',')];
            const client = transport === 'mcp-stdio' ? lines(exe, args) : await httpRpc(exe, args);
            try {
                assert.deepEqual(await handshake(client), selected);
                const {tools} = await client.call('tools/list', {});
                assert.deepEqual(Object.keys(tools.find(t => t.name === 'list_projects').inputSchema.properties ?? {}), []);
                await assert.rejects(client.call('tools/call', {name: 'submit_feedback', arguments: {}}),
                    /unknown tool|not found|not be found|does not exist|no tool|not available/i);
            } finally { client.close(); }
        }
        for (const invalid of ['unknown_tool', 'list_projects,']) {
            const result = spawnSync(exe, ['mcp-stdio', ...scope, '--tools', invalid],
                {encoding: 'utf8', windowsHide: true, timeout: 8000});
            assert.ok(result.status > 0 && result.stderr.includes('--tools'));
        }
    } finally { fs.rmSync(state, {force: true, recursive: true, maxRetries: 5, retryDelay: 150}); }
    console.log('STDIO and HTTP tool allowlists, hidden tool calls, schemas and invalid selections passed.');
}
run().catch(error => { console.error(error); process.exitCode = 1; });
