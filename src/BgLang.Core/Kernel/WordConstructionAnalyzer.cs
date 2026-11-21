using BgLang.Contracts;

namespace BgLang.Core;

// Exact declaration ownership and accepted own forms; no lemma-wide generation or second lookup.
internal static class WordConstructionAnalyzer
{
    internal static WordConstructionResult GetCandidates(IRuntimeLexicon lexicon,
        PackageLexicalCatalog? catalog, string entryId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entryId);
        if (catalog is null) return new(WordConstructionStatus.OwnerMetadataUnavailable, null, null, []);
        LexicalEntry? entry = catalog.Find(entryId).Entry;
        if (entry is null) return new(WordConstructionStatus.OwnerNotFound, null, null, []);
        if (!catalog.TryGetOwnerOrdinal(entryId, out int ordinal, cancellationToken))
            throw new InvalidOperationException("An exact lexical entry has no declaration ordinal.");
        string? runtimeKey = lexicon.GetLexemeKey(ordinal, cancellationToken);
        if (runtimeKey is not null && runtimeKey != entry.LexemeKey)
            throw new InvalidOperationException("Runtime and annotated owner identities differ.");
        var collected = lexicon.CollectAcceptedOwnForms(new[] { ordinal }, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WordReading>? forms = RequireForms(collected, ordinal);
        if (forms is null) return new(WordConstructionStatus.OwnFormsUnavailable, entry, null, []);
        if (forms.Count == 0) return new(WordConstructionStatus.NoApplicable, entry, [], []);
        string lemma = forms[0].Lemma;
        IReadOnlyList<TextReadingSnapshot> captured = Capture(forms, lemma, cancellationToken);
        IReadOnlyList<WordConstructionCandidate> candidates = ConstructionTemplateComposer.Compose(
            entry.EntryId, entry.LexemeKey, lemma, captured, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(candidates.Count == 0 ? WordConstructionStatus.NoApplicable : WordConstructionStatus.Ready,
            entry, captured, candidates);
    }

    internal static TextWordConstructionEvidence AnalyzeText(TextAnalysis analysis, IRuntimeLexicon lexicon,
        PackageLexicalCatalog? catalog, Func<string, BulgarianStressLookup.ExactInventory> getExact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TextReadingInventory inventory = analysis.ReadingInventory ??
            throw new InvalidOperationException("Constructions require the response complete reading inventory.");
        IReadOnlyList<WordReading>[] complete = new IReadOnlyList<WordReading>[analysis.Tokens.Count];
        string[] resolvedKeys = new string[analysis.Tokens.Count];
        Dictionary<int, List<Anchor>> anchorsByOwner = [];
        Dictionary<int, string> ownerKeys = [];
        List<TextReadingReference> unavailable = [];
        HashSet<int> unavailableTokens = [];
        void Unavailable(Anchor anchor)
        {
            unavailable.Add(new(anchor.TokenIndex, anchor.ReadingIndex));
            unavailableTokens.Add(anchor.TokenIndex);
        }
        int occurrenceIndex = 0;
        for (int tokenIndex = 0; tokenIndex < analysis.Tokens.Count; tokenIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[tokenIndex];
            complete[tokenIndex] = [];
            resolvedKeys[tokenIndex] = string.Empty;
            if (token.Kind != TokenKind.Word) continue;
            BulgarianStressLookup.ExactInventory exact = getExact(token.Analysis?.Word ??
                throw new InvalidOperationException("A Word token has no actual memo lookup key."));
            IReadOnlyList<WordReading> readings = exact.ResolvedReadings;
            complete[tokenIndex] = readings;
            resolvedKeys[tokenIndex] = exact.SupportReadings is not null ? exact.SupportKey! : exact.CanonicalKey;
            if (occurrenceIndex >= inventory.Occurrences.Count || inventory.Occurrences[occurrenceIndex].TokenIndex != tokenIndex)
                throw new InvalidOperationException("Complete occurrence indices differ from the original tokens.");
            TextReadingOccurrence occurrence = inventory.Occurrences[occurrenceIndex++];
            IReadOnlyList<TextReadingSnapshot> snapshots = inventory.Sets[occurrence.ReadingSetIndex].ResolvedReadings;
            if (snapshots.Count != readings.Count)
                throw new InvalidOperationException("Complete exact reading counts differ.");
            if (exact.ResolvedOwnerOrdinals is null) unavailableTokens.Add(tokenIndex);
            for (int readingIndex = 0; readingIndex < readings.Count; readingIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WordReading reading = snapshots[readingIndex].Raw;
                if (!ReferenceEquals(reading, readings[readingIndex]))
                    throw new InvalidOperationException("A public complete reading is not its acquired exact reference.");
                Anchor anchor = new(tokenIndex, readingIndex, reading);
                int? owner = exact.RequireOwnerOrdinal(readingIndex, reading, cancellationToken);
                if (owner is null) { Unavailable(anchor); continue; }
                if (!ownerKeys.TryGetValue(owner.Value, out string? key))
                {
                    key = lexicon.GetLexemeKey(owner.Value, cancellationToken);
                    if (catalog is not null)
                    {
                        IReadOnlyList<LexicalEntry> aliases = catalog.GetOwners(owner.Value, cancellationToken);
                        if (aliases.Count == 0) throw new InvalidOperationException("An annotated owner has no aliases.");
                        if (key is not null && key != aliases[0].LexemeKey)
                            throw new InvalidOperationException("Runtime and annotated owner identities differ.");
                        key ??= aliases[0].LexemeKey;
                    }
                    if (key is null) { Unavailable(anchor); continue; }
                    ownerKeys.Add(owner.Value, key);
                    anchorsByOwner.Add(owner.Value, []);
                }
                anchorsByOwner[owner.Value].Add(anchor);
            }
        }
        if (occurrenceIndex != inventory.Occurrences.Count)
            throw new InvalidOperationException("Complete inventory contains a foreign Word occurrence.");
        cancellationToken.ThrowIfCancellationRequested();
        // Empty requests deliberately do not probe backend capability or scan payloads.
        var collected = anchorsByOwner.Count == 0 ? null :
            lexicon.CollectAcceptedOwnForms(anchorsByOwner.Keys, cancellationToken);
        List<WordConstructionOwnerEvidence> owners = [];
        List<WordConstructionCandidate> candidates = [];
        List<ConstructionSpanMatcher.BoundCandidate> bound = [];
        bool completeOwners = true;
        foreach (var (ordinal, anchors) in anchorsByOwner)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<LexicalEntry> aliases = catalog?.GetOwners(ordinal, cancellationToken) ?? [];
            List<string?> entryIds = [];
            if (aliases.Count == 0) entryIds.Add(null);
            foreach (LexicalEntry alias in aliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entryIds.Add(alias.EntryId);
            }
            IReadOnlyList<WordReading>? forms = RequireForms(collected, ordinal);
            if (forms is null)
            {
                completeOwners = false;
                foreach (string? id in entryIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    owners.Add(new(id, ownerKeys[ordinal], WordConstructionStatus.OwnFormsUnavailable, null));
                }
                foreach (Anchor anchor in anchors) { cancellationToken.ThrowIfCancellationRequested(); Unavailable(anchor); }
                continue;
            }
            Dictionary<WordReading, List<Anchor>> byReading = new(ReferenceEqualityComparer.Instance);
            HashSet<WordReading> selected = new(ReferenceEqualityComparer.Instance);
            foreach (Anchor anchor in anchors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byReading.TryGetValue(anchor.Reading, out var found)) byReading.Add(anchor.Reading, found = []);
                found.Add(anchor);
                selected.Add(anchor.Reading);
            }
            HashSet<WordReading> own = new(ReferenceEqualityComparer.Instance);
            foreach (WordReading form in forms) { cancellationToken.ThrowIfCancellationRequested(); own.Add(form); }
            foreach (WordReading selectedReading in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!own.Contains(selectedReading)) throw new InvalidOperationException("A paired exact anchor is missing from its complete own forms.");
            }
            string lemma = anchors[0].Reading.Lemma;
            IReadOnlyList<TextReadingSnapshot> captured = Capture(forms, lemma, cancellationToken);
            IReadOnlyList<WordConstructionCandidate> prototypes = ConstructionTemplateComposer.Compose(
                null, ownerKeys[ordinal], lemma, captured, cancellationToken, selected);
            bool hasMultiPart = prototypes.Any(candidate => candidate.Parts.Count >= 2);
            foreach (string? id in entryIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owners.Add(new(id, ownerKeys[ordinal], hasMultiPart ? WordConstructionStatus.Ready : WordConstructionStatus.NoApplicable, captured));
                foreach (WordConstructionCandidate prototype in prototypes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (prototype.Parts.Count < 2) continue;
                    WordConstructionCandidate candidate = prototype with { EntryId = id };
                    int candidateIndex = candidates.Count;
                    candidates.Add(candidate);
                    if (!byReading.TryGetValue(candidate.BaseForm.Raw, out var supports))
                        throw new InvalidOperationException("A text candidate is not a selected exact anchor.");
                    foreach (Anchor anchor in supports)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        bound.Add(new(candidateIndex, anchor.TokenIndex, anchor.ReadingIndex, anchor.Reading, candidate));
                    }
                }
            }
        }
        IReadOnlyList<WordConstructionMatch> matches = ConstructionSpanMatcher.Find(
            analysis.Text, analysis.Tokens, analysis.Sentences, complete, bound, cancellationToken, resolvedKeys);
        cancellationToken.ThrowIfCancellationRequested();
        TextWordConstructionEvidence result = new("word-constructions.v1", completeOwners && unavailableTokens.Count == 0,
            owners.AsReadOnly(), candidates.AsReadOnly(), matches, unavailable.AsReadOnly())
        { UnavailableTokenIndices = Array.AsReadOnly(unavailableTokens.Order().ToArray()) };
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static IReadOnlyList<WordReading>? RequireForms(
        IReadOnlyDictionary<int, IReadOnlyList<WordReading>?>? collected, int ordinal)
    {
        if (collected is null) return null;
        if (!collected.TryGetValue(ordinal, out var forms))
            throw new InvalidOperationException("A bulk own-form result omitted a requested owner.");
        return forms;
    }

    private static IReadOnlyList<TextReadingSnapshot> Capture(IReadOnlyList<WordReading> forms, string ownerLemma,
        CancellationToken cancellationToken)
    {
        List<TextReadingSnapshot> captured = new(forms.Count);
        string normalizedLemma = BulgarianTextNormalizer.NormalizeKey(ownerLemma);
        foreach (WordReading reading in forms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BulgarianTextNormalizer.NormalizeKey(reading.Lemma) != normalizedLemma)
                throw new InvalidOperationException("Accepted forms cross the selected owner lemma.");
            captured.Add(new(reading, reading.Features));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return captured.AsReadOnly();
    }

    private sealed record Anchor(int TokenIndex, int ReadingIndex, WordReading Reading);
}
