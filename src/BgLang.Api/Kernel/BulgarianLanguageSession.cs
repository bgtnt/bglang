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
        return AnalyzeWordCore(word, maxReadings, null, null, null, null, null, cancellationToken);
    }

    private WordAnalysis AnalyzeWordCore(string word, int maxReadings,
        UnknownMorphologyAnalyzer.ReplayBudget? textBudget, UnknownMorphologyAnalyzer.ReplayBudget? knownBaseBudget,
        UnknownMorphologyAnalyzer.ReplayBudget? baseTypeBudget, BinaryProofBudget? binaryBudget,
        GraphSearchBudget? graphBudget, CancellationToken cancellationToken,
        UnknownMorphologyAnalyzer.DonorCache? donorCache = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimePackageState package = GetPackage();
        graphBudget ??= new GraphSearchBudget(262_144, cancellationToken);
        IReadOnlyList<WordReading> readings = BulgarianStressLookup.LookupExact(package.Lexicon, word,
            maxReadings, out string normalized, cancellationToken);
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

    /// <summary>Returns package forms from stored readings or explicitly declared productive paradigms.</summary>
    public IReadOnlyList<WordForm> GenerateForms(string lemma, int limit = 128)
    {
        ArgumentNullException.ThrowIfNull(lemma);
        ValidateBound(limit);
        return GetPackage().Lexicon.GenerateForms(lemma, limit);
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumTextLength)
            throw new ArgumentException($"Input must not exceed {MaximumTextLength} UTF-16 characters.", nameof(text));
        Dictionary<string, WordAnalysis> memo = new(StringComparer.Ordinal);
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
            if (!memo.TryGetValue(surface, out WordAnalysis? analysis))
            {
                analysis = AnalyzeWordCore(surface, 32, candidateBudget, knownBaseBudget, baseTypeBudget, binaryBudget, graphBudget,
                    cancellationToken, donorCache);
                memo.Add(surface, analysis);
            }

            return analysis;
        }

        IReadOnlyList<TokenAnalysis> tokens = _tokenizer.Tokenize(text, AnalyzeCached);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<SentenceSpan> sentences = SentenceBoundaryDetector.Split(text, tokens);
        cancellationToken.ThrowIfCancellationRequested();
        List<TextDiagnostic> collectedDiagnostics = [];
        void AddDiagnostics(Func<IEnumerable<TextDiagnostic>> analyze)
        {
            cancellationToken.ThrowIfCancellationRequested();
            collectedDiagnostics.AddRange(analyze());
            cancellationToken.ThrowIfCancellationRequested();
        }
        AddDiagnostics(() => BasicSentenceShapeAnalyzer.Analyze(text, sentences));
        AddDiagnostics(() => SpellingProjectionAnalyzer.Analyze(tokens, package.Lexicon, cancellationToken));
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
            Phonetics = BulgarianPhoneticAnalyzer.Analyze(
                text, tokens, package.Lexicon, package.WordFormation, package.LexicalStress, cancellationToken),
            Diagnostics = diagnostics
        };
        cancellationToken.ThrowIfCancellationRequested();
        return analysis;
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
