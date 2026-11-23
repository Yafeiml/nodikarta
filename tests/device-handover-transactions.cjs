// Additional assertions for the disposable database created by windows-device-handover.cjs.
const assert = require('node:assert/strict');
const { randomBytes, randomUUID } = require('node:crypto');
const { spawn } = require('node:child_process');

async function until(predicate) {
    const deadline = Date.now() + 10000;
    while (Date.now() < deadline) {
        if (await predicate()) return;
        await new Promise(resolve => setTimeout(resolve, 75));
    }
    throw Error('Timed out waiting for the test transaction barrier.');
}

// Hold the target row while the admin POST and Edge confirm arrive in a known order.
// Observe real blocked PostgreSQL backends before releasing them; neither endpoint is mocked.
async function holdTarget({ distro, pg, name, sql }, deviceId) {
    assert.match(deviceId, /^[a-f\d]{8}(?:-[a-f\d]{4}){3}-[a-f\d]{12}$/i);
    const options = ['-d',distro,'-u','root','--','docker','exec','-i','-u','postgres',pg,
        'psql','-XAtq','-v','ON_ERROR_STOP=1','-d',name];
    const holder = spawn('wsl.exe', options, { windowsHide: true });
    let output = '';
    holder.stdout.on('data', part => output += part);
    const closed = new Promise((resolve, reject) => { holder.once('error', reject); holder.once('close', resolve); });
    holder.stdin.write("BEGIN;\nSELECT id FROM projecthub.devices WHERE id='" + deviceId + "' FOR UPDATE;\n");
    try { await until(() => output.includes(deviceId)); }
    catch (error) { holder.stdin.end('ROLLBACK;\n'); await closed; throw error; }
    return {
        waitFor: count => until(() => Number(sql(name, "SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock';")) === count),
        release: async () => { holder.stdin.end('COMMIT;\n'); assert.equal(await closed, 0); }
    };
}

async function run(ctx) {
    const { page, core, api, addDevice, update, parse, project, otherProject, sql, name, pass } = ctx;
    for (const firstAction of ['downgrade','confirm']) {
        const old = await addDevice();
        const invitation = parse(await update(page, old.deviceId));
        const route = '/device-pairings/' + invitation.id;
        const attemptSecret = randomBytes(32).toString('hex');
        const candidate = await api(core, route + '/claim', null, {
            oneTimeCode: invitation.secret, instanceNonce: randomUUID(), deviceLabel: '新机', attemptSecret
        });
        await page.goto('/devices/' + old.deviceId);
        const form = page.locator('form[data-device-id="' + old.deviceId + '"][data-project-id="' + project + '"]');
        const fields = await form.evaluate(f => Object.fromEntries(new FormData(f)));
        const edit = () => page.request.post('/devices?handler=ChangeGrant', {
            form: { ...fields, accessMode: 'readOnly' }, maxRedirects: 0
        }).then(r => r.status());
        const confirm = () => api(core, route + '/confirm', null, { attemptSecret });
        const queue = firstAction === 'downgrade' ? [edit,confirm] : [confirm,edit];
        const barrier = await holdTarget(ctx, old.deviceId);
        const replies = [];
        try {
            for (const operation of queue) {
                const pending = operation();
                // Keep early HTTP failures observable while waiting for the other request to arrive.
                pending.catch(() => {});
                replies.push(pending);
                await barrier.waitFor(replies.length);
            }
        } finally { await barrier.release(); }
        const results = await Promise.all(replies);
        const firstWasEdit = firstAction === 'downgrade';
        assert.equal(results[firstWasEdit ? 0 : 1], firstWasEdit ? 302 : 404);
        const receipt = results[firstWasEdit ? 1 : 0];
        assert(receipt.deviceId === candidate.deviceId && receipt.refreshToken === candidate.refreshToken);
        const actual = await api(core, '/projects/' + project, receipt.accessToken);
        assert.equal(actual.accessMode, firstWasEdit ? 'readOnly' : 'readWrite');
        await api(core, '/projects', old.accessToken, undefined, 401);
        pass(firstWasEdit ? '两条事务同时等待：降权先取得设备锁，管理页成功，新设备得到只读权限'
            : '两条事务同时等待：接替先取得设备锁，旧管理表单返回 404，新设备继承此前有效权限');

        if (!firstWasEdit) continue;
        // Move only this private receipt and its access-token deadline into the past.
        // An aged receipt must still contain precisely the original credentials with their old deadline.
        const past = '2025-01-01T00:00:00Z';
        sql(name, "UPDATE projecthub.device_pairings SET expires_at='" + past + "',confirmed_access_expiry='" + past + "' WHERE id='" + invitation.id + "';\n"
            + "UPDATE projecthub.devices SET access_expires_at='" + past + "' WHERE id='" + receipt.deviceId + "';");
        const denied = await api(core, route + '/confirm', null, { attemptSecret: randomBytes(32).toString('hex') }, 401);
        assert(!('refreshToken' in denied) && !('accessToken' in denied));
        await api(core, '/projects', receipt.accessToken, undefined, 401);
        const stale = await confirm();
        assert(stale.accessToken === receipt.accessToken && stale.refreshToken === receipt.refreshToken);
        assert.equal(Date.parse(stale.expiresAt), Date.parse(past));
        await api(core, '/projects', stale.accessToken, undefined, 401);
        const rotated = await api(core, '/tokens/refresh', null, { refreshToken: stale.refreshToken });
        assert(rotated.accessToken !== stale.accessToken && rotated.refreshToken !== stale.refreshToken);
        const refused = await api(core, route + '/confirm', null, { attemptSecret }, 401);
        assert(!('accessToken' in refused) && !('refreshToken' in refused));
        await api(core, '/projects/' + project, rotated.accessToken);
        await api(core, '/projects/' + otherProject, rotated.accessToken, undefined, 404);
        assert.equal(sql(name, "SELECT count(*) FROM projecthub.refresh_tokens WHERE device_id='" + receipt.deviceId + "' AND used_at IS NULL;"), '1');
        pass('确认回执过期后重试只取回已过期的原访问凭据；正常续期后旧回执返回 401，新访问仅限原授权');
    }
}

module.exports = { run };
