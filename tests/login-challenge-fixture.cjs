const assert = require('node:assert/strict');
const {createHash, randomInt} = require('node:crypto');

// Tests own the SQL connection to these disposable databases. The deployed server
// has no test mode and exposes neither the generated answer nor its digest.
function fixtureCaptcha(database, id, sql) {
    assert(/^(?:nodicarta_(?:adminqa|admin_scale|activity_qa_[a-f0-9]{10})|projecthub_(?:m\d+_(?:accept|final|clean)|win_coreqa(?:_fresh|_fromv2)?)|phhandover_[a-f0-9]{12})$/.test(database),
        'Login fixture requires a dedicated acceptance database.');
    assert.match(id, /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/);
    const alphabet = '347ACEFHJKMNRTUVWXY';
    const answer = [alphabet[randomInt(3,alphabet.length)],alphabet[randomInt(3)],
        alphabet[randomInt(alphabet.length)],alphabet[randomInt(alphabet.length)]].join('');
    const digest = createHash('sha256').update(id.replaceAll('-','') + answer).digest('hex');
    assert.equal(sql(`WITH target AS (UPDATE projecthub.login_challenges SET answer_digest=decode('${digest}','hex')
          WHERE id='${id}' AND expires_at>clock_timestamp() RETURNING id) SELECT count(*) FROM target`).trim(),'1');
    return answer;
}
module.exports = {fixtureCaptcha};
