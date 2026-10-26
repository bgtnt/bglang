using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Immutable block minima for bounded selection over a contiguous lexical payload range.</summary>
internal sealed class BlockRankedRangeSelector
{
    private const int BlockSize = 32;
    private readonly WordReading[] _readings;
    private readonly int[] _tree;
    private readonly int _leafCount;

    internal BlockRankedRangeSelector(WordReading[] readings)
    {
        _readings = readings;
        int blocks = (readings.Length - 1) / BlockSize + 1;
        _leafCount = 1;
        while (_leafCount < blocks) _leafCount = checked(_leafCount * 2);
        _tree = new int[checked(2 * _leafCount)];
        Array.Fill(_tree, -1);
        for (int index = 0; index < readings.Length; index++)
        {
            int leaf = _leafCount + index / BlockSize;
            _tree[leaf] = Better(_tree[leaf], index);
        }
        for (int index = _leafCount - 1; index > 0; index--)
            _tree[index] = Better(_tree[2 * index], _tree[2 * index + 1]);
    }

    internal bool TrySelect(int start, int end, int limit, out IReadOnlyList<WordReading> result)
    {
        result = [];
        if (limit <= 0 || limit >= end - start) return false;
        PriorityQueue<(int Start, int End, int Winner), WordReading> queue = new(WordReadingOrder.Comparer);
        void Add(int left, int right)
        {
            if (left >= right) return;
            int winner = Best(left, right);
            queue.Enqueue((left, right, winner), _readings[winner]);
        }
        Add(start, end);
        WordReading[] selected = new WordReading[limit];
        WordReading? previous = null;
        for (int index = 0; index <= limit; index++)
        {
            var interval = queue.Dequeue();
            WordReading current = _readings[interval.Winner];
            // Current scan insertion is not stable for comparer-equal raw records (e.g. signed zero).
            // Inspect K+1, including the cutoff tie, then let the caller restart the exact old scanner.
            if (previous is not null && WordReadingOrder.Comparer.Compare(previous, current) == 0)
                return false;
            if (index == limit) break;
            selected[index] = current;
            previous = current;
            Add(interval.Start, interval.Winner);
            Add(interval.Winner + 1, interval.End);
        }
        result = selected;
        return true;
    }

    // Enumerates retained payload positions in dictionary order. Equal readings keep
    // original payload order; unlike TrySelect this path never falls back or drops ties.
    internal IEnumerable<int> EnumeratePositions(int start, int end,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (start < 0 || end < start || end > _readings.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        PriorityQueue<(int Start, int End, int Winner), int> queue = new(
            Comparer<int>.Create((left, right) =>
            {
                int order = WordReadingOrder.Comparer.Compare(_readings[left], _readings[right]);
                return order != 0 ? order : left.CompareTo(right);
            }));
        void Add(int left, int right)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (left >= right) return;
            int winner = Best(left, right, cancellationToken);
            queue.Enqueue((left, right, winner), winner);
        }
        Add(start, end);
        while (queue.TryDequeue(out var interval, out _))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Add(interval.Start, interval.Winner);
            Add(interval.Winner + 1, interval.End);
            yield return interval.Winner;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private int Best(int start, int end, CancellationToken cancellationToken = default)
    {
        int firstFullBlock = start / BlockSize + (start % BlockSize == 0 ? 0 : 1);
        int lastFullBlock = end / BlockSize;
        int leftEnd = Math.Min(end, firstFullBlock * BlockSize);
        int best = -1;
        for (int index = start; index < leftEnd; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            best = Better(best, index);
        }
        for (int left = firstFullBlock + _leafCount, right = lastFullBlock + _leafCount; left < right; left /= 2, right /= 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((left & 1) != 0) best = Better(best, _tree[left++]);
            if ((right & 1) != 0) best = Better(best, _tree[--right]);
        }
        for (int index = Math.Max(leftEnd, lastFullBlock * BlockSize); index < end; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            best = Better(best, index);
        }
        return best;
    }

    private int Better(int left, int right)
    {
        if (left < 0) return right;
        if (right < 0) return left;
        int order = WordReadingOrder.Comparer.Compare(_readings[left], _readings[right]);
        return order < 0 || order == 0 && left < right ? left : right;
    }
}
