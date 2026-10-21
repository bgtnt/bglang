namespace BgLang.Web;

/// <summary>Fixed per-client windows with a hard ceiling on retained IP entries.</summary>
internal sealed class ClientRateLimit(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, Window> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    internal (int Status, int RetryAfter) TryAcquire(string client)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_clients.TryGetValue(client, out Window? window) || now >= window.Reset)
            {
                if (window is null && _clients.Count >= HttpContract.ClientCapacity)
                {
                    foreach (string expired in _clients.Where(pair => now >= pair.Value.Reset)
                                 .Select(pair => pair.Key).ToArray())
                        _clients.Remove(expired);
                    if (_clients.Count >= HttpContract.ClientCapacity) return (503, 1);
                }
                window = new(now.AddMinutes(1));
                _clients[client] = window;
            }
            if (window.Count >= HttpContract.RequestsPerMinute)
                return (429, Math.Max(1, (int)Math.Ceiling((window.Reset - now).TotalSeconds)));
            window.Count++;
            return (200, 0);
        }
    }

    private sealed class Window(DateTimeOffset reset)
    {
        internal DateTimeOffset Reset { get; } = reset;
        internal int Count { get; set; }
    }
}
