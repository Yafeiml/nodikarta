const assert = require('node:assert/strict');
const {createHash, randomUUID} = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const {tool} = require('./windows-mcp.cjs');

const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');

// Only publish to a caller-selected workspace on its already-authorized project.
async function publishPosterCase(writer, workspace, title) {
    const folder = fs.mkdtempSync(path.join(workspace, 'poster-proof-'));
    try {
        const oldMark = '<path fill="none" stroke="#24456b" stroke-width="5" d="M8 44V8h20M25 44V22h22"/>';
        const newMark = '<circle cx="26" cy="26" r="19" fill="#ce5829"/>';
        const reason = '单色印刷打样保留了双折线之间的留白，便于小尺寸辨认。';
        const proof = '印样单 ' + randomUUID().slice(0, 8);
        function svg(content, size = '54 54') {
            return '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 '+size+'">'+content+'</svg>';
        }
        async function publish(file, content, name, purpose, previous) {
            fs.writeFileSync(path.join(folder,file),content);
            const result = await tool(writer,'publish_asset',{requestId:randomUUID(),
                relativePath:path.relative(workspace,path.join(folder,file)),name:title+' · '+name,
                purpose,tags:['ProjectHub 验收样例',title],...(previous && {assetId:previous.assetId,baseVersion:previous.version})});
            assert.equal(result.sha256,sha256(content));
            return result;
        }
        const logo = await publish('logo.svg',svg(oldMark),'标志','验收样例的单色标志');
        const poster = await publish('poster.svg',svg('<rect width="180" height="240" fill="#f3eddf"/><g transform="translate(20 24)">'+oldMark+'</g><text x="22" y="140" font-size="22">留白</text>', '180 240'),'旧海报','复原历史印刷交付');
        const evidence = await publish('approval.svg',svg('<text x="8" y="28">'+proof+'</text><text x="8" y="55">'+reason+'</text>', '640 80'),'印样单','记录旧海报采用标志的印样依据');
        const assetRefs = [
            {role:'deliverable',assetId:poster.assetId,version:poster.version},
            {role:'used',assetId:logo.assetId,version:logo.version},
            {role:'evidence',assetId:evidence.assetId,version:evidence.version,locator:'印样说明'}
        ];
        const input = {requestId:randomUUID(),title:title+' · 旧海报定版决定',body:reason,evidence:proof,assetRefs};
        const decision = await tool(writer,'submit_knowledge_commit',input);
        assert.ok(decision.commitId);
        assert.deepEqual(await tool(writer,'submit_knowledge_commit',input),decision);
        const current = await publish('logo.svg',svg(newMark),'标志','验收样例的后续彩色标志',logo);
        assert.notEqual(current.sha256,logo.sha256);
        return {title,poster,original:logo,latest:current,evidence,decision,input};
    } finally {
        fs.rmSync(folder,{recursive:true,force:true});
    }
}

async function readPosterCase(reader, workspace, fixture) {
    const poster = (await tool(reader,'list_assets',{query:fixture.title+' · 旧海报'})).items[0];
    assert.equal(poster.assetId,fixture.poster.assetId);
    const lookup = {assetId:poster.assetId,version:poster.version};
    const [decision] = (await tool(reader,'get_asset_decisions',lookup)).items;
    assert.equal(decision.commitId,fixture.decision.commitId);
    assert.equal(decision.body,fixture.input.body);
    assert.equal(decision.isCurrent,true);
    const used = decision.assetRefs.find(x=>x.role==='used');
    const evidence = decision.assetRefs.find(x=>x.role==='evidence');
    assert.equal(evidence.assetId,fixture.evidence.assetId);
    assert.equal(evidence.locator,'印样说明');
    assert.equal((await tool(reader,'get_asset',{assetId:used.assetId})).version,fixture.latest.version);
    const file = 'received-'+randomUUID()+'.svg';
    const saved = await tool(reader,'download_asset',{assetId:used.assetId,version:used.version,destinationPath:file});
    assert.equal(saved.sha256,fixture.original.sha256);
    assert.equal(sha256(fs.readFileSync(path.join(workspace,file))),fixture.original.sha256);
    return lookup;
}

module.exports = {publishPosterCase,readPosterCase};
