using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ProjectHub.Core;

public sealed record LoginChallenge(Guid Id, string Drawing);

public sealed partial class CoreStore
{
    public async Task<LoginChallenge> IssueLoginChallengeAsync(Guid browser, Guid? replace)
    {
        var id = Guid.NewGuid();
        var answer = LoginCaptcha.Generate();
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var prune = new NpgsqlCommand("""
            DELETE FROM projecthub.login_challenges
            WHERE expires_at <= clock_timestamp() OR (id = $1 AND browser_id = $2)
            """, conn, tx);
        prune.Parameters.AddWithValue(replace ?? Guid.Empty);
        prune.Parameters.AddWithValue(browser);
        await prune.ExecuteNonQueryAsync();
        await using var insert = new NpgsqlCommand("""
            INSERT INTO projecthub.login_challenges(id, browser_id, answer_digest) VALUES ($1, $2, $3)
            """, conn, tx);
        insert.Parameters.AddWithValue(id);
        insert.Parameters.AddWithValue(browser);
        insert.Parameters.AddWithValue(LoginCaptcha.Hash(id, answer));
        await insert.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return new(id, LoginCaptcha.Draw(answer));
    }

    public async Task<bool> ConsumeLoginChallengeAsync(Guid browser, Guid id, string answer)
    {
        await using var consume = db.CreateCommand("""
            DELETE FROM projecthub.login_challenges WHERE id = $1 AND browser_id = $2
            RETURNING expires_at > clock_timestamp() AND answer_digest = $3
            """);
        consume.Parameters.AddWithValue(id);
        consume.Parameters.AddWithValue(browser);
        var normalized = answer.AsSpan().Trim();
        consume.Parameters.AddWithValue(LoginCaptcha.Hash(id, normalized.Length == LoginCaptcha.Length
            ? normalized.ToString().ToUpperInvariant() : ""));
        return await consume.ExecuteScalarAsync() is true;
    }
}

internal static class LoginCaptcha
{
    internal const int Length = 4;
    private const string Letters = "ACEFHJKMNRTUVWXY";
    private const string Digits = "347";
    public static string Generate()
    {
        var characters = new char[Length];
        characters[0] = RandomNumberGenerator.GetString(Letters, 1)[0];
        characters[1] = RandomNumberGenerator.GetString(Digits, 1)[0];
        RandomNumberGenerator.GetItems((Letters + Digits).AsSpan(), characters.AsSpan(2));
        RandomNumberGenerator.Shuffle(characters.AsSpan());
        return new(characters);
    }

    public static byte[] Hash(Guid id, string answer) =>
        SHA256.HashData(Encoding.ASCII.GetBytes(id.ToString("N") + answer));

    // Draw strokes directly so the document never contains an answer in text or metadata.
    private static readonly Dictionary<char, string> Strokes = new()
    {
        ['3']="M2 2Q15 -2 15 5Q15 10 7 10Q16 10 16 15Q16 23 2 19",
        ['4']="M12 1L1 14H17M12 1V21",
        ['7']="M1 2H17L6 21",
        ['A']="M1 21L9 1L17 21M4 14H14",
        ['C']="M16 4Q10 -1 5 3Q1 6 1 11Q1 17 5 20Q10 24 16 18",
        ['E']="M16 1H2V21H16M2 11H13",
        ['F']="M16 1H2V21M2 10H13",
        ['H']="M2 1V21M16 1V21M2 11H16",
        ['J']="M4 1H15V15Q15 22 9 22Q3 22 1 16",
        ['K']="M2 1V21M16 1L3 12M8 8L17 21",
        ['M']="M1 21V1L9 13L17 1V21",
        ['N']="M2 21V1L16 21V1",
        ['R']="M2 21V1H9Q16 1 16 6Q16 11 9 11H3M9 11L17 21",
        ['T']="M1 1H17M9 1V21",
        ['U']="M2 1V14Q2 21 9 21Q16 21 16 14V1",
        ['V']="M1 1L9 21L17 1",
        ['W']="M1 1L4 21L9 9L14 21L17 1",
        ['X']="M1 1L17 21M17 1L1 21",
        ['Y']="M1 1L9 12L17 1M9 12V21"
    };

    internal static string Draw(string answer)
    {
        var paths = new StringBuilder();
        for (var i = 0; i < answer.Length; i++)
        {
            var x = 6 + i * 23 + RandomNumberGenerator.GetInt32(3);
            var y = RandomNumberGenerator.GetInt32(7, 11);
            var tilt = RandomNumberGenerator.GetInt32(-6, 7);
            paths.Append(FormattableString.Invariant($"<path transform=\"translate({x} {y}) rotate({tilt} 9 11)\" d=\"{Strokes[answer[i]]}\"/>"));
        }
        return paths.ToString();
    }
}
