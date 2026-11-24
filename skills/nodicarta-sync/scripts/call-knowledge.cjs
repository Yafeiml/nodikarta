// Codex starts the registered MCP in this working directory without a model turn.
const { spawn } = require('node:child_process');
const { readFileSync } = require('node:fs');
const { createInterface } = require('node:readline');

const knowledgeTools = new Set([
    'list_projects', 'get_project_snapshot', 'search_project_knowledge',
    'get_knowledge_history', 'get_asset_decisions', 'submit_knowledge_commit'
]);

async function main() {
    const [tool, ...extra] = process.argv.slice(2);
    if (!knowledgeTools.has(tool) || extra.length) throw new Error('Specify one Nodicarta knowledge tool and pass its JSON arguments on standard input.');
    const input = process.stdin.isTTY ? '' : readFileSync(0, 'utf8').trim();
    const args = JSON.parse(input || '{}');
    if (!args || typeof args !== 'object' || Array.isArray(args)) throw new Error('Tool arguments must be a JSON object.');
    if (tool === 'submit_knowledge_commit' && !args.requestId) throw new Error('A knowledge commit requires a requestId. Reuse the same value when retrying.');

    const proc = spawn(process.platform === 'win32' ? 'codex.exe' : 'codex',
        ['app-server', '--listen', 'stdio://'], { windowsHide: true, stdio: ['pipe', 'pipe', 'inherit'] });
    const lines = createInterface({ input: proc.stdout })[Symbol.asyncIterator]();
    let seq = 0;
    async function rpc(method, params) {
        const id = ++seq;
        proc.stdin.write(JSON.stringify({ method, params, id }) + '\n');
        for (;;) {
            const { done, value } = await lines.next();
            if (done) throw new Error('The Codex connection closed. If you just submitted knowledge, check the outcome using the original requestId.');
            const msg = JSON.parse(value);
            if (msg.id !== id || !('result' in msg || 'error' in msg)) continue;
            if (msg.error) throw new Error(msg.error.message);
            return msg.result;
        }
    }
    proc.on('error', err => { process.stderr.write(err.message + '\n'); process.exitCode = 1; });
    proc.stdin.on('error', err => { if (err.code !== 'EPIPE') process.stderr.write(err.message + '\n'); });
    const timeout = setTimeout(() => proc.kill(), 60000);
    try {
        await rpc('initialize', { clientInfo: { name: 'nodicarta-sync', version: '1' }, capabilities: { experimentalApi: true } });
        proc.stdin.write(JSON.stringify({ method: 'initialized' }) + '\n');
        const { thread } = await rpc('thread/start', { ephemeral: true, cwd: process.cwd(), sandbox: 'read-only', approvalPolicy: 'never' });
        const status = async serverName => {
            const inventory = await rpc('mcpServerStatus/list', { threadId: thread.id, serverName });
            return inventory.data.find(entry => entry.name === serverName);
        };
        const current = await status('nodicarta');
        const legacy = await status('projecthub');
        if (current && legacy) throw new Error('Two Nodicarta MCP entries are configured. Keep only the entry for this workspace before retrying.');
        const { name: server, tools } = current ?? legacy ?? {};
        if (tools && Object.hasOwn(tools, 'get_edge_status') && !Object.hasOwn(tools, tool)) {
            const diagnostic = await rpc('mcpServer/tool/call', { threadId: thread.id, server, tool: 'get_edge_status', arguments: {} });
            const info = diagnostic.structuredContent ?? JSON.parse(diagnostic.content.find(c => c.type === 'text').text);
            throw new Error(info.message || 'The current directory could not access a Nodicarta project.');
        }
        if (!tools || !Object.hasOwn(tools, tool))
            throw new Error('This Nodicarta tool is unavailable in the current project.');
        const result = await rpc('mcpServer/tool/call', { threadId: thread.id, server, tool, arguments: args });
        const text = result.content?.find(c => c.type === 'text')?.text;
        if (result.isError) throw new Error(text || 'Edge rejected the tool call.');
        console.log(JSON.stringify(result.structuredContent ?? JSON.parse(text), null, 2));
    } finally {
        clearTimeout(timeout);
        proc.stdin.end();
        proc.kill();
    }
}

main().catch(error => { console.error(error.message); process.exitCode = 1; });
