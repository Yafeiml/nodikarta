using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace ProjectHub.Core;

public sealed partial class CoreStore
{
    private const int BriefItems = 5;
    private static readonly string[][] SearchAliases =
    [
        ["海报", "宣传画", "招贴", "poster"],
        ["标志", "标识", "徽标", "logo"],
        ["规范", "准则", "标准", "guideline"],
        ["手册", "指南", "guide"],
        ["故障", "排障", "报错", "错误", "error"],
        ["安装", "装机", "install"],
        ["附件", "附档", "attachment"],
        ["权限", "授权", "permission"]
    ];

    private static readonly Regex SearchNoise = new(
        "请问|告诉我|帮我|我想|继续|上一次|上次|以前|之前|当时|现在|那[张份个次版]|这[张份个次版]|哪[张份个次版]|为什么|怎么|什么|是否|用了|一下",
        RegexOptions.CultureInvariant);

    private static readonly Regex SearchWord = new(
        @"[\u3400-\u9fff]+|[a-z0-9]+(?:[._+#-][a-z0-9]+)*",
        RegexOptions.CultureInvariant);

    // Expand equivalent terms as one vote, so a bilingual title gets no extra weight for the same concept.
    private static Dictionary<string, int> KnowledgeTerms(string query)
    {
        var remaining = SearchNoise.Replace(query.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), " ");
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var aliases in SearchAliases)
        {
            var found = false;
            foreach (var alias in aliases)
            {
                // Preserve identifiers such as "install-tools" as a whole.
                var pattern = char.IsAscii(alias[0]) ? @"(?<![a-z0-9_.+#-])" + alias + @"(?![a-z0-9_.+#-])" : alias;
                if (!Regex.IsMatch(remaining, pattern)) continue;
                remaining = Regex.Replace(remaining, pattern, " ");
                found = true;
            }
            if (!found) continue;
            var concept = terms.Count;
            foreach (var alias in aliases) terms.Add(alias, concept);
        }
        foreach (Match match in SearchWord.Matches(remaining))
        {
            var word = match.Value;
            if (word[0] is >= '\u3400' and <= '\u9fff')
            {
                for (var i = 0; i + 1 < word.Length && terms.Count < 96; i++)
                    terms.TryAdd(word.Substring(i, 2), terms.Count);
            }
            else if (word.Length > 1 && terms.Count < 96)
                terms.TryAdd(word, terms.Count);
        }
        return terms;
    }

    private async Task<object> FindKnowledgeAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        Guid projectId, string query, bool brief)
    {
        // Read only metadata of the pinned asset version. New filenames or tags must not rewrite history.
        const string rank = """
            SELECT k.*,
                CASE WHEN $2='' THEN 0 WHEN strpos(fields.title,$2)>0 THEN 3
                     WHEN strpos(concat_ws(' ',fields.title,fields.description),$2)>0 THEN 2
                     WHEN strpos(fields.files,$2)>0 THEN 1 ELSE 0 END AS literal,
                score.hits, score.weight
            FROM latest k
            CROSS JOIN LATERAL (
                SELECT lower(string_agg(concat_ws(' ',a.asset_id,a.name,a.file_name,a.purpose,array_to_string(a.tags,' ')),' ')) AS labels
                FROM projecthub.knowledge_asset_refs r
                JOIN projecthub.asset_versions a ON (a.project_id,a.asset_id,a.version)=(r.project_id,r.asset_id,r.asset_version)
                WHERE r.project_id=k.project_id AND r.item_id=k.item_id AND r.knowledge_version=k.version AND $2<>''
            ) assets
            CROSS JOIN LATERAL (SELECT lower(k.title) AS title,
                lower(concat_ws(' ',k.body,k.evidence,k.validity->>'scope',k.validity->>'subject')) AS description,
                coalesce(assets.labels,'') AS files) fields
            CROSS JOIN LATERAL (
                SELECT count(*) FILTER (WHERE best>0) AS hits, coalesce(sum(best),0) AS weight FROM (
                    SELECT max(CASE WHEN strpos(fields.title,t.word)>0 THEN 5 WHEN strpos(fields.files,t.word)>0 THEN 3
                                    WHEN strpos(fields.description,t.word)>0 THEN 1 ELSE 0 END) AS best
                    FROM unnest($3::text[], $4::integer[]) AS t(word,concept) GROUP BY t.concept
                ) matched
            ) score
            """;
        const string order = "literal DESC,hits DESC,weight DESC,created_at DESC,item_id";
        var sql = $$"""
            WITH latest AS (
                SELECT DISTINCT ON (item_id) * FROM projecthub.knowledge_versions WHERE project_id=$1
                ORDER BY item_id,version DESC
            ), ranked AS ({{rank}}), matches AS (
                SELECT *,row_number() OVER (ORDER BY {{order}}) AS position FROM ranked WHERE $2='' OR literal>0 OR hits>0
            ), selected AS (
                SELECT * FROM matches WHERE literal>0 OR position<=30 ORDER BY position
                {{(brief ? $"LIMIT {BriefItems + 1}" : "")}}
            )
            SELECT CASE WHEN $5 THEN jsonb_build_object('itemId',s.item_id,'commitId',s.commit_id,'version',s.version,
                'title',s.title,'body',left(s.body,240),'evidence',left(s.evidence,160),'createdAt',s.created_at,
                'assetRefs',refs.files,'versionUseChecks',refs.use_checks,'validity',k.record->'validity',
                'truncated',length(s.body)>240 OR length(s.evidence)>160 OR refs.shortened)
                ELSE k.record || jsonb_build_object('linkedAssets',refs.files,'versionUseChecks',refs.use_checks) END,
                NOT $5 AND EXISTS (SELECT 1 FROM matches WHERE literal=0 AND position>30)
            FROM selected s
            JOIN projecthub.knowledge_records k ON k.commit_id=s.commit_id
            CROSS JOIN (VALUES($5::boolean)) shape(brief)
            {{ReadLinkedKnowledgeAssets}}
            ORDER BY s.literal DESC,s.hits DESC,s.weight DESC,s.created_at DESC,s.item_id
            """;
        var terms = KnowledgeTerms(query);
        await using var cmd = Query(conn, tx, sql, projectId, query.ToLowerInvariant(), terms.Keys.ToArray(), terms.Values.ToArray(), brief);
        var items = new List<JsonElement>();
        var more = false;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (brief && items.Count == BriefItems)
            {
                CaptureMatches(items.Count);
                return new { view = "brief", items, hasMore = true };
            }
            items.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
            more = reader.GetBoolean(1);
        }
        CaptureMatches(items.Count);
        return brief ? new { view = "brief", items, hasMore = false }
            : new { items, nextCursor = (string?)null, hasMore = more };
    }
}
