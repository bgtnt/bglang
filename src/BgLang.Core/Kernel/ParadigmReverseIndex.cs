namespace BgLang.Core;

/// <summary>Conservative selection by the existing final literal guard; all other programs remain eligible.</summary>
internal sealed class ParadigmReverseIndex
{
    private readonly Dictionary<string, int[]> _byEnding;
    private readonly int[] _fallback;
    private readonly int _maximumEndingLength;

    internal ParadigmReverseIndex(IEnumerable<(int Edge, string? Ending)> rows)
    {
        var groups = rows.ToLookup(row => row.Ending, StringComparer.Ordinal);
        _fallback = groups[null].Select(row => row.Edge).ToArray();
        _byEnding = groups.Where(group => group.Key is not null).ToDictionary(
            group => group.Key!, group => group.Select(row => row.Edge).ToArray(), StringComparer.Ordinal);
        _maximumEndingLength = _byEnding.Keys.Select(ending => ending.Length).DefaultIfEmpty().Max();
    }

    internal IEnumerable<int> Match(string surface)
    {
        foreach (int edge in _fallback) yield return edge;
        // Preserve CanStartReverse's strict length comparison, including an empty literal.
        int maximum = Math.Min(_maximumEndingLength, surface.Length - 1);
        for (int length = 0; length <= maximum; length++)
            if (_byEnding.TryGetValue(surface[^length..], out int[]? edges))
                foreach (int edge in edges) yield return edge;
    }

}
