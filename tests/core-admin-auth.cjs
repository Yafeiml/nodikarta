// Invoked by core-activity.cjs against its own randomly named database and administrator.
const assert = require('node:assert/strict');
const {randomBytes} = require('node:crypto');
const {fixtureCaptcha} = require('./login-challenge-fixture.cjs');

module.exports = async function verifyAdminAuth(origin, database, sql, password, restart) {
    function browser() {
        const jar = new Map();
        async function request(path, fields) {
            const response = await fetch(origin + path, {
                method: fields ? 'POST' : 'GET', redirect: 'manual',
                headers: {Cookie: Array.from(jar, ([key,value])=>key+'='+value).join('; ')},
                body: fields && new URLSearchParams(fields)
            });
            for (const line of response.headers.getSetCookie()) {
                const [pair] = line.split(';');
                const offset = pair.indexOf('=');
                jar.set(pair.slice(0,offset),pair.slice(offset+1));
            }
            return response;
        }
        function tokens(html) {
            const csrf = html.match(/name="__RequestVerificationToken"[^>]*value="([^"]+)"/)?.[1];
            assert.ok(csrf,'Form missing antiforgery token');
            const id = html.match(/name="challengeId" value="([^"]+)"/)?.[1];
            if (id) {
                const image = html.match(/<svg class="captcha-image"[^>]*>([\s\S]*?)<\/svg>/)?.[1];
                assert.equal(image?.match(/<path /g)?.length,4,'Expected four drawn characters');
                assert.match(html,/id="captcha"[^>]*minlength="4" maxlength="4"/);
            }
            return {__RequestVerificationToken:csrf,...(id && {challengeId:id})};
        }
        async function form(route) {
            const reply = await request(route);
            assert.equal(reply.status,200);
            return tokens(await reply.text());
        }
        async function credentials() {
            const fields = await form('/login');
            return {...fields, username:'fixture', password, captcha:fixtureCaptcha(database,fields.challengeId,sql).toLowerCase()};
        }
        return {request,form,credentials,tokens};
    }
    const a = browser(), b = browser();
    assert.equal((await a.request('/account/password')).status,302);
    const original = await a.credentials();
    const other = await b.credentials();
    assert.equal((await b.request('/login',{...other, challengeId:original.challengeId,captcha:original.captcha})).status,400);
    assert.equal((await a.request('/login',{...original,captcha:'0000'})).status,400);
    assert.equal((await a.request('/login',original)).status,400,'Wrong attempt consumes challenge');
    const expired = await a.credentials();
    sql(`UPDATE login_challenges SET expires_at=now()-interval '1 second' WHERE id='${expired.challengeId}'`);
    assert.equal((await a.request('/login',expired)).status,400);

    const firstTab = await a.credentials(), secondTab = await a.credentials();
    const refresh = await a.request('/login?handler=Refresh',firstTab);
    assert.equal(refresh.status,200);
    assert.notEqual(a.tokens(await refresh.text()).challengeId,firstTab.challengeId);
    assert.equal((await a.request('/login', firstTab)).status,400,'Refreshing invalidates the replaced image');
    const race = await Promise.all([a.request('/login',secondTab),a.request('/login',secondTab)]);
    assert.deepEqual(race.map(r=>r.status).sort(),[302,400],'Second tab works; duplicate submissions admit one');
    assert.equal((await b.request('/login',other)).status,302,'Foreign-browser replay did not consume original challenge');
    console.log('PASS login verification: wrong, expired, refreshed, foreign browser, parallel tabs, mixed case and single-use concurrent submission');

    await restart();
    const replacement = randomBytes(28).toString('hex');
    const route = '/account/password';
    const currentForm = await a.form(route), otherForm = await b.form(route);
    const update = {currentPassword:password,newPassword:replacement,confirmPassword:replacement};
    assert.equal((await a.request(route,update)).status,400,'CSRF token required');
    for (const invalid of [{currentPassword:'wrong-current-password'},{newPassword:'short'},{confirmPassword:'mismatch'},
        {newPassword:password,confirmPassword:password}]) {
        const failure = await a.request(route,{...currentForm,...update,...invalid});
        assert.equal(failure.status,400);
        const text = await failure.text();
        assert.ok(!text.includes(password) && !text.includes(replacement),'Secret echoed on validation failure');
    }
    assert.equal((await b.request('/projects')).status,200);
    const changes = await Promise.all([a.request(route,{...currentForm,...update}),b.request(route,{...otherForm,...update})]);
    assert.equal(sql("SELECT count(*) FROM audit_log WHERE action='owner.password_changed'"),'1');
    assert.ok(changes.some(r=>r.status===302 && new URL(r.headers.get('location'),origin).pathname==='/login'));
    assert.equal(sql('SELECT session_version FROM owner_account'),'2');
    for (const user of [a,b]) assert.equal((await user.request('/projects')).status,302,'Prior login should be invalid');
    assert.equal((await a.request('/login',await a.credentials())).status,401);
    assert.equal((await a.request('/login',{...await a.credentials(),password:replacement})).status,302);
    assert.equal((await a.request('/projects')).status,200);
    console.log('PASS password: old-password verification, length/confirmation/reuse, CSRF, concurrent update, audit, invalidated sessions and new login');
};
