// Runs against paired disposable Edge states and an explicitly authorized existing Desktop test chat.
const assert = require('node:assert/strict');
const { spawn, execFileSync } = require('node:child_process');
const { readFileSync, writeFileSync, existsSync } = require('node:fs');
const { createInterface } = require('node:readline');
const { randomUUID } = require('node:crypto');
const { setTimeout: delay } = require('node:timers/promises');

const [exe, fixturePath, endpoint, recordPath] = process.argv.slice(2);
assert(exe && fixturePath && endpoint && recordPath, 'Pass Edge executable, two-device fixture, approved target endpoint and evidence file.');
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8'));
const dev = fixture.devices.find(d => d.role === 'development');
const source = fixture.devices.find(d => d.role === 'feedback');

async function connect(device) {
    const binding = device.projects.find(p => p.projectId === fixture.project).bindingId;
    const child = spawn(exe, ['mcp-stdio', '--state', device.state, '--project', fixture.project,
        '--binding', binding, '--mode', 'readWrite'], { windowsHide: true, stdio: ['pipe', 'pipe', 'inherit'] });
    const pending = new Map();
    let next = 0;
    createInterface({ input: child.stdout }).on('line', line => {
        const message = JSON.parse(line);
        if (pending.has(message.id)) pending.get(message.id)(message);
    });
    const send = msg => child.stdin.write(JSON.stringify({ jsonrpc: '2.0', ...msg }) + '\n');
    async function rpc(method, params) {
        const id = ++next;
        const reply = await new Promise((resolve, reject) => {
            const timer = setTimeout(() => { pending.delete(id); reject(new Error('MCP timeout: ' + method)); }, 30000);
            pending.set(id, msg => { clearTimeout(timer); pending.delete(id); resolve(msg); });
            send({ id, method, params });
        });
        assert(!reply.error, 'MCP request failed');
        return reply.result;
    }
    await rpc('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'projecthub-delivery-test', version: '1' } });
    send({ method: 'notifications/initialized' });
    return { close: () => child.kill(), async call(name, args) {
        const result = await rpc('tools/call', { name, arguments: args });
        assert(!result.isError, 'MCP tool failed: ' + name);
        return result.structuredContent ?? JSON.parse(result.content[0].text);
    } };
}

(async () => {
    const client = await connect(source);
    try {
        // Persist the request IDs before any side effect. Reopening the same record only reconciles the same job.
        const state = existsSync(recordPath) ? JSON.parse(readFileSync(recordPath, 'utf8')) : {
            endpoint, project: fixture.project, knowledgeRequest: randomUUID(), feedbackRequest: randomUUID()
        };
        assert.equal(state.endpoint, endpoint); assert.equal(state.project, fixture.project);
        const save = () => writeFileSync(recordPath, JSON.stringify(state, null, 2) + '\n');
        save();
        const unique = 'Windows-Desktop-' + state.knowledgeRequest;
        const commit = { requestId: state.knowledgeRequest, title: unique, body: '同一知识提交重试后应只有一个版本。',
            evidence: 'tests/windows-desktop.cjs 的两次同标识实际提交' };
        const first = await client.call('submit_knowledge_commit', commit);
        const second = await client.call('submit_knowledge_commit', commit);
        assert.equal(first.commitId, second.commitId);
        const current = await client.call('search_project_knowledge', { query: unique });
        assert.equal(current.items.length, 1);
        const observation = { kind: 'duplicateCommitCount', expected: 1, observed: current.items.length };
        const feedback = await client.call('submit_feedback', { requestId: state.feedbackRequest,
            content: '请分析同一知识提交连续重试后的记录数。', targetEndpointId: endpoint, ...observation });
        assert(feedback.taskId && feedback.feedbackId);
        Object.assign(state, { commitId: first.commitId, taskId: feedback.taskId, feedbackId: feedback.feedbackId, observation });
        save();
        for (let attempt = 0; attempt < 35; attempt++) {
            execFileSync(exe, ['dispatch', '--state', dev.state], { windowsHide: true, timeout: 50000, stdio: ['ignore','ignore','inherit'] });
            const visible = await client.call('list_feedback', {});
            const own = visible.items.find(item => item.feedbackId === state.feedbackId);
            if (own?.status === 'completed' && own.result) {
                const analysis = JSON.parse(own.result);
                assert(analysis['诊断'] && analysis['建议']);
                Object.assign(state, { checkedAtUtc: new Date().toISOString(), status: own.status, actualResult: analysis });
                save(); console.log('PASS Windows Edge → Core → existing Desktop chat → Core → originating Edge'); return;
            }
            await delay(3000);
        }
        throw new Error('Final result has not reached the submitting device.');
    } finally { client.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
