using BgLang.Contracts;

namespace BgLang.Core;

// One package-bound cache per text request. The default broad result may feed the diagnostic prefix.
internal sealed class SpellingCandidateCache(IRuntimeLexicon lexicon)
{
    private readonly Dictionary<(string Query, int Distance, int Limit), IReadOnlyList<FuzzyMatch>> _matches = [];

    internal IReadOnlyList<FuzzyMatch> FindSimilarNormalized(string query, int distance, int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (query, distance, limit);
        if (!_matches.TryGetValue(key, out IReadOnlyList<FuzzyMatch>? matches))
        {
            if (distance == 1 && limit == 2 &&
                _matches.TryGetValue((query, 2, 10), out IReadOnlyList<FuzzyMatch>? broader))
            {
                // Distance sorts first; keep the first two distance-at-most-one form groups.
                matches = Array.AsReadOnly(broader.TakeWhile(match => match.Distance <= 1).Take(2).ToArray());
            }
            else
            {
                IReadOnlyList<FuzzyMatch> found = lexicon.FindSimilarNormalized(query, distance, limit);
                cancellationToken.ThrowIfCancellationRequested();
                FuzzyMatch[] snapshot = new FuzzyMatch[found.Count];
                for (int i = 0; i < snapshot.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FuzzyMatch match = found[i];
                    snapshot[i] = new(match.Form, match.Distance, Array.AsReadOnly(match.Readings.ToArray()));
                }
                matches = Array.AsReadOnly(snapshot);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _matches.Add(key, matches);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return matches;
    }
}
