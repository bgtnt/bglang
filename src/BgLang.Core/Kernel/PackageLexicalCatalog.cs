using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Validated owner metadata; exact lookup shares the package's forward/routed payloads.</summary>
internal sealed class PackageLexicalCatalog
{
    private readonly Dictionary<string, LexicalEntry> _entries;
    private readonly LexicalEntry[][] _owners;
    private readonly Dictionary<string, int> _ordinalByLexemeKey;
    private readonly Dictionary<string, Phrase[]> _phrases;
    private readonly Dictionary<(string EntryId, string Form, string? Tag), LexemeAnnotations.StressRow> _stress;
    private readonly Dictionary<string, LexemeAccentParadigm> _accentParadigms;
    private readonly Dictionary<string, (LexemeStressPattern Pattern, WordReading[] Readings)> _patterns;
    private readonly bool _incomplete;

    internal PackageLexicalCatalog(LexemeAnnotations annotations, IReadOnlyList<string> lexemeKeys,
        IReadOnlyDictionary<string, WordReading[]>? inventory = null)
    {
        Dictionary<string, WordReading[]> canonical = new(StringComparer.Ordinal);
        _patterns = new(StringComparer.Ordinal);
        foreach (var entry in annotations.Entries.Where(entry => entry.StressPattern is not null))
        {
            if (!canonical.TryGetValue(entry.LexemeKey, out var readings))
                canonical.Add(entry.LexemeKey, readings = LexemeStressPattern.CanonicalReadings((inventory ??
                    throw new InvalidDataException("Compact stress requires its exact validated owner inventory."))[entry.LexemeKey]));
            _patterns.Add(entry.EntryId, (entry.StressPattern!, readings));
        }
        _accentParadigms = annotations.Entries.Where(entry => entry.AccentParadigm is not null)
            .ToDictionary(entry => entry.EntryId, entry => entry.AccentParadigm!, StringComparer.Ordinal);
        _stress = annotations.Entries.SelectMany(entry => entry.Stress.Select(row => (entry.EntryId, Row: row)))
            .ToDictionary(item => (item.EntryId, item.Row.Form, item.Row.FullTag), item => item.Row);
        _entries = annotations.Entries.ToDictionary(entry => entry.EntryId, entry => new LexicalEntry(
            entry.EntryId, entry.LexemeKey, Array.AsReadOnly((string[])entry.Meaning.Clone()),
            Array.AsReadOnly(entry.Related.Select(group => new LexicalRelationGroup(group.Kind,
                Array.AsReadOnly(group.Phrases.Select(phrase => new LexicalRelationPhrase(
                    Array.AsReadOnly(phrase.Words.Select(word => new LexicalWordReference(
                        word.EntryId, word.Form, word.FullTag)).ToArray()),
                    Array.AsReadOnly((uint[])phrase.UnresolvedSourcePhrase.Clone()))).ToArray()))).ToArray())), StringComparer.Ordinal);
        _incomplete = _entries.Values.Any(entry => !entry.RelationsComplete);
        var ownersByKey = _entries.Values.GroupBy(entry => entry.LexemeKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.EntryId, StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        _owners = lexemeKeys.Select(key => ownersByKey[key]).ToArray();
        _ordinalByLexemeKey = lexemeKeys.Select((key, ordinal) => (key, ordinal))
            .ToDictionary(item => item.key, item => item.ordinal, StringComparer.Ordinal);
        _phrases = _entries.Values.SelectMany(entry => entry.Related.SelectMany((group, relationIndex) =>
                group.Phrases.Select((phrase, phraseIndex) => (Phrase: phrase, Index: phraseIndex))
                    .Where(item => item.Phrase.Words.Count > 1)
                    .Select(item => new Phrase(entry.EntryId, relationIndex, item.Index, group.Kind, item.Phrase.Words))))
            .GroupBy(phrase => BulgarianTextNormalizer.NormalizeKey(phrase.Words[0].Form), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(phrase => phrase.EntryId, StringComparer.Ordinal)
                .ThenBy(phrase => phrase.RelationIndex).ThenBy(phrase => phrase.PhraseIndex).ToArray(), StringComparer.Ordinal);
    }

    internal GeneratedStressParadigm GenerateStressForms(string entryId, string lemma, WordFormationAnalyzer graph,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _accentParadigms.TryGetValue(entryId, out var paradigm)
            ? paradigm.Generate(entryId, lemma, graph, cancellationToken)
            : new(false, entryId, null, lemma, null, null, graph.SourceId, []);
    }

    internal LexicalEntryResult Find(string entryId) => new(true, _entries.GetValueOrDefault(entryId)) { PackageHasUnresolvedRelations = _incomplete };

    internal int OwnerCount => _owners.Length;

    internal LexemeAnnotations.StressRow? GetStress(string entryId, WordReading reading)
    {
        if (_stress.TryGetValue((entryId, reading.Form, reading.PartOfSpeech), out var row)) return row;
        if (!_patterns.TryGetValue(entryId, out var pattern)) return null;
        int low = 0, high = pattern.Readings.Length - 1;
        while (low <= high)
        {
            int index = low + (high - low) / 2;
            WordReading candidate = pattern.Readings[index];
            int order = StringComparer.Ordinal.Compare(candidate.Form, reading.Form);
            if (order == 0) order = StringComparer.Ordinal.Compare(candidate.PartOfSpeech, reading.PartOfSpeech);
            if (order < 0) low = index + 1;
            else if (order > 0) high = index - 1;
            else return pattern.Pattern.Find(entryId, pattern.Readings, index);
        }
        return null;
    }

    internal bool TryGetOwnerOrdinal(string entryId, out int lexemeOrdinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entryId);
        lexemeOrdinal = -1;
        if (!_entries.TryGetValue(entryId, out LexicalEntry? entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        if (!_ordinalByLexemeKey.TryGetValue(entry.LexemeKey, out lexemeOrdinal))
            throw new InvalidOperationException("Catalog entry has no validated owner ordinal.");
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    internal IReadOnlyList<LexicalEntry> GetOwners(int lexemeOrdinal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)lexemeOrdinal >= (uint)_owners.Length)
            throw new InvalidOperationException("Paired owner ordinal is outside the loaded catalog.");
        // Existing aliases are immutable and already EntryId-ordered. Do not select the first alias.
        IReadOnlyList<LexicalEntry> aliases = Array.AsReadOnly(_owners[lexemeOrdinal]);
        cancellationToken.ThrowIfCancellationRequested();
        return aliases;
    }

    internal LexicalEntryLookup Lookup(IRuntimeLexicon lexicon, string text, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string normalized = BulgarianTextNormalizer.NormalizeKey(text);
        IReadOnlyList<int> ordinals = lexicon.FindExactLexemeOrdinals(normalized);
        if (ordinals.Count == 0 && BulgarianStressLookup.TryGetSupportKey(text, normalized, out string support))
        {
            ordinals = lexicon.FindExactLexemeOrdinals(support);
            if (ordinals.Count > 0) normalized = support;
        }
        HashSet<LexicalEntry> owners = new(ReferenceEqualityComparer.Instance);
        foreach (int ordinal in ordinals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (LexicalEntry entry in _owners[ordinal]) owners.Add(entry);
        }
        LexicalEntry[] selected = owners.OrderBy(entry => entry.EntryId, StringComparer.Ordinal).Take(limit).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return new(text, normalized, true, Array.AsReadOnly(selected), owners.Count > selected.Length) { PackageHasUnresolvedRelations = _incomplete };
    }

    internal LexicalTextEvidence Analyze(string text, IReadOnlyList<TokenAnalysis> tokens, IRuntimeLexicon lexicon,
        CancellationToken cancellationToken)
    {
        Dictionary<string, LexicalEntryLookup> memo = new(StringComparer.Ordinal);
        List<LexicalTokenEvidence> words = [];
        foreach (TokenAnalysis token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.Kind != TokenKind.Word) continue;
            if (!memo.TryGetValue(token.Text, out LexicalEntryLookup? owners))
                memo[token.Text] = owners = Lookup(lexicon, token.Text, 32, cancellationToken);
            words.Add(new(token.Text, token.Start, token.Length, owners));
        }
        List<LexicalPhraseEvidence> phrases = [];
        for (int start = 0; start < words.Count; start++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_phrases.TryGetValue(words[start].Owners.Normalized, out Phrase[]? candidates)) continue;
            foreach (Phrase phrase in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (phrase.Words.Count > words.Count - start) continue;
                bool matched = true;
                for (int offset = 0; offset < phrase.Words.Count; offset++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (words[start + offset].Owners.Normalized != BulgarianTextNormalizer.NormalizeKey(phrase.Words[offset].Form) ||
                        offset > 0 && !CanJoin(text, words[start + offset - 1], words[start + offset]))
                    { matched = false; break; }
                }
                if (!matched) continue;
                LexicalTokenEvidence first = words[start], last = words[start + phrase.Words.Count - 1];
                int length = last.Start + last.Length - first.Start;
                phrases.Add(new(text.Substring(first.Start, length), first.Start, length, phrase.EntryId,
                    phrase.RelationIndex, phrase.PhraseIndex, phrase.Kind, phrase.Words));
            }
        }
        return new(true, Array.AsReadOnly(words.ToArray()), Array.AsReadOnly(phrases.ToArray())) { PackageHasUnresolvedRelations = _incomplete };
    }

    private static bool CanJoin(string text, LexicalTokenEvidence left, LexicalTokenEvidence right)
    {
        int start = left.Start + left.Length;
        if (start >= right.Start) return false;
        for (int index = start; index < right.Start; index++)
            if (!char.IsWhiteSpace(text[index]) || text[index] is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029')
                return false;
        return true;
    }

    private sealed record Phrase(string EntryId, int RelationIndex, int PhraseIndex, string Kind,
        IReadOnlyList<LexicalWordReference> Words);
}
