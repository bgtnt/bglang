using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Bounded longest-first exact DAWG segmentation adapted from FindOrSplit.</summary>
internal static class ExactWordSegmenter
{
    internal const string SourceName = "exact-dawg-segmentation.v1";
    internal const string FusedSourceName = "reviewed-word-formation-fused-segmentation.v1";
    private const int MaximumInputLength = 256;
    private const int MaximumCandidates = 64;
    private const int MaximumSegments = 16;
    private static readonly string[] NonLexicalFamilies =
        ["unknown", "неизвестно", "prefix", "suffix", "affix", "префикс", "представка", "суфикс"];

    internal static IReadOnlyList<WordSegmentationCandidate> Segment(
        IRuntimeLexicon index,
        string text,
        int limit,
        int maxSegments,
        Func<string, WordDerivation?> tryMorphology)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tryMorphology);
        ValidateBound(limit, MaximumCandidates, nameof(limit));
        ValidateBound(maxSegments, MaximumSegments, nameof(maxSegments));

        string normalized = BulgarianTextNormalizer.NormalizeKey(text);
        if (normalized.Length == 0)
            return [];
        if (normalized.Length > MaximumInputLength)
        {
            throw new ArgumentException(
                $"Normalized input length must not exceed {MaximumInputLength} characters.",
                nameof(text));
        }

        List<WordSegment[]> paths = [];
        List<WordSegment> current = [];
        Dictionary<(int Start, int End), IReadOnlyList<WordReading>> exactMatches = [];
        HashSet<(int Start, int SegmentCount)> deadEnds = [];
        Search(index, normalized, 0, maxSegments, limit, current, paths, exactMatches, deadEnds);

        if (paths.Count > 0)
            return CreateCandidates(text, normalized, paths, SourceName);
        if (maxSegments < 2)
            return [];

        var fused = new List<WordSegment[]>();
        var exactByText = new Dictionary<string, IReadOnlyList<WordReading>>(StringComparer.Ordinal);
        for (int split = 1; split < normalized.Length; split++)
        {
            string left = normalized[..split];
            string right = normalized[split..];
            TryAddFused(index, left, right, graphOnLeft: true, tryMorphology, exactByText, fused);
            TryAddFused(index, left, right, graphOnLeft: false, tryMorphology, exactByText, fused);
        }

        WordSegment[][] ordered = fused
            .DistinctBy(SegmentPathIdentity)
            .OrderByDescending(segments => segments[0].Text.Length)
            .ThenBy(SegmentPathIdentity, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        return CreateCandidates(text, normalized, ordered, FusedSourceName);
    }

    private static void TryAddFused(
        IRuntimeLexicon index,
        string left,
        string right,
        bool graphOnLeft,
        Func<string, WordDerivation?> tryMorphology,
        Dictionary<string, IReadOnlyList<WordReading>> exactByText,
        List<WordSegment[]> candidates)
    {
        string graphText = graphOnLeft ? left : right;
        string exactText = graphOnLeft ? right : left;
        IReadOnlyList<WordReading> graphReadings = LookupComplete(index, graphText, exactByText);
        IReadOnlyList<WordReading> exactReadings = LookupComplete(index, exactText, exactByText);
        if (graphReadings.Count != 0 || exactText.Length < 2 || !HasLexicalReading(exactReadings))
            return;

        WordDerivation? evidence = tryMorphology(graphText);
        if (evidence is null)
            return;
        var graphSegment = new WordSegment(graphText, []) { MorphologyEvidence = evidence };
        var exactSegment = new WordSegment(exactText, exactReadings);
        candidates.Add(graphOnLeft ? [graphSegment, exactSegment] : [exactSegment, graphSegment]);
    }

    private static IReadOnlyList<WordReading> LookupComplete(
        IRuntimeLexicon index,
        string text,
        Dictionary<string, IReadOnlyList<WordReading>> cache)
    {
        if (!cache.TryGetValue(text, out IReadOnlyList<WordReading>? readings))
        {
            readings = index.LookupExactNormalized(text, int.MaxValue);
            cache.Add(text, readings);
        }
        return readings;
    }

    private static bool HasLexicalReading(IReadOnlyList<WordReading> readings) => readings.Any(reading =>
    {
        string tag = reading.PartOfSpeech?.Trim() ?? string.Empty;
        int colon = tag.IndexOf(':');
        string family = (colon < 0 ? tag : tag[..colon]).Trim();
        return family.Length > 0 &&
            !NonLexicalFamilies.Contains(family, StringComparer.OrdinalIgnoreCase);
    });

    private static string SegmentPathIdentity(WordSegment[] segments) => string.Join(
        '\u001f',
        segments.Select(segment => segment.MorphologyEvidence is not { } evidence
            ? segment.Text
            : string.Join('\u001e', segment.Text, evidence.Base, evidence.FromState, evidence.ToState,
                string.Join('>', evidence.EdgeIds), string.Join('>', evidence.RuleIds))));

    private static WordSegmentationCandidate[] CreateCandidates(
        string original,
        string normalized,
        IEnumerable<WordSegment[]> paths,
        string source) => paths.Select((segments, rank) => new WordSegmentationCandidate(
            original,
            normalized,
            string.Join(' ', segments.Select(segment => segment.Text)),
            segments,
            rank,
            IsSyntaxRanked: false,
            source)).ToArray();

    private static bool Search(
        IRuntimeLexicon index,
        string normalized,
        int start,
        int maxSegments,
        int limit,
        List<WordSegment> current,
        List<WordSegment[]> paths,
        Dictionary<(int Start, int End), IReadOnlyList<WordReading>> exactMatches,
        HashSet<(int Start, int SegmentCount)> deadEnds)
    {
        if (start == normalized.Length)
        {
            if (current.Count < 2)
                return false;

            paths.Add(current.ToArray());
            return true;
        }

        if (current.Count >= maxSegments || paths.Count >= limit)
            return false;

        var state = (Start: start, SegmentCount: current.Count);
        if (deadEnds.Contains(state))
            return false;

        bool found = false;
        for (int end = normalized.Length; end > start; end--)
        {
            var range = (Start: start, End: end);
            if (!exactMatches.TryGetValue(range, out IReadOnlyList<WordReading>? readings))
            {
                readings = index.LookupExactNormalized(normalized[start..end], int.MaxValue);
                exactMatches.Add(range, readings);
            }
            if (readings.Count == 0)
                continue;

            current.Add(new WordSegment(normalized[start..end], readings));
            found |= Search(
                index,
                normalized,
                end,
                maxSegments,
                limit,
                current,
                paths,
                exactMatches,
                deadEnds);
            current.RemoveAt(current.Count - 1);

            if (paths.Count >= limit)
                break;
        }

        if (!found)
            deadEnds.Add(state);
        return found;
    }

    private static void ValidateBound(int value, int maximum, string parameterName)
    {
        if (value is < 1 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"Value must be between 1 and {maximum}.");
        }
    }
}
