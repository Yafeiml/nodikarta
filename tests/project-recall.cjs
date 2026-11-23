const assert = require('node:assert/strict');
const {randomUUID} = require('node:crypto');
const {tool} = require('./windows-mcp.cjs');
const {publishPosterCase} = require('./poster-decisions.cjs');

function versionCheck(row, poster) {
    assert.equal(row.versionUseChecks?.length,1,'The historical decision must flag the newer published original');
    const [check] = row.versionUseChecks;
    assert.deepEqual([check.assetId,check.usedVersion,check.latestPublishedVersion,check.currentAdoption],
        [poster.original.assetId,1,2,'requiresEvidence']);
    assert.match(check.guidance,/当时|历史|旧稿/);
    assert.match(check.guidance,/依据|证据|规范/);
    assert.ok(check.searchQuery.includes(poster.original.assetId));
    return check;
}

async function checkDecisionVersions(reader, poster) {
    const response = await tool(reader,'get_asset_decisions',{assetId:poster.poster.assetId,version:1});
    const decision = response.items.find(item=>item.commitId===poster.decision.commitId);
    assert.ok(decision);
    // Older clients may submit these references verbatim. Extra read-only fields belong beside them.
    assert.deepEqual(decision.assetRefs,poster.input.assetRefs);
    const used = decision.linkedAssets?.find(ref=>ref.role==='used');
    assert.deepEqual([used?.version,used?.latestPublishedVersion],[1,2]);
    return versionCheck(decision,poster);
}

async function checkValidity(writer, reader, otherProject, poster) {
    const commit = (title,body,evidence,validity,extra={}) => tool(writer,'submit_knowledge_commit',{
        requestId:randomUUID(),title,body,evidence,validity,...extra
    });
    const scope = 'OFFSET-EXHIBIT';
    const old = await commit('OLD-DIE-17','旧展品采用单色标志。','打样单 A17',
        {kind:'decision',scope},{assetRefs:poster.input.assetRefs});
    const second = await commit('临时展品规范','过渡批次使用灰色标志。','签准单 B18',
        {kind:'decision',scope,supersedesItemId:old.itemId,supersedesVersion:old.version});
    const current = await commit('彩印定稿','彩色展品使用双色标志。','签准单 C19',
        {kind:'decision',scope,supersedesItemId:second.itemId,supersedesVersion:second.version});
    const legacySearch = await tool(reader,'search_project_knowledge',{query:'OLD-DIE-17'});
    const legacyBrief = await tool(reader,'get_project_snapshot',{task:'OLD-DIE-17'});
    const oldHistory = await tool(reader,'get_knowledge_history',{itemId:old.itemId});
    const linked = await tool(reader,'get_asset_decisions',{assetId:poster.poster.assetId,version:poster.poster.version});
    for (const result of [legacySearch,legacyBrief,oldHistory,linked]) {
        const match = result.items.find(item=>item.commitId===old.commitId);
        assert.equal(match.evidence,old.evidence);
        assert.equal(match.validity.status,'superseded');
        assert.equal(match.validity.currentDecision.itemId,current.itemId);
        assert.equal(match.validity.currentDecision.version,current.version);
        assert.equal(match.validity.currentDecision.scope,scope);
    }
    const policyByScope = await tool(reader,'search_project_knowledge',{query:scope});
    assert.ok(policyByScope.items.some(row=>row.commitId===current.commitId && row.validity.status==='current' && row.evidence==='签准单 C19'));
    const isolated = await tool(otherProject,'get_project_snapshot',{task:scope});
    assert.deepEqual(isolated.items,[]);

    const observedAt = '2026-01-12T08:30:00+08:00';
    const observation = await commit('终端配置','实测已安装离线阅读器。','现场文件清单',
        {kind:'observation',subject:'KIOSK-41',observedAt});
    const device = (await tool(reader,'get_project_snapshot',{task:'KIOSK-41'})).items[0];
    assert.equal(device.commitId,observation.commitId);
    assert.equal(device.validity.status,'lastObserved');
    assert.equal(device.validity.subject,'KIOSK-41');
    assert.equal(Date.parse(device.validity.observedAt),Date.parse(observedAt));
    return {historicalItemId:old.itemId,currentDecisionItemId:current.itemId,
        pastEvidence:old.evidence,currentEvidence:current.evidence,
        deviceSubject:device.validity.subject,deviceStatus:device.validity.status,observedAt:device.validity.observedAt};
}

async function checkProjectRecall(writer, reader, otherProject, workspace, poster) {
    const phrase = '继续做上次那张宣传画';
    const brief = (client,task) => tool(client,'get_project_snapshot',{task});
    const search = (client,query) => tool(client,'search_project_knowledge',{query});
    const commit = (client,title,body,evidence) => tool(client,'submit_knowledge_commit',{requestId:randomUUID(),title,body,evidence});
    const diagnostic = await commit(writer,'PX-4403 校准结果','探头温漂。'.repeat(450),'实验室记录 QA-003');
    const foreign = await commit(otherProject,poster.decision.title,'同名海报仅用于乙项目展台。','乙项目印制记录');
    const historicalUse = await checkDecisionVersions(reader,poster);
    const snapshot = await tool(reader,'get_project_snapshot');
    assert.ok(snapshot.items.some(row=>row.commitId===diagnostic.commitId && row.body===diagnostic.body));
    const queries = [];
    for (const query of [phrase,'以前那张宣传画用了哪个标识',poster.decision.title,'Logo']) {
        const result = await search(reader,query);
        const rank = result.items.findIndex(row=>row.commitId===poster.decision.commitId)+1;
        assert.equal(rank,1,query+' must match the original decision first');
        assert.ok(result.items.every(row=>row.commitId!==foreign.commitId));
        const pin = result.items[0].linkedAssets.find(ref=>ref.role==='used');
        assert.deepEqual([pin.assetId,pin.version,pin.latestPublishedVersion],[poster.original.assetId,1,2]);
        versionCheck(result.items[0],poster);
        queries.push({query,rank,count:result.items.length});
    }
    const handoff = await brief(reader,phrase);
    assert.equal(handoff.view,'brief');
    assert.equal(handoff.items.length,1);
    const old = handoff.items[0];
    versionCheck(old,poster);
    assert.equal(old.commitId,poster.decision.commitId);
    assert.equal(old.body,poster.input.body);
    assert.equal(old.evidence,poster.input.evidence);
    const used = old.assetRefs.find(ref=>ref.role==='used');
    assert.equal(used.assetId,poster.original.assetId);
    assert.equal(used.version,poster.original.version);
    assert.equal(used.latestPublishedVersion,poster.latest.version);
    assert.equal(old.assetRefs.find(ref=>ref.role==='deliverable').assetId,poster.poster.assetId);
    assert.equal(old.assetRefs.find(ref=>ref.role==='evidence').assetId,poster.evidence.assetId);
    assert.deepEqual((await brief(otherProject,phrase)).items.map(item=>item.commitId),[foreign.commitId]);
    const noise = '继续做上次那个量子钟校准星历推算';
    // A completely unrelated task must not fall back to recent project records.
    const unrelated = '继续做上次那份深海鲸群声纹图谱';
    assert.deepEqual((await brief(reader,unrelated)).items,[]);
    assert.deepEqual((await search(reader,unrelated)).items,[]);
    // A common word can legitimately match the diagnostic, never the poster.
    assert.ok((await search(reader,noise)).items.every(row=>row.commitId!==poster.decision.commitId));
    const tech = await brief(reader,'PX-4403');
    assert.equal(tech.items[0].commitId,diagnostic.commitId);
    assert.deepEqual(tech.items[0].versionUseChecks,[]);
    assert.equal(tech.items[0].truncated,true);
    assert.ok(tech.items[0].body.length<diagnostic.body.length);
    const full = await tool(reader,'get_knowledge_history',{itemId:diagnostic.itemId});
    assert.equal(full.items[0].body,diagnostic.body);

    const nextPoster = await publishPosterCase(writer,workspace,'异地展台交付');
    const candidates = (await brief(reader,phrase)).items.map(item=>item.commitId);
    assert.ok(candidates.includes(poster.decision.commitId) && candidates.includes(nextPoster.decision.commitId),
        'Two similar poster decisions must remain distinguishable');
    const recommendation = await commit(writer,'品牌换版复核','已否决把最新发布的 Logo v2 自动用于新稿，目前没有新稿用标决定。','独立评审记录 QA-008');
    const approvalResults = await search(reader,'品牌换版复核');
    assert.equal(approvalResults.items[0].commitId,recommendation.commitId);
    assert.equal(approvalResults.items[0].body,recommendation.body);
    const policy = await commit(writer,'品牌标志现行规范','经批准，城市展览新出品采用 Logo v1，继续以单色版印刷。','已签批的 QA-009');
    const policyHits = (await search(reader,'Logo 现行规范')).items;
    assert.equal(policyHits[0].commitId,policy.commitId);
    assert.equal(policyHits[0].body,policy.body);
    assert.equal(policyHits[0].evidence,policy.evidence);
    const followup = await search(reader,historicalUse.searchQuery);
    assert.ok(followup.items.some(item=>item.commitId===policy.commitId && item.evidence===policy.evidence));
    assert.equal((await tool(reader,'get_asset',{assetId:poster.original.assetId})).version,2);
    assert.equal((await tool(reader,'get_asset_decisions',{assetId:poster.poster.assetId,version:1})).items[0].assetRefs.find(r=>r.role==='used').version,1);
    const atLimit = await brief(reader,'');
    assert.equal(atLimit.items.length,5);
    assert.equal(atLimit.hasMore,false);
    await commit(writer,'仓储温控','设备温度保持在允许范围内。','观察单 QA-010');
    const overLimit = await brief(reader,'');
    assert.equal(overLimit.items.length,5);
    assert.equal(overLimit.hasMore,true);
    const symbols = await commit(writer,'x','y','z');
    assert.equal((await search(reader,'x y')).items[0]?.commitId,symbols.commitId,
        'Keep literal matches spanning the title and body');
    const fullBytes = Buffer.byteLength(JSON.stringify(snapshot));
    const briefBytes = Buffer.byteLength(JSON.stringify(handoff));
    assert.ok(briefBytes<fullBytes);
    assert.equal(tech.items[0].validity.status,'unclassified');
    const validity = await checkValidity(writer,reader,otherProject,nextPoster);
    return {queries,matchedByBrief:1,similarPosterDecisions:candidates.length,
        negativeLookup:unrelated,legacySnapshotBytes:fullBytes,taskBriefBytes:briefBytes,
        pinnedUsedVersion:used.version,latestPublishedVersion:used.latestPublishedVersion,
        historicalUseRequiresEvidence:historicalUse.currentAdoption,historicalGuidance:historicalUse.guidance,
        policyFromSuggestedQuery:true,validity,
        currentUseEvidence:'No policy existed at first. A denied proposal followed by approval QA-009 for Logo v1 remained readable while the latest file was v2.'};
}

module.exports = {checkProjectRecall,checkDecisionVersions};
