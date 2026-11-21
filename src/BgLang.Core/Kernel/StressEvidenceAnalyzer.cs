using System.Text;
using System.Globalization;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Exact reading evidence and conservative rendering over the existing package mechanisms.</summary>
internal static class StressEvidenceAnalyzer
{
    internal static StressEvidence Analyze(string text, IRuntimeLexicon lexicon, PackageLexicalCatalog? catalog,
        WordFormationAnalyzer graph, PackageProvenance provenance, StressReadingSelection? selection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, BulgarianStressLookup.ExactInventory> acquired = new(StringComparer.Ordinal);
        WordAnalysis Acquire(string surface)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!acquired.TryGetValue(surface, out var inventory))
            {
                _ = BulgarianStressLookup.LookupExact(lexicon, surface, int.MaxValue, true,
                    out _, out inventory, cancellationToken, captureOwnerPairing: true);
                acquired.Add(surface, inventory!);
            }
            return new(surface, inventory!.ResolvedKey, inventory.ResolvedReadings, null);
        }
        IReadOnlyList<TokenAnalysis> tokens = new BulgarianTokenizer().Tokenize(text, Acquire);
        List<StressTokenEvidence> results = [];
        bool selectionMatched = false;
        for (int tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = tokens[tokenIndex];
            if (token.Kind != TokenKind.Word) continue;
            if (!acquired.ContainsKey(token.Text)) _ = Acquire(token.Text);
            var exact = acquired[token.Text];
            bool valid = BulgarianOrthography.IsWellFormedWord(token.Text) &&
                !UnsafeBoundary(text, token.Start - 1) && !UnsafeBoundary(text, token.Start + token.Length);
            (int[] vowels, StressMark[] observed) = ReadMarks(token.Text, token.Start);
            IReadOnlyList<StressRealization> observations = observed.Length == 0 ? [] :
                Array.AsReadOnly(new[] { new StressRealization(StressOrigin.InputObservation,
                    valid ? StressStatus.Unclassified : StressStatus.InvalidInput, Array.AsReadOnly(observed), null) });
            IReadOnlyList<StressRealization> inferences = valid && vowels.Length == 1 ?
                Array.AsReadOnly(new[] { new StressRealization(StressOrigin.SingleVowelInference, StressStatus.Inferred,
                    Array.AsReadOnly(new[] { new StressMark(0, token.Start + vowels[0], StressMarkKind.Unspecified) }), null) }) : [];
            List<StressReadingEvidence> readings = [];
            bool hasUnstressedAlternative = false;
            for (int i = 0; i < exact.ResolvedReadings.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WordReading reading = exact.ResolvedReadings[i];
                if (vowels.Length == 1)
                {
                    MorphologicalFeatures features = MorphologicalFeatures.FromTag(reading.PartOfSpeech);
                    hasUnstressedAlternative |= features.PartOfSpeech is MorphologicalPartOfSpeech.Preposition or
                        MorphologicalPartOfSpeech.Conjunction or MorphologicalPartOfSpeech.Particle ||
                        features.PartOfSpeech == MorphologicalPartOfSpeech.PersonalPronoun && features.IsShortForm is true;
                }
                int? ownerOrdinal = exact.RequireOwnerOrdinal(i, reading, cancellationToken);
                IReadOnlyList<LexicalEntry> owners = ownerOrdinal is { } ordinal && catalog is not null
                    ? catalog.GetOwners(ordinal, cancellationToken) : [];
                List<StressOwnerEvidence> ownerEvidence = [];
                foreach (LexicalEntry owner in owners)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    LexemeAnnotations.StressRow? row = catalog!.GetStress(owner.EntryId, reading);
                    IReadOnlyList<StressRealization> realizations = row is null ? [] :
                        Array.AsReadOnly(new[] { FromRow(row, token.Start, vowels, provenance.AnnotationsSource) });
                    ownerEvidence.Add(new(owner.EntryId, owner.LexemeKey, realizations));
                }
                int? reviewed = graph.ResolveReviewedExactStressVowelOrdinal(exact.ResolvedKey, new[] { reading });
                IReadOnlyList<StressRealization> reviewedEvidence = reviewed is { } value && (uint)value < (uint)vowels.Length ?
                    Array.AsReadOnly(new[] { new StressRealization(StressOrigin.ReviewedExact, StressStatus.Supported,
                        Array.AsReadOnly(new[] { new StressMark(value, token.Start + vowels[value], StressMarkKind.Primary) }),
                        provenance.WordFormationSource) }) : [];
                readings.Add(new(i, reading, ownerEvidence.AsReadOnly(), reviewedEvidence));
            }
            IReadOnlyList<StressReadingEvidence> considered = readings;
            string? selectedOwner = null;
            bool requestsToken = selection?.TokenIndex == tokenIndex;
            if (requestsToken)
            {
                considered = readings.Where(row => row.Reading.Form == selection!.Form &&
                    row.Reading.PartOfSpeech == selection.FullTag && row.Owners.Any(owner => owner.EntryId == selection.EntryId)).ToArray();
                if (considered.Count > 0) { selectionMatched = true; selectedOwner = selection!.EntryId; }
            }
            (StressStatus status, int? chosen) = valid
                ? Resolve(considered, selectedOwner, cancellationToken) : (StressStatus.InvalidInput, null);
            if (requestsToken && considered.Count == 0) { status = StressStatus.Unknown; chosen = null; }
            else if (valid && vowels.Length == 1 && chosen is null && status is StressStatus.Unknown or StressStatus.Incomplete)
            { status = StressStatus.Inferred; chosen = 0; }
            string rendered = token.Text;
            if (chosen is { } chosenOrdinal)
            {
                if (observed.Any(mark => mark.VowelOrdinal != chosenOrdinal))
                    status = StressStatus.Conflict;
                else if (observed.Length == 0 &&
                    (vowels.Length != 1 || selectedOwner is not null || !hasUnstressedAlternative))
                    rendered = token.Text.Insert(vowels[chosenOrdinal] + 1, "\u0300");
            }
            results.Add(new(tokenIndex, token.Text, token.Start, token.Length, status,
                observations, inferences, readings.AsReadOnly(), rendered));
        }
        StringBuilder renderedText = new(text);
        foreach (StressTokenEvidence token in results.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (token.RenderedText != token.Text)
                renderedText.Remove(token.Start, token.Length).Insert(token.Start, token.RenderedText);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(text, renderedText.ToString(), catalog is not null, selection is null ? StressSelectionStatus.NotRequested :
            selectionMatched ? StressSelectionStatus.Matched : StressSelectionStatus.NotConnected, results.AsReadOnly());
    }

    private static StressRealization FromRow(LexemeAnnotations.StressRow row, int start, int[] vowels, string? annotationsSource)
    {
        StressOrigin origin = row.Source?.Kind switch
        {
            "LegacyStored" => StressOrigin.LegacyStored,
            "LegacyCarried" => StressOrigin.LegacyCarried,
            "LegacyDisplayInferred" => StressOrigin.LegacyDisplayInferred,
            _ => StressOrigin.Editorial
        };
        bool valid = row.VowelOrdinals.All(ordinal => (uint)ordinal < (uint)vowels.Length);
        StressStatus status = !valid ? StressStatus.Conflict : row.VowelOrdinals.Length != 1
            ? StressStatus.Unclassified : origin is StressOrigin.LegacyCarried or StressOrigin.LegacyDisplayInferred
                ? StressStatus.Inferred : StressStatus.Supported;
        StressMark[] marks = valid ? row.VowelOrdinals.Select(ordinal =>
            new StressMark(ordinal, start + vowels[ordinal], StressMarkKind.Unspecified)).ToArray() : [];
        return new(origin, status, Array.AsReadOnly(marks), row.Source?.SourceId ?? annotationsSource)
        {
            LegacyUtf16Positions = row.Source?.Utf16Positions is { } raw ? Array.AsReadOnly((int[])raw.Clone()) : null,
            LegacyDisplayUtf16Positions = row.Source?.DisplayUtf16Positions is { } display ? Array.AsReadOnly((int[])display.Clone()) : null
        };
    }

    private static (StressStatus Status, int? Ordinal) Resolve(IReadOnlyList<StressReadingEvidence> readings,
        string? selectedOwner, CancellationToken cancellationToken)
    {
        if (readings.Count == 0) return (StressStatus.Unknown, null);
        HashSet<int> ordinals = [];
        bool missing = false, unclassified = false, inferred = false;
        foreach (StressReadingEvidence reading in readings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<StressOwnerEvidence> owners = selectedOwner is null ? reading.Owners :
                reading.Owners.Where(owner => owner.EntryId == selectedOwner).ToArray();
            // Tuple-wide review is separately inspectable; it cannot fill a missing owner's row.
            if (owners.Any(owner => owner.Realizations.Count == 0)) missing = true;
            IEnumerable<IReadOnlyList<StressRealization>> groups = owners.Count == 0
                ? new[] { reading.ReviewedEvidence } : owners.Select(owner =>
                    (IReadOnlyList<StressRealization>)owner.Realizations.Concat(reading.ReviewedEvidence).ToArray());
            foreach (IReadOnlyList<StressRealization> group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (group.Count == 0) missing = true;
                HashSet<int> withinOwner = [];
                foreach (StressRealization realization in group)
                {
                    if (realization.Status == StressStatus.Conflict) return (StressStatus.Conflict, null);
                    if (realization.Marks.Count != 1) { unclassified = true; continue; }
                    withinOwner.Add(realization.Marks[0].VowelOrdinal);
                    inferred |= realization.Status == StressStatus.Inferred;
                }
                if (withinOwner.Count > 1) return (StressStatus.Conflict, null);
                ordinals.UnionWith(withinOwner);
            }
        }
        if (ordinals.Count > 1) return (StressStatus.Ambiguous, null);
        if (unclassified) return (StressStatus.Unclassified, null);
        if (missing) return (ordinals.Count > 0 ? StressStatus.Incomplete : StressStatus.Unknown, null);
        return ordinals.Count == 1 ? (inferred ? StressStatus.Inferred : StressStatus.Supported, ordinals.Single()) :
            (StressStatus.Unknown, null);
    }

    internal static (int[] Vowels, StressMark[] Observed) ReadMarks(string text, int start)
    {
        List<int> vowels = [];
        List<StressMark> marks = [];
        int previousVowel = -1;
        for (int index = 0; index < text.Length;)
        {
            int offset = index;
            char value = char.ToLowerInvariant(BulgarianOrthography.ReadLetter(text, index, out int width));
            index += width;
            if (BulgarianOrthography.IsVowel(value))
            {
                vowels.Add(offset);
                previousVowel = vowels.Count - 1;
                if (value is 'ѐ' or 'ѝ')
                    marks.Add(new(previousVowel, start + offset, StressMarkKind.Grave));
            }
            else if (value is '\u0300' or '\u0301' && previousVowel >= 0)
            {
                marks.Add(new(previousVowel, start + vowels[previousVowel], value == '\u0300' ? StressMarkKind.Grave : StressMarkKind.Acute));
                previousVowel = -1;
            }
            else previousVowel = -1;
        }
        return (vowels.ToArray(), marks.ToArray());
    }

    private static bool UnsafeBoundary(string text, int index)
    {
        if ((uint)index >= (uint)text.Length) return false;
        char value = text[index];
        return char.GetUnicodeCategory(value) is UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark ||
            char.IsHighSurrogate(value) && (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])) ||
            char.IsLowSurrogate(value) && (index == 0 || !char.IsHighSurrogate(text[index - 1]));
    }
}
