using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Loads reviewed linguistic source and projects one reversible graph engine.</summary>
internal sealed class WordFormationAnalyzer
{
    internal const string SchemaVersion = "bglang-word-formation-source.v0";
    internal const string SchemaVersionV1 = "bglang-word-formation-source.v1";
    internal const string SchemaVersionV2 = "bglang-word-formation-source.v2";
    internal const string SurfaceStemEvidencePrefix = "reviewed_surface_stem=";
    private const string ReviewedStressSchemaVersion = "bglang-reviewed-exact-stress-source.v1";
    private const string ReviewedStressSourceId = "official-beron-ibl-reviewed-exact-stress.v1";
    private const string ReviewedStressReviewer = "codex-assisted-official-source-review";
    private const string BeronManEvidence = "https://beron.mon.bg/leksema/b2c59b88ebc06dbcef3725c8f7c0c1a3";
    private const string IblBookEvidence = "https://ibl.bas.bg/dictionary_portal/lang/bg/%D0%BA%D0%BD%D0%B8%D0%B3%D0%B0";
    private static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly IReadOnlyDictionary<string, (int VowelOrdinal, string Evidence)> ReviewedStressRows =
        new Dictionary<string, (int, string)>(StringComparer.Ordinal)
        {
            [ReviewedStressKey("книги", "книга", "Съществително:41,Женски,мн.ч.")] = (0, IblBookEvidence),
            [ReviewedStressKey("книгите", "книга", "Съществително:41,Женски,мн.ч.,Членуван")] = (0, IblBookEvidence),
            [ReviewedStressKey("мъжа", "мъж", "Съществително:24,Мъжки,Членуван")] = (1, BeronManEvidence),
            [ReviewedStressKey("мъже", "мъж", "Съществително:24,Мъжки,мн.ч.")] = (1, BeronManEvidence),
            [ReviewedStressKey("мъжете", "мъж", "Съществително:24,Мъжки,мн.ч.,Членуван")] = (1, BeronManEvidence),
            [ReviewedStressKey("мъжът", "мъж", "Съществително:24,Мъжки,Пълен")] = (1, BeronManEvidence)
        };

    private readonly WordFormationGraph _graph;
    private readonly OrdinaryBaseProofCache _ordinaryBaseProofs;
    private readonly HashSet<string> _acceptedParadigmTags;
    private readonly HashSet<string> _acceptedParadigmBaseTags;
    private readonly ReviewedVerbCliticRelationSourceNode? _sourceRelations;
    private readonly ReviewedExactStressSourceNode? _reviewedExactStress;

    private WordFormationAnalyzer(SourceDocument source)
    {
        source = ExpandFormPrograms(source);
        Validate(source);
        _graph = WordFormationGraphCompiler.Compile(source);
        _ordinaryBaseProofs = new(_graph);
        foreach (ReviewedVerbCliticRelationNode relation in source.ReviewedVerbCliticRelations?.Items ?? [])
        {
            if (relation.Placement.Witness is { VerbFullTag: not null } witness &&
                !_graph.MatchesParadigmForm(relation.Frame.VerbLemma, relation.Frame.VerbTypeStateId,
                    relation.Frame.ProductiveParadigmRuleId, witness.VerbFullTag, witness.Tokens[witness.VerbTokenIndex]))
                throw new InvalidDataException("Reviewed placement witness is not an accepted form of its exact paradigm.");
        }
        _sourceRelations = source.ReviewedVerbCliticRelations;
        _reviewedExactStress = source.ReviewedExactStress;
        HashSet<string> acceptedTransformIds = source.MorphophonologicalTransforms
            .Where(transform => transform.ReviewState == "accepted")
            .Select(transform => transform.Id).ToHashSet(StringComparer.Ordinal);
        // Match the compiled graph's least-trusted edge/transform dependency policy.
        ParadigmEdgeNode[] acceptedParadigmEdges = (source.ParadigmEdges ?? [])
            .Where(edge => edge.ReviewState == "accepted" && acceptedTransformIds.Contains(edge.StemTransformId))
            .ToArray();
        _acceptedParadigmTags = acceptedParadigmEdges
            .SelectMany(edge => edge.Forms.SelectMany(form => form.FullTags.Select(tag =>
                ParadigmTagKey(edge.StateId, edge.RuleId, tag))))
            .ToHashSet(StringComparer.Ordinal);
        _acceptedParadigmBaseTags = acceptedParadigmEdges
            .Select(edge => ParadigmTagKey(edge.StateId, edge.RuleId, edge.BaseFullTag))
            .ToHashSet(StringComparer.Ordinal);
    }

    internal string SourceId => _graph.SourceId;

    internal bool HasGraphAccentProfiles => _graph.HasAccentProfiles;
    internal WordFormationGraph.AccentRoute? GetAccentRoute(WordDerivation candidate,
        CancellationToken cancellationToken, out string reason) =>
        _graph.GetAccentRoute(candidate, cancellationToken, out reason);

    internal bool HasReviewedExactStress => _reviewedExactStress is not null;

    internal IReadOnlyList<ReviewedVerbCliticRelationNode> ReviewedVerbCliticRelations =>
        _sourceRelations?.Items ?? [];

    internal static WordFormationAnalyzer Load(string path, string expectedSourceId)
    {
        SourceDocument source = StrictJson.Read<SourceDocument>(
            path,
            "word-formation component",
            ValidateSourceJson);
        return Create(source, expectedSourceId, "manifest");
    }

    internal static WordFormationAnalyzer LoadSource(JsonElement sourceElement, string expectedSourceId)
        => LoadSource(sourceElement.GetRawText(), expectedSourceId);

    internal static WordFormationAnalyzer LoadSource(string sourceJson, string expectedSourceId)
    {
        SourceDocument source = StrictJson.ReadText<SourceDocument>(
            sourceJson,
            "word-formation source",
            ValidateSourceJson);
        return Create(source, expectedSourceId, "package");
    }

    internal WordDecomposition Decompose(string surface, IRuntimeLexicon lexicon, BinaryProofBudget? sharedBudget = null,
        GraphSearchBudget? graphBudget = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(lexicon);
        return _graph.Decompose(surface, lexicon, sharedBudget, graphBudget: graphBudget);
    }

    internal (WordDecomposition Evidence, MorphologySearch Search) AnalyzePaths(
        string surface, IRuntimeLexicon lexicon, int limit, BinaryProofBudget? sharedBudget,
        GraphSearchBudget? graphBudget = null)
    {
        graphBudget ??= new GraphSearchBudget(262_144);
        MorphologySearch search = new(limit);
        try { return (_graph.Decompose(surface, lexicon, sharedBudget, search, graphBudget), search); }
        catch (Exception error) when (error is InvalidDataException or GraphSearchBudget.Exhausted)
        {
            search.Refuse();
            return (new(surface, BulgarianTextNormalizer.NormalizeKey(surface), null, [], null, null, 0, [], SourceId), search);
        }
    }

    internal sealed class MorphologySearch(int limit)
    {
        internal WordDerivation[] Candidates { get; private set; } = [];
        internal bool Incomplete { get; set; }
        internal bool Truncated { get; private set; }
        private bool _exactRootRetentionComplete;
        internal bool GraphExactRootCandidatesComplete => !Incomplete && _exactRootRetentionComplete;

        internal void Complete(IEnumerable<WordDerivation> ordered, bool incomplete)
        {
            Incomplete |= incomplete;
            WordDerivation[] retained = ordered.Take(limit + 1).ToArray();
            Truncated = retained.Length > limit;
            // Inverse ranking places every exact-root path before the conditional tail.
            _exactRootRetentionComplete = retained.Length <= limit || retained[limit].IsConditional;
            Candidates = retained.Take(limit).ToArray();
        }

        internal void Refuse()
        {
            Candidates = [];
            Incomplete = true;
            Truncated = false;
            _exactRootRetentionComplete = false;
        }
    }

    internal IReadOnlyList<WordDerivation> Derive(
        string baseLemma,
        IRuntimeLexicon lexicon,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(baseLemma);
        ArgumentNullException.ThrowIfNull(lexicon);
        return _graph.Compose(baseLemma, lexicon, WordFormationGraph.SearchBounds.ForLimit(limit));
    }

    internal WordDerivation? TryDeriveUniqueProductiveSingleGenericEdge(
        string surface,
        IRuntimeLexicon lexicon) =>
        _graph.TryDeriveUniqueProductiveSingleGenericEdge(surface, lexicon);

    internal WordReading[] GenerateLexicalReadings(
        string lemma,
        string baseFullTag,
        string ruleId,
        double score,
        Action<WordFormationGraph.GenerationObservation>? observe = null) =>
        _graph.GenerateLexicalReadings(lemma, baseFullTag, ruleId, score, observe);

    internal MorphologyProposal[]? ProposeMorphology(string normalized, IReadOnlySet<string>? observedTags,
        bool baseOnly = false, bool includeSelf = false, CancellationToken cancellationToken = default) =>
        _graph.ProposeMorphology(normalized, observedTags, baseOnly, includeSelf, cancellationToken);

    internal HashSet<(string Form, string Tag)> GenerateLexicalTuples(string lemma, string baseFullTag, string ruleId,
        CancellationToken cancellationToken = default) =>
        _graph.GenerateLexicalTuples(lemma, baseFullTag, ruleId, cancellationToken: cancellationToken);

    internal HashSet<(string Form, string Tag)> GenerateAccentTuples(string lemma, string baseFullTag, string ruleId,
        Action<WordFormationGraph.GenerationObservation> observe, CancellationToken cancellationToken) =>
        _graph.GenerateLexicalTuples(lemma, baseFullTag, ruleId, observe, cancellationToken: cancellationToken);

    internal void ValidateBase(string lemma, string baseFullTag, string ruleId, CancellationToken cancellationToken = default) =>
        _graph.GenerateLexicalTuples(lemma, baseFullTag, ruleId, cancellationToken: cancellationToken);

    internal bool IsValidOrdinaryBase(string sourceLemma, string baseFullTag, string ruleId, CancellationToken cancellationToken = default) =>
        _ordinaryBaseProofs.IsValid(sourceLemma, baseFullTag, ruleId, cancellationToken);

    private static WordFormationAnalyzer Create(
        SourceDocument source,
        string expectedSourceId,
        string identityOwner)
    {
        if (!string.Equals(source.SourceId, expectedSourceId, StringComparison.Ordinal))
            throw new InvalidDataException($"Word-formation source identity does not match the {identityOwner}.");
        return new WordFormationAnalyzer(source);
    }

    internal bool IsAcceptedParadigmTag(string stateId, string ruleId, string? fullTag) =>
        fullTag is not null && _acceptedParadigmTags.Contains(ParadigmTagKey(stateId, ruleId, fullTag));

    internal bool IsAcceptedParadigmBaseTag(string stateId, string ruleId, string? fullTag) =>
        fullTag is not null && _acceptedParadigmBaseTags.Contains(ParadigmTagKey(stateId, ruleId, fullTag));

    internal int? ResolveReviewedExactStressVowelOrdinal(
        string normalizedSurface,
        IReadOnlyList<WordReading> completeReadings)
    {
        if (_reviewedExactStress is null || completeReadings.Count == 0)
            return null;

        int? resolved = null;
        foreach (WordReading reading in completeReadings)
        {
            string? rawFullTag = reading.PartOfSpeech;
            if (rawFullTag is null || WordFormationGraph.TryCreateStateId(rawFullTag) is null ||
                BulgarianTextNormalizer.NormalizeKey(reading.Form) != normalizedSurface)
                return null;
            string key = ReviewedStressKey(
                normalizedSurface,
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
                rawFullTag);
            ReviewedExactStressRowNode? row = _reviewedExactStress.Rows
                .SingleOrDefault(candidate => ReviewedStressKey(candidate) == key);
            if (row is null || resolved is not null && resolved != row.VowelOrdinal)
                return null;
            resolved = row.VowelOrdinal;
        }
        return resolved;
    }

    private static void ValidateSourceJson(JsonElement root)
    {
        const string label = "Word-formation source";
        string schemaVersion = StrictJson.RequireString(root, "schemaVersion", label);
        string[] required =
        [
            "schemaVersion", "sourceId", "morphs", "variantSets", "rules", "applications", "evidence",
            "negativeControls", "typedTransitions", "morphophonologicalTransforms"
        ];
        string[] versionedRequired = schemaVersion switch
        {
            SchemaVersionV1 => [.. required, "paradigmEdges", "reviewedVerbCliticRelations"],
            SchemaVersionV2 => [.. required, "paradigmEdges", "reviewedVerbCliticRelations", "reviewedExactStress"],
            _ => required
        };
        StrictJson.RequireObject(root, label, versionedRequired, schemaVersion == SchemaVersion
            ? ["paradigmEdges", "lexicalCliticProjections", "ordinaryParadigmAliases", "graphAccentProfiles"]
            : ["lexicalCliticProjections", "ordinaryParadigmAliases", "graphAccentProfiles"]);
        GraphAccentProfile.ValidateJson(root);
        StrictJson.RequireString(root, "sourceId", label);
        ValidateObjectArray(root, "morphs", ["id", "kind", "canonicalText", "variantSetId", "reviewState"], ValidateMorphJson);
        ValidateObjectArray(root, "variantSets", ["id", "canonical", "variants"], ValidateVariantSetJson);
        ValidateObjectArray(
            root,
            "rules",
            ["id", "kind", "pattern", "outputPos", "supportCount", "reviewState", "mayGenerateCandidates", "negativeControlIds"],
            ValidateRuleJson,
            ["evidenceCodes"]);
        ValidateApplications(root);
        ValidateObjectArray(root, "evidence", ["id", "kind", "reference", "note"], ValidateEvidenceJson);
        ValidateObjectArray(
            root,
            "negativeControls",
            ["id", "surface", "mustNotSplit", "mustNotMatchRuleId", "reason"],
            ValidateNegativeControlJson);
        ValidateObjectArray(
            root,
            "typedTransitions",
            [
                "id", "kind", "morphId", "ruleId", "stemTransformId",
                "reviewState", "mayGenerateCandidates", "supportCount", "applicationIds", "evidenceIds", "evidenceCodes"
            ],
            ValidateTypedTransitionJson,
            ["fromStateId", "toStateId", "stateBinding", "prefixClass", "compoundBinding"]);
        ValidateObjectArray(
            root,
            "morphophonologicalTransforms",
            ["id", "kind", "fromTail", "toTail", "variantSetId", "reviewState", "evidenceIds"],
            ValidateTransformJson,
            ["minimumInputLength", "inputFinalCharacterClass", "inputPreTailCharacterClass", "excludedInputTail", "lastOccurrence", "exactInput"]);
        if (root.TryGetProperty("paradigmEdges", out _))
        {
            ValidateObjectArray(
                root,
                "paradigmEdges",
                ["id", "stateId", "ruleId", "baseFullTag", "fullTagPrefix", "stemTransformId", "reviewState", "evidenceIds"],
                ValidateParadigmEdgeJson,
                ["forms", "formsFrom", "executionKind"]);
        }
        if (root.TryGetProperty("lexicalCliticProjections", out _))
            ValidateObjectArray(root, "lexicalCliticProjections",
                ["id", "ruleId", "baseFullTag", "parentRuleId", "parentBaseFullTag", "cliticTagPart", "reviewState", "evidenceIds"],
                (item, itemLabel) =>
                {
                    foreach (string field in new[] { "id", "ruleId", "baseFullTag", "parentRuleId", "parentBaseFullTag", "cliticTagPart", "reviewState" })
                        StrictJson.RequireString(item, field, itemLabel);
                    StrictJson.RequireStringArray(item, "evidenceIds", itemLabel);
                });
        if (root.TryGetProperty("ordinaryParadigmAliases", out _))
            ValidateObjectArray(root, "ordinaryParadigmAliases",
                ["id", "ruleId", "baseFullTag", "targetRuleId", "reviewState", "evidenceIds"],
                (item, itemLabel) =>
                {
                    foreach (string field in new[] { "id", "ruleId", "baseFullTag", "targetRuleId", "reviewState" })
                        StrictJson.RequireString(item, field, itemLabel);
                    StrictJson.RequireStringArray(item, "evidenceIds", itemLabel);
                });
        if (schemaVersion is SchemaVersionV1 or SchemaVersionV2)
        {
            JsonElement relations = StrictJson.RequireProperty(root, "reviewedVerbCliticRelations", JsonValueKind.Object, label);
            foreach (JsonElement relation in StrictJson.RequireArray(relations, "items", label))
            {
                JsonElement placement = StrictJson.RequireProperty(relation, "placement", JsonValueKind.Object, label);
                StrictJson.RequireObject(placement, label + ".placement", ["kind", "evidenceId"], ["witness"]);
                if (placement.TryGetProperty("witness", out JsonElement witness))
                {
                    StrictJson.RequireObject(witness, label + ".witness",
                        ["tokens", "verbTokenIndex", "semanticHash", "reviewedBy"], ["verbFullTag", "supplementalSourceUri"]);
                    StrictJson.RequireStringArray(witness, "tokens", label);
                    StrictJson.RequireProperty(witness, "verbTokenIndex", JsonValueKind.Number, label);
                    StrictJson.RequireString(witness, "semanticHash", label);
                    StrictJson.RequireString(witness, "reviewedBy", label);
                    foreach (string optional in new[] { "verbFullTag", "supplementalSourceUri" })
                        if (witness.TryGetProperty(optional, out _)) StrictJson.RequireString(witness, optional, label);
                }
                JsonElement decision = StrictJson.RequireProperty(relation, "decision", JsonValueKind.Object, label);
                if (StrictJson.RequireString(decision, "rowKind", label) == "accepted-reviewed-ambiguous-candidate-decision")
                    StrictJson.RequireStringArray(decision, "rejectedProjectionCandidateIds", label);
                else if (decision.TryGetProperty("rejectedProjectionCandidateIds", out _))
                    throw new InvalidDataException("Only reviewed ambiguous decisions may contain rejected candidates.");
            }
        }
        if (schemaVersion == SchemaVersionV2)
        {
            JsonElement stress = StrictJson.RequireProperty(root, "reviewedExactStress", JsonValueKind.Object, label);
            StrictJson.RequireObject(stress, label + ".reviewedExactStress",
                ["schemaVersion", "sourceId", "evidenceSha256", "rows"]);
            StrictJson.RequireString(stress, "schemaVersion", label);
            StrictJson.RequireString(stress, "sourceId", label);
            StrictJson.RequireString(stress, "evidenceSha256", label);
            ValidateObjectArray(stress, "rows",
                ["semanticHash", "surface", "lemma", "rawFullTag", "vowelOrdinal", "reviewState", "reviewedBy", "reviewedAt", "evidenceIdentity"],
                ValidateReviewedStressRowJson);
        }
        else if (schemaVersion is not (SchemaVersion or SchemaVersionV1))
            throw new InvalidDataException($"Unsupported word-formation schema '{schemaVersion}'.");
    }

    private static void ValidateReviewedStressRowJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "semanticHash", label);
        StrictJson.RequireString(item, "surface", label);
        StrictJson.RequireString(item, "lemma", label);
        StrictJson.RequireString(item, "rawFullTag", label);
        StrictJson.RequireProperty(item, "vowelOrdinal", JsonValueKind.Number, label);
        StrictJson.RequireString(item, "reviewState", label);
        StrictJson.RequireString(item, "reviewedBy", label);
        StrictJson.RequireString(item, "reviewedAt", label);
        StrictJson.RequireString(item, "evidenceIdentity", label);
    }

    private static void ValidateMorphJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "id", label);
        StrictJson.RequireString(item, "kind", label);
        StrictJson.RequireString(item, "canonicalText", label);
        StrictJson.RequireStringOrNull(item, "variantSetId", label);
        StrictJson.RequireString(item, "reviewState", label);
    }

    private static void ValidateVariantSetJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "id", label);
        StrictJson.RequireString(item, "canonical", label);
        StrictJson.RequireStringArray(item, "variants", label);
    }

    private static void ValidateRuleJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "id", label);
        StrictJson.RequireString(item, "kind", label);
        StrictJson.RequireStringArray(item, "pattern", label);
        StrictJson.RequireString(item, "outputPos", label);
        StrictJson.RequireProperty(item, "supportCount", JsonValueKind.Number, label);
        StrictJson.RequireString(item, "reviewState", label);
        StrictJson.RequireProperty(item, "mayGenerateCandidates", JsonValueKind.False, JsonValueKind.True, label);
        StrictJson.RequireStringArray(item, "negativeControlIds", label);
        if (item.TryGetProperty("evidenceCodes", out _))
            StrictJson.RequireStringArray(item, "evidenceCodes", label);
    }

    private static void ValidateEvidenceJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "id", label);
        StrictJson.RequireString(item, "kind", label);
        StrictJson.RequireString(item, "reference", label);
        StrictJson.RequireString(item, "note", label);
    }

    private static void ValidateNegativeControlJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "id", label);
        StrictJson.RequireString(item, "surface", label);
        StrictJson.RequireStringArrayOrNull(item, "mustNotSplit", label);
        StrictJson.RequireStringOrNull(item, "mustNotMatchRuleId", label);
        StrictJson.RequireString(item, "reason", label);
    }

    private static void ValidateTypedTransitionJson(JsonElement item, string label)
    {
        if (item.TryGetProperty("compoundBinding", out JsonElement compound) && compound.ValueKind != JsonValueKind.Null)
        {
            StrictJson.RequireObject(compound, label + ".compoundBinding",
                ["leftBaseFullTag", "rightBaseFullTag", "leftLemmas", "rightLemmas", "rightParadigmRuleId"]);
            foreach (string field in new[] { "leftBaseFullTag", "rightBaseFullTag", "rightParadigmRuleId" })
                StrictJson.RequireString(compound, field, label + ".compoundBinding");
            foreach (string field in new[] { "leftLemmas", "rightLemmas" })
                StrictJson.RequireStringArray(compound, field, label + ".compoundBinding");
        }
        StrictJson.RequireString(item, "id", label);
        bool hasFromState = item.TryGetProperty("fromStateId", out _);
        bool hasToState = item.TryGetProperty("toStateId", out _);
        bool hasStateBinding = item.TryGetProperty("stateBinding", out JsonElement stateBinding);
        if (hasStateBinding)
        {
            if (hasFromState || hasToState)
                throw new InvalidDataException($"{label} must use either exact states or stateBinding, not both.");
            StrictJson.RequireObject(
                stateBinding,
                label + ".stateBinding",
                ["kind", "partOfSpeech"]);
            StrictJson.RequireString(stateBinding, "kind", label + ".stateBinding");
            StrictJson.RequireString(stateBinding, "partOfSpeech", label + ".stateBinding");
        }
        else
        {
            if (!hasFromState || !hasToState)
                throw new InvalidDataException($"{label} exact binding requires fromStateId and toStateId.");
            StrictJson.RequireString(item, "fromStateId", label);
            StrictJson.RequireString(item, "toStateId", label);
        }
        StrictJson.RequireString(item, "kind", label);
        if (item.TryGetProperty("prefixClass", out _))
            StrictJson.RequireString(item, "prefixClass", label);
        StrictJson.RequireStringOrNull(item, "morphId", label);
        StrictJson.RequireStringOrNull(item, "ruleId", label);
        StrictJson.RequireStringOrNull(item, "stemTransformId", label);
        StrictJson.RequireString(item, "reviewState", label);
        StrictJson.RequireProperty(item, "mayGenerateCandidates", JsonValueKind.False, JsonValueKind.True, label);
        StrictJson.RequireProperty(item, "supportCount", JsonValueKind.Number, label);
        StrictJson.RequireStringArray(item, "applicationIds", label);
        StrictJson.RequireStringArray(item, "evidenceIds", label);
        StrictJson.RequireStringArray(item, "evidenceCodes", label);
    }

    private static void ValidateTransformJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "id", label);
        StrictJson.RequireString(item, "kind", label);
        StrictJson.RequireProperty(item, "fromTail", JsonValueKind.String, label);
        StrictJson.RequireProperty(item, "toTail", JsonValueKind.String, label);
        StrictJson.RequireStringOrNull(item, "variantSetId", label);
        StrictJson.RequireString(item, "reviewState", label);
        StrictJson.RequireStringArray(item, "evidenceIds", label);
        bool lastReplacement = item.GetProperty("kind").GetString() == "last_occurrence_replace";
        if (lastReplacement)
        {
            JsonElement last = StrictJson.RequireProperty(item, "lastOccurrence", JsonValueKind.Object, label);
            StrictJson.RequireObject(last, label + ".lastOccurrence", ["from", "to", "minimumIndex", "minimumFollowingLength"]);
            StrictJson.RequireString(last, "from", label);
            StrictJson.RequireString(last, "to", label);
            foreach (string guard in new[] { "minimumIndex", "minimumFollowingLength" })
                if (!StrictJson.RequireProperty(last, guard, JsonValueKind.Number, label).TryGetInt32(out int value) || value is < 0 or > 256)
                    throw new InvalidDataException($"{label}.lastOccurrence.{guard} must be an integer from 0 through 256.");
        }
        else if (item.TryGetProperty("lastOccurrence", out _))
            throw new InvalidDataException($"{label}.lastOccurrence requires last_occurrence_replace.");
        if (item.TryGetProperty("excludedInputTail", out _))
            StrictJson.RequireString(item, "excludedInputTail", label);
        if (item.TryGetProperty("exactInput", out _))
            StrictJson.RequireString(item, "exactInput", label);
        if (item.TryGetProperty("minimumInputLength", out JsonElement minimumInputLength))
        {
            if (minimumInputLength.ValueKind != JsonValueKind.Number ||
                !minimumInputLength.TryGetInt32(out int value) || value is < 1 or > 256)
            {
                throw new InvalidDataException($"{label}.minimumInputLength must be an integer from 1 through 256.");
            }
        }
        if (item.TryGetProperty("inputFinalCharacterClass", out JsonElement inputFinalCharacterClass) &&
            (inputFinalCharacterClass.ValueKind != JsonValueKind.String ||
             !string.Equals(inputFinalCharacterClass.GetString(), "bulgarian_consonant", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"{label}.inputFinalCharacterClass must be the canonical closed value 'bulgarian_consonant'.");
        }
        if (item.TryGetProperty("inputPreTailCharacterClass", out JsonElement inputPreTailCharacterClass) &&
            (inputPreTailCharacterClass.ValueKind != JsonValueKind.String ||
             inputPreTailCharacterClass.GetString() is not
                 ("bulgarian_consonant" or "not_bulgarian_consonant" or "bulgarian_consonant_except_dt")))
        {
            throw new InvalidDataException(
                $"{label}.inputPreTailCharacterClass must be a canonical closed value: 'bulgarian_consonant', 'not_bulgarian_consonant' or 'bulgarian_consonant_except_dt'.");
        }
    }

    private static void ValidateParadigmEdgeJson(JsonElement item, string label)
    {
        StrictJson.RequireString(item, "id", label);
        StrictJson.RequireString(item, "stateId", label);
        StrictJson.RequireString(item, "ruleId", label);
        StrictJson.RequireString(item, "baseFullTag", label);
        StrictJson.RequireString(item, "fullTagPrefix", label);
        StrictJson.RequireString(item, "stemTransformId", label);
        StrictJson.RequireString(item, "reviewState", label);
        StrictJson.RequireStringArray(item, "evidenceIds", label);
        if (item.TryGetProperty("executionKind", out _))
            _ = StrictJson.RequireString(item, "executionKind", label);
        if (item.TryGetProperty("formsFrom", out _))
        {
            StrictJson.RequireString(item, "formsFrom", label);
            if (item.TryGetProperty("forms", out _))
                throw new InvalidDataException($"{label} must use either forms or formsFrom, not both.");
            return;
        }
        int index = 0;
        foreach (JsonElement form in StrictJson.RequireArray(item, "forms", label))
        {
            string formLabel = $"{label}.forms[{index}]";
            StrictJson.RequireObject(form, formLabel, ["source", "ending", "fullTags"]);
            StrictJson.RequireString(form, "source", formLabel);
            StrictJson.RequireProperty(form, "ending", JsonValueKind.String, formLabel);
            StrictJson.RequireStringArray(form, "fullTags", formLabel);
            index++;
        }
    }

    private static void ValidateApplications(JsonElement root)
    {
        int index = 0;
        foreach (JsonElement application in StrictJson.RequireArray(root, "applications", "Word-formation source"))
        {
            string label = $"Word-formation source.applications[{index}]";
            StrictJson.RequireObject(
                application,
                label,
                [
                    "id", "ruleId", "inputs", "outputCanonical", "depth", "reviewState", "transparency",
                    "blockReason", "evidenceIds", "evidenceCodes"
                ]);
            int inputIndex = 0;
            foreach (JsonElement input in StrictJson.RequireArray(application, "inputs", label))
            {
                string inputLabel = $"{label}.inputs[{inputIndex}]";
                StrictJson.RequireObject(input, inputLabel, ["nodeId", "role"]);
                StrictJson.RequireString(input, "nodeId", inputLabel);
                StrictJson.RequireString(input, "role", inputLabel);
                inputIndex++;
            }
            StrictJson.RequireString(application, "id", label);
            StrictJson.RequireStringOrNull(application, "ruleId", label);
            StrictJson.RequireString(application, "outputCanonical", label);
            StrictJson.RequireString(application, "depth", label);
            StrictJson.RequireString(application, "reviewState", label);
            StrictJson.RequireString(application, "transparency", label);
            StrictJson.RequireStringOrNull(application, "blockReason", label);
            StrictJson.RequireStringArray(application, "evidenceIds", label);
            StrictJson.RequireStringArray(application, "evidenceCodes", label);
            index++;
        }
    }

    private static void ValidateObjectArray(
        JsonElement root,
        string propertyName,
        IReadOnlyCollection<string> required,
        Action<JsonElement, string> validate,
        IReadOnlyCollection<string>? optional = null)
    {
        int index = 0;
        foreach (JsonElement item in StrictJson.RequireArray(root, propertyName, "Word-formation source"))
        {
            string label = $"Word-formation source.{propertyName}[{index}]";
            StrictJson.RequireObject(item, label, required, optional);
            validate(item, label);
            index++;
        }
    }

    private static void Validate(SourceDocument source)
    {
        if (source.SchemaVersion is not (SchemaVersion or SchemaVersionV1 or SchemaVersionV2))
            throw new InvalidDataException($"Unsupported word-formation schema '{source.SchemaVersion}'.");
        if ((source.SchemaVersion is SchemaVersionV1 or SchemaVersionV2) !=
            (source.ReviewedVerbCliticRelations is not null))
            throw new InvalidDataException("Word-formation v1/v2 must contain reviewed verb-clitic relations.");
        if ((source.SchemaVersion == SchemaVersionV2) != (source.ReviewedExactStress is not null))
            throw new InvalidDataException("Word-formation v2 must contain reviewed exact stress.");
        if (string.IsNullOrWhiteSpace(source.SourceId))
            throw new InvalidDataException("Word-formation sourceId is required.");
        EnsurePresent(source.Morphs, "morphs");
        EnsurePresent(source.VariantSets, "variantSets");
        EnsurePresent(source.Rules, "rules");
        EnsurePresent(source.Applications, "applications");
        EnsurePresent(source.Evidence, "evidence");
        EnsurePresent(source.NegativeControls, "negativeControls");
        EnsurePresent(source.TypedTransitions, "typedTransitions");
        EnsurePresent(source.MorphophonologicalTransforms, "morphophonologicalTransforms");
        ParadigmEdgeNode[] paradigmEdges = source.ParadigmEdges ?? [];
        EnsureUnique(source.Morphs.Select(item => item.Id), "morph");
        EnsureUnique(source.VariantSets.Select(item => item.Id), "variant set");
        EnsureUnique(source.Rules.Select(item => item.Id), "rule");
        EnsureUnique(source.Applications.Select(item => item.Id), "application");
        EnsureUnique(source.Evidence.Select(item => item.Id), "evidence");
        EnsureUnique(source.NegativeControls.Select(item => item.Id), "negative control");
        EnsureUnique(source.TypedTransitions.Select(item => item.Id), "typed transition");
        EnsureUnique(source.MorphophonologicalTransforms.Select(item => item.Id), "transform");
        EnsureUnique(paradigmEdges.Select(item => item.Id), "paradigm edge");
        if (source.Applications.Length == 0 && source.TypedTransitions.Length == 0 && paradigmEdges.Length == 0)
            throw new InvalidDataException("Word-formation source must contain an application, transition or paradigm edge.");

        Dictionary<string, MorphNode> morphIds = source.Morphs.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, RuleNode> ruleIds = source.Rules.ToDictionary(item => item.Id, StringComparer.Ordinal);
        HashSet<string> applicationIds = source.Applications.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        HashSet<string> evidenceIds = source.Evidence.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        GraphAccentProfile.Validate(source.GraphAccentProfiles, source.TypedTransitions, evidenceIds);
        HashSet<string> controlIds = source.NegativeControls.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, TransformNode> transformIds = source.MorphophonologicalTransforms
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (MorphNode morph in source.Morphs)
        {
            RequireValue(morph.Kind, $"Morph '{morph.Id}' kind",
                "prefix", "root", "interfix", "derivational_suffix", "inflectional_ending", "stem");
            _ = TrustLevel(morph.ReviewState, $"Morph '{morph.Id}'");
            if (BulgarianTextNormalizer.NormalizeKey(morph.CanonicalText).Length == 0)
                throw new InvalidDataException($"Morph '{morph.Id}' must have non-empty canonical text.");
        }
        foreach (VariantSetNode variant in source.VariantSets)
        {
            string canonical = BulgarianTextNormalizer.NormalizeKey(variant.Canonical);
            string[] variants = variant.Variants.Select(BulgarianTextNormalizer.NormalizeKey).ToArray();
            if (canonical.Length == 0 ||
                !string.Equals(canonical, variant.Canonical, StringComparison.Ordinal) ||
                variants.Length == 0 ||
                variants.Any(value => value.Length == 0) ||
                !variants.SequenceEqual(variant.Variants, StringComparer.Ordinal) ||
                variants.Distinct(StringComparer.Ordinal).Count() != variants.Length ||
                variants.Contains(canonical, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Variant set '{variant.Id}' must contain distinct canonical forms.");
            }
        }
        foreach (RuleNode rule in source.Rules)
        {
            RequireValue(rule.Kind, $"Rule '{rule.Id}' kind", "prefix_derivation", "suffix_derivation", "compounding");
            _ = TrustLevel(rule.ReviewState, $"Rule '{rule.Id}'");
            if (rule.SupportCount < 0)
                throw new InvalidDataException($"Rule '{rule.Id}' supportCount must be non-negative.");
            RequireKnown(rule.NegativeControlIds, controlIds, $"Rule '{rule.Id}' negative control");
        }
        foreach (ApplicationNode application in source.Applications)
        {
            int applicationTrust = TrustLevel(application.ReviewState, $"Application '{application.Id}'");
            RequireValue(application.Depth, $"Application '{application.Id}' depth",
                "compounding", "diachronic_candidate", "synchronic_derivational");
            RequireValue(application.Transparency, $"Application '{application.Id}' transparency",
                "transparent", "lexicalized", "unknown");
            string normalizedOutput = BulgarianTextNormalizer.NormalizeKey(application.OutputCanonical);
            if (normalizedOutput.Length == 0 ||
                !string.Equals(normalizedOutput, application.OutputCanonical, StringComparison.Ordinal) ||
                application.Inputs.Length == 0)
            {
                throw new InvalidDataException($"Application '{application.Id}' must have output and inputs.");
            }
            if (application.RuleId is not null && !ruleIds.ContainsKey(application.RuleId))
                throw new InvalidDataException($"Application '{application.Id}' references an unknown rule.");
            int dependencyTrust = application.RuleId is null
                ? applicationTrust
                : TrustLevel(ruleIds[application.RuleId].ReviewState, $"Rule '{application.RuleId}'");
            foreach (InputNode input in application.Inputs)
            {
                RequireValue(input.Role, $"Application '{application.Id}' input role",
                    "base", "compound_left", "compound_right", "interfix", "prefix", "root", "stem", "suffix");
                if (input.NodeId.StartsWith("lemma:", StringComparison.Ordinal))
                {
                    string lemma = BulgarianTextNormalizer.NormalizeKey(input.NodeId["lemma:".Length..]);
                    if (lemma.Length == 0 || !string.Equals(input.NodeId, "lemma:" + lemma, StringComparison.Ordinal))
                        throw new InvalidDataException($"Application '{application.Id}' has a noncanonical lemma input.");
                    continue;
                }
                if (!morphIds.TryGetValue(input.NodeId, out MorphNode? inputMorph))
                    throw new InvalidDataException($"Application '{application.Id}' references an unknown input node.");
                dependencyTrust = Math.Min(
                    dependencyTrust,
                    TrustLevel(inputMorph.ReviewState, $"Morph '{input.NodeId}'"));
            }
            const string surfaceStemPrefix = SurfaceStemEvidencePrefix;
            string[] alignments = application.EvidenceCodes.Where(code => code.StartsWith(surfaceStemPrefix, StringComparison.Ordinal)).ToArray();
            if (alignments.Length > 0)
            {
                string surfaceStem = alignments[0][surfaceStemPrefix.Length..];
                InputNode[] pieces = application.Inputs.Where(input => !input.NodeId.StartsWith("lemma:", StringComparison.Ordinal)).ToArray();
                if (alignments.Length != 1 || application.Transparency != "transparent" || applicationTrust < 2 ||
                    pieces.Length != 2 || pieces[0].Role != "stem" || pieces[1].Role != "suffix" ||
                    morphIds[pieces[0].NodeId].Kind != "stem" || morphIds[pieces[1].NodeId].Kind != "derivational_suffix" ||
                    !BulgarianOrthography.IsWellFormedWord(surfaceStem) || surfaceStem != BulgarianTextNormalizer.NormalizeKey(surfaceStem) ||
                    surfaceStem + morphIds[pieces[1].NodeId].CanonicalText != application.OutputCanonical ||
                    application.EvidenceCodes.Any(code => code.StartsWith("mechanical_split=", StringComparison.Ordinal)))
                    throw new InvalidDataException($"Application '{application.Id}' requires one exact reviewed surface-stem/suffix alignment.");
            }
            if (applicationTrust >= 2 && dependencyTrust == 0)
                throw new InvalidDataException($"Application '{application.Id}' review state exceeds a referenced dependency.");
            RequireKnown(application.EvidenceIds, evidenceIds, $"Application '{application.Id}' evidence");
        }
        if (source.TypedTransitions.Count(transition => transition.CompoundBinding is not null) > 8)
            throw new InvalidDataException("The source exceeds the eight binary relation bound.");
        foreach (TypedTransitionNode transition in source.TypedTransitions)
        {
            int transitionTrust = TrustLevel(transition.ReviewState, $"Typed transition '{transition.Id}'");
            RequireValue(transition.Kind, $"Typed transition '{transition.Id}' kind",
                transition.CompoundBinding is null ? ["prefix", "suffix"] : ["prefix", "suffix", "compound"]);
            if ((transition.Kind == "compound") != (transition.CompoundBinding is not null))
                throw new InvalidDataException($"Typed transition '{transition.Id}' has an incompatible compound binding.");
            if (transition.Kind == "prefix")
            {
                if (transition.PrefixClass is null)
                    throw new InvalidDataException($"Typed prefix transition '{transition.Id}' requires prefixClass.");
                RequireValue(
                    transition.PrefixClass,
                    $"Typed transition '{transition.Id}' prefixClass",
                    "negative", "degree", "intensifier");
            }
            else if (transition.PrefixClass is not null)
            {
                throw new InvalidDataException($"Typed suffix transition '{transition.Id}' must not declare prefixClass.");
            }
            if (transition.StateBinding is null)
            {
                if (transition.FromStateId is null || transition.ToStateId is null)
                    throw new InvalidDataException($"Typed transition '{transition.Id}' exact binding is incomplete.");
            }
            else
            {
                if (transition.FromStateId is not null || transition.ToStateId is not null)
                    throw new InvalidDataException($"Typed transition '{transition.Id}' mixes exact and scoped state bindings.");
                RequireValue(
                    transition.StateBinding.Kind,
                    $"Typed transition '{transition.Id}' state binding kind",
                    "preserve_matching_state");
                RequireValue(
                    transition.StateBinding.PartOfSpeech,
                    $"Typed transition '{transition.Id}' state binding part of speech",
                    "noun", "adjective", "verb", "adverb", "given_name", "family_name", "city_name");
            }
            if (transition.PrefixClass == "degree" &&
                (transition.StateBinding?.Kind != "preserve_matching_state" ||
                 transition.StateBinding.PartOfSpeech is not ("adjective" or "adverb")))
            {
                throw new InvalidDataException(
                    $"Typed degree transition '{transition.Id}' requires a preserve-matching adjective or adverb state binding.");
            }
            if (transition.SupportCount < 0)
                throw new InvalidDataException($"Typed transition '{transition.Id}' supportCount must be non-negative.");
            if (transition.RuleId is null || !ruleIds.TryGetValue(transition.RuleId, out RuleNode? transitionRule) ||
                transition.MorphId is null || !morphIds.TryGetValue(transition.MorphId, out MorphNode? transitionMorph) ||
                transition.StemTransformId is null ||
                !transformIds.TryGetValue(transition.StemTransformId, out TransformNode? transitionTransform))
            {
                throw new InvalidDataException($"Typed transition '{transition.Id}' has an unknown rule, morph, or transform.");
            }
            if (transition.PrefixClass == "degree" && transition.StateBinding?.PartOfSpeech == "adverb" &&
                (transitionTransform.Kind != "identity" ||
                 transitionTransform.ExactInput is not { Length: > 0 } exactInput ||
                 exactInput != BulgarianTextNormalizer.NormalizeKey(exactInput) ||
                 transitionTransform.Id != "stem-transform:identity:exact:" + exactInput ||
                 transition.Id != $"transition|prefix|{transition.MorphId}|preserve:adverb|{transitionTransform.Id}|{transition.RuleId}"))
            {
                throw new InvalidDataException(
                    $"Typed adverb degree transition '{transition.Id}' requires a canonical exact-base identity admission.");
            }
            string expectedRuleKind = transition.Kind == "compound" ? "compounding" : transition.Kind == "prefix" ? "prefix_derivation" : "suffix_derivation";
            if (!string.Equals(transitionRule.Kind, expectedRuleKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Typed transition '{transition.Id}' kind does not match its referenced rule kind.");
            }
            string expectedMorphKind = transition.Kind == "compound" ? "interfix" : transition.Kind == "prefix" ? "prefix" : "derivational_suffix";
            if (!string.Equals(transitionMorph.Kind, expectedMorphKind, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Typed transition '{transition.Id}' morph kind does not match its direction.");
            }
            if (!transitionRule.Pattern.Contains(transition.MorphId, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"Typed transition '{transition.Id}' morph is not present in its referenced rule pattern.");
            }
            string outputPartOfSpeech = transition.StateBinding?.PartOfSpeech ??
                WordFormationGraph.GetStatePartOfSpeech(transition.ToStateId!);
            if (!string.Equals(transitionRule.OutputPos, outputPartOfSpeech, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Typed transition '{transition.Id}' output part of speech does not match its referenced rule output.");
            }
            int dependencyTrust = new[]
            {
                TrustLevel(transitionRule.ReviewState, $"Rule '{transition.RuleId}'"),
                TrustLevel(transitionMorph.ReviewState, $"Morph '{transition.MorphId}'"),
                TrustLevel(transitionTransform.ReviewState, $"Transform '{transition.StemTransformId}'")
            }.Min();
            if (transitionTrust >= 2 && dependencyTrust == 0)
            {
                throw new InvalidDataException(
                    $"Typed transition '{transition.Id}' review state exceeds a referenced dependency.");
            }
            RequireKnown(transition.ApplicationIds, applicationIds, $"Typed transition '{transition.Id}' application");
            RequireKnown(transition.EvidenceIds, evidenceIds, $"Typed transition '{transition.Id}' evidence");
        }
        foreach (TransformNode transform in source.MorphophonologicalTransforms)
        {
            RequireValue(transform.Kind, $"Transform '{transform.Id}' kind",
                "identity", "tail_replace", "tail_delete", "variant_set_normalize", "last_occurrence_replace", "penultimate_delete");
            if (transform.Kind == "penultimate_delete" &&
                (transform.FromTail is not { Length: 1 } || char.IsSurrogate(transform.FromTail[0]) ||
                 transform.FromTail != BulgarianTextNormalizer.NormalizeKey(transform.FromTail) ||
                 transform.ToTail != "" || transform.MinimumInputLength < 2 ||
                 transform.VariantSetId is not null || transform.LastOccurrence is not null ||
                 transform.InputPreTailCharacterClass is not null))
                throw new InvalidDataException($"Transform '{transform.Id}' has invalid penultimate-deletion operands or guards.");
            if (transform.Kind == "last_occurrence_replace")
            {
                LastOccurrenceNode? last = transform.LastOccurrence;
                bool CanonicalCharacter(string? value) => value is { Length: 1 } && !char.IsSurrogate(value[0]) &&
                    value == BulgarianTextNormalizer.NormalizeKey(value);
                if (last is null || !CanonicalCharacter(last.From) || !CanonicalCharacter(last.To) ||
                    last.MinimumIndex is < 0 or > 256 || last.MinimumFollowingLength is < 0 or > 256 ||
                    transform.VariantSetId is not null ||
                    transform.FromTail != BulgarianTextNormalizer.NormalizeKey(transform.FromTail) ||
                    transform.ToTail != BulgarianTextNormalizer.NormalizeKey(transform.ToTail))
                    throw new InvalidDataException($"Transform '{transform.Id}' has invalid last-occurrence operands or guards.");
            }
            else if (transform.LastOccurrence is not null)
                throw new InvalidDataException($"Transform '{transform.Id}' has last-occurrence semantics on another kind.");
            if (transform.InputPreTailCharacterClass is not null &&
                (transform.Kind is not ("tail_replace" or "tail_delete") ||
                 BulgarianTextNormalizer.NormalizeKey(transform.FromTail).Length == 0))
            {
                throw new InvalidDataException(
                    $"Transform '{transform.Id}' input pre-tail character class requires a non-empty tail replacement or deletion.");
            }
            _ = TrustLevel(transform.ReviewState, $"Transform '{transform.Id}'");
            if (transform.ExcludedInputTail is not null &&
                (transform.ExcludedInputTail.Length == 0 ||
                 transform.ExcludedInputTail != BulgarianTextNormalizer.NormalizeKey(transform.ExcludedInputTail)))
                throw new InvalidDataException($"Transform '{transform.Id}' has a noncanonical excluded input tail.");
            if (transform.ExactInput is not null &&
                (transform.ExactInput.Length == 0 || transform.ExactInput != BulgarianTextNormalizer.NormalizeKey(transform.ExactInput)))
                throw new InvalidDataException($"Transform '{transform.Id}' has a noncanonical exact input.");
            RequireKnown(transform.EvidenceIds, evidenceIds, $"Transform '{transform.Id}' evidence");
        }
        var programSizes = paradigmEdges.GroupBy(edge => (edge.BaseFullTag, edge.RuleId))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (ParadigmEdgeNode edge in paradigmEdges)
        {
            int edgeTrust = TrustLevel(edge.ReviewState, $"Paradigm edge '{edge.Id}'");
            if (string.IsNullOrWhiteSpace(edge.RuleId) || string.IsNullOrWhiteSpace(edge.BaseFullTag) ||
                string.IsNullOrWhiteSpace(edge.FullTagPrefix) ||
                !transformIds.TryGetValue(edge.StemTransformId, out TransformNode? edgeTransform) ||
                edge.Forms is null || edge.Forms.Length == 0)
            {
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' has an unknown state, transform, or empty form table.");
            }
            if (!string.Equals(edge.BaseFullTag, edge.BaseFullTag.Trim(), StringComparison.Ordinal))
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' base full tag must be raw-canonical and unpadded.");
            if (!string.Equals(edge.FullTagPrefix, edge.FullTagPrefix.Trim(), StringComparison.Ordinal))
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' full-tag prefix must be raw-canonical and unpadded.");
            if (!string.Equals(WordFormationGraph.TryCreateStateId(edge.BaseFullTag), edge.StateId, StringComparison.Ordinal))
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' base full tag does not match its state.");
            if (!string.Equals(WordFormationGraph.TryCreateStateId(edge.FullTagPrefix), edge.StateId, StringComparison.Ordinal) ||
                !(string.Equals(edge.BaseFullTag, edge.FullTagPrefix, StringComparison.Ordinal) ||
                  edge.BaseFullTag.StartsWith(edge.FullTagPrefix + ",", StringComparison.Ordinal)))
            {
                throw new InvalidDataException(
                    $"Paradigm edge '{edge.Id}' full-tag prefix does not match its base/state.");
            }
            RequireKnown(edge.EvidenceIds, evidenceIds, $"Paradigm edge '{edge.Id}' evidence");
            bool invariant = IsInvariantProgram(edge) && programSizes[(edge.BaseFullTag, edge.RuleId)] == 1;
            bool lexical = IsLexicalParadigm(edge);
            if (edge.ExecutionKind is not null &&
                (edge.ExecutionKind != "lexical" || edgeTransform.ExactInput is null))
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' explicit lexical execution requires an exact-input guard.");
            if (invariant && edgeTransform.Kind != "identity")
                throw new InvalidDataException($"Invariant program '{edge.Id}' requires an identity guard.");
            if (!invariant && edgeTransform.ExactInput is not null &&
                edge.Forms.Any(form => form.Source != "stem" && !(lexical && form.Source == "literal")))
                throw new InvalidDataException($"Exact-input paradigm '{edge.Id}' requires only stem forms.");
            HashSet<string> patterns = new(StringComparer.Ordinal);
            foreach (ParadigmFormNode form in edge.Forms)
            {
                RequireValue(form.Source, $"Paradigm edge '{edge.Id}' form source", "lemma", "stem", "literal");
                string normalizedEnding = BulgarianTextNormalizer.NormalizeKey(form.Ending);
                bool hasEmptyEnding = form.Ending.Length == 0;
                if (form.Source == "literal" && (!lexical || hasEmptyEnding))
                    throw new InvalidDataException($"Paradigm edge '{edge.Id}' literal form requires a lexical program and a non-empty literal.");
                if (!hasEmptyEnding &&
                    (normalizedEnding.Length == 0 ||
                     !string.Equals(normalizedEnding, form.Ending, StringComparison.Ordinal)))
                {
                    throw new InvalidDataException($"Paradigm edge '{edge.Id}' has a noncanonical ending.");
                }
                if (hasEmptyEnding && !invariant &&
                    !(lexical && (form.Source == "lemma" || form.Source == "stem" && edgeTransform.Kind == "identity")) &&
                    !IsSupplementaryLemmaForm(edge, form) &&
                    (!string.Equals(form.Source, "stem", StringComparison.Ordinal) ||
                     !AllowsEmptyStemEnding(edgeTransform)))
                {
                    throw new InvalidDataException(
                        $"Paradigm edge '{edge.Id}' has an unlicensed empty stem ending; ending must be non-empty unless a surface-changing tail transform owns the stem form.");
                }
                if (form.FullTags is null || form.FullTags.Length == 0 ||
                    form.FullTags.Any(string.IsNullOrWhiteSpace) ||
                    form.FullTags.Distinct(StringComparer.Ordinal).Count() != form.FullTags.Length ||
                    !patterns.Add(form.Source + "|" + form.Ending))
                {
                    throw new InvalidDataException($"Paradigm edge '{edge.Id}' has an empty or duplicate form pattern.");
                }
                string fullTagPrefix = edge.FullTagPrefix + ",";
                if (form.FullTags.Any(tag =>
                        !string.Equals(tag, tag.Trim(), StringComparison.Ordinal) ||
                        !string.Equals(WordFormationGraph.TryCreateStateId(tag), edge.StateId, StringComparison.Ordinal) ||
                        !(tag.StartsWith(fullTagPrefix, StringComparison.Ordinal) || (invariant || lexical) && tag == edge.BaseFullTag)))
                {
                    throw new InvalidDataException(
                        $"Paradigm edge '{edge.Id}' has a form full tag that is not canonical or does not match its base/state.");
                }
            }
            int dependencyTrust = TrustLevel(
                edgeTransform.ReviewState,
                $"Transform '{edge.StemTransformId}'");
            if (edgeTrust >= 2 && dependencyTrust == 0)
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' review state exceeds a referenced dependency.");
        }
        foreach (var program in paradigmEdges.GroupBy(edge => (edge.BaseFullTag, edge.RuleId)))
            if (program.Any(IsInvariantProgram) && program.Skip(1).Any() && !program.All(IsLexicalParadigm))
                throw new InvalidDataException("Invariant program must be the sole declaration for its base tag and rule.");
        foreach (TypedTransitionNode transition in source.TypedTransitions)
            if (transition.CompoundBinding is { } compound)
                ValidateCompoundBinding(source, transition, compound,
                    morphIds[transition.MorphId!], transformIds[transition.StemTransformId!], paradigmEdges);
        if (source.ReviewedVerbCliticRelations is not null)
            ValidateReviewedRelations(source.ReviewedVerbCliticRelations, paradigmEdges);
        if (source.ReviewedExactStress is not null)
            ValidateReviewedExactStress(source.ReviewedExactStress);
        LexicalCliticProjection[] projections = source.LexicalCliticProjections ?? [];
        EnsureUnique(projections.Select(projection => projection.Id), "lexical clitic projection");
        HashSet<(string, string)> projectionBindings = [];
        foreach (LexicalCliticProjection projection in projections)
        {
            ParadigmEdgeNode[] parents = paradigmEdges.Where(edge => edge.RuleId == projection.ParentRuleId &&
                edge.BaseFullTag == projection.ParentBaseFullTag).ToArray();
            string? suffix = projection.CliticTagPart switch { "+се" => "se", "+си" => "si", _ => null };
            if (suffix is null || projection.ReviewState != "accepted" ||
                !projection.ParentBaseFullTag.StartsWith("Глагол:", StringComparison.Ordinal) ||
                projection.ParentBaseFullTag.Contains('+') || projection.BaseFullTag != projection.ProjectTag(projection.ParentBaseFullTag) ||
                projection.RuleId != projection.ParentRuleId + ".legacy_clitic_projection_" + suffix ||
                !projectionBindings.Add((projection.BaseFullTag, projection.RuleId)) || parents.Length == 0 ||
                parents.Any(edge => IsInvariantProgram(edge) || edge.Forms.Any(form => form.FullTags.Any(tag => tag.Contains('+'))) || edge.ReviewState != "accepted" ||
                    transformIds[edge.StemTransformId].ReviewState != "accepted") ||
                paradigmEdges.Any(edge => edge.RuleId == projection.RuleId && edge.BaseFullTag == projection.BaseFullTag))
                throw new InvalidDataException("Lexical clitic projection requires an exact approved ordinary verb parent and +се/+си binding.");
            RequireKnown(projection.EvidenceIds, evidenceIds, $"Lexical clitic projection '{projection.Id}' evidence");
        }
        OrdinaryParadigmAlias[] aliases = source.OrdinaryParadigmAliases ?? [];
        EnsureUnique(aliases.Select(alias => alias.Id), "ordinary paradigm alias");
        HashSet<(string, string)> aliasBindings = [];
        foreach (OrdinaryParadigmAlias alias in aliases)
        {
            ParadigmEdgeNode[] targets = paradigmEdges.Where(edge => edge.RuleId == alias.TargetRuleId &&
                edge.BaseFullTag == alias.BaseFullTag).ToArray();
            if (new[] { alias.Id, alias.RuleId, alias.BaseFullTag, alias.TargetRuleId }.Any(value =>
                    string.IsNullOrWhiteSpace(value) || value != value.Trim()) ||
                alias.ReviewState != "accepted" || alias.EvidenceIds.Length == 0 ||
                !aliasBindings.Add((alias.BaseFullTag, alias.RuleId)) || targets.Length == 0 ||
                programSizes.ContainsKey((alias.BaseFullTag, alias.RuleId)) ||
                projectionBindings.Contains((alias.BaseFullTag, alias.RuleId)) ||
                targets.Any(edge => IsInvariantProgram(edge) || IsLexicalParadigm(edge) ||
                    edge.ReviewState != "accepted" || transformIds[edge.StemTransformId].ReviewState != "accepted"))
                throw new InvalidDataException("Ordinary paradigm alias requires a unique binding to an exact approved ordinary program.");
            RequireKnown(alias.EvidenceIds, evidenceIds, $"Ordinary paradigm alias '{alias.Id}' evidence");
        }
    }

    // Share an existing table, not an executable graph path. Expansion happens once
    // at load; the graph receives the same typed, fully validated edge representation.
    internal static SourceDocument ExpandFormPrograms(SourceDocument source)
    {
        ParadigmEdgeNode[] edges = source.ParadigmEdges ?? [];
        if (!edges.Any(edge => edge.FormsFrom is not null))
            return source;
        EnsureUnique(edges.Select(edge => edge.Id), "paradigm edge");
        EnsurePresent(source.MorphophonologicalTransforms, "morphophonologicalTransforms");
        EnsureUnique(source.MorphophonologicalTransforms.Select(transform => transform.Id), "transform");
        Dictionary<string, ParadigmEdgeNode> donors = edges.ToDictionary(edge => edge.Id, StringComparer.Ordinal);
        Dictionary<string, TransformNode> transforms = source.MorphophonologicalTransforms
            .ToDictionary(transform => transform.Id, StringComparer.Ordinal);
        ParadigmEdgeNode[] expanded = new ParadigmEdgeNode[edges.Length];
        for (int index = 0; index < edges.Length; index++)
        {
            ParadigmEdgeNode edge = edges[index];
            expanded[index] = edge;
            if (edge.FormsFrom is null)
                continue;
            if (edge.Forms is not null || !donors.TryGetValue(edge.FormsFrom, out ParadigmEdgeNode? donor) ||
                donor.FormsFrom is not null || donor.Forms is null || donor.Forms.Length == 0 ||
                string.IsNullOrWhiteSpace(donor.FullTagPrefix) || string.IsNullOrWhiteSpace(edge.FullTagPrefix) ||
                !transforms.TryGetValue(donor.StemTransformId, out TransformNode? donorTransform) ||
                !transforms.TryGetValue(edge.StemTransformId, out TransformNode? targetTransform))
            {
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' requires one inline form-program donor.");
            }
            string[] dependencies = [edge.ReviewState, donor.ReviewState,
                donorTransform.ReviewState, targetTransform.ReviewState];
            string effectiveReview = dependencies.MinBy(value => TrustLevel(value, $"Paradigm edge '{edge.Id}'"))!;
            if (TrustLevel(edge.ReviewState, edge.Id) >= 2 && TrustLevel(effectiveReview, edge.Id) == 0)
                throw new InvalidDataException($"Paradigm edge '{edge.Id}' review state exceeds a referenced dependency.");
            string prefix = donor.FullTagPrefix + ",";
            ParadigmFormNode[] forms = new ParadigmFormNode[donor.Forms.Length];
            for (int formIndex = 0; formIndex < forms.Length; formIndex++)
            {
                ParadigmFormNode form = donor.Forms[formIndex];
                if (form.FullTags is null || form.FullTags.Any(tag =>
                        tag is null || !tag.StartsWith(prefix, StringComparison.Ordinal)))
                    throw new InvalidDataException($"Paradigm edge '{donor.Id}' has a form outside its exact full-tag prefix.");
                forms[formIndex] = form with
                {
                    FullTags = form.FullTags.Select(tag => edge.FullTagPrefix + tag[donor.FullTagPrefix.Length..]).ToArray()
                };
            }
            expanded[index] = edge with { Forms = forms, FormsFrom = null, ReviewState = effectiveReview };
        }
        return source with { ParadigmEdges = expanded };
    }

    private static void ValidateReviewedExactStress(ReviewedExactStressSourceNode source)
    {
        if (source.Rows is null || source.Rows.Length == 0 ||
            source.SchemaVersion != ReviewedStressSchemaVersion || source.SourceId != ReviewedStressSourceId)
            throw new InvalidDataException("Reviewed exact-stress source identity is incomplete or unsupported.");
        RequireSha(source.EvidenceSha256, "reviewed exact-stress evidence");

        HashSet<string> seen = new(StringComparer.Ordinal);
        string? previous = null;
        foreach (ReviewedExactStressRowNode row in source.Rows)
        {
            if (row is null || HasMissing(row.SemanticHash, row.Surface, row.Lemma, row.RawFullTag,
                    row.ReviewState, row.ReviewedBy, row.EvidenceIdentity) || row.ReviewedAt == default ||
                row.ReviewedAt.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Reviewed exact-stress row is incomplete.");
            string key = ReviewedStressKey(row);
            if (BulgarianTextNormalizer.NormalizeKey(row.Surface) != row.Surface ||
                BulgarianTextNormalizer.NormalizeKey(row.Lemma) != row.Lemma ||
                row.RawFullTag != row.RawFullTag.Trim() || WordFormationGraph.TryCreateStateId(row.RawFullTag) is null ||
                row.VowelOrdinal < 0 || !TryGetVowelOffset(row.Surface, row.VowelOrdinal, out _))
                throw new InvalidDataException($"Reviewed exact-stress row '{key}' has an invalid canonical tuple or vowel ordinal.");
            if (row.ReviewState != "accepted" || row.ReviewedBy != ReviewedStressReviewer ||
                !ReviewedStressRows.TryGetValue(key, out var expected) ||
                expected.VowelOrdinal != row.VowelOrdinal || expected.Evidence != row.EvidenceIdentity)
                throw new InvalidDataException($"Reviewed exact-stress row '{key}' has unsupported review or evidence.");
            RequireSha(row.SemanticHash, "reviewed exact-stress semantic identity");
            if (row.SemanticHash != ComputeReviewedStressHash(row))
                throw new InvalidDataException($"Reviewed exact-stress row '{key}' has an invalid semantic hash.");
            if (previous is not null && StringComparer.Ordinal.Compare(previous, key) >= 0 || !seen.Add(key))
                throw new InvalidDataException("Reviewed exact-stress rows must be unique and in canonical order.");
            previous = key;
        }
        if (!seen.SetEquals(ReviewedStressRows.Keys))
            throw new InvalidDataException("Reviewed exact-stress source must contain the closed six-row workload.");

        string evidence = string.Join('\n', source.Rows.Select(row => row.EvidenceIdentity)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        string evidenceHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        if (source.EvidenceSha256 != evidenceHash)
            throw new InvalidDataException("Reviewed exact-stress evidence hash is not canonical.");
    }

    private static string ComputeReviewedStressHash(ReviewedExactStressRowNode row)
    {
        StringBuilder canonical = new();
        foreach (string value in new[]
                 {
                     "bglang-reviewed-exact-stress-row.v1", row.Surface, row.Lemma, row.RawFullTag,
                     row.VowelOrdinal.ToString(CultureInfo.InvariantCulture), row.ReviewState, row.ReviewedBy,
                     row.ReviewedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), row.EvidenceIdentity
                 })
        {
            string normalized = value.Normalize(NormalizationForm.FormC);
            canonical.Append(normalized.Length).Append(':').Append(normalized).Append('|');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static string ReviewedStressKey(ReviewedExactStressRowNode row) =>
        ReviewedStressKey(row.Surface, row.Lemma, row.RawFullTag);

    private static string ReviewedStressKey(string surface, string lemma, string rawFullTag) =>
        string.Join('\u001f', surface, lemma, rawFullTag);

    private static bool TryGetVowelOffset(string surface, int ordinal, out int offset)
    {
        offset = -1;
        int current = 0;
        for (int index = 0; index < surface.Length; index++)
        {
            char value = surface[index];
            if (!BulgarianOrthography.IsLowercaseLetter(value))
                return false;
            if (char.ToLowerInvariant(value) is not ('а' or 'ъ' or 'о' or 'у' or 'е' or 'и' or 'ю' or 'я'))
                continue;
            if (current++ == ordinal)
                offset = index;
        }
        return offset >= 0 && current > 1;
    }

    private static void ValidateReviewedRelations(
        ReviewedVerbCliticRelationSourceNode source,
        IReadOnlyList<ParadigmEdgeNode> paradigmEdges)
    {
        if (HasMissing(source.FrameSourceSchemaVersion, source.FrameSourceSha256,
                source.DecisionSourceId, source.DecisionSourceSha256) || source.Items is null)
            throw new InvalidDataException("Reviewed verb-clitic relation source is incomplete.");
        RequireValue(source.FrameSourceSchemaVersion, "Reviewed frame source schema",
            "reviewed-verb-clitic-frame-source.v1");
        RequireValue(source.DecisionSourceId, "Reviewed frame decision source",
            "reviewed-verb-clitic-frame-decisions.v1");
        RequireSha(source.FrameSourceSha256, "reviewed frame source");
        RequireSha(source.DecisionSourceSha256, "reviewed frame decision source");
        if (source.Items is null || source.Items.Length == 0)
            throw new InvalidDataException("Reviewed verb-clitic relations must contain items.");

        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> decisions = new(StringComparer.Ordinal);
        HashSet<string> groups = new(StringComparer.Ordinal);
        int ordinal = 0;
        foreach (ReviewedVerbCliticRelationNode item in source.Items)
        {
            if (item is null || item.Frame is null || item.Decision is null || item.Placement is null ||
                HasMissing(item.DecisionId, item.DecisionHash))
                throw new InvalidDataException("Reviewed verb-clitic relation item is incomplete.");
            ReviewedVerbCliticFrameNode frame = item.Frame;
            ReviewedVerbCliticDecisionNode decision = item.Decision;
            if (frame.Slots is null || frame.EvidenceIds is null || decision.EvidenceIds is null ||
                decision.LinguisticEvidenceIds is null || frame.ReviewedAt is null || decision.ReviewedAt is null ||
                HasMissing(frame.SchemaVersion, frame.RowKind, frame.Id, frame.SemanticHash, frame.VerbLemma,
                    frame.VerbTypeStateId, frame.ProductiveParadigmRuleId, frame.CliticSignature, frame.FrameKind,
                    frame.Requiredness, frame.ReviewState, frame.EvidenceSourceId, frame.EvidenceSourceSha256,
                    frame.HumanReviewEvidenceHash, frame.ReviewedBy, decision.SchemaVersion, decision.RowKind,
                    decision.ObservationId, decision.ObservedSurface, decision.CliticSignature,
                    decision.ProjectionCandidateId, decision.VerbLemma, decision.VerbTypeStateId,
                    decision.ProductiveParadigmRuleId, decision.FrameKind, decision.Requiredness,
                    decision.ReviewState, decision.ReviewedBy, decision.Rationale, item.Placement.Kind,
                    item.Placement.EvidenceId))
                throw new InvalidDataException("Reviewed verb-clitic relation fields are incomplete.");
            if (item.SourceOrdinal <= ordinal)
                throw new InvalidDataException("Reviewed verb-clitic relations must follow canonical source order.");
            ordinal = item.SourceOrdinal;
            RequireValue(frame.SchemaVersion, "Reviewed frame schema", source.FrameSourceSchemaVersion);
            RequireValue(frame.RowKind, "Reviewed frame row kind", "accepted-verb-clitic-frame");
            RequireValue(frame.ReviewState, "Reviewed frame state", "accepted");
            RequireValue(frame.Requiredness, "Reviewed frame requiredness", "allowed");
            RequireValue(decision.SchemaVersion, "Reviewed frame decision schema",
                "reviewed-verb-clitic-frame-decision.v1");
            RequireValue(decision.RowKind, "Reviewed frame decision kind",
                "accepted-single-candidate-decision", "accepted-reviewed-ambiguous-candidate-decision");
            if (decision.RowKind == "accepted-reviewed-ambiguous-candidate-decision")
            {
                string[] rejected = decision.RejectedProjectionCandidateIds ?? [];
                EnsureUnique(rejected, "rejected clitic projection candidate");
                const string prefix = "verb-clitic-base-projection:v1:";
                if (rejected.Length == 0 || rejected.Contains(decision.ProjectionCandidateId, StringComparer.Ordinal) ||
                    rejected.Any(id => !id.StartsWith(prefix, StringComparison.Ordinal) || id.Length != prefix.Length + 24 ||
                        id.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef")))
                    throw new InvalidDataException("Reviewed ambiguous decision has invalid rejected candidates.");
            }
            RequireValue(decision.ReviewState, "Reviewed frame decision state", "accepted");
            RequireValue(item.Placement.Kind, "Reviewed frame placement", "postverbal", "hosted_preverbal");
            RequireSha(frame.SemanticHash, "reviewed frame semantic identity");
            RequireSha(frame.EvidenceSourceSha256, "reviewed frame evidence source");
            RequireSha(frame.HumanReviewEvidenceHash, "reviewed frame human decision");
            RequireSha(item.DecisionHash, "reviewed frame decision");
            ValidateCliticShape(frame);
            string frameHash = ComputeFrameHash(frame);
            string decisionHash = ComputeDecisionHash(decision);
            if (frame.SemanticHash != frameHash || frame.Id != "verb-clitic-frame:v1:" + frameHash[..24] ||
                item.DecisionHash != decisionHash || item.DecisionId != "verb-clitic-frame-review-decision:v1:" + decisionHash[..24] ||
                frame.HumanReviewEvidenceHash != decisionHash || frame.EvidenceSourceId != source.DecisionSourceId ||
                frame.EvidenceSourceSha256 != source.DecisionSourceSha256 ||
                !frame.EvidenceIds.Contains(item.DecisionId, StringComparer.Ordinal) ||
                !decision.EvidenceIds.Contains(decision.ObservationId, StringComparer.Ordinal) ||
                !frame.EvidenceIds.Contains(decision.ProjectionCandidateId, StringComparer.Ordinal) ||
                !item.Placement.EvidenceId.Equals(item.Placement.EvidenceId.Trim(), StringComparison.Ordinal) ||
                !decision.LinguisticEvidenceIds.Contains(item.Placement.EvidenceId, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Reviewed verb-clitic relation identity or source binding is invalid " +
                    $"for '{frame.Id}' (frame {frameHash}, decision {decisionHash}).");
            }
            string[] observedEvidence = frame.EvidenceIds
                .Where(id => id.StartsWith("reviewed-observed-surface:", StringComparison.Ordinal)).ToArray();
            if (observedEvidence.Length != 1 ||
                observedEvidence[0] != "reviewed-observed-surface:" + decision.ObservedSurface ||
                frame.VerbLemma != decision.VerbLemma || frame.VerbTypeStateId != decision.VerbTypeStateId ||
                frame.ProductiveParadigmRuleId != decision.ProductiveParadigmRuleId ||
                frame.CliticSignature != decision.CliticSignature || frame.FrameKind != decision.FrameKind ||
                frame.Requiredness != decision.Requiredness || frame.ReviewState != decision.ReviewState ||
                BulgarianTextNormalizer.NormalizeKey(frame.VerbLemma) != frame.VerbLemma ||
                BulgarianTextNormalizer.NormalizeKey(decision.ObservedSurface) != decision.ObservedSurface)
            {
                throw new InvalidDataException("Reviewed verb-clitic frame and decision do not agree.");
            }
            if (!HasPlacementEvidence(item))
                throw new InvalidDataException("Reviewed verb-clitic placement does not match its evidence token order.");
            string group = decision.ObservedSurface + '\u001f' + frame.CliticSignature;
            if (!ids.Add(frame.Id) || !decisions.Add(item.DecisionId) || !groups.Add(group))
                throw new InvalidDataException("Reviewed verb-clitic relations contain duplicate or ambiguous bindings.");
            string[] acceptedRules = paradigmEdges
                .Where(edge => edge.ReviewState == "accepted" && !IsLexicalParadigm(edge) && edge.StateId == frame.VerbTypeStateId)
                .Select(edge => edge.RuleId).Distinct(StringComparer.Ordinal).ToArray();
            if (acceptedRules.Length != 1 || acceptedRules[0] != frame.ProductiveParadigmRuleId)
                throw new InvalidDataException("Reviewed verb-clitic relation lacks one accepted state-to-rule binding.");
        }
    }

    private static bool HasPlacementEvidence(ReviewedVerbCliticRelationNode relation)
    {
        string[] fields = relation.Placement.EvidenceId.Split(':');
        if (fields.Length < 3 || fields[0] is not ("lexical-evidence" or "linguistic-evidence" or "corpus-kwic" or "corpus-evidence"))
            return false;
        if (relation.Placement.Witness is { } witness)
            return HasPlacementWitness(relation, witness, fields);
        // Existing suffix proofs retain their boundary; variable persons share the runtime slot matcher.
        int count = relation.Frame.Slots.Length;
        bool postverbal = relation.Placement.Kind == "postverbal";
        foreach (string field in fields.Skip(2))
        {
            string[] tokens = field.Split('-');
            if (tokens.Length <= count) continue;
            int verb = postverbal ? tokens.Length - count - 1 : tokens.Length - 1;
            int firstSlot = postverbal ? verb + 1 : verb - count;
            if ((tokens[verb] == relation.Decision.ObservedSurface || tokens[verb] == relation.Frame.VerbLemma) &&
                relation.Frame.Slots.Select((slot, index) =>
                    ReviewedCliticPolicy.MatchesSlot(slot.AllowedCliticIds[0], slot.IsVariable, tokens[firstSlot + index])).All(match => match))
                return true;
        }
        return false;
    }

    private static bool HasPlacementWitness(ReviewedVerbCliticRelationNode relation,
        ReviewedVerbCliticWitnessNode witness, string[] evidenceFields)
    {
        string[] tokens = witness.Tokens;
        if (tokens is null || tokens.Length == 0 || tokens.Any(token => string.IsNullOrEmpty(token) ||
                token != BulgarianTextNormalizer.NormalizeKey(token) || !token.All(char.IsLetter)) ||
            witness.VerbTokenIndex < 0 || witness.VerbTokenIndex >= tokens.Length ||
            witness.ReviewedBy != "codex-assisted-official-source-review" ||
            witness.SemanticHash != ComputePlacementWitnessHash(relation) ||
            witness.VerbFullTag is not null && (string.IsNullOrWhiteSpace(witness.VerbFullTag) ||
                WordFormationGraph.TryCreateStateId(witness.VerbFullTag) != relation.Frame.VerbTypeStateId))
            return false;
        int verb = witness.VerbTokenIndex;
        int count = relation.Frame.Slots.Length;
        bool postverbal = relation.Placement.Kind == "postverbal";
        int firstSlot = postverbal ? verb + 1 : verb - count;
        if (firstSlot < 0 || firstSlot + count > tokens.Length ||
            !postverbal && (firstSlot == 0 || !ReviewedCliticPolicy.IsHostWord(tokens[firstSlot - 1])))
            return false;
        for (int index = 0; index < tokens.Length; index++)
        {
            if (index >= firstSlot && index < firstSlot + count)
            {
                ReviewedCliticSlotNode slot = relation.Frame.Slots[index - firstSlot];
                if (!ReviewedCliticPolicy.MatchesSlot(slot.AllowedCliticIds[0], slot.IsVariable, tokens[index]))
                    return false;
            }
            else if (index != verb && !ReviewedCliticPolicy.IsHostWord(tokens[index]))
                return false; // A witness cannot cross another dictionary alternative's clitics.
        }
        if (witness.SupplementalSourceUri is not null)
        {
            // Reviewed package data, never network-derived runtime authority. No fallback from canonical proof.
            return Uri.TryCreate(witness.SupplementalSourceUri, UriKind.Absolute, out Uri? uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
                witness.SupplementalSourceUri == witness.SupplementalSourceUri.Trim() &&
                witness.VerbFullTag is null && tokens[verb] == relation.Decision.ObservedSurface &&
                postverbal && verb == 0 && tokens.Length == count + 1;
        }
        return evidenceFields.Skip(2).Contains(string.Join('-', tokens), StringComparer.Ordinal) &&
            (witness.VerbFullTag is not null || tokens[verb] == relation.Decision.ObservedSurface ||
                tokens[verb] == relation.Frame.VerbLemma);
    }

    internal static string ComputePlacementWitnessHash(ReviewedVerbCliticRelationNode relation)
    {
        ReviewedVerbCliticWitnessNode witness = relation.Placement.Witness!;
        string[] fields = ["reviewed-verb-clitic-placement-witness.v1", relation.DecisionHash,
            relation.Placement.Kind, relation.Placement.EvidenceId, witness.ReviewedBy,
            witness.VerbTokenIndex.ToString(CultureInfo.InvariantCulture), witness.VerbFullTag ?? string.Empty,
            witness.SupplementalSourceUri ?? string.Empty, witness.Tokens.Length.ToString(CultureInfo.InvariantCulture),
            .. witness.Tokens];
        StringBuilder framed = new();
        foreach (string field in fields)
            framed.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('|');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(framed.ToString())));
    }

    private static void ValidateCliticShape(ReviewedVerbCliticFrameNode frame)
    {
        string[] parts = frame.CliticSignature.StartsWith('+')
            ? frame.CliticSignature[1..].Split('_', StringSplitOptions.None)
            : [];
        if (parts.Length != frame.Slots.Length || parts.Length is < 1 or > 2)
            throw new InvalidDataException("Reviewed verb-clitic signature does not align with its slots.");
        for (int index = 0; index < parts.Length; index++)
        {
            string expected = parts[index] switch
            {
                "ми" => "short-dative-clitic",
                "ме" => "short-accusative-clitic",
                "се" => "reflexive-accusative-se",
                _ => string.Empty
            };
            ReviewedCliticSlotNode? slot = frame.Slots[index];
            string role = index == 0 ? "primary" : "reflexive";
            bool variable = parts[index] is "ми" or "ме";
            if (expected.Length == 0 || slot is null || slot.Role != role || slot.IsVariable != variable ||
                slot.AllowedCliticIds is null || !slot.AllowedCliticIds.SequenceEqual([expected], StringComparer.Ordinal))
                throw new InvalidDataException("Reviewed verb-clitic signature contains an unsupported slot.");
        }
        bool frameMatches = frame.FrameKind == "ShortDativeCliticFrame" && parts.SequenceEqual(["ми"]) ||
                            frame.FrameKind == "ShortAccusativeCliticFrame" && parts.SequenceEqual(["ме"]) ||
                            frame.FrameKind == "DativeExperiencerSe" && parts.SequenceEqual(["ми", "се"]) ||
                            frame.FrameKind == "AmbiguousOrNeedsSyntax" && parts.SequenceEqual(["се"]);
        if (!frameMatches)
            throw new InvalidDataException("Reviewed verb-clitic frame kind is incompatible with its signature.");
    }

    internal static string ComputeFrameHash(ReviewedVerbCliticFrameNode frame)
    {
        DateTimeOffset reviewedAt = frame.ReviewedAt ?? throw new InvalidDataException("Reviewed frame date is missing.");
        StringBuilder canonical = new();
        void Add(string value)
        {
            string normalized = value.Normalize(NormalizationForm.FormC);
            canonical.Append(normalized.Length).Append(':').Append(normalized).Append('|');
        }
        void Count(int value) => Add(value.ToString(CultureInfo.InvariantCulture));
        Add(frame.SchemaVersion); Add(frame.RowKind); Add(frame.VerbLemma); Add(frame.VerbTypeStateId);
        Add(frame.ProductiveParadigmRuleId); Add(frame.CliticSignature); Add(frame.FrameKind); Count(frame.Slots.Length);
        foreach (ReviewedCliticSlotNode slot in frame.Slots)
        {
            Add(slot.Role); Add(slot.IsVariable == true ? "1" : "0"); Count(slot.AllowedCliticIds.Length);
            foreach (string id in slot.AllowedCliticIds) Add(id);
        }
        Add(frame.Requiredness); Add(frame.ReviewState); Add(frame.EvidenceSourceId); Add(frame.EvidenceSourceSha256);
        Add(frame.HumanReviewEvidenceHash); Add(frame.ReviewedBy);
        Add(reviewedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Count(frame.EvidenceIds.Length); foreach (string id in frame.EvidenceIds) Add(id); Add(frame.Note ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    internal static string ComputeDecisionHash(ReviewedVerbCliticDecisionNode decision) =>
        Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(decision, DecisionJsonOptions)));

    internal static bool IsInvariantProgram(ParadigmEdgeNode edge) =>
        edge.Forms is [{ Source: "lemma", Ending: "", FullTags: var tags }] && tags is not null &&
        (edge.FullTagPrefix == edge.BaseFullTag
            ? tags is [var tag] && tag == edge.BaseFullTag
            : tags.Length > 1 && tags.Contains(edge.BaseFullTag, StringComparer.Ordinal));

    internal static bool IsLexicalParadigm(ParadigmEdgeNode edge) =>
        edge.ExecutionKind == "lexical" ||
        WordFormationGraph.StatePartOfSpeech(edge.StateId) is "numeral" or "personal_pronoun" or "pronoun";

    internal static bool IsSupplementaryLemmaForm(ParadigmEdgeNode edge, ParadigmFormNode form) =>
        WordFormationGraph.StatePartOfSpeech(edge.StateId) == "noun" && form.Source == "lemma" && form.Ending == "" &&
        form.FullTags is { Length: > 0 } && !form.FullTags.Contains(edge.BaseFullTag, StringComparer.Ordinal);

    private static bool AllowsEmptyStemEnding(TransformNode transform)
    {
        string from = BulgarianTextNormalizer.NormalizeKey(transform.FromTail);
        string to = BulgarianTextNormalizer.NormalizeKey(transform.ToTail);
        return transform.Kind is "tail_replace" or "tail_delete" &&
               !string.Equals(from, to, StringComparison.Ordinal);
    }

    private static int TrustLevel(string value, string label) => value switch
    {
        "blocked" => 0,
        "proposed" or "unreviewed" => 1,
        "reviewed" => 2,
        "accepted" => 3,
        _ => throw new InvalidDataException($"{label} has unsupported review state '{value}'.")
    };

    private static void RequireValue(string value, string label, params string[] allowed)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new InvalidDataException($"{label} has unsupported value '{value}'.");
    }

    private static void RequireSha(string value, string label)
    {
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException($"{label} must be lowercase SHA-256.");
    }

    private static bool HasMissing(params string?[] values) => values.Any(string.IsNullOrWhiteSpace);

    private static string ParadigmTagKey(string stateId, string ruleId, string fullTag) =>
        string.Join('\u001f', stateId, ruleId, fullTag);

    private static void EnsurePresent<T>(T[]? values, string name)
    {
        if (values is null)
            throw new InvalidDataException($"Word-formation {name} must be an array.");
    }

    private static void EnsureUnique(IEnumerable<string> ids, string kind)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string id in ids)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                throw new InvalidDataException($"Word-formation {kind} ids must be non-empty and unique.");
        }
    }

    private static void RequireKnown(IEnumerable<string> ids, HashSet<string> known, string label)
    {
        foreach (string id in ids)
        {
            if (!known.Contains(id))
                throw new InvalidDataException($"{label} '{id}' is unknown.");
        }
    }

    internal sealed record SourceDocument(
        string SchemaVersion,
        string SourceId,
        MorphNode[] Morphs,
        VariantSetNode[] VariantSets,
        RuleNode[] Rules,
        ApplicationNode[] Applications,
        EvidenceNode[] Evidence,
        NegativeControlNode[] NegativeControls,
        TypedTransitionNode[] TypedTransitions,
        TransformNode[] MorphophonologicalTransforms,
        ParadigmEdgeNode[]? ParadigmEdges,
        ReviewedVerbCliticRelationSourceNode? ReviewedVerbCliticRelations,
        ReviewedExactStressSourceNode? ReviewedExactStress,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LexicalCliticProjection[]? LexicalCliticProjections = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OrdinaryParadigmAlias[]? OrdinaryParadigmAliases = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GraphAccentProfile[]? GraphAccentProfiles = null);

    internal sealed record MorphNode(
        string Id,
        string Kind,
        string CanonicalText,
        string? VariantSetId,
        string ReviewState);

    internal sealed record VariantSetNode(string Id, string Canonical, string[] Variants);

    internal sealed record RuleNode(
        string Id,
        string Kind,
        string[] Pattern,
        string OutputPos,
        int SupportCount,
        string ReviewState,
        bool MayGenerateCandidates,
        string[] NegativeControlIds,
        string[]? EvidenceCodes);

    internal sealed record InputNode(string NodeId, string Role);

    internal sealed record ApplicationNode(
        string Id,
        string? RuleId,
        InputNode[] Inputs,
        string OutputCanonical,
        string Depth,
        string ReviewState,
        string Transparency,
        string? BlockReason,
        string[] EvidenceIds,
        string[] EvidenceCodes);

    internal sealed record EvidenceNode(string Id, string Kind, string Reference, string Note);

    internal sealed record NegativeControlNode(
        string Id,
        string Surface,
        string[]? MustNotSplit,
        string? MustNotMatchRuleId,
        string Reason);

    private static void ValidateCompoundBinding(SourceDocument source, TypedTransitionNode transition,
        CompoundBindingNode binding, MorphNode morph, TransformNode transform, ParadigmEdgeNode[] programs)
    {
        const string state = "state:adjective:76", tag = "Прилагателно:76,Мъжки", program = "productive.adjective.type76.v0";
        if (source.SchemaVersion != SchemaVersionV2 || transition.StateBinding is not null ||
            transition.FromStateId != state || transition.ToStateId != state || transition.ApplicationIds.Length != 0 ||
            binding.LeftBaseFullTag != tag || binding.RightBaseFullTag != tag || binding.RightParadigmRuleId != program ||
            morph.Kind != "interfix" || morph.CanonicalText != "о-" || morph.VariantSetId is not null ||
            transform.Kind != "identity" || transform.FromTail.Length != 0 || transform.ToTail.Length != 0 ||
            transform.MinimumInputLength is > 0 || transform.InputFinalCharacterClass is not null ||
            transform.InputPreTailCharacterClass is not null || transform.ExcludedInputTail is not null ||
            !programs.Any(row => row.StateId == state && row.BaseFullTag == tag && row.RuleId == program))
            throw new InvalidDataException($"Typed transition '{transition.Id}' has an unsupported binary program, state, or operation.");
        foreach (string[] members in new[] { binding.LeftLemmas, binding.RightLemmas })
            if (members is null || members.Length is < 1 or > 32 || members.Distinct(StringComparer.Ordinal).Count() != members.Length ||
                members.Any(member => member is null || member.Length is < 3 or > 64 ||
                    member != BulgarianTextNormalizer.NormalizeKey(member) ||
                    member.Any(character => !BulgarianOrthography.IsLowercaseLetter(character) && character != 'ѝ')))
                throw new InvalidDataException($"Typed transition '{transition.Id}' has invalid binary members.");
    }

    internal sealed record CompoundBindingNode(string LeftBaseFullTag, string RightBaseFullTag,
        string[] LeftLemmas, string[] RightLemmas, string RightParadigmRuleId);

    internal sealed record TypedTransitionNode(
        string Id,
        string? FromStateId,
        string? ToStateId,
        string Kind,
        string? PrefixClass,
        string? MorphId,
        string? RuleId,
        string? StemTransformId,
        string ReviewState,
        bool MayGenerateCandidates,
        int SupportCount,
        string[] ApplicationIds,
        string[] EvidenceIds,
        string[] EvidenceCodes,
        StateBindingNode? StateBinding)
    {
        public CompoundBindingNode? CompoundBinding { get; init; }
    }

    internal sealed record StateBindingNode(string Kind, string PartOfSpeech);

    internal sealed record TransformNode(
        string Id,
        string Kind,
        string FromTail,
        string ToTail,
        string? VariantSetId,
        string ReviewState,
        string[] EvidenceIds,
        int MinimumInputLength,
        string? InputFinalCharacterClass,
        string? InputPreTailCharacterClass,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExcludedInputTail = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LastOccurrenceNode? LastOccurrence = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExactInput = null);

    internal sealed record LastOccurrenceNode(string From, string To, int MinimumIndex, int MinimumFollowingLength);

    internal sealed record ParadigmEdgeNode(
        string Id,
        string StateId,
        string RuleId,
        string BaseFullTag,
        string FullTagPrefix,
        string StemTransformId,
        string ReviewState,
        string[] EvidenceIds,
        ParadigmFormNode[] Forms,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FormsFrom = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExecutionKind = null);

    internal sealed record ParadigmFormNode(
        string Source,
        string Ending,
        string[] FullTags);

    internal sealed record ReviewedVerbCliticRelationSourceNode(
        string FrameSourceSchemaVersion,
        string FrameSourceSha256,
        string DecisionSourceId,
        string DecisionSourceSha256,
        ReviewedVerbCliticRelationNode[] Items);

    internal sealed record ReviewedVerbCliticRelationNode(
        int SourceOrdinal,
        ReviewedVerbCliticFrameNode Frame,
        string DecisionId,
        string DecisionHash,
        ReviewedVerbCliticDecisionNode Decision,
        ReviewedVerbCliticPlacementNode Placement);

    internal sealed record ReviewedVerbCliticFrameNode(
        string SchemaVersion,
        string RowKind,
        string Id,
        string SemanticHash,
        string VerbLemma,
        string VerbTypeStateId,
        string ProductiveParadigmRuleId,
        string CliticSignature,
        string FrameKind,
        ReviewedCliticSlotNode[] Slots,
        string Requiredness,
        string ReviewState,
        string EvidenceSourceId,
        string EvidenceSourceSha256,
        string HumanReviewEvidenceHash,
        string ReviewedBy,
        DateTimeOffset? ReviewedAt,
        string[] EvidenceIds,
        string? Note);

    internal sealed record ReviewedCliticSlotNode(string Role, string[] AllowedCliticIds, bool? IsVariable);

    internal sealed record ReviewedVerbCliticDecisionNode(
        string SchemaVersion,
        string RowKind,
        string ObservationId,
        string ObservedSurface,
        string CliticSignature,
        string ProjectionCandidateId,
        string VerbLemma,
        string VerbTypeStateId,
        string ProductiveParadigmRuleId,
        string FrameKind,
        string Requiredness,
        string ReviewState,
        string ReviewedBy,
        DateTimeOffset? ReviewedAt,
        string[] EvidenceIds,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? RejectedProjectionCandidateIds,
        string[] LinguisticEvidenceIds,
        string Rationale);

    internal sealed record ReviewedVerbCliticPlacementNode(string Kind, string EvidenceId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ReviewedVerbCliticWitnessNode? Witness = null);

    internal sealed record ReviewedVerbCliticWitnessNode(string[] Tokens, int VerbTokenIndex,
        string SemanticHash, string ReviewedBy,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerbFullTag = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SupplementalSourceUri = null);

    internal sealed record ReviewedExactStressSourceNode(
        string SchemaVersion,
        string SourceId,
        string EvidenceSha256,
        ReviewedExactStressRowNode[] Rows);

    internal sealed record ReviewedExactStressRowNode(
        string SemanticHash,
        string Surface,
        string Lemma,
        string RawFullTag,
        int VowelOrdinal,
        string ReviewState,
        string ReviewedBy,
        DateTimeOffset ReviewedAt,
        string EvidenceIdentity);
}
