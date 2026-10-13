using static BgLang.Core.WordFormationGraph;
using Transform = BgLang.Core.GraphSurfaceOperations.Transform;

namespace BgLang.Core;

/// <summary>Necessary tail eligibility only; full branch guards and proof remain authoritative.</summary>
internal sealed class ConditionalSeedIndex
{
    private readonly ConditionalProgram[] _programs;
    private readonly Dictionary<char, int[]> _byFinal;
    private readonly int[] _wildcards;

    internal ConditionalSeedIndex(ConditionalProgram[] programs, Transform[] transforms)
    {
        _programs = programs;
        Dictionary<char, List<int>> buckets = [];
        List<int> wildcards = [];
        for (int ordinal = 0; ordinal < programs.Length; ordinal++)
        {
            HashSet<char> keys = [];
            bool wildcard = false;
            foreach (ConditionalBranch branch in programs[ordinal].Branches)
            {
                Transform transform = transforms[branch.TransformIndex];
                string[] tails = transform.Kind switch
                {
                    TransformKind.TailReplace or TransformKind.TailDelete or TransformKind.LastOccurrenceReplace => [transform.From],
                    TransformKind.VariantSetNormalize => transform.Variants,
                    _ => [string.Empty]
                };
                if (tails.Length == 0)
                    wildcard = true;
                foreach (string tail in tails)
                {
                    if (tail.Length == 0)
                    {
                        wildcard = true;
                        break;
                    }
                    keys.Add(tail[^1]);
                }
                if (wildcard)
                    break;
            }
            if (wildcard)
            {
                // Store unrestricted programs once, rather than in every character bucket.
                wildcards.Add(ordinal);
                continue;
            }
            foreach (char key in keys)
            {
                if (!buckets.TryGetValue(key, out List<int>? bucket))
                    buckets[key] = bucket = [];
                bucket.Add(ordinal);
            }
        }
        _byFinal = buckets.ToDictionary(item => item.Key, item => item.Value.ToArray());
        _wildcards = wildcards.ToArray();
    }

    internal IEnumerable<ConditionalProgram> Match(string surface, GraphSearchBudget? budget)
    {
        if (_programs.Length == 0)
            yield break;
        budget?.Take(); // Actual constant dispatch and character lookup.
        int[] specific = surface.Length > 0 && _byFinal.TryGetValue(surface[^1], out int[]? bucket) ? bucket : [];
        int selected = 0, wildcard = 0;
        while (selected < specific.Length || wildcard < _wildcards.Length)
        {
            budget?.Take(); // Meter every selected ordinal, including a later seeded-state skip.
            int ordinal = wildcard == _wildcards.Length ||
                selected < specific.Length && specific[selected] < _wildcards[wildcard]
                ? specific[selected++] : _wildcards[wildcard++];
            yield return _programs[ordinal];
        }
    }
}
