using System.Runtime.CompilerServices;
using BgLang.Contracts;
using BgLang.Core;

namespace BgLang.Api;

/// <summary>One immutable package-bound facade for the Bulgarian language runtime.</summary>
public sealed class BulgarianLanguageSession : IDisposable
{
    private const int MaximumBound = 256;
    private const int MaximumTextLength = 65_536;
    private readonly BulgarianTokenizer _tokenizer = new();
    private RuntimePackageState? _package;

    private BulgarianLanguageSession(RuntimePackageState package) => _package = package;

    /// <summary>Opens and fully validates a runtime session package.</summary>
    public static BulgarianLanguageSession Open(string manifestPath) =>
        new(RuntimePackageState.Open(manifestPath));

    /// <summary>Deterministic identity of the validated package and both runtime components.</summary>
    public PackageProvenance Provenance => GetPackage().Provenance;

    /// <summary>Returns exact lexical owners, preserving homonyms and literal source metadata.</summary>
    public LexicalEntryLookup LookupLexicalEntries(string text, int limit = 32, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);
        ValidateBound(limit);
        RuntimePackageState package = GetPackage();
        return package.LexicalCatalog?.Lookup(package.Lexicon, text, limit, cancellationToken)
            ?? new(text, BulgarianTextNormalizer.NormalizeKey(text), false, [], false);
    }

    /// <summary>Returns one exact opaque owner identity, with explicit annotation availability.</summary>
    public LexicalEntryResult FindLexicalEntry(string entryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entryId);
        return GetPackage().LexicalCatalog?.Find(entryId) ?? new(false, null);
    }

    /// <summary>Returns candidate constructions from one exact opaque owner's accepted own forms.</summary>
    public WordConstructionResult GetWordConstructionCandidates(string entryId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entryId);
        RuntimePackageState package = GetPackage();
        return WordConstructionAnalyzer.GetCandidates(package.Lexicon, package.LexicalCatalog, entryId, cancellationToken);
    }

    /// <summary>Returns all exact stress alternatives and conservatively accents unambiguous word tokens.</summary>
    /// <remarks>Written marks are observations; single-vowel inference does not predict sentence prosody.</remarks>
    public StressEvidence GetStressEvidence(string text, StressReadingSelection? selection = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumTextLength)
            throw new ArgumentException($"Input must not exceed {MaximumTextLength} UTF-16 characters.", nameof(text));
        if (selection is not null)
        {
            ArgumentNullException.ThrowIfNull(selection.EntryId);
            ArgumentNullException.ThrowIfNull(selection.Form);
        }
        RuntimePackageState package = GetPackage();
        return StressEvidenceAnalyzer.Analyze(text, package.Lexicon, package.LexicalCatalog,
            package.WordFormation, package.Provenance, selection, cancellationToken);
    }

    /// <summary>Performs exact, bounded prefix, or bounded suffix lookup over immutable minimal DAWGs.</summary>
    public IReadOnlyList<WordReading> Lookup(
        string text,
        LookupMode mode = LookupMode.Exact,
        int limit = 16)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateBound(limit);
        IRuntimeLexicon index = GetPackage().Lexicon;
        return mode switch
        {
            LookupMode.Exact => BulgarianStressLookup.LookupExact(index, text, limit, out _),
            LookupMode.Prefix => index.FindByPrefix(text, limit),
            LookupMode.Suffix => index.FindBySuffix(text, limit),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported lookup mode.")
        };
    }

    /// <summary>Returns nearest loaded-package forms with exact bounded OSA edit-distance evidence.</summary>
    public IReadOnlyList<FuzzyMatch> FindSimilar(
        string text,
        int maxDistance = 2,
        int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxDistance is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(maxDistance), maxDistance, "Distance must be between 0 and 2.");
        ValidateBound(limit);

        string normalized = BulgarianTextNormalizer.NormalizeKey(text);
        if (normalized.Length > MaximumBound)
            throw new ArgumentException($"Normalized input must not exceed {MaximumBound} UTF-16 characters.", nameof(text));
        return GetPackage().Lexicon.FindSimilarNormalized(normalized, maxDistance, limit);
    }

    /// <summary>Returns typed package readings and graph-backed base/state evidence for an unknown form.</summary>
    public WordAnalysis AnalyzeWord(string word, int maxReadings = 32) =>
        AnalyzeWord(word, maxReadings, CancellationToken.None);

    /// <summary>Analyzes one word with cooperative cancellation of linguistic work.</summary>
    public WordAnalysis AnalyzeWord(string word, int maxReadings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(word);
        ValidateBound(maxReadings);
        return AnalyzeWordCore(word, maxReadings, null, null, null, null, null, cancellationToken, out _);
    }

    private WordAnalysis AnalyzeWordCore(string word, int maxReadings,
        UnknownMorphologyAnalyzer.ReplayBudget? textBudget, UnknownMorphologyAnalyzer.ReplayBudget? knownBaseBudget,
        UnknownMorphologyAnalyzer.ReplayBudget? baseTypeBudget, BinaryProofBudget? binaryBudget,
        GraphSearchBudget? graphBudget, CancellationToken cancellationToken,
        out BulgarianStressLookup.ExactInventory? exactInventory,
        UnknownMorphologyAnalyzer.DonorCache? donorCache = null, bool captureCompleteExact = false,
        bool captureOwnerPairing = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimePackageState package = GetPackage();
        graphBudget ??= new GraphSearchBudget(262_144, cancellationToken);
        IReadOnlyList<WordReading> readings = BulgarianStressLookup.LookupExact(package.Lexicon, word,
            maxReadings, captureCompleteExact, out string normalized, out exactInventory, cancellationToken, captureOwnerPairing);
        WordDecomposition? baseEvidence = null;
        WordFormationAnalyzer.MorphologySearch? graphSearch = null;
        if (readings.Count == 0)
        {
            (WordDecomposition decomposition, graphSearch) = package.WordFormation.AnalyzePaths(
                word, package.Lexicon, maxReadings, binaryBudget, graphBudget);
            if (decomposition.HasEvidence)
                baseEvidence = decomposition;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (readings.Count > 0) return new WordAnalysis(word, normalized, readings, baseEvidence);
        donorCache?.BeginUnknown(normalized);
        UnknownMorphologyAnalyzer.Result hypotheses = UnknownMorphologyAnalyzer.Analyze(word, normalized,
            package.Lexicon, package.WordFormation, package.Provenance.PackageId, maxReadings,
            textBudget ?? new(UnknownMorphologyAnalyzer.WordReplayLimit, wordLimit: UnknownMorphologyAnalyzer.WordReplayLimit,
                cancellationToken: cancellationToken), donorCache: donorCache);
        UnknownMorphologyAnalyzer.Result knownBases = UnknownMorphologyAnalyzer.AnalyzeKnownBases(word, normalized,
            package.Lexicon, package.WordFormation, package.Provenance.PackageId, maxReadings,
            knownBaseBudget ?? new(UnknownMorphologyAnalyzer.KnownBaseReplayLimit, wordLimit: UnknownMorphologyAnalyzer.KnownBaseReplayLimit,
                cancellationToken: cancellationToken), donorCache);
        UnknownMorphologyAnalyzer.Result baseTypes = UnknownMorphologyAnalyzer.Analyze(word, normalized,
            package.Lexicon, package.WordFormation, package.Provenance.PackageId, maxReadings,
            baseTypeBudget ?? new(UnknownMorphologyAnalyzer.BaseTypeReplayLimit,
                wordLimit: UnknownMorphologyAnalyzer.BaseTypeReplayLimit, cancellationToken: cancellationToken),
            baseOnly: true, donorCache: donorCache);
        cancellationToken.ThrowIfCancellationRequested();
        return new WordAnalysis(word, normalized, readings, baseEvidence)
        {
            GraphCandidates = graphSearch?.Candidates ?? [],
            GraphSearchIncomplete = graphSearch?.Incomplete ?? false,
            GraphCandidatesTruncated = graphSearch?.Truncated ?? false,
            Candidates = hypotheses.Candidates,
            CandidateSearchLimited = hypotheses.WorkLimited,
            CandidateEvidenceLimited = hypotheses.EvidenceLimited,
            KnownBaseCandidates = knownBases.Candidates,
            KnownBaseSearchLimited = knownBases.WorkLimited,
            BaseTypeCandidates = baseTypes.Candidates,
            BaseTypeSearchLimited = baseTypes.WorkLimited,
            BaseTypeEvidenceLimited = baseTypes.EvidenceLimited
        };
    }

    /// <summary>Generates qualified accents through an explicitly selected lexical prototype and its guarded graph program.</summary>
    /// <remarks>A new lemma inherits the selected lexical accent class, not an accent inferred from its part of speech.</remarks>
    public GeneratedStressParadigm GenerateStressForms(string prototypeEntryId, string lemma, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prototypeEntryId);
        ArgumentNullException.ThrowIfNull(lemma);
        cancellationToken.ThrowIfCancellationRequested();
        RuntimePackageState package = GetPackage();
        string normalized = BulgarianTextNormalizer.NormalizeKey(lemma);
        if (normalized.Length > MaximumBound || !BulgarianOrthography.IsWellFormedWord(normalized))
            throw new ArgumentException("A well-formed word within the existing graph input bound is required.", nameof(lemma));
        return package.LexicalCatalog?.GenerateStressForms(prototypeEntryId, normalized, package.WordFormation, cancellationToken)
            ?? new(false, prototypeEntryId, null, normalized, null, null, package.WordFormation.SourceId, []);
    }

    /// <summary>Returns package forms from stored readings or explicitly declared productive paradigms.</summary>
    public IReadOnlyList<WordForm> GenerateForms(string lemma, int limit = 128)
    {
        ArgumentNullException.ThrowIfNull(lemma);
        ValidateBound(limit);
        return GetPackage().Lexicon.GenerateForms(lemma, limit);
    }

    /// <summary>Projects a qualified lexical accent transition from the word's actual reviewed decomposition.</summary>
    /// <remarks>Reviewed non-generating applications remain non-generating; this query does not license arbitrary suffix formations.</remarks>
    public DerivationalStressEvidence GetDerivationalStressEvidence(string word, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WordDecomposition decomposition = DecomposeWord(word);
        cancellationToken.ThrowIfCancellationRequested();
        return DerivationalAccent.Project(decomposition);
    }

    /// <summary>Composes licensed paths from an exact typed lemma anchor, shortest first, with evidence and semantic diversity within each depth tier.</summary>
    /// <remarks>The limit also bounds discovery; larger limits can discover additional paths.</remarks>
    public IReadOnlyList<WordDerivation> DeriveWords(string lemma, int limit = 16)
    {
        ArgumentNullException.ThrowIfNull(lemma);
        ValidateBound(limit);
        RuntimePackageState package = GetPackage();
        string normalized = BulgarianTextNormalizer.NormalizeKey(lemma);
        if (normalized.Length > MaximumBound)
            throw new ArgumentException($"Normalized input must not exceed {MaximumBound} UTF-16 characters.", nameof(lemma));
        return package.WordFormation.Derive(normalized, package.Lexicon, limit);
    }

    /// <summary>
    /// Returns exact-first covers, then two-part covers with one lexical exact anchor and one
    /// uniquely licensed productive graph segment, without claiming syntax validity.
    /// </summary>
    public IReadOnlyList<WordSegmentationCandidate> SegmentWord(
        string text,
        int limit = 5,
        int maxSegments = 5)
    {
        ArgumentNullException.ThrowIfNull(text);
        RuntimePackageState package = GetPackage();
        return ExactWordSegmenter.Segment(
            package.Lexicon,
            text,
            limit,
            maxSegments,
            surface => package.WordFormation.TryDeriveUniqueProductiveSingleGenericEdge(
                surface,
                package.Lexicon));
    }

    /// <summary>Tokenizes bounded Bulgarian text and analyzes every word token exactly once per surface.</summary>
    public TextAnalysis AnalyzeText(string text) => AnalyzeText(text, CancellationToken.None);

    /// <summary>Analyzes text with request-local ordinary donor reuse and cooperative cancellation.</summary>
    public TextAnalysis AnalyzeText(string text, CancellationToken cancellationToken)
        => AnalyzeText(text, false, cancellationToken);

    /// <summary>Optionally includes four advisory unknown-context proper-name lanes without selecting exact readings.</summary>
    public TextAnalysis AnalyzeText(string text, bool includeUnknownContextProperNames, CancellationToken cancellationToken)
        => AnalyzeText(text, includeUnknownContextProperNames, default, cancellationToken);

    /// <summary>Analyzes complete readings with bounded context, syntax and individually requested diagnostic advice.</summary>
    public TextAnalysis AnalyzeText(string text, bool includeUnknownContextProperNames,
        TextContextOptions contextOptions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumTextLength)
            throw new ArgumentException($"Input must not exceed {MaximumTextLength} UTF-16 characters.", nameof(text));
        Dictionary<string, (WordAnalysis Display, BulgarianStressLookup.ExactInventory Exact)> memo =
            new(StringComparer.Ordinal);
        RuntimePackageState package = GetPackage();
        UnknownMorphologyAnalyzer.DonorCache donorCache = new(package.Lexicon, package.WordFormation);
        BinaryProofBudget binaryBudget = new(cancellationToken: cancellationToken);
        GraphSearchBudget graphBudget = new(524_288, cancellationToken);
        UnknownMorphologyAnalyzer.ReplayBudget candidateBudget = new(
            UnknownMorphologyAnalyzer.WordReplayLimit * UnknownMorphologyAnalyzer.TextAnalysisLimit,
            UnknownMorphologyAnalyzer.TextAnalysisLimit, UnknownMorphologyAnalyzer.WordReplayLimit, cancellationToken);
        UnknownMorphologyAnalyzer.ReplayBudget knownBaseBudget = new(
            UnknownMorphologyAnalyzer.KnownBaseReplayLimit * UnknownMorphologyAnalyzer.TextKnownBaseLimit,
            UnknownMorphologyAnalyzer.TextKnownBaseLimit, UnknownMorphologyAnalyzer.KnownBaseReplayLimit, cancellationToken);
        UnknownMorphologyAnalyzer.ReplayBudget baseTypeBudget = new(
            UnknownMorphologyAnalyzer.BaseTypeReplayLimit * UnknownMorphologyAnalyzer.TextAnalysisLimit,
            UnknownMorphologyAnalyzer.TextAnalysisLimit, UnknownMorphologyAnalyzer.BaseTypeReplayLimit, cancellationToken);
        WordAnalysis AnalyzeCached(string surface)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!memo.TryGetValue(surface, out var cached))
            {
                WordAnalysis display = AnalyzeWordCore(surface, 32, candidateBudget, knownBaseBudget, baseTypeBudget, binaryBudget, graphBudget,
                    cancellationToken, out var exact, donorCache, captureCompleteExact: true,
                    captureOwnerPairing: contextOptions.IncludeWordConstructions);
                cancellationToken.ThrowIfCancellationRequested();
                cached = (display, exact!);
                memo.Add(surface, cached);
            }

            return cached.Display;
        }

        IReadOnlyList<TokenAnalysis> tokens = Array.AsReadOnly(_tokenizer.Tokenize(text, AnalyzeCached).ToArray());
        TextReadingInventory readingInventory = TextReadingInventorySnapshot.Create(text, tokens, memo, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SentenceSpan> sentences = Array.AsReadOnly(SentenceBoundaryDetector.Split(text, tokens).ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        SpellingCandidateCache spellingCache = new(package.Lexicon);
        ProperNameEvidence properNames = ProperNameAnalyzer.Analyze(text, tokens, sentences, readingInventory,
            includeUnknownContextProperNames, cancellationToken);
        foreach (ProperNameCandidate candidate in properNames.Candidates)
            foreach (TextReadingReference support in candidate.ExactSupports.Concat(candidate.ContextSupports))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = TextReadingInventorySnapshot.RequireSupport(readingInventory, support.TokenIndex, support.CompleteReadingIndex);
            }
        List<TextDiagnostic> collectedDiagnostics = [];
        void AddDiagnostics(Func<IEnumerable<TextDiagnostic>> analyze)
        {
            cancellationToken.ThrowIfCancellationRequested();
            collectedDiagnostics.AddRange(analyze());
            cancellationToken.ThrowIfCancellationRequested();
        }
        AddDiagnostics(() => BasicSentenceShapeAnalyzer.Analyze(text, sentences));
        TextSpellingEvidence spelling = TextSpellingAnalyzer.Analyze(tokens, spellingCache, cancellationToken);
        AddDiagnostics(() => SpellingProjectionAnalyzer.Analyze(tokens, package.Lexicon, cancellationToken, spellingCache));
        AddDiagnostics(() => WordFormationEvidenceAnalyzer.Analyze(tokens));
        AddDiagnostics(() => PronounVerbAgreementAnalyzer.Analyze(tokens, sentences, package.Lexicon));
        AddDiagnostics(() => PrepositionComplementAnalyzer.Analyze(tokens, sentences, package.Lexicon));
        AddDiagnostics(() => ArticleCoverageEvidenceAnalyzer.Analyze(text, tokens, sentences, package.Lexicon));
        AddDiagnostics(() => HyphenatedDegreeEvidenceAnalyzer.Analyze(tokens, package.Lexicon));
        AddDiagnostics(() => ProperNameEvidenceAnalyzer.Analyze(text, tokens, package.Lexicon));
        AddDiagnostics(() => ReviewedVerbCliticPredicateGroupAnalyzer.Analyze(
            tokens, sentences, package.Lexicon, package.WordFormation));
        AddDiagnostics(() => StyleRepetitionAnalyzer.Analyze(text, tokens));
        TextDiagnostic[] diagnostics = collectedDiagnostics
            .OrderBy(diagnostic => diagnostic.Start)
            .ThenBy(diagnostic => diagnostic.Length)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        TextAnalysis analysis = new(text, tokens)
        {
            Sentences = sentences,
            ReadingInventory = readingInventory,
            Metrics = TextMetricsAnalyzer.Analyze(tokens, sentences, cancellationToken),
            Spelling = spelling,
            Profile = DocumentReadingProfileAnalyzer.Calculate(tokens, readingInventory, cancellationToken),
            ProperNames = properNames,
            Lexical = package.LexicalCatalog?.Analyze(text, tokens, package.Lexicon, cancellationToken) ?? new(false, [], []),
            Phonetics = BulgarianPhoneticAnalyzer.Analyze(
                text, tokens, package.Lexicon, package.WordFormation, package.LexicalStress, cancellationToken),
            Diagnostics = diagnostics
        };
        (TextContextEvidence context, TextSyntaxEvidence syntax) =
            TextContextAnalyzer.Calculate(analysis, contextOptions, cancellationToken);
        Dictionary<string, IReadOnlyList<TextReadingSnapshot>> counterpartMemo = new(StringComparer.Ordinal);
        IReadOnlyList<TextReadingSnapshot> ExactCounterpart(string surface)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (counterpartMemo.TryGetValue(surface, out var captured)) return captured;
            BulgarianStressLookup.ExactInventory exact;
            if (memo.TryGetValue(surface, out var acquired)) exact = acquired.Exact;
            else
            {
                _ = BulgarianStressLookup.LookupExact(package.Lexicon, surface, int.MaxValue, true,
                    out _, out var complete, cancellationToken);
                exact = complete!;
            }
            List<TextReadingSnapshot> rows = [];
            foreach (WordReading reading in exact.ResolvedReadings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(new(reading, reading.Features));
            }
            captured = rows.AsReadOnly();
            counterpartMemo.Add(surface, captured);
            return captured;
        }
        TextValidationEvidence validation = TextValidationAnalyzer.Analyze(analysis, context, syntax,
            contextOptions, cancellationToken, ExactCounterpart);
        TextWordConstructionEvidence? constructions = contextOptions.IncludeWordConstructions
            ? WordConstructionAnalyzer.AnalyzeText(analysis, package.Lexicon, package.LexicalCatalog,
                lookupText => memo.TryGetValue(lookupText, out var acquired) ? acquired.Exact :
                    throw new InvalidOperationException("Construction acquisition is outside the actual text memo."), cancellationToken)
            : null;
        cancellationToken.ThrowIfCancellationRequested();
        return analysis with { Context = context, Syntax = syntax, Validation = validation, WordConstructions = constructions };
    }

    /// <summary>Returns reviewed typed graph segmentation evidence for a surface.</summary>
    public WordDecomposition DecomposeWord(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        RuntimePackageState package = GetPackage();
        return package.WordFormation.Decompose(word, package.Lexicon);
    }

    public void Dispose() => Interlocked.Exchange(ref _package, null)?.Dispose();

    private RuntimePackageState GetPackage() =>
        _package ?? throw new ObjectDisposedException(nameof(BulgarianLanguageSession));

    private static void ValidateBound(
        int value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (value is < 1 or > MaximumBound)
            throw new ArgumentOutOfRangeException(parameterName, value, $"Bound must be between 1 and {MaximumBound}.");
    }
}
