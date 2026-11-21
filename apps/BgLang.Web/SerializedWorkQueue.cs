using System.Threading.Channels;
using System.Runtime.ExceptionServices;

namespace BgLang.Web;

/// <summary>Owns one execution lane even after an HTTP waiter times out.</summary>
internal sealed class SerializedWorkQueue : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel;
    private readonly TimeProvider _time;
    private readonly TimeSpan _queueWait;
    private readonly Task _worker;
    private readonly object _startGate = new();
    private int _closing;

    internal SerializedWorkQueue(int capacity, TimeSpan queueWait, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _queueWait = queueWait;
        _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ConsumeAsync);
    }

    internal Task Completion => _worker;

    internal Task<object> RunAsync(Func<object> operation, CancellationToken cancellation) =>
        RunAsyncCore(() => Task.FromResult(operation()), cancellation, waitForActiveResponse: false);

    /// <summary>Retains the caller and execution lane until a started response callback actually completes.</summary>
    internal Task<object> RunResponseAsync(Func<Task<object>> operation, CancellationToken cancellation) =>
        RunAsyncCore(operation, cancellation, waitForActiveResponse: true);

    private async Task<object> RunAsyncCore(Func<Task<object>> operation, CancellationToken cancellation,
        bool waitForActiveResponse)
    {
        cancellation.ThrowIfCancellationRequested();
        var item = new WorkItem(operation, cancellation, _time.GetTimestamp());
        if (Volatile.Read(ref _closing) != 0 || !_channel.Writer.TryWrite(item))
            throw Unavailable("queue-full", "Опашката е запълнена или услугата спира. Опитай отново.");
        try { await item.Started.Task.WaitAsync(_queueWait, _time, cancellation); }
        catch (TimeoutException)
        {
            // Starting and expiring race through one state transition; expired work never runs later.
            item.Skip(Unavailable("queue-expired", "Заявката не започна навреме. Опитай отново."));
        }
        catch (OperationCanceledException)
        {
            bool skipped = item.Skip(Unavailable("request-cancelled", "HTTP заявката е прекратена."));
            if (!waitForActiveResponse || skipped) throw;
            // A committed Start may own the HTTP context; observe completion instead of abandoning it.
        }
        Outcome outcome = waitForActiveResponse
            ? await item.Completion.Task
            : await item.Completion.Task.WaitAsync(cancellation);
        if (outcome.Error is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        return outcome.Value!;
    }

    internal void Close()
    {
        lock (_startGate)
        {
            Interlocked.Exchange(ref _closing, 1);
            _channel.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        // Active CPU work or response transfer still owns the session until the worker actually completes.
        await _worker;
    }

    private async Task ConsumeAsync()
    {
        await foreach (WorkItem item in _channel.Reader.ReadAllAsync())
        {
            if (!TryStart(item))
            {
                item.Skip(Unavailable("queue-expired", "Заявката не започна навреме. Опитай отново."));
                continue;
            }
            try { item.Completion.TrySetResult(new(await item.Operation(), null)); }
            catch (Exception error) { item.Completion.TrySetResult(new(null, error)); }
        }
    }

    private bool TryStart(WorkItem item)
    {
        lock (_startGate)
        {
            if (Volatile.Read(ref _closing) != 0 || item.Cancellation.IsCancellationRequested ||
                _time.GetElapsedTime(item.Enqueued) >= _queueWait) return false;
            return item.Start();
        }
    }

    private static ApiRequestException Unavailable(string code, string message) => new(503, code, message);
    private sealed record Outcome(object? Value, Exception? Error);
    private sealed record WorkItem(Func<Task<object>> Operation, CancellationToken Cancellation, long Enqueued)
    {
        private int _state;
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<Outcome> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Start()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return false;
            Started.TrySetResult(true);
            return true;
        }

        internal bool Skip(Exception error)
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) != 0) return false;
            Completion.TrySetResult(new(null, error));
            Started.TrySetResult(false);
            return true;
        }
    }
}
