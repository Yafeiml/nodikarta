// node tests/windows-access-fixture.cjs <desktop.exe> [evidence directory] [--interface-status | --action-details | --unbind | --antigravity [--previous-agy-cli <Edge.exe>] | --codex-windows | --codex-copies | --skills | --back-card]
const assert = require('node:assert/strict');
const {spawn} = require('node:child_process');
const {randomBytes, randomUUID} = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const {once} = require('node:events');

async function execute(file,args,input,showOutput=false) {
    const child = spawn(file,args,{windowsHide:true,stdio:['pipe',showOutput?'inherit':'ignore','inherit']});
    child.stdin.on('error',()=>{});
    child.stdin.end(input);
    assert.equal((await once(child,'exit'))[0],0,path.basename(file)+' failed');
}
async function main() {
    const exe = path.resolve(process.argv[2]), cli = path.join(path.dirname(exe),'ProjectHub.Edge.exe');
    const projectId = randomUUID();
    const longNames = process.argv.includes('--unbind');
    const projectName = longNames ? 'ProjectHub 跨平台客户端与共享素材协作验收项目' :
        process.argv.includes('--language') ? '目录设置' : 'ProjectHub';
    const root = path.join(os.tmpdir(),'projecthub-accessqa-'+randomUUID().replaceAll('-',''));
    fs.mkdirSync(root);
    const devices = ['none','readOnly','readWrite'].map(mode=>({mode,offer:randomUUID(),code:randomBytes(16).toString('hex'),
        deviceId:randomUUID(),edgeId:randomUUID(),bindingId:randomUUID(),accessToken:randomBytes(32).toString('hex'),refreshToken:randomBytes(32).toString('hex')}));
    const server = http.createServer(async (req,res)=>{
        const send = (status,value) => { res.writeHead(status,{'Content-Type':'application/json'}); res.end(JSON.stringify(value)); };
        const offer = devices.find(d=>req.url===`/v1/device-pairings/${d.offer}/redeem`);
        if (req.method==='POST' && offer && !offer.used) {
            let body=''; for await(const data of req) body+=data;
            if (JSON.parse(body).oneTimeCode!==offer.code) return send(403,{});
            offer.used = true;
            const {deviceId,edgeId,accessToken,refreshToken}=offer;
            return send(201,{deviceId,edgeId,accessToken,refreshToken,expiresAt:new Date(Date.now()+3600_000).toISOString()});
        }
        const device = devices.find(d=>req.headers.authorization==='Bearer '+d.accessToken);
        if (device && fs.existsSync(path.join(root,'core-offline'))) {
            const fault = fs.readFileSync(path.join(root,'core-offline'),'utf8');
            if (fault === 'drop') return req.socket.destroy();
            if (fault === 'delay') await new Promise(resolve=>setTimeout(resolve,2000));
            else return send(fault === '403' ? 403 : 503,{});
        }
        if (device && req.method==='GET' && req.url==='/v1/projects')
            return send(200,{items:device.mode==='none'?[]:[{name:projectName,projectId,bindingId:device.bindingId,accessMode:device.mode}]});
        return send(404,{});
    });
    let targetCore;
    let leakedCredential = false;
    try {
        server.listen(0,'127.0.0.1'); await once(server,'listening');
        for (const d of devices) {
            const args=['--state',path.join(root,d.mode),'pair','--core','http://127.0.0.1:'+server.address().port];
            if (longNames) args.push('--label','设计协作工作站-Windows-11-演示设备-Long-Device-Name');
            await execute(cli,args,d.offer+'.'+d.code+'\n');
        }
        if (process.argv.includes('--codex-copies')) {
            const args=['-File',path.join(__dirname,'windows-codex-copies.ps1'),'-Executable',exe,'-RunRoot',root];
            if (process.argv[3] && !process.argv[3].startsWith('--')) args.push('-EvidenceDirectory',path.resolve(process.argv[3]));
            await execute('pwsh.exe',args,null,true);
            return;
        }
        const args=['-File',path.join(__dirname,'windows-access-gui.ps1'),'-Executable',exe,'-RunRoot',root,'-ProjectId',projectId,
            '-FixtureDescription','独立回环模拟 Core、临时配对状态、实际 Windows 桌面程序和 MCP CLI'];
        if(process.argv.includes('--interface-status')) args.push('-InterfaceStatus');
        if(process.argv.includes('--action-details')) args.push('-ActionDetails');
        if(process.argv.includes('--unbind')) args.push('-Unbind');
        if(process.argv.includes('--antigravity')) args.push('-Antigravity');
        const previous = process.argv.indexOf('--previous-agy-cli');
        if(previous >= 0) args.push('-PreviousAntigravityCli',fs.realpathSync(process.argv[previous+1]));
        if(process.argv.includes('--codex-windows')) args.push('-CodexWindows');
        if(process.argv.includes('--skills')) args.push('-SkillsTab');
        if(process.argv.includes('--back-card')) args.push('-BackCard');
        if(process.argv.includes('--language')) args.push('-Language');
        if(process.argv.includes('--brand') || process.argv.includes('--brand-titles')) args.push('-BrandIcons');
        if(process.argv.includes('--brand-titles')) args.push('-TitleMarksOnly');
        if(process.argv.includes('--navigation') || process.argv.includes('--navigation-baseline')) args.push('-Navigation');
        if(process.argv.includes('--navigation-baseline')) args.push('-NavigationBaseline');
        if(process.argv.includes('--harness-upgrade')) {
            const previous = process.argv[process.argv.indexOf('--harness-upgrade') + 1];
            assert.ok(previous, 'Specify the previous packaged desktop executable.');
            args.push('-UpgradeFrom', path.resolve(previous));
        }
        if (process.argv.includes('--change-core')) {
            const offer = randomUUID(), code = randomBytes(16).toString('hex');
            const deviceId = randomUUID(), edgeId = randomUUID(), bindingId = randomUUID();
            const accessToken = randomBytes(32).toString('hex'), refreshToken = randomBytes(32).toString('hex');
            targetCore = http.createServer(async (req, res) => {
                const send = (status, value) => {
                    res.writeHead(status, {'Content-Type': 'application/json'});
                    res.end(JSON.stringify(value));
                };
                if (devices.some(d => req.headers.authorization === 'Bearer ' + d.accessToken))
                    leakedCredential = true;
                if (req.method === 'POST' && req.url === '/v1/device-pairings/' + offer + '/redeem') {
                    let body = '';
                    for await (const chunk of req) body += chunk;
                    if (JSON.parse(body).oneTimeCode !== code) return send(403, {});
                    return send(201, {deviceId, edgeId, accessToken, refreshToken, expiresAt: new Date(Date.now() + 3600000).toISOString()});
                }
                if (req.url === '/v1/projects' && req.headers.authorization === 'Bearer ' + accessToken)
                    return send(200, {items: [{name: 'Second Core Project', projectId, bindingId, accessMode: 'readOnly'}]});
                return send(404, {});
            });
            targetCore.listen(0, '127.0.0.1');
            await once(targetCore, 'listening');
            fs.writeFileSync(path.join(root, 'target.json'), JSON.stringify({
                address: 'http://127.0.0.1:' + targetCore.address().port, pairing: offer + '.' + code
            }));
            args.push('-ChangeCore');
        }
        if(process.argv[3] && !process.argv[3].startsWith('--')) {
            const evidence = path.resolve(process.argv[3]); fs.mkdirSync(evidence,{recursive:true});
            args.push('-EvidenceFile',path.join(evidence,'checks.json'),'-ScreenshotDirectory',evidence);
        }
        await execute('pwsh.exe',args,null,true);
        assert.equal(leakedCredential, false, 'Old Core access token reached new Core');
    } finally {
        if (targetCore) { targetCore.closeAllConnections(); targetCore.close(); }
        server.closeAllConnections(); server.close();
        assert.equal(path.dirname(fs.realpathSync(root)),fs.realpathSync(os.tmpdir()));
        assert.match(path.basename(root),/^projecthub-accessqa-[a-f0-9]{32}$/);
        fs.rmSync(root,{recursive:true,force:true,maxRetries:5,retryDelay:150});
    }
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});
