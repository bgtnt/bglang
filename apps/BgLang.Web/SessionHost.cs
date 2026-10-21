using BgLang.Api;
using BgLang.Contracts;

namespace BgLang.Web;

internal sealed class SessionHost : IHostedService, IAsyncDisposable
{
    private readonly Func<BulgarianLanguageSession> _open;
    private readonly string _expectedHash;
    private BulgarianLanguageSession? _session;
    private SerializedWorkQueue? _queue;
    private volatile bool _ready;

    internal SessionHost(string manifest, string expectedHash, Func<BulgarianLanguageSession>? open = null)
    {
        if (string.IsNullOrWhiteSpace(manifest) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
            throw new ArgumentException("Configure BgLang:ManifestPath and a 64-digit BgLang:ManifestSha256.");
        _expectedHash = expectedHash;
        _open = open ?? (() => BulgarianLanguageSession.Open(Path.GetFullPath(manifest)));
    }

    internal bool Ready => _ready;
    internal PackageProvenance Provenance { get; private set; } = null!;
    internal RuntimeVersions Versions { get; } = HttpContract.Versions();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_session is not null) throw new InvalidOperationException("Session host has already started.");
        BulgarianLanguageSession session = _open();
        if (!string.Equals(session.Provenance.ManifestSha256, _expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            session.Dispose();
            throw new InvalidOperationException("The validated package does not match BgLang:ManifestSha256.");
        }
        _session = session;
        Provenance = session.Provenance;
        _queue = new(HttpContract.QueueCapacity, HttpContract.QueueWait);
        _ready = true;
        return Task.CompletedTask;
    }

    internal Task<object> ExecuteAsync(string operation, WebInput input, CancellationToken cancellation)
    {
        if (!_ready || _queue is null)
            throw new ApiRequestException(503, "not-ready", "Езиковият пакет още не е готов или услугата спира.");
        return _queue.RunAsync(() => ApiOperations.Invoke(operation, input, _session!, Provenance, Versions, cancellation), cancellation);
    }

    internal Task ExecuteResponseAsync(string operation, WebInput input,
        Func<ApiEnvelope, CancellationToken, Task> respond, CancellationToken cancellation)
    {
        if (!_ready || _queue is null)
            throw new ApiRequestException(503, "not-ready", "Езиковият пакет още не е готов или услугата спира.");
        return _queue.RunResponseAsync(async () =>
        {
            cancellation.ThrowIfCancellationRequested();
            ApiEnvelope result = ApiOperations.Invoke(operation, input, _session!, Provenance, Versions, cancellation);
            // The execution lane remains owned until linguistic cancellation or response transfer completes.
            cancellation.ThrowIfCancellationRequested();
            await respond(result, cancellation);
            return result;
        }, cancellation);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ready = false;
        if (_queue is null) return;
        _queue.Close();
        await _queue.Completion.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _ready = false;
        if (_queue is not null) await _queue.DisposeAsync();
        Interlocked.Exchange(ref _session, null)?.Dispose();
    }
}
