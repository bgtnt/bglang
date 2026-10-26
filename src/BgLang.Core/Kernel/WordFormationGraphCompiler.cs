using Transform = BgLang.Core.GraphSurfaceOperations.Transform;
using StateBinding = BgLang.Core.GraphState.Binding;
using static BgLang.Core.WordFormationGraph;

namespace BgLang.Core;

/// <summary>Compiles validated source into the runtime graph without retaining source documents.</summary>
internal static class WordFormationGraphCompiler
{
    private static readonly StructuralDecisionSpec[] StructuralDecisionSpecs = CreateStructuralDecisionSpecs();

    internal static WordFormationGraph Compile(WordFormationAnalyzer.SourceDocument source)
    {
        Dictionary<string, WordFormationAnalyzer.MorphNode> sourceMorphs = source.Morphs
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, WordFormationAnalyzer.RuleNode> rules = source.Rules
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, WordFormationAnalyzer.VariantSetNode> variants = source.VariantSets
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, WordFormationAnalyzer.NegativeControlNode> controls = source.NegativeControls
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        Morph[] morphs = source.Morphs.Select(CompileMorph).ToArray();
        Dictionary<string, int> morphIndex = morphs
            .Select((item, index) => (item.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        Transform[] transforms = source.MorphophonologicalTransforms
            .Select(item => CompileTransform(item, variants))
            .ToArray();
        Dictionary<string, int> transformIndex = transforms
            .Select((item, index) => (item.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        string[] literals = (source.ParadigmEdges ?? [])
            .SelectMany(edge => edge.Forms)
            .Select(form => form.Ending)
            .Where(ending => ending.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, int> literalIndex = literals
            .Select((value, index) => (value, index))
            .ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);

        string[] states = source.TypedTransitions
            .Where(item => item.StateBinding is null)
            .SelectMany(item => new[] { item.FromStateId!, item.ToStateId! })
            .Concat((source.ParadigmEdges ?? []).Select(item => item.StateId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string state in states)
            ValidateStateId(state);

        List<InputSlot> slots = [];
        List<SurfaceOperation> operations = [];
        List<Edge> edges = [];
        Dictionary<int, WordFormationAnalyzer.CompoundBindingNode>? binaryBindings = null;
        List<(int Edge, StructuralDecision Decision)>? structuralDecisions = null;
        foreach (WordFormationAnalyzer.TypedTransitionNode row in source.TypedTransitions)
        {
            WordFormationAnalyzer.RuleNode rule = rules[row.RuleId!];
            WordFormationAnalyzer.MorphNode sourceMorph = sourceMorphs[row.MorphId!];
            int transformSlot = transformIndex[row.StemTransformId!];
            WordFormationAnalyzer.TransformNode sourceTransform = source.MorphophonologicalTransforms[transformSlot];
            Transform transform = transforms[transformSlot];
            EdgeKind kind = ParseTransitionKind(row.Kind, sourceMorph.Kind, row.Id);
            PrefixClass prefixClass = kind == EdgeKind.Prefix
                ? ParsePrefixClass(row.PrefixClass!, row.Id)
                : PrefixClass.None;
            StateBinding stateBinding = row.StateBinding is null
                ? StateBinding.Exact(row.FromStateId!, row.ToStateId!)
                : StateBinding.Preserve(row.StateBinding.PartOfSpeech);
            int slotOffset = slots.Count;
            slots.Add(new InputSlot(
                row.CompoundBinding is null ? "base" : "left",
                row.FromStateId ?? "part_of_speech:" + row.StateBinding!.PartOfSpeech,
                true));
            if (row.CompoundBinding is not null)
                slots.Add(new InputSlot("right", row.ToStateId!, true));
            int operationOffset = operations.Count;
            operations.Add(new SurfaceOperation(OperationKind.Transform, transformSlot));
            operations.Add(new SurfaceOperation(
                kind == EdgeKind.Prefix ? OperationKind.PrependMorph : OperationKind.AppendMorph,
                morphIndex[row.MorphId!]));
            string[] negativeIds = rule.NegativeControlIds
                .Concat(controls.Values
                    .Where(item => string.Equals(item.MustNotMatchRuleId, rule.Id, StringComparison.Ordinal))
                    .Select(item => item.Id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            string[] blocked = negativeIds
                .Select(id => BulgarianTextNormalizer.NormalizeKey(controls[id].Surface))
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            ReviewTrust trust = LeastTrust(
                ParseTrust(row.ReviewState),
                ParseTrust(rule.ReviewState),
                ParseTrust(sourceMorph.ReviewState),
                transform.Trust);
            StructuralDecision? structuralDecision = TryCompileStructuralDecision(
                row,
                rule,
                sourceMorph,
                sourceTransform,
                transform,
                trust);
            if (structuralDecision is { } decision)
                (structuralDecisions ??= []).Add((edges.Count, decision));
            IEnumerable<string> stateEvidence = row.StateBinding is null
                ? ["from_state=" + row.FromStateId, "to_state=" + row.ToStateId]
                : [
                    "state_binding=preserve_matching_state",
                    "part_of_speech=" + row.StateBinding.PartOfSpeech
                ];
            string[] evidence = row.EvidenceIds
                .Concat(row.EvidenceCodes)
                .Concat(rule.EvidenceCodes ?? [])
                .Append("transition=" + row.Id)
                .Concat(stateEvidence)
                .Append("stem_transform=" + transform.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (row.CompoundBinding is { } binding)
                (binaryBindings ??= []).Add(edges.Count, binding with
                {
                    LeftLemmas = binding.LeftLemmas.Order(StringComparer.Ordinal).ToArray(),
                    RightLemmas = binding.RightLemmas.Order(StringComparer.Ordinal).ToArray()
                });
            edges.Add(new Edge(
                row.Id,
                kind,
                EdgeOrigin.Generic,
                prefixClass,
                stateBinding,
                slotOffset,
                row.CompoundBinding is null ? 1 : 2,
                operationOffset,
                2,
                row.RuleId,
                null,
                null,
                null,
                trust,
                row.MayGenerateCandidates && rule.MayGenerateCandidates &&
                (prefixClass != PrefixClass.Degree || row.StateBinding?.PartOfSpeech != "adverb" ||
                 structuralDecision is not null),
                row.SupportCount,
                row.ApplicationIds,
                blocked,
                evidence,
                null,
                null));
        }

        foreach (WordFormationAnalyzer.ApplicationNode application in source.Applications)
        {
            WordFormationAnalyzer.RuleNode? rule = application.RuleId is null ? null : rules[application.RuleId];
            bool hasBlockReason = !string.IsNullOrWhiteSpace(application.BlockReason);
            if (application.Transparency == "lexicalized" &&
                (application.ReviewState != "blocked" || !hasBlockReason))
            {
                throw new InvalidDataException(
                    $"Application '{application.Id}' lexicalized decision requires blocked review state and blockReason.");
            }
            if (application.ReviewState == "blocked" && !hasBlockReason)
                throw new InvalidDataException($"Application '{application.Id}' blocked review state requires blockReason.");
            if (rule?.MayGenerateCandidates == true && !hasBlockReason &&
                (application.ReviewState != "accepted" || application.Transparency != "transparent"))
            {
                throw new InvalidDataException(
                    $"Application '{application.Id}' generative rule requires an accepted transparent unblocked application.");
            }
            int slotOffset = slots.Count;
            slots.AddRange(application.Inputs.Select(item => new InputSlot(
                item.Role,
                item.NodeId,
                item.NodeId.StartsWith("lemma:", StringComparison.Ordinal))));
            int operationOffset = operations.Count;
            string[] negativeIds = rule is null
                ? []
                : rule.NegativeControlIds
                    .Concat(controls.Values
                        .Where(item => string.Equals(item.MustNotMatchRuleId, rule.Id, StringComparison.Ordinal))
                        .Select(item => item.Id))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            string[] blocked = negativeIds
                .Select(id => BulgarianTextNormalizer.NormalizeKey(controls[id].Surface))
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            ReviewTrust[] inputTrust = application.Inputs
                .Where(item => !item.NodeId.StartsWith("lemma:", StringComparison.Ordinal))
                .Select(item => morphs[morphIndex[item.NodeId]].Trust)
                .ToArray();
            ReviewTrust trust = LeastTrust(
                [
                    ParseTrust(application.ReviewState),
                    rule is null ? ReviewTrust.Reviewed : ParseTrust(rule.ReviewState),
                    .. inputTrust
                ]);
            string[] surfaces = GetApplicationSurfaces(application).Distinct(StringComparer.Ordinal).ToArray();
            string[] evidence = application.EvidenceIds
                .Concat(application.EvidenceCodes)
                .Append("application=" + application.Id)
                .Concat(hasBlockReason ? ["block_reason=" + application.BlockReason] : [])
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            edges.Add(new Edge(
                "application|" + application.Id,
                ParseApplicationKind(rule?.Kind, application.Depth),
                EdgeOrigin.Application,
                PrefixClass.None,
                default,
                slotOffset,
                application.Inputs.Length,
                operationOffset,
                0,
                application.RuleId,
                application.Id,
                surfaces,
                new ApplicationDecision(application.Transparency, application.BlockReason, rule?.OutputPos)
                {
                    SurfaceStem = application.EvidenceCodes.FirstOrDefault(code => code.StartsWith(
                        WordFormationAnalyzer.SurfaceStemEvidencePrefix, StringComparison.Ordinal))?[WordFormationAnalyzer.SurfaceStemEvidencePrefix.Length..]
                },
                trust,
                trust == ReviewTrust.Accepted &&
                    application.Transparency == "transparent" &&
                    rule?.MayGenerateCandidates == true && !hasBlockReason,
                rule?.SupportCount ?? 0,
                [application.Id],
                blocked,
                evidence,
                null,
                null));
        }

        Dictionary<(string State, string BaseTag, string Rule, ParadigmProgram.Kind Kind), List<ParadigmProgram.Cell>> additionalCells = [];
        var programSizes = (source.ParadigmEdges ?? []).GroupBy(row => (row.BaseFullTag, row.RuleId))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (WordFormationAnalyzer.ParadigmEdgeNode row in source.ParadigmEdges ?? [])
        {
            int stemTransform = transformIndex[row.StemTransformId];
            bool lexical = WordFormationAnalyzer.IsLexicalParadigm(row);
            bool invariant = WordFormationAnalyzer.IsInvariantProgram(row) && programSizes[(row.BaseFullTag, row.RuleId)] == 1;
            if (invariant)
            {
                ReviewTrust trust = LeastTrust(ParseTrust(row.ReviewState), transforms[stemTransform].Trust);
                additionalCells.Add((row.StateId, row.BaseFullTag, row.RuleId, ParadigmProgram.Kind.Invariant),
                    [new(-1, stemTransform, 0, 0, false, trust == ReviewTrust.Accepted,
                        trust >= ReviewTrust.Reviewed, row.Forms[0].FullTags)]);
                continue;
            }
            if (transforms[stemTransform].ExactInput is not null &&
                row.Forms.Any(form => form.Source != "stem" && !(lexical && form.Source == "literal")))
                throw new InvalidDataException($"Exact-input paradigm '{row.Id}' requires only stem forms.");
            string paradigmEvidence = "paradigm_edge=" + row.Id;
            string baseTagEvidence = "base_full_tag=" + row.BaseFullTag;
            string tagPrefixEvidence = "full_tag_prefix=" + row.FullTagPrefix;
            foreach (WordFormationAnalyzer.ParadigmFormNode form in row.Forms)
            {
                int operationOffset = operations.Count;
                if (form.Source == "stem")
                    operations.Add(new SurfaceOperation(OperationKind.Transform, stemTransform));
                if (form.Source == "literal")
                    operations.Add(new SurfaceOperation(OperationKind.EmitLiteral, literalIndex[form.Ending]));
                else if (form.Ending.Length > 0)
                    operations.Add(new SurfaceOperation(OperationKind.AppendLiteral, literalIndex[form.Ending]));
                ReviewTrust trust = LeastTrust(
                    ParseTrust(row.ReviewState),
                    transforms[stemTransform].Trust);
                if (lexical || WordFormationAnalyzer.IsSupplementaryLemmaForm(row, form))
                {
                    var key = (row.StateId, row.BaseFullTag, row.RuleId,
                        lexical ? ParadigmProgram.Kind.Lexical : ParadigmProgram.Kind.Inflecting);
                    if (!additionalCells.TryGetValue(key, out List<ParadigmProgram.Cell>? cells))
                        additionalCells.Add(key, cells = []);
                    cells.Add(new(-1, stemTransform, operationOffset, operations.Count - operationOffset,
                        form.Source == "stem", trust == ReviewTrust.Accepted, trust >= ReviewTrust.Reviewed, form.FullTags));
                    continue;
                }
                string[] evidence = row.EvidenceIds
                    .Append(paradigmEvidence)
                    .Append(baseTagEvidence)
                    .Append(tagPrefixEvidence)
                    .Concat(form.FullTags.Order(StringComparer.Ordinal).Select(tag => "full_tag=" + tag))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                edges.Add(new Edge(
                    row.Id + "|source:" + form.Source + "|ending:" + form.Ending,
                    EdgeKind.Paradigm,
                    EdgeOrigin.Paradigm,
                    PrefixClass.None,
                    StateBinding.Exact(row.StateId, row.StateId),
                    slots.Count,
                    0,
                    operationOffset,
                    operations.Count - operationOffset,
                    row.RuleId,
                    null,
                    null,
                    null,
                    trust,
                    trust == ReviewTrust.Accepted,
                    0,
                    [],
                    [],
                    evidence,
                    row.BaseFullTag,
                    form.FullTags,
                    stemTransform) { ParadigmBranchId = row.Id });
            }
        }

        return new WordFormationGraph(
            source.SourceId,
            states,
            morphs,
            transforms,
            literals,
            slots.ToArray(),
            operations.ToArray(),
            edges.ToArray(),
            binaryBindings,
            structuralDecisions?.ToArray(),
            additionalCells.Select(pair => new ParadigmRuntime.ProgramCells(pair.Key.State, pair.Key.BaseTag,
                pair.Key.Rule, pair.Value.ToArray(), pair.Key.Kind)).ToArray(), source.LexicalCliticProjections,
            source.OrdinaryParadigmAliases);
    }

    private static StructuralDecision? TryCompileStructuralDecision(
        WordFormationAnalyzer.TypedTransitionNode row,
        WordFormationAnalyzer.RuleNode rule,
        WordFormationAnalyzer.MorphNode morph,
        WordFormationAnalyzer.TransformNode sourceTransform,
        Transform transform,
        ReviewTrust trust)
    {
        StructuralDecisionSpec? spec = StructuralDecisionSpecs.FirstOrDefault(item =>
            string.Equals(item.TransitionId, row.Id, StringComparison.Ordinal));
        if (spec is null && sourceTransform.ExactInput is { Length: > 0 } exactInput)
        {
            string? morphId = row.RuleId switch
            {
                "productive.adverb.degree_comparative_po.v1" => "m:prefix:по-degree",
                "productive.adverb.degree_superlative_nai.v1" => "m:prefix:най-degree",
                _ => null
            };
            if (morphId is not null)
                spec = PrefixSpec(morphId, morphId == "m:prefix:по-degree" ? "по-" : "най-",
                    "degree", row.RuleId!, "adverb",
                    ["ev:beron:adverb-degree-prefixes", "ev:beron:adverb-degree:" + exactInput], exactInput);
        }
        if (spec is null)
            return null;

        bool exactState = spec.StateBindingKind is null
            ? row.StateBinding is null && row.FromStateId == spec.FromStateId && row.ToStateId == spec.ToStateId
            : row.FromStateId is null && row.ToStateId is null &&
              row.StateBinding is { } binding &&
              binding.Kind == spec.StateBindingKind &&
              binding.PartOfSpeech == spec.StateBindingPartOfSpeech;
        bool exactEvidence = row.EvidenceIds.Length == spec.EvidenceIds.Length &&
            spec.EvidenceIds.All(item => row.EvidenceIds.Contains(item, StringComparer.Ordinal));
        if (row.Kind != spec.Kind || row.PrefixClass != spec.PrefixClass || !exactState ||
            row.MorphId != spec.MorphId || morph.Kind != spec.MorphKind ||
            morph.CanonicalText != spec.MorphCanonicalText || morph.VariantSetId is not null ||
            row.StemTransformId != spec.TransformId || sourceTransform.Id != spec.TransformId ||
            sourceTransform.Kind != spec.TransformKind || sourceTransform.FromTail != spec.TransformFrom ||
            sourceTransform.ToTail != spec.TransformTo || sourceTransform.VariantSetId is not null ||
            sourceTransform.MinimumInputLength != 0 || sourceTransform.InputFinalCharacterClass is not null ||
            sourceTransform.InputPreTailCharacterClass is not null || sourceTransform.ExcludedInputTail is not null ||
            sourceTransform.ExactInput != spec.TransformExactInput || transform.ExactInput != spec.TransformExactInput ||
            transform.Kind != spec.RuntimeTransformKind || transform.From != spec.TransformFrom ||
            transform.To != spec.TransformTo || row.RuleId != spec.RuleId || rule.Kind != spec.RuleKind ||
            rule.OutputPos != spec.OutputPos || !rule.Pattern.SequenceEqual(spec.Pattern, StringComparer.Ordinal) ||
            row.ApplicationIds.Length != 0 || row.CompoundBinding is not null || !exactEvidence ||
            trust != ReviewTrust.Accepted)
        {
            return null;
        }

        return new StructuralDecision(
            "transparent",
            row.MayGenerateCandidates && rule.MayGenerateCandidates);
    }

    private static StructuralDecisionSpec[] CreateStructuralDecisionSpecs() =>
    [
        SuffixSpec("state:noun:41"),
        SuffixSpec("state:noun:41b"),
        PrefixSpec("m:prefix:не", "не", "negative", "productive.adjective.negative_prefix_ne.v1", "adjective",
            ["ev:systematic-m4-v6:adjective-prefix-ne",
             "ev:user-authority:adjective-negative-prefix-ne",
             "ev:user-authority:productive-pos-scoped-prefixes"]),
        PrefixSpec("m:prefix:по-degree", "по-", "degree", "productive.adjective.degree_comparative_po.v1", "adjective",
            ["ev:ibl:regular-adjective-degree-prefixes"]),
        PrefixSpec("m:prefix:най-degree", "най-", "degree", "productive.adjective.degree_superlative_nai.v1", "adjective",
            ["ev:ibl:regular-adjective-degree-prefixes"]),
        PrefixSpec("m:prefix:супер", "супер", "intensifier", "productive.adjective.prefix_super.v1", "adjective",
            ["ev:systematic-m4-v6:adjective-prefix-super",
             "ev:user-authority:adjective-prefix-super",
             "ev:user-authority:productive-pos-scoped-prefixes"]),
        PrefixSpec("m:prefix:супер", "супер", "intensifier", "productive.noun.prefix_super.v1", "noun",
            ["ev:systematic-m4-v6:noun-prefix-super",
             "ev:user-authority:productive-pos-scoped-prefixes"]),
        PrefixSpec("m:prefix:свръх", "свръх", "intensifier", "productive.adjective.prefix_svarh.v1", "adjective",
            ["ev:systematic-m4-v6:adjective-prefix-svarh",
             "ev:user-authority:productive-pos-scoped-prefixes"]),
        PrefixSpec("m:prefix:свръх", "свръх", "intensifier", "productive.noun.prefix_svarh.v1", "noun",
            ["ev:systematic-m4-v6:noun-prefix-svarh",
             "ev:user-authority:productive-pos-scoped-prefixes"])
    ];

    private static StructuralDecisionSpec SuffixSpec(string toStateId) => new(
        "transition|suffix|m:suffix:ица|state:noun:41|stem-transform:tail:ка=>ч|" + toStateId +
        "|productive.noun.diminutive_ica.v1",
        "suffix", null, "m:suffix:ица", "derivational_suffix", "ица", "stem-transform:tail:ка=>ч",
        "tail_replace", "ка", "ч", TransformKind.TailReplace, "productive.noun.diminutive_ica.v1",
        "suffix_derivation", "noun", ["exact_feminine_noun_lemma", "m:suffix:ица"],
        "state:noun:41", toStateId, null, null,
        ["ev:m4-exact-join:noun-diminutive-ica-41",
         "ev:user-authority:productive-noun-diminutive-ica"]);

    private static StructuralDecisionSpec PrefixSpec(
        string morphId, string canonicalText, string prefixClass, string ruleId, string outputPos,
        string[] evidenceIds, string? exactInput = null) => new(
        $"transition|prefix|{morphId}|preserve:{outputPos}|stem-transform:identity{(exactInput is null ? "" : ":exact:" + exactInput)}|{ruleId}",
        "prefix", prefixClass, morphId, "prefix", canonicalText,
        "stem-transform:identity" + (exactInput is null ? "" : ":exact:" + exactInput),
        "identity", "", "", TransformKind.Identity, ruleId, "prefix_derivation", outputPos,
        [morphId, "exact_" + outputPos + "_lemma"], null, null, "preserve_matching_state", outputPos,
        evidenceIds, exactInput);

    private sealed record StructuralDecisionSpec(
        string TransitionId,
        string Kind,
        string? PrefixClass,
        string MorphId,
        string MorphKind,
        string MorphCanonicalText,
        string TransformId,
        string TransformKind,
        string TransformFrom,
        string TransformTo,
        TransformKind RuntimeTransformKind,
        string RuleId,
        string RuleKind,
        string OutputPos,
        string[] Pattern,
        string? FromStateId,
        string? ToStateId,
        string? StateBindingKind,
        string? StateBindingPartOfSpeech,
        string[] EvidenceIds,
        string? TransformExactInput = null);

    private static Morph CompileMorph(WordFormationAnalyzer.MorphNode source)
    {
        string text = BulgarianTextNormalizer.NormalizeKey(source.CanonicalText);
        if (text.Length == 0)
            throw new InvalidDataException($"Morph '{source.Id}' has empty canonical text.");
        _ = MapMorphKind(source.Kind);
        return new Morph(source.Id, source.Kind, text, ParseTrust(source.ReviewState));
    }

    private static Transform CompileTransform(
        WordFormationAnalyzer.TransformNode source,
        IReadOnlyDictionary<string, WordFormationAnalyzer.VariantSetNode> variantSets)
    {
        TransformKind kind = source.Kind switch
        {
            "identity" => TransformKind.Identity,
            "tail_replace" => TransformKind.TailReplace,
            "tail_delete" => TransformKind.TailDelete,
            "variant_set_normalize" => TransformKind.VariantSetNormalize,
            "last_occurrence_replace" => TransformKind.LastOccurrenceReplace,
            "penultimate_delete" => TransformKind.PenultimateDelete,
            _ => throw new InvalidDataException($"Transform '{source.Id}' has unsupported kind '{source.Kind}'.")
        };
        string from = BulgarianTextNormalizer.NormalizeKey(source.FromTail);
        string to = BulgarianTextNormalizer.NormalizeKey(source.ToTail);
        string[] variants = [];
        if (kind == TransformKind.PenultimateDelete &&
            (source.FromTail is not { Length: 1 } || char.IsSurrogate(source.FromTail[0]) || source.FromTail != from ||
             source.ToTail != "" || source.MinimumInputLength < 2 || source.VariantSetId is not null ||
             source.LastOccurrence is not null || source.InputPreTailCharacterClass is not null))
            throw new InvalidDataException($"Transform '{source.Id}' has invalid penultimate-deletion operands or guards.");
        if (kind == TransformKind.VariantSetNormalize)
        {
            if (source.VariantSetId is null || !variantSets.TryGetValue(source.VariantSetId, out var variantSet))
                throw new InvalidDataException($"Transform '{source.Id}' has no valid variant set.");
            to = BulgarianTextNormalizer.NormalizeKey(variantSet.Canonical);
            variants = variantSet.Variants
                .Select(BulgarianTextNormalizer.NormalizeKey)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        if (kind == TransformKind.Identity && (from.Length > 0 || to.Length > 0) ||
            kind == TransformKind.TailReplace && from.Length == 0 && to.Length == 0 ||
            kind == TransformKind.TailDelete && (from.Length == 0 || to.Length > 0))
        {
            throw new InvalidDataException($"Transform '{source.Id}' has invalid operands.");
        }
        return new Transform(
            source.Id,
            kind,
            from,
            to,
            variants,
            ParseTrust(source.ReviewState),
            source.MinimumInputLength,
            source.InputFinalCharacterClass switch
            {
                null => InputFinalCharacterClass.None,
                "bulgarian_consonant" => InputFinalCharacterClass.BulgarianConsonant,
                _ => throw new InvalidDataException(
                    $"Transform '{source.Id}' has unsupported input final character class '{source.InputFinalCharacterClass}'.")
            },
            source.InputPreTailCharacterClass switch
            {
                null => InputPreTailCharacterClass.None,
                "bulgarian_consonant" => InputPreTailCharacterClass.BulgarianConsonant,
                "not_bulgarian_consonant" => InputPreTailCharacterClass.NotBulgarianConsonant,
                "bulgarian_consonant_except_dt" => InputPreTailCharacterClass.BulgarianConsonantExceptDt,
                _ => throw new InvalidDataException(
                    $"Transform '{source.Id}' has unsupported input pre-tail character class '{source.InputPreTailCharacterClass}'.")
            },
            source.ExcludedInputTail,
            source.LastOccurrence is { } last
                ? new LastOccurrenceReplacement(last.From[0], last.To[0], last.MinimumIndex, last.MinimumFollowingLength)
                : null,
            source.ExactInput);
    }

    private static EdgeKind ParseTransitionKind(string kind, string morphKind, string id) => (kind, morphKind) switch
    {
        ("prefix", "prefix") => EdgeKind.Prefix,
        ("suffix", "derivational_suffix") => EdgeKind.Suffix,
        ("compound", "interfix") => EdgeKind.Compound,
        _ => throw new InvalidDataException($"Typed transition '{id}' has incompatible kind and morph.")
    };

    private static PrefixClass ParsePrefixClass(string value, string id) => value switch
    {
        "negative" => PrefixClass.Negative,
        "degree" => PrefixClass.Degree,
        "intensifier" => PrefixClass.Intensifier,
        _ => throw new InvalidDataException($"Typed prefix transition '{id}' has unsupported prefixClass '{value}'.")
    };

    private static EdgeKind ParseApplicationKind(string? ruleKind, string depth) => (ruleKind, depth) switch
    {
        ("compounding", _) or (_, "compounding") => EdgeKind.Compound,
        ("prefix_derivation", _) => EdgeKind.Prefix,
        ("suffix_derivation", _) => EdgeKind.Suffix,
        (null, "diachronic_candidate") => EdgeKind.Suffix,
        _ => throw new InvalidDataException(
            $"Application rule kind '{ruleKind ?? "<null>"}' and depth '{depth}' are incompatible.")
    };

    private static ReviewTrust ParseTrust(string value) => value switch
    {
        "accepted" => ReviewTrust.Accepted,
        "reviewed" => ReviewTrust.Reviewed,
        "proposed" or "unreviewed" => ReviewTrust.Unreviewed,
        "blocked" => ReviewTrust.Blocked,
        _ => throw new InvalidDataException($"Unsupported word-formation review state '{value}'.")
    };

    private static IEnumerable<string> GetApplicationSurfaces(WordFormationAnalyzer.ApplicationNode application)
    {
        yield return BulgarianTextNormalizer.NormalizeKey(application.OutputCanonical);
        const string prefix = "observed_surface=";
        foreach (string code in application.EvidenceCodes)
        {
            if (code.StartsWith(prefix, StringComparison.Ordinal))
            {
                string surface = BulgarianTextNormalizer.NormalizeKey(code[prefix.Length..]);
                if (surface.Length > 0)
                    yield return surface;
            }
        }
    }

}
