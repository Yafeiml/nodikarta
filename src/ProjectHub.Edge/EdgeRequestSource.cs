using System.Globalization;

namespace ProjectHub.Edge;

internal sealed record EdgeRequestSource(string? Entry, string? Client = null, string? Version = null,
    DateTimeOffset? OriginAt = null, bool Replay = false)
{
    internal static readonly EdgeRequestSource Cli = new("cli");
    internal static readonly EdgeRequestSource Gui = new("gui");
    internal static readonly EdgeRequestSource Background = new("background");
    private static readonly AsyncLocal<EdgeRequestSource?> Ambient = new();
    internal static EdgeRequestSource? Current => Ambient.Value;

    internal static EdgeRequestSource ForMcp(string? name, string? version) =>
        new("mcp", HeaderText(name, 64), HeaderText(version, 48));

    // The same Core connection can service concurrent MCP calls and a separate outbox worker.
    internal IDisposable Use()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new Scope(previous);
    }

    private sealed class Scope(EdgeRequestSource? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    private static string? HeaderText(string? value, int limit)
    {
        if (value is null || value.Length > limit) return null;
        value = value.Trim(' ');
        return value.Length > 0 && value.All(c => c is >= ' ' and <= '~')
            ? value : null;
    }

    internal void ApplyTo(HttpRequestMessage request)
    {
        if (Entry is "mcp" or "gui" or "cli" or "background")
            request.Headers.Add("X-Nodicarta-Entry", Entry);
        if (HeaderText(Client, 64) is { } name) request.Headers.Add("X-Nodicarta-Client", name);
        if (HeaderText(Version, 48) is { } version) request.Headers.Add("X-Nodicarta-Client-Version", version);
        if (OriginAt is { } time)
            request.Headers.Add("X-Nodicarta-Origin-At", time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        if (Replay) request.Headers.Add("X-Nodicarta-Replay", "1");
    }
}
