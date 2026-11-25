// Loopback pairing service and a live MCP client for windows-draft-installer.ps1.
const assert = require('node:assert/strict');
const {randomUUID, randomBytes} = require('node:crypto');
const {once} = require('node:events');
const http = require('node:http');
const path = require('node:path');
const readline = require('node:readline');
const {handshake, lines} = require('./windows-mcp.cjs');

async function main() {
    const [launcher, state] = process.argv.slice(2);
    const offer = randomUUID(), code = randomBytes(16).toString('hex');
    const identity = {deviceId:randomUUID(), edgeId:randomUUID(), accessToken:randomBytes(32).toString('hex'),
        refreshToken:randomBytes(32).toString('hex'), expiresAt:new Date(Date.now()+3600000).toISOString()};
    const project = {projectId:randomUUID(),bindingId:randomUUID(),name:'Release QA',accessMode:'readWrite'};
    let redeemed = false;
    const core = http.createServer(async (req, res) => {
        function respond(status, data) {
            res.writeHead(status, {'Content-Type':'application/json'});
            res.end(JSON.stringify(data));
        }
        if (req.method === 'POST' && req.url === '/v1/device-pairings/'+offer+'/redeem' && !redeemed) {
            let body = '';
            for await (const chunk of req) body += chunk;
            if (JSON.parse(body).oneTimeCode !== code) return respond(403, {});
            redeemed = true;
            return respond(201, identity);
        }
        if (req.url === '/v1/projects' && req.headers.authorization === 'Bearer '+identity.accessToken)
            return respond(200, {items:[project]});
        respond(404, {});
    });
    const client = () => lines(launcher, ['mcp-stdio-global', '--state', state], {cwd:path.dirname(state)});
    const connections = [];
    const output = value => process.stdout.write(JSON.stringify(value)+'\n');
    try {
        core.listen(0, '127.0.0.1');
        await once(core, 'listening');
        output({url:'http://127.0.0.1:'+core.address().port, pairing:offer+'.'+code, projectId:project.projectId});
        for await (const cmd of readline.createInterface({input:process.stdin})) {
            if (cmd === 'connect') {
                const connection = client();
                connections.push(connection);
                assert.ok((await handshake(connection)).includes('get_edge_status'));
            } else if (cmd === 'probe') {
                assert.ok(connections.length);
                for (const c of connections) {
                    const result = await c.call('tools/list', {});
                    assert.ok(result.tools.some(tool => tool.name === 'get_edge_status'));
                }
            } else if (cmd === 'disconnect') {
                for (const c of connections.splice(0)) c.close();
            } else throw Error('Unknown fixture command');
            output({ok:true, connections:connections.length});
        }
    } finally {
        for (const c of connections) c.close();
        core.closeAllConnections();
        core.close();
    }
}
main().catch(error => {console.error(error.message);process.exitCode=1;});
