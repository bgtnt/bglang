using BgLang.Contracts;
using BgLang.Core;

namespace BgLang.Api;

// Request-local projection over the existing complete-exact memo. No lookup, tokenization or graph replay.
internal static class TextReadingInventorySnapshot
{
    internal static TextReadingInventory Create(string text, IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyDictionary<string, (WordAnalysis Display, BulgarianStressLookup.ExactInventory Exact)> memo,
        CancellationToken cancellationToken)
    {
        List<TextReadingSet> sets = [];
        List<TextReadingOccurrence> occurrences = [];
        Dictionary<string, int> setIndices = new(StringComparer.Ordinal);
        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = tokens[tokenIndex];
            if (token.Kind != TokenKind.Word) continue;
            WordAnalysis analysis = token.Analysis ??
                throw new InvalidOperationException("A Word occurrence has no acquired analysis.");
            if (token.Start < 0 || token.Length < 0 || token.Start > text.Length - token.Length ||
                !text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text.AsSpan()))
                throw new InvalidOperationException("A Word occurrence does not match its raw UTF-16 span.");
            // Abbreviations may have raw text different from the actual AnalyzeCached lookup text.
            if (!memo.TryGetValue(analysis.Word, out var cached) ||
                !ReferenceEquals(cached.Display, analysis) || cached.Exact is null)
                throw new InvalidOperationException("Complete exact acquisition is unavailable for a Word occurrence.");
            if (!setIndices.TryGetValue(analysis.Word, out int setIndex))
            {
                var exact = cached.Exact;
                TextReadingSet set = new(analysis.Word, exact.CanonicalKey,
                    Capture(exact.CanonicalReadings, cancellationToken), exact.SupportKey,
                    exact.SupportReadings is null ? null : Capture(exact.SupportReadings, cancellationToken),
                    analysis.Readings.Count);
                if (!analysis.Readings.SequenceEqual(set.ResolvedReadings.Take(set.DisplayedReadingCount).Select(row => row.Raw)))
                    throw new InvalidOperationException("Displayed exact rows are not the positional complete-inventory prefix.");
                setIndex = sets.Count;
                sets.Add(set);
                setIndices.Add(analysis.Word, setIndex);
            }
            occurrences.Add(new(tokenIndex, token.Text, token.Start, token.Length, setIndex));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(sets, occurrences);
    }

    private static List<TextReadingSnapshot> Capture(IReadOnlyList<WordReading> readings,
        CancellationToken cancellationToken)
    {
        List<TextReadingSnapshot> rows = new(readings.Count);
        foreach (WordReading reading in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep every original index and duplicate. Reuse the existing typed getter, never a second tag parser.
            MorphologicalFeatures features = reading.Features;
            rows.Add(new(reading, features));
        }
        return rows;
    }

    // Call before publishing each future complete-reading support reference. This is not a selector.
    internal static TextReadingSnapshot RequireSupport(TextReadingInventory? inventory,
        int tokenIndex, int completeReadingIndex)
    {
        if (inventory is null || tokenIndex < 0 || completeReadingIndex < 0)
            throw new InvalidOperationException("A support reference has no response-local inventory.");
        // The inventory constructor guarantees unique, strictly increasing TokenIndex values.
        int low = 0, high = inventory.Occurrences.Count - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            TextReadingOccurrence occurrence = inventory.Occurrences[middle];
            if (occurrence.TokenIndex < tokenIndex) { low = middle + 1; continue; }
            if (occurrence.TokenIndex > tokenIndex) { high = middle - 1; continue; }
            IReadOnlyList<TextReadingSnapshot> resolved = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
            if (completeReadingIndex >= resolved.Count)
                throw new InvalidOperationException("A support reference is outside the complete resolved lookup route.");
            return resolved[completeReadingIndex];
        }
        throw new InvalidOperationException("A support reference does not name a Word occurrence.");
    }
}
