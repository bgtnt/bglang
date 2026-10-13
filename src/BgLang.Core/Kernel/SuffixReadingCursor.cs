using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Bounded forward walk over one suffix payload range.</summary>
internal sealed class SuffixReadingCursor
{
    private readonly IReadOnlyList<WordReading> _readings;
    private readonly IReadOnlySet<string>? _tags;
    private readonly bool _sourceComplete;
    private int _position;

    internal SuffixReadingCursor(
        IReadOnlyList<WordReading> readings,
        IReadOnlySet<string>? tags = null,
        bool sourceComplete = true)
    {
        _readings = readings;
        _tags = tags;
        _sourceComplete = sourceComplete;
    }

    internal SuffixReadingCursor(WordReading[] payloads, int start, int end, IReadOnlySet<string>? tags = null,
        int[]? positions = null)
    {
        _readings = new PayloadRange(payloads, start, end, positions);
        _tags = tags;
        _sourceComplete = true;
    }

    internal bool Complete { get; private set; }
    internal bool HasRows => _readings.Count > 0;

    internal bool Exhausted { get; private set; }

    internal bool TryRead(out WordReading reading, GraphSearchBudget? budget = null)
    {
        while (_position < _readings.Count)
        {
            if (budget is not null && !budget.TryTake())
            {
                Stop();
                reading = default!;
                return false;
            }
            WordReading next = _readings[_position++];
            Complete = _sourceComplete && _position == _readings.Count;
            if (_tags is null || next.PartOfSpeech is string tag && _tags.Contains(tag))
            {
                reading = next;
                return true;
            }
        }
        Complete = _sourceComplete;
        Exhausted = true;
        reading = default!;
        return false;
    }

    internal void Stop()
    {
        if (!Complete)
            Exhausted = true;
    }

    private sealed class PayloadRange(WordReading[] payloads, int start, int end, int[]? positions) : IReadOnlyList<WordReading>
    {
        public int Count => end - start;
        public WordReading this[int index] => payloads[positions is null ? start + index : positions[start + index]];
        public IEnumerator<WordReading> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
