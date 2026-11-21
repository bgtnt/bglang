namespace BgLang.Core;

/// <summary>Shared bounded work allowance for graph discovery and conditional support checks.</summary>
internal sealed class GraphSearchBudget(int limit, CancellationToken cancellationToken = default)
{
    private int _remaining = Math.Max(0, limit);

    internal CancellationToken CancellationToken => cancellationToken;
    internal int Remaining => Volatile.Read(ref _remaining);

    internal void Take()
    {
        if (!TryTake()) throw new Exhausted();
    }

    internal sealed class Exhausted : Exception;

    internal bool TryTake(int amount = 1)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (amount <= 0)
            return true;
        while (true)
        {
            int current = Volatile.Read(ref _remaining);
            if (current < amount)
                return false;
            if (Interlocked.CompareExchange(ref _remaining, current - amount, current) == current)
                return true;
        }
    }
}
