using System.Text.Json.Nodes;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ProjectHub.Edge;

public sealed record CodexProbe(bool Available, string Detail);
public sealed record CodexDelivery(string Status, string? TurnId, string? Text, string Detail);
public sealed record CodexObservation(string Kind, int Expected, int Observed);

// The Desktop queue inherits the user's tools. Messages contain fixed local text and validated numbers only.
public sealed class CodexRuntime
{
    private readonly string executable;
    private readonly string workspace;
    private readonly Guid threadId;

    public CodexRuntime(string executablePath, string workspacePath, Guid threadId)
    {
        if (!Path.IsPathFullyQualified(executablePath) ||
            !Path.GetFileName(executablePath).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executablePath))
            throw new ArgumentException("请指定本机 Codex 可执行文件的绝对路径。");
        if (!Path.IsPathFullyQualified(workspacePath) || !Directory.Exists(workspacePath) || threadId == Guid.Empty)
            throw new ArgumentException("请指定已绑定的工作目录及测试会话 ID。");
        executable = executablePath;
        workspace = Normalize(workspacePath);
        this.threadId = threadId;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private sealed record Submission(string Message, Func<string, bool> AcceptsFinal);

    private static void RequireAttempt(Guid taskId, Guid attemptId)
    {
        if (taskId == Guid.Empty || attemptId == Guid.Empty) throw new ArgumentException("缺少任务或执行尝试 ID。");
    }

    private static Submission Challenge(Guid taskId, Guid attemptId)
    {
        RequireAttempt(taskId, attemptId);
        var left = 101 + taskId.ToByteArray()[0];
        var right = 27 + attemptId.ToByteArray()[0];
        var answer = (left + right).ToString(CultureInfo.InvariantCulture);
        return new(FormattableString.Invariant($"脉络已绑定会话的投递验证。任务 {taskId:D}，尝试 {attemptId:D}。请心算 {left} + {right}，仅回复十进制整数。请勿使用工具或操作文件。"),
            text => text == answer);
    }

    private static Submission Observation(Guid taskId, Guid attemptId, CodexObservation value)
    {
        RequireAttempt(taskId, attemptId);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Expected is < 0 or > 1_000_000 || value.Observed is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(value), "实测与预期记录数须为 0 至 1000000 的整数。");
        var metric = value.Kind switch
        {
            "readOnlyWriteCount" => "只读设备写入后新增的记录数",
            "duplicateCommitCount" => "一次逻辑提交最终产生的记录数",
            "retrievalResultCount" => "项目知识检索命中的记录数",
            _ => throw new ArgumentException("不支持此类观测。", nameof(value))
        };
        var message = FormattableString.Invariant($"脉络只读业务分析。任务 {taskId:D}，尝试 {attemptId:D}。观测指标为“{metric}”，预期 {value.Expected}，实际 {value.Observed}。只根据这些数据，说明该观测能证明什么、有什么局限，并给出一个下一步核对建议。无需调用任何工具。仅返回含“诊断”和“建议”两个字符串的 JSON 对象，不使用 Markdown。");
        return new(message, text =>
        {
            if (text.Length > 2000) return false;
            try
            {
                using var json = JsonDocument.Parse(text);
                var obj = json.RootElement;
                return obj.ValueKind == JsonValueKind.Object && obj.EnumerateObject().Count() == 2 &&
                    obj.TryGetProperty("诊断", out var finding) && finding.ValueKind == JsonValueKind.String && finding.GetString()?.Trim().Length >= 6 &&
                    obj.TryGetProperty("建议", out var advice) && advice.ValueKind == JsonValueKind.String && advice.GetString()?.Trim().Length >= 6;
            }
            catch (JsonException) { return false; }
        });
    }

    public async Task<CodexProbe> ProbeAsync(CancellationToken ct)
    {
        using var timeout = Timeout(ct);
        try
        {
            using var reader = new History(this);
            await VerifyBindingAsync(reader, timeout.Token);
            return new(true, "已核对会话 ID 与工作目录，尚未验证窗口状态。");
        }
        catch (Exception e) when (Recoverable(e, ct)) { return new(false, "当前账户无法核对绑定的 Codex 会话及工作目录。"); }
    }

    // The worker records dispatch intent durably before calling this once; an unknown outcome requires reconciliation.
    public Task<CodexDelivery> SendOnceAsync(Guid taskId, Guid attemptId, CancellationToken ct) =>
        SendAsync(Challenge(taskId, attemptId), ct);

    public Task<CodexDelivery> SendObservationAsync(Guid taskId, Guid attemptId, CodexObservation observation, CancellationToken ct) =>
        SendAsync(Observation(taskId, attemptId, observation), ct);

    internal Task<CodexDelivery> SendObservationAsync(Guid taskId, Guid attemptId, CodexObservation observation, CancellationToken ct,
        Func<IDisposable> holdRoute) => SendAsync(Observation(taskId, attemptId, observation), ct, holdRoute);

    private async Task<CodexDelivery> SendAsync(Submission submission, CancellationToken ct, Func<IDisposable>? holdRoute = null)
    {
        var probe = await ProbeAsync(ct);
        if (!probe.Available) return new("failed", null, null, probe.Detail);
        using var timeout = Timeout(ct);
        using var route = holdRoute?.Invoke();
        try
        {
            using var process = Start("queue", "--thread", threadId.ToString("D"), "--message", submission.Message);
            try
            {
                process.BeginOutputReadLine();
                process.StandardInput.Close();
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0
                    ? new("queued", null, null, "Codex 已接受排队请求，等待目标会话实际执行。")
                    : new("unknown", null, null, "排队结果未确认，请核对原尝试。");
            }
            finally { Stop(process); }
        }
        catch (Exception e) when (Recoverable(e, ct)) { return new("unknown", null, null, "投递结果未知，请核对原尝试。"); }
    }

    public Task<CodexDelivery> TryReadFinalAsync(Guid taskId, Guid attemptId, CancellationToken ct) =>
        FindAsync(Challenge(taskId, attemptId), ct);

    public Task<CodexDelivery> TryReadObservationAsync(Guid taskId, Guid attemptId, CodexObservation observation, CancellationToken ct) =>
        FindAsync(Observation(taskId, attemptId, observation), ct);

    private async Task<CodexDelivery> FindAsync(Submission submission, CancellationToken ct)
    {
        using var timeout = Timeout(ct);
        try
        {
            using var reader = new History(this);
            await VerifyBindingAsync(reader, timeout.Token);
            string? cursor = null;
            var cursors = new HashSet<string>();
            do
            {
                var page = await reader.CallAsync("thread/turns/list", new JsonObject
                {
                    ["threadId"] = threadId, ["cursor"] = cursor, ["limit"] = 4, ["sortDirection"] = "desc", ["itemsView"] = "full"
                }, timeout.Token);
                foreach (var turn in page.GetProperty("data").EnumerateArray())
                {
                    var result = MatchTurn(turn, submission);
                    if (result is not null) return result;
                }
                cursor = page.GetProperty("nextCursor").GetString();
            } while (cursor is not null && cursors.Add(cursor));
        }
        catch (Exception e) when (Recoverable(e, ct)) { return new("unknown", null, null, "读取会话历史暂未成功。"); }
        return new("unknown", null, null, "目标会话中暂未观察到此次投递。");
    }

    private static CodexDelivery? MatchTurn(JsonElement turn, Submission submission)
    {
        var items = turn.GetProperty("items").EnumerateArray().ToArray();
        var users = items.Where(i => i.GetProperty("type").GetString() == "userMessage").ToArray();
        if (users.Length != 1) return null;
        var input = users[0].GetProperty("content").EnumerateArray().ToArray();
        if (input.Length != 1 || input[0].GetProperty("type").GetString() != "text" ||
            input[0].GetProperty("text").GetString() != submission.Message) return null;

        var turnId = turn.GetProperty("id").GetString();
        if (items.Any(i => i.GetProperty("type").GetString() is not ("userMessage" or "agentMessage" or "reasoning")))
            return new("failed", turnId, null, "此次会话包含额外操作，未采用其结果。");
        var status = turn.GetProperty("status").GetString();
        if (status == "inProgress") return new("running", turnId, null, "目标会话正在处理。");
        if (status is "failed" or "interrupted") return new("failed", turnId, null, "Codex 报告此次执行失败或中断。");
        if (status != "completed") return new("unknown", turnId, null, "尚未确认执行终态。");
        var final = items.LastOrDefault(i => i.GetProperty("type").GetString() == "agentMessage" &&
            (!i.TryGetProperty("phase", out var phase) || phase.GetString() is null or "final_answer"));
        if (final.ValueKind == JsonValueKind.Undefined) return new("unknown", turnId, null, "等待最终答复写入历史。");
        var text = final.GetProperty("text").GetString()?.Trim();
        if (text is null || !submission.AcceptsFinal(text))
            return new("failed", turnId, null, "最终答复未通过本次请求的校验。");
        return new("completed", turnId, text, "已从目标会话读回并核对最终答复。");
    }

    private async Task VerifyBindingAsync(History reader, CancellationToken ct)
    {
        await reader.CallAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "nodicarta-edge", ["version"] = "1" } }, ct);
        await reader.NotifyInitializedAsync(ct);
        var meta = await reader.CallAsync("thread/read", new JsonObject { ["threadId"] = threadId, ["includeTurns"] = false }, ct);
        var thread = meta.GetProperty("thread");
        var cwd = thread.GetProperty("cwd").GetString();
        if (!Guid.TryParse(thread.GetProperty("id").GetString(), out var id) || id != threadId ||
            cwd is null || !Path.IsPathFullyQualified(cwd) || !StringComparer.OrdinalIgnoreCase.Equals(Normalize(cwd), workspace))
            throw new InvalidDataException("会话与工作目录不匹配。");
    }

    private Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workspace, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new IOException("无法启动 Codex。");
        process.BeginErrorReadLine();
        return process;
    }

    private static CancellationTokenSource Timeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return timeout;
    }

    private static bool Recoverable(Exception e, CancellationToken ct) =>
        !ct.IsCancellationRequested && e is IOException or InvalidDataException or System.ComponentModel.Win32Exception or
            JsonException or KeyNotFoundException or InvalidOperationException or OperationCanceledException or UnauthorizedAccessException;

    private static void Stop(Process child)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (child.HasExited) { }
    }

    private sealed class History(CodexRuntime owner) : IDisposable
    {
        private readonly Process child = owner.Start("app-server", "--stdio");
        private int request;

        public Task NotifyInitializedAsync(CancellationToken ct) =>
            WriteAsync("""{"method":"initialized"}""", ct);

        private async Task WriteAsync(string json, CancellationToken ct)
        {
            await child.StandardInput.WriteLineAsync(json.AsMemory(), ct);
            await child.StandardInput.FlushAsync(ct);
        }

        public async Task<JsonElement> CallAsync(string method, JsonObject parameters, CancellationToken ct)
        {
            var id = ++request;
            await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }.ToJsonString(), ct);
            while (await child.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                using var response = JsonDocument.Parse(line);
                var body = response.RootElement;
                if (!body.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number ||
                    !replyId.TryGetInt32(out var number) || number != id) continue;
                if (!body.TryGetProperty("result", out var result)) throw new InvalidDataException("Codex 拒绝此次协议请求。");
                return result.Clone();
            }
            throw new EndOfStreamException();
        }

        public void Dispose() { try { Stop(child); } finally { child.Dispose(); } }
    }
}
