// node tests/windows-mcp-manual.cjs <Edge.exe> <fixture.json> <evidence.json>
// Uses the two disposable devices from windows-mcp.cjs. Manual endpoints have no Codex session.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {randomUUID} = require('node:crypto');
const {lines,runExe,handshake,tool,argsFor}=require('./windows-mcp.cjs');

async function run(){
    const [executable,fixtureFile,output]=process.argv.slice(2);
    const exe=path.resolve(executable), fixture=JSON.parse(fs.readFileSync(fixtureFile));
    const dev=fixture.devices.find(d=>d.role==='development'),feedback=fixture.devices.find(d=>d.role==='feedback');
    const primary=fixture.project,binding=dev.projects.find(p=>p.projectId===primary).bindingId;
    const register=name=>JSON.parse(runExe(exe,['--state',dev.state,'register-endpoint','--project',primary,
        '--binding',binding,'--name',name])).endpointId;
    const endpoint=register('manual.qa'), otherEndpoint=register('manual.qa.other');
    assert(endpoint && otherEndpoint && endpoint!==otherEndpoint);
    const sessions=[];
    async function connect(device,mode,assigned) {
        const options=argsFor(device,primary,'mcp-stdio',mode);
        if(assigned)options.push('--endpoint',assigned);
        const c=lines(exe,options);sessions.push(c);await handshake(c);return c;
    }
    try{
        const sender=await connect(feedback,'readWrite');
        const who=await connect(dev,'readWrite',endpoint);
        const noEndpoint=await connect(dev,'readWrite');
        const wrongEndpoint=await connect(dev,'readWrite',otherEndpoint);
        const readOnly=await connect(dev,'readOnly',endpoint);
        const wrongDevice=await connect(feedback,'readWrite',endpoint);
        const content='订单导出漏了最后一页';
        const reported=await tool(sender,'submit_feedback',{requestId:randomUUID(),content,targetEndpointId:endpoint});
        const taskId=reported.taskId;
        assert.equal(reported.status,'pending');assert.equal(reported.observation,null);
        const own=await tool(who,'list_tasks');
        assert(own.items.some(t=>t.taskId===taskId && t.targetEndpointId===endpoint && t.content===content));
        const rejected=[];
        for(const [name,scope] of [['endpoint missing',noEndpoint],['other endpoint on same device',wrongEndpoint],
            ['another device with target id',wrongDevice],['read-only client',readOnly]]) {
            if(scope!==readOnly)assert(!(await tool(scope,'list_tasks')).items.some(t=>t.taskId===taskId));
            const response=await scope.call('tools/call',{name:'claim_analysis_task',arguments:{taskId,requestId:randomUUID()}});
            assert.equal(response.isError,true,name);rejected.push(name);
        }
        const before=await tool(sender,'get_feedback',{feedbackId:reported.feedbackId});
        assert.equal(before.status,'pending');assert.equal(before.result,null);
        const claim={taskId,requestId:randomUUID()};
        const claimed=await tool(who,'claim_analysis_task',claim);
        assert.equal(claimed.status,'claimed');assert.deepEqual(await tool(who,'claim_analysis_task',claim),claimed);
        const result='人工录入的验收分析：已记录最后一页缺失，建议核对导出分页结束条件。本次没有运行订单系统。';
        const entry={taskId,requestId:randomUUID(),result};
        for(const scope of [noEndpoint,wrongEndpoint,wrongDevice,readOnly])
            assert.equal((await scope.call('tools/call',{name:'submit_task_result',arguments:entry})).isError,true);
        const done=await tool(who,'submit_task_result',entry);
        assert.equal(done.status,'completed');assert.deepEqual(await tool(who,'submit_task_result',entry),done);
        const got=await tool(sender,'get_feedback',{feedbackId:reported.feedbackId});
        assert.deepEqual([got.feedbackId,got.taskId,got.targetEndpointId,got.content,got.result,got.status,got.observation],
            [reported.feedbackId,taskId,endpoint,content,result,'completed',null]);
        fs.mkdirSync(path.dirname(output),{recursive:true});
        fs.writeFileSync(output,JSON.stringify({checkedAtUtc:new Date().toISOString(),endpointName:'manual.qa',endpoint,
            feedbackId:got.feedbackId,taskId,content,result,mode:'manually entered acceptance observation',observation:null,
            statuses:['pending','claimed','completed'],claimAndResultRejectedFor:rejected,idempotentRetries:true,
            feedbackReadBackOnOtherDevice:true,codexChatBound:false,modelTurns:0},null,2)+'\n');
        console.log('PASS 独立设备提交普通反馈，人工端点领取并返回录入结果，原反馈可查；四种越权领取和提交均被拒，重放返回原回执。');
    }finally{for(const c of sessions)c.close();}
}
run().catch(e=>{console.error(e.message);process.exitCode=1;});
