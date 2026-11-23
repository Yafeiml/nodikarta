// Called by the disposable Core / Windows Edge integration test.
const assert = require('node:assert/strict');

exports.run = async ({ proxy, add, update, profile, pair, success, cli, pass }) => {
    const install = profile('pending-installer');
    const active = await success(pair(install, await add(), proxy));
    const source = await success(pair(profile('pending-source'), await add(), proxy));
    const other = await success(pair(profile('other-source'), await add(), proxy));
    const original = await update(source.deviceId);
    const alternativeUpdate = await update(other.deviceId);
    const alternativeAdd = await add();
    try {
        proxy.after = action => action === 'confirm';
        assert.notEqual((await pair(install, original, proxy, '--replace')).code, 0);
        const winner = [...proxy.tokens.values()].at(-1).deviceId;
        proxy.after = undefined;
        proxy.before = action => action === 'confirm';
        const start = proxy.calls.length;
        for (const code of [alternativeUpdate, alternativeAdd]) {
            const attempt = await pair(install, code, proxy, '--replace');
            assert.notEqual(attempt.code, 0);
            assert.match(attempt.err, /上次设备接替尚待核实/);
        }
        assert.equal((await success(cli(install, ['status']))).deviceId, active.deviceId);
        assert(!proxy.calls.slice(start).some(x => x.action === 'claim' || x.action === 'redeem'));

        proxy.before = undefined;
        const recovered = await pair(install, alternativeAdd, proxy, '--replace');
        assert.notEqual(recovered.code, 0);
        assert.match(recovered.err, /上次设备接替已确认并启用/);
        assert.equal((await success(cli(install, ['status']))).deviceId, winner);
        assert(!proxy.calls.slice(start).some(x => x.action === 'claim' || x.action === 'redeem'));
        const next = await success(pair(install, alternativeAdd, proxy, '--replace'));
        assert.notEqual(next.deviceId, winner);
        assert.equal((await success(cli(install, ['status']))).deviceId, next.deviceId);
        pass('确认回包丢失时输入不同的更新码或添加码，原接替保留；恢复后先启用原回执，再次明确配对才消费新码');

        const fresh = profile('superseded-pending');
        proxy.before = action => action === 'confirm';
        assert.notEqual((await pair(fresh, alternativeUpdate, proxy)).code, 0);
        const freshAdd = await add();
        const pausedAt = proxy.calls.length;
        const newPair = await pair(fresh, freshAdd, proxy);
        assert.notEqual(newPair.code, 0);
        assert.match(newPair.err, /上次设备接替尚待核实/);
        assert(!proxy.calls.slice(pausedAt).some(x => x.action === 'redeem'));
        proxy.before = undefined;
        const reissued = await update(other.deviceId);
        const checkpoint = proxy.calls.length;
        const replacement = await success(pair(fresh, reissued, proxy));
        assert.deepEqual(proxy.calls.slice(checkpoint).map(({ action, status }) => [action,status]),
            [['confirm',401], ['claim',200], ['confirm',200]]);
        assert.equal((await success(cli(fresh, ['status']))).deviceId, replacement.deviceId);
        pass('未配对 Edge 已领取旧码，管理员重新签发后直接输入新码，先核实旧接替已失效再完成新接替');
    } finally { proxy.before = proxy.after = undefined; }
};
