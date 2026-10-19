using Transform = BgLang.Core.GraphSurfaceOperations.Transform;
using StateBinding = BgLang.Core.GraphState.Binding;
using StateConstraint = BgLang.Core.GraphState.Constraint;
using Piece = BgLang.Core.GraphSegmentation.Piece;
using BgLang.Contracts;
using System.Runtime.CompilerServices;
using MorphPolicy = BgLang.Core.PathMorphologyPolicy;

namespace BgLang.Core;

/// <summary>One typed and reversible path graph compiled from reviewed linguistic source.</summary>
internal sealed class WordFormationGraph
{
    private const double LexicalizedEvidenceConfidence = 0.45;
    internal static readonly IComparer<PathCandidate> CandidateOrder =
        Comparer<PathCandidate>.Create(CompareCandidates);
    private static readonly IComparer<PathCandidate> ForwardCandidateOrder = Comparer<PathCandidate>.Create((left, right) =>
    {
        int depth = left.EdgePath.Length.CompareTo(right.EdgePath.Length);
        return depth != 0 ? depth : CompareCandidates(left, right);
    });

    internal enum CandidateRanking { Inverse, Forward }

    internal static IComparer<PathCandidate> OrderFor(CandidateRanking ranking) =>
        ranking == CandidateRanking.Forward ? ForwardCandidateOrder : CandidateOrder;

    internal static (int Depth, bool Conditional) TierFor(PathCandidate candidate, CandidateRanking ranking) =>
        (ranking == CandidateRanking.Forward ? candidate.EdgePath.Length : 0, candidate.IsConditional);

    private readonly string _sourceId;
    private readonly Morph[] _morphs;
    private readonly Transform[] _transforms;
    private readonly string[] _literals;
    private readonly InputSlot[] _slots;
    private readonly SurfaceOperation[] _operations;
    private readonly Edge[] _edges;
    private readonly string[][] _conditionalFullTags;
    private readonly GraphSurfaceOperations _surfaceOperations;
    private readonly ParadigmRuntime _paradigms;
    private readonly GraphSegmentation _segmentation;
    private readonly ConditionalRootProof _conditionalProof;
    private readonly int[] _genericEdges;
    private readonly IReadOnlyDictionary<string, int> _morphIndexById;
    private readonly IReadOnlyDictionary<string, int[]> _outgoingEdges;
    private readonly IReadOnlyDictionary<string, int[]> _incomingEdges;
    private readonly IReadOnlyDictionary<string, int[]> _incomingEdgesByToPartOfSpeech;
    private readonly IReadOnlyDictionary<string, int[]> _paradigmEdgesByState;
    private readonly IReadOnlyDictionary<string, int[]> _paradigmEdgesByPartOfSpeech;
    private readonly (string Tag, string Rule)[] _productiveBaseModels;
    private readonly IReadOnlyDictionary<string, int[]> _preservingEdgesByPartOfSpeech;
    private readonly IReadOnlyDictionary<string, MorphPolicy.Program[]> _invariantMorphologyPrograms;
    private readonly IReadOnlyDictionary<string, int[]> _applicationEdgesByLemma;
    private readonly IReadOnlyDictionary<string, int[]> _applicationEdgesBySurface;
    private readonly ConditionalProgram[] _conditionalPrograms;
    private readonly ConditionalSeedIndex _conditionalSeeds;
    private readonly IReadOnlyDictionary<string, (int Edge, WordFormationAnalyzer.CompoundBindingNode Binding)[]>? _binaryEdgesByLeft;
    private readonly (int Edge, StructuralDecision Decision)[]? _structuralDecisions;
    private readonly ConditionalWeakTable<IRuntimeLexicon, ConditionalSupportInventory> _conditionalSupport = new();

    internal WordFormationGraph(
        string sourceId,
        string[] states,
        Morph[] morphs,
        Transform[] transforms,
        string[] literals,
        InputSlot[] slots,
        SurfaceOperation[] operations,
        Edge[] edges,
        IReadOnlyDictionary<int, WordFormationAnalyzer.CompoundBindingNode>? binaryBindings,
        (int Edge, StructuralDecision Decision)[]? structuralDecisions,
        ParadigmRuntime.ProgramCells[]? additionalPrograms = null,
        LexicalCliticProjection[]? lexicalProjections = null, OrdinaryParadigmAlias[]? ordinaryAliases = null)
    {
        _sourceId = sourceId;
        _morphs = morphs;
        _transforms = transforms;
        _literals = literals;
        _slots = slots;
        _operations = operations;
        _edges = edges;
        _conditionalFullTags = edges.Select(edge => edge.AllowedFullTags is { } tags
            ? tags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : []).ToArray();
        _surfaceOperations = new(morphs, transforms, literals, operations);
        _structuralDecisions = structuralDecisions;
        if (binaryBindings is not null)
            _binaryEdgesByLeft = binaryBindings.SelectMany(pair => pair.Value.LeftLemmas.Select(left => (left, pair)))
                .GroupBy(item => item.left, StringComparer.Ordinal).ToDictionary(group => group.Key,
                    group => group.Select(item => (Edge: item.pair.Key, Binding: item.pair.Value))
                        .OrderBy(item => edges[item.Edge].Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        _morphIndexById = morphs
            .Select((item, index) => (item.Id, index))
            .ToDictionary(item => item.Id, item => item.index, StringComparer.Ordinal);
        _genericEdges = OrderEdgeIndices(Enumerable.Range(0, edges.Length)
            .Where(index => edges[index].Origin == EdgeOrigin.Generic && edges[index].Kind != EdgeKind.Compound), edges);
        _outgoingEdges = BuildStateEdgeIndex(states, edges, outgoing: true);
        _incomingEdges = BuildStateEdgeIndex(states, edges, outgoing: false);
        _incomingEdgesByToPartOfSpeech = edges
            .Select((edge, index) => (edge, index))
            .Where(item => item.edge.Origin == EdgeOrigin.Generic && item.edge.Kind != EdgeKind.Compound &&
                           item.edge.StateBinding.ToStateId is not null)
            .GroupBy(
                item => StatePartOfSpeech(item.edge.StateBinding.ToStateId!),
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => OrderEdgeIndices(group.Select(item => item.index), edges),
                StringComparer.Ordinal);
        _paradigmEdgesByState = states.ToDictionary(
            state => state,
            state => OrderEdgeIndices(
                Enumerable.Range(0, edges.Length).Where(index =>
                    edges[index].Origin == EdgeOrigin.Paradigm &&
                    string.Equals(edges[index].StateBinding.FromStateId, state, StringComparison.Ordinal)),
                edges),
            StringComparer.Ordinal);
        _paradigms = new ParadigmRuntime(
            _paradigmEdgesByState.Values.SelectMany(indices => indices).Select(index =>
            {
                Edge edge = edges[index];
                bool reuseGuardStem = edge.OperationCount > 0 &&
                    operations[edge.OperationOffset] is { Kind: OperationKind.Transform } first &&
                    first.OperandIndex == edge.ParadigmStemTransform;
                var cell = new ParadigmProgram.Cell(index, edge.ParadigmStemTransform,
                    edge.OperationOffset, edge.OperationCount, reuseGuardStem, Approved(edge),
                    edge.Trust >= ReviewTrust.Reviewed, edge.AllowedFullTags!);
                return (edge.StateBinding.FromStateId!, edge.RequiredBaseFullTag!, edge.RuleId!, cell);
            }), _surfaceOperations, additionalPrograms, lexicalProjections, ordinaryAliases);
        _invariantMorphologyPrograms = (additionalPrograms ?? [])
            .Where(program => program.Kind == ParadigmProgram.Kind.Invariant)
            .GroupBy(program => program.StateId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.SelectMany(program => program.Cells.Select(cell =>
                new MorphPolicy.Program(program.BaseFullTag, cell.Approved, cell.GuardTransformIndex))).ToArray(),
                StringComparer.Ordinal);
        _paradigmEdgesByPartOfSpeech = edges
            .Select((edge, index) => (edge, index))
            .Where(item => item.edge.Origin == EdgeOrigin.Paradigm && item.edge.StateBinding.FromStateId is not null)
            .GroupBy(item => StatePartOfSpeech(item.edge.StateBinding.FromStateId!), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => OrderEdgeIndices(group.Select(item => item.index), edges), StringComparer.Ordinal);
        _productiveBaseModels = edges.Where(edge => edge.Origin == EdgeOrigin.Paradigm &&
                Approved(edge) && transforms[edge.ParadigmStemTransform].ExactInput is null)
            .Select(edge => (edge.RequiredBaseFullTag!, edge.RuleId!)).Distinct().ToArray();
        _preservingEdgesByPartOfSpeech = edges
            .Select((edge, index) => (edge, index))
            .Where(item => item.edge.Origin == EdgeOrigin.Generic &&
                           item.edge.StateBinding.PreserveMatchingPartOfSpeech is not null)
            .GroupBy(item => item.edge.StateBinding.PreserveMatchingPartOfSpeech!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => OrderEdgeIndices(group.Select(item => item.index), edges),
                StringComparer.Ordinal);
        _segmentation = new(morphs, transforms, literals, slots, operations, edges, _morphIndexById, _surfaceOperations);
        _applicationEdgesByLemma = BuildApplicationIndex(edges, bySurface: false);
        _applicationEdgesBySurface = BuildApplicationIndex(edges, bySurface: true);
        _conditionalPrograms = ConditionalRootProof.CompilePrograms(edges, transforms);
        _conditionalSeeds = new(_conditionalPrograms, transforms);
        _conditionalProof = new(edges, transforms, _conditionalPrograms, _conditionalSupport, _surfaceOperations);
    }

    internal string SourceId => _sourceId;

    private static IReadOnlyDictionary<string, int[]> BuildStateEdgeIndex(
        IEnumerable<string> states,
        Edge[] edges,
        bool outgoing) =>
        states.ToDictionary(
            state => state,
            state => OrderEdgeIndices(
                Enumerable.Range(0, edges.Length).Where(index =>
                    edges[index].Origin == EdgeOrigin.Generic && edges[index].Kind != EdgeKind.Compound &&
                    string.Equals(
                        outgoing
                            ? edges[index].StateBinding.FromStateId
                            : edges[index].StateBinding.ToStateId,
                        state,
                        StringComparison.Ordinal)),
                edges),
            StringComparer.Ordinal);

    private IReadOnlyDictionary<string, int[]> BuildApplicationIndex(Edge[] edges, bool bySurface)
    {
        IEnumerable<(string Key, int Edge)> rows = Enumerable.Range(0, edges.Length)
            .Where(index => edges[index].Origin == EdgeOrigin.Application)
            .SelectMany(index => (bySurface
                    ? edges[index].SurfaceKeys ?? []
                    : GetSlots(edges[index])
                        .Where(slot => slot.IsLexicalAnchor)
                        .Select(slot => NormalizeLemmaSlot(slot.NodeId)))
                .Select(key => (key, index)));
        return rows
            .GroupBy(row => row.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => OrderEdgeIndices(group.Select(row => row.Edge).Distinct(), edges),
                StringComparer.Ordinal);
    }

    private static int[] OrderEdgeIndices(IEnumerable<int> indices, Edge[] edges) => indices
        .OrderByDescending(index => edges[index].Trust)
        .ThenByDescending(index => edges[index].GenerationEligible)
        .ThenByDescending(index => edges[index].SupportCount)
        .ThenBy(index => edges[index].Id, StringComparer.Ordinal)
        .ToArray();

    internal IReadOnlyList<WordDerivation> Compose(
        string baseLemma,
        IRuntimeLexicon lexicon,
        SearchBounds bounds)
    {
        string normalizedBase = BulgarianTextNormalizer.NormalizeKey(baseLemma);
        if (normalizedBase.Length == 0)
            return [];

        if (!HasExactSelfReading(lexicon, normalizedBase))
            return [];

        var applicationCandidates = new BoundedGraphCandidatePool(bounds.MaxApplicationCandidates, CandidateRanking.Forward);
        foreach (PathCandidate candidate in FindApplicationCompositions(normalizedBase, lexicon))
            applicationCandidates.Add(candidate);

        string[] anchorStates = FindAnchorStates(lexicon, normalizedBase);
        if (anchorStates.Length > bounds.MaxExpandedStates)
            throw new InvalidDataException("Exact lemma anchors exceed the graph search-state bound.");

        var graphCandidates = new BoundedGraphCandidatePool(bounds.MaxGraphCandidates, CandidateRanking.Forward);
        var nodes = new List<SearchNode>();
        var frontier = new Queue<int>();
        foreach (string state in anchorStates)
        {
            nodes.Add(new SearchNode(
                normalizedBase,
                StateConstraint.Exact(state),
                -1,
                -1,
                0,
                normalizedBase));
            int seedIndex = nodes.Count - 1;
            frontier.Enqueue(seedIndex);
            foreach (int paradigmEdge in GetParadigmEdges(state))
            {
                Edge paradigm = _edges[paradigmEdge];
                if (!Approved(paradigm) ||
                    !HasParadigmBaseAuthority(paradigm, normalizedBase, lexicon, allowOov: false))
                    continue;
                foreach (string form in ApplyOperations(normalizedBase, paradigm, forward: true, budget: (lexicon as GraphWorkLexicon)?.Budget))
                {
                    if (HasConflictingExactOutput(lexicon, form, StateConstraint.Exact(state), paradigm))
                    {
                        continue;
                    }
                    AddForwardGraphCandidate(
                        CreatePathCandidate(nodes, seedIndex, forward: true, paradigmEdge, form));
                }
            }
        }

        int expanded = 0;
        while (frontier.Count > 0 && expanded < bounds.MaxExpandedStates)
        {
            int nodeIndex = frontier.Dequeue();
            SearchNode node = nodes[nodeIndex];
            expanded++;
            if (node.Depth >= bounds.MaxDepth)
                continue;
            foreach (int edgeIndex in GetOutgoingEdges(node.State))
            {
                Edge edge = _edges[edgeIndex];
                if (!Approved(edge) ||
                    !edge.StateBinding.TryTraverse(TraversalDirection.Forward, node.State, out StateConstraint nextState) ||
                    edge.BlockedSurfaces.Contains(node.Surface, StringComparer.Ordinal) ||
                    !AllowsPrefix(nodeIndex, edgeIndex, nodes, TraversalDirection.Forward) ||
                    HasIncompatibleExactPrefixHistory(edge, node.Surface, node.State, lexicon))
                {
                    continue;
                }

                foreach (string nextSurface in ApplyOperations(node.Surface, edge, forward: true, budget: (lexicon as GraphWorkLexicon)?.Budget))
                {
                    if (edge.BlockedSurfaces.Contains(nextSurface, StringComparer.Ordinal) ||
                        HasConflictingExactOutput(lexicon, nextSurface, nextState, edge) ||
                        HasVisited(nodeIndex, nextState, nextSurface, nodes) ||
                        nodes.Count >= bounds.MaxExpandedStates)
                    {
                        continue;
                    }
                    nodes.Add(new SearchNode(nextSurface, nextState, nodeIndex, edgeIndex, node.Depth + 1, normalizedBase));
                    int nextIndex = nodes.Count - 1;
                    frontier.Enqueue(nextIndex);
                    graphCandidates.Add(CreatePathCandidate(nodes, nextIndex, forward: true));
                    foreach (int paradigmEdge in GetParadigmEdges(nextState.ExactStateId!))
                    {
                        Edge paradigm = _edges[paradigmEdge];
                        if (!Approved(paradigm) ||
                            !HasParadigmBaseAuthority(paradigm, nextSurface, lexicon, allowOov: true))
                            continue;
                        foreach (string form in ApplyOperations(nextSurface, paradigm, forward: true, budget: (lexicon as GraphWorkLexicon)?.Budget))
                        {
                            if (HasConflictingExactOutput(lexicon, form, nextState, paradigm))
                                continue;
                            AddForwardGraphCandidate(
                                CreatePathCandidate(nodes, nextIndex, forward: true, paradigmEdge, form));
                        }
                    }
                }
                if (nodes.Count >= bounds.MaxExpandedStates)
                    break;
            }
        }

        BinaryResult binary = FindBinaryCandidates(normalizedBase, null, new BinaryProofBudget(lexicon), forward: true);
        return Rank(applicationCandidates.Values.Concat(graphCandidates.Values).Concat(binary.Candidates), CandidateRanking.Forward)
            .Take(bounds.MaxCandidatesReturned)
            .Select(candidate => ToPublic(candidate, lexicon))
            .ToArray();

        void AddForwardGraphCandidate(PathCandidate candidate)
        {
            candidate = candidate with { Projection = ProjectMorphology(candidate, lexicon) };
            if (candidate.Projection?.Outcome == MorphPolicy.Outcome.Licensed)
                graphCandidates.Add(candidate);
        }
    }

    private BinaryResult FindBinaryCandidates(string left, string? requested, BinaryProofBudget budget, bool forward)
    {
        if (_binaryEdgesByLeft is null || !_binaryEdgesByLeft.TryGetValue(left, out var relations))
            return new(BinaryProofBudget.Outcome.NotApplicable, []);
        Dictionary<(string Word, string Tag), PathCandidate> candidates = [];
        Dictionary<string, PathCandidate> proofs = new(StringComparer.Ordinal);
        try
        {
            foreach (var (relation, binding) in relations)
            {
                budget.Structural();
                Edge edge = _edges[relation];
                if (forward ? !Approved(edge) : edge.Trust < ReviewTrust.Reviewed)
                    continue;
                budget.Pair();
                if (!HasBinaryOperand(left, binding.LeftBaseFullTag, edge.StateBinding.FromStateId!, budget) ||
                    edge.BlockedSurfaces.Contains(left, StringComparer.Ordinal)) continue;
                string prefix = ApplyOperations(left, edge, forward: true).Single();
                foreach (string right in binding.RightLemmas)
                {
                    budget.Structural();
                    if (right == left) continue;
                    budget.Pair();
                    if (!HasBinaryOperand(right, binding.RightBaseFullTag, edge.StateBinding.ToStateId!, budget) ||
                        edge.BlockedSurfaces.Contains(right, StringComparer.Ordinal)) continue;
                    budget.Replay();
                    List<GenerationObservation> observations = [];
                    HashSet<(string Form, string Tag)> tuples = GenerateLexicalTuples(right, binding.RightBaseFullTag,
                        binding.RightParadigmRuleId, observations.Add, maximumObservations: 256,
                        cancellationToken: budget.CancellationToken);
                    Dictionary<(string Form, string Tag), int> paths = new() { [(right, binding.RightBaseFullTag)] = -1 };
                    foreach (GenerationObservation observation in observations)
                    {
                        var key = (observation.Form, observation.FullTag);
                        if (paths.TryGetValue(key, out int previous))
                        {
                            if (previous == -1) continue; // Exact base reading is the canonical seed proof.
                            if (!EquivalentBinaryParadigm(previous, observation.EdgeIndex))
                                throw new BinaryProofBudget.Failure(BinaryProofBudget.Outcome.Ambiguous);
                            if (StringComparer.Ordinal.Compare(_edges[previous].Id, _edges[observation.EdgeIndex].Id) <= 0) continue;
                        }
                        paths[key] = observation.EdgeIndex;
                    }
                    foreach (var group in tuples.GroupBy(tuple => tuple.Form).OrderBy(group => group.Key, StringComparer.Ordinal))
                    {
                        int proof = paths[group.First()];
                        if (group.Any(tuple => !EquivalentBinaryParadigm(proof, paths[tuple])))
                            throw new BinaryProofBudget.Failure(BinaryProofBudget.Outcome.Ambiguous);
                        string word = prefix + group.Key;
                        if (requested is not null && word != requested || edge.BlockedSurfaces.Contains(word, StringComparer.Ordinal)) continue;
                        HashSet<string> tags = group.Select(tuple => tuple.Tag).ToHashSet(StringComparer.Ordinal);
                        if (budget.LookupExactNormalized(word, 257).Any(row =>
                                BulgarianTextNormalizer.NormalizeKey(row.Lemma) != prefix + right ||
                                TryCreateStateId(row.PartOfSpeech) != edge.StateBinding.ToStateId ||
                                row.PartOfSpeech is null || !tags.Contains(row.PartOfSpeech)) ||
                            forward && TryDecomposeLexicalizedApplication(word, word, budget, out _)) continue;
                        foreach (var tuple in group.OrderBy(tuple => tuple.Tag, StringComparer.Ordinal))
                        {
                            int paradigm = paths[tuple];
                            int[] path = paradigm < 0 ? [relation] : [relation, paradigm];
                            string pathId = edge.Id + "|left=" + left + "|right=" + right +
                                (paradigm < 0 ? "" : ">" + _edges[paradigm].Id) + "|tag=" + tuple.Tag;
                            var candidate = new PathCandidate(word, left, edge.StateBinding.FromStateId, edge.StateBinding.ToStateId,
                                path, [], edge.Trust, edge.SupportCount, true, false, pathId)
                            { Binary = new(right, binding.LeftBaseFullTag, binding.RightBaseFullTag, binding.RightParadigmRuleId, tuple.Tag) };
                            if (proofs.TryGetValue(word, out PathCandidate? prior) && !EquivalentBinaryBinding(prior, candidate))
                                throw new BinaryProofBudget.Failure(BinaryProofBudget.Outcome.Ambiguous);
                            proofs.TryAdd(word, candidate);
                            candidates.TryAdd((word, tuple.Tag), candidate); // Relations are in ordinal ID order.
                        }
                    }
                }
            }
            return new(BinaryProofBudget.Outcome.Complete, candidates.Values.ToArray());
        }
        catch (BinaryProofBudget.Failure failure) { return new(failure.Reason, []); }
        catch (InvalidDataException) { return new(BinaryProofBudget.Outcome.ProgramUnproved, []); }
    }

    private static bool HasBinaryOperand(string lemma, string tag, string state, BinaryProofBudget budget)
    {
        IReadOnlyList<WordReading> rows = budget.LookupOrdinaryExactNormalized(lemma, 257);
        return rows.Any(row => row.PartOfSpeech == tag) && rows.All(row =>
            BulgarianTextNormalizer.NormalizeKey(row.Lemma) == lemma &&
            TryCreateStateId(row.PartOfSpeech) == state);
    }

    private bool EquivalentBinaryBinding(PathCandidate left, PathCandidate right)
    {
        Edge a = _edges[left.EdgePath[0]], b = _edges[right.EdgePath[0]];
        return left.Base == right.Base && left.FromStateId == right.FromStateId && left.ToStateId == right.ToStateId &&
            left.Binary!.Right == right.Binary!.Right && left.Binary.LeftTag == right.Binary.LeftTag &&
            left.Binary.RightTag == right.Binary.RightTag && left.Binary.Program == right.Binary.Program &&
            a.RuleId == b.RuleId && a.Trust == b.Trust && a.GenerationEligible == b.GenerationEligible;
    }

    private bool EquivalentBinaryParadigm(int left, int right)
    {
        if (left == right || left < 0 || right < 0) return left == right;
        Edge a = _edges[left], b = _edges[right];
        if (a.OperationCount != b.OperationCount) return false;
        for (int i = 0; i < a.OperationCount; i++)
        {
            SurfaceOperation x = _operations[a.OperationOffset + i], y = _operations[b.OperationOffset + i];
            if (x.Kind != y.Kind) return false;
            if (x.Kind == OperationKind.AppendLiteral)
            { if (_literals[x.OperandIndex] != _literals[y.OperandIndex]) return false; }
            else if (x.Kind == OperationKind.Transform)
            {
                Transform p = _transforms[x.OperandIndex], q = _transforms[y.OperandIndex];
                if (p.Kind != q.Kind || p.From != q.From || p.To != q.To || p.Trust != q.Trust ||
                    p.MinimumInputLength != q.MinimumInputLength || p.FinalCharacterClass != q.FinalCharacterClass ||
                    p.PreTailCharacterClass != q.PreTailCharacterClass || p.ExcludedInputTail != q.ExcludedInputTail ||
                    p.LastOccurrence != q.LastOccurrence ||
                    !p.Variants.SequenceEqual(q.Variants, StringComparer.Ordinal)) return false;
            }
            else return false;
        }
        return true;
    }

    internal WordReading[] GenerateLexicalReadings(
        string lemma,
        string baseFullTag,
        string ruleId,
        double score,
        Action<GenerationObservation>? observe = null) =>
        GenerateLexicalTuples(lemma, baseFullTag, ruleId, observe)
            .OrderBy(tuple => tuple.Form, StringComparer.Ordinal).ThenBy(tuple => tuple.Tag, StringComparer.Ordinal)
            .Select(tuple => new WordReading(tuple.Form, lemma, tuple.Tag, score, null, null, null, string.Empty)).ToArray();

    internal HashSet<(string Form, string Tag)> GenerateLexicalTuples(
        string lemma, string baseFullTag, string ruleId, Action<GenerationObservation>? observe = null, int maximumObservations = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TryCreateStateId(baseFullTag) is null)
            throw new InvalidDataException("Generated lexeme has an invalid base state.");
        return _paradigms.Generate(lemma, baseFullTag, ruleId, observe, maximumObservations, cancellationToken);
    }

    internal MorphologyProposal[]? ProposeMorphology(string normalized, IReadOnlySet<string>? observedTags,
        bool baseOnly = false, bool includeSelf = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool combineSelf = includeSelf && observedTags is null;
        if (combineSelf && _productiveBaseModels.Length > 4096) return null;
        HashSet<MorphologyProposal> proposals = [];
        bool Add(MorphologyProposal proposal) => proposals.Contains(proposal) ||
            (proposals.Count < 4096 && proposals.Add(proposal));
        if (observedTags is not null)
            foreach (var model in _productiveBaseModels)
                if ((observedTags is null || observedTags.Contains(model.Tag)) && !Add(new(normalized, model.Tag, model.Tag, model.Rule)))
                    return null;
        int expanded = 0;
        try
        {
            if (!baseOnly)
            foreach (var seed in _paradigms.Reverse(normalized, observedTags, productiveOnly: true,
                         budget: new GraphSearchBudget(32_768, cancellationToken)))
            {
                if (++expanded > 4096) return null;
                Edge edge = _edges[seed.Edge];
                foreach (string tag in edge.AllowedFullTags!)
                    if ((observedTags is null || observedTags.Contains(tag)) && !Add(new(seed.Lemma, edge.RequiredBaseFullTag!, tag, edge.RuleId!)))
                        return null;
            }
        }
        catch (GraphSearchBudget.Exhausted) { return null; }
        MorphologyProposal[] ordered = combineSelf
            ? new MorphologyProposal[proposals.Count + _productiveBaseModels.Length] : proposals.ToArray();
        if (combineSelf)
        {
            proposals.CopyTo(ordered);
            int offset = proposals.Count;
            foreach (var model in _productiveBaseModels)
                ordered[offset++] = new(normalized, model.Tag, model.Tag, model.Rule);
        }
        Array.Sort(ordered, static (left, right) =>
        {
            int result = StringComparer.Ordinal.Compare(left.Lemma, right.Lemma);
            if (result == 0) result = StringComparer.Ordinal.Compare(left.BaseFullTag, right.BaseFullTag);
            if (result == 0) result = StringComparer.Ordinal.Compare(left.FullTag, right.FullTag);
            return result == 0 ? StringComparer.Ordinal.Compare(left.RuleId, right.RuleId) : result;
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (!combineSelf) return ordered;
        int unique = 0;
        foreach (MorphologyProposal proposal in ordered)
            if (unique == 0 || proposal != ordered[unique - 1])
            {
                if (unique == 4096) return null;
                ordered[unique++] = proposal;
            }
        return unique == ordered.Length ? ordered : ordered[..unique];
    }

    internal bool MatchesParadigmForm(string lemma, string stateId, string ruleId, string fullTag, string surface)
    {
        if (TryCreateStateId(fullTag) != stateId) return false;
        return _paradigms.MatchesForm(lemma, stateId, ruleId, fullTag, surface);
    }

    internal WordDerivation? TryDeriveUniqueProductiveSingleGenericEdge(
        string surface,
        IRuntimeLexicon lexicon)
    {
        string normalized = BulgarianTextNormalizer.NormalizeKey(surface);
        if (normalized.Length == 0)
            return null;

        List<PathCandidate> candidates = [];
        foreach (int edgeIndex in _genericEdges)
        {
            Edge edge = _edges[edgeIndex];
            if (edge.Origin != EdgeOrigin.Generic || !Approved(edge) || edge.ApplicationIds.Length != 0 ||
                edge.BlockedSurfaces.Contains(normalized, StringComparer.Ordinal) ||
                !edge.StateBinding.TryTraverse(
                    TraversalDirection.Reverse,
                    edge.StateBinding.OutputConstraint,
                    out StateConstraint baseConstraint))
            {
                continue;
            }

            foreach (string baseSurface in ApplyOperations(normalized, edge, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget))
            {
                if (edge.BlockedSurfaces.Contains(baseSurface, StringComparer.Ordinal))
                    continue;
                foreach (StateRefinement refined in RefineAgainstExactAnchors(
                             baseSurface,
                             baseConstraint,
                             lexicon))
                {
                    if (!refined.HasExactAnchor || refined.Constraint.ExactStateId is null ||
                        HasIncompatibleExactPrefixHistory(edge, baseSurface, refined.Constraint, lexicon) ||
                        !edge.StateBinding.TryTraverse(
                            TraversalDirection.Forward,
                            refined.Constraint,
                            out StateConstraint outputState) ||
                        HasReversePathExactOutputConflict(
                            [],
                            refined.Constraint,
                            parentIndex: -1,
                            edgeIndex,
                            normalized,
                            lexicon))
                    {
                        continue;
                    }

                    foreach (string replay in ApplyOperations(baseSurface, edge, forward: true, budget: (lexicon as GraphWorkLexicon)?.Budget))
                    {
                        if (!string.Equals(replay, normalized, StringComparison.Ordinal))
                            continue;
                        candidates.Add(new PathCandidate(
                            normalized,
                            baseSurface,
                            refined.Constraint.ExactStateId,
                            outputState.ExactStateId,
                            [edgeIndex],
                            [],
                            edge.Trust,
                            edge.SupportCount,
                            !edge.StateBinding.PreservesMatchingState,
                            false,
                            edge.Id)
                        { RootFeaturesPreserved = edge.StateBinding.PreservesMatchingState || edge.StateBinding.FromStateId == edge.StateBinding.ToStateId });
                    }
                }
            }
        }

        PathCandidate[] distinct = candidates.DistinctBy(candidate => candidate.Identity).ToArray();
        return distinct.Length == 1 ? ToPublic(distinct[0], lexicon) : null;
    }

    internal WordDecomposition Decompose(string surface, IRuntimeLexicon lexicon, BinaryProofBudget? sharedBudget = null,
        WordFormationAnalyzer.MorphologySearch? search = null, GraphSearchBudget? graphBudget = null)
    {
        string normalized = BulgarianTextNormalizer.NormalizeKey(surface);
        if (normalized.Length == 0)
            return Empty(surface, normalized);
        int separator = normalized.IndexOf('-');
        string? left = _binaryEdgesByLeft is not null && normalized.Length <= 256 && separator > 1 && normalized[separator - 1] == 'о' &&
            normalized.LastIndexOf('-') == separator && normalized.All(character => character == '-' ||
                BulgarianOrthography.IsLowercaseLetterOrStressedVowel(character)) ? normalized[..(separator - 1)] : null;
        if (left is null || !_binaryEdgesByLeft!.ContainsKey(left))
            return DecomposeCore(surface, normalized, lexicon, search: search, graphBudget: graphBudget);
        var budget = new BinaryProofBudget(lexicon, sharedBudget, graphBudget?.CancellationToken ?? CancellationToken.None);
        try
        {
            if (TryDecomposeLexicalizedApplication(surface, normalized, budget, out WordDecomposition? lexicalized))
                return lexicalized ?? Empty(surface, normalized);
            BinaryResult binary = FindBinaryCandidates(left, normalized, budget, forward: false);
            if (binary.Outcome != BinaryProofBudget.Outcome.Complete) search?.Refuse();
            return binary.Outcome == BinaryProofBudget.Outcome.Complete
                ? DecomposeCore(surface, normalized, budget, Rank(binary.Candidates).FirstOrDefault(), binaryApplicable: true,
                    search, graphBudget)
                : Empty(surface, normalized);
        }
        catch (BinaryProofBudget.Failure) { search?.Refuse(); return Empty(surface, normalized); }
        catch (InvalidDataException) { search?.Refuse(); return Empty(surface, normalized); }
    }

    private WordDecomposition DecomposeCore(string surface, string normalized, IRuntimeLexicon lexicon,
        PathCandidate? binary = null, bool binaryApplicable = false, WordFormationAnalyzer.MorphologySearch? search = null,
        GraphSearchBudget? graphBudget = null)
    {
        if (graphBudget is not null) lexicon = new GraphWorkLexicon(lexicon, graphBudget);
        if (!binaryApplicable && TryDecomposeLexicalizedApplication(surface, normalized, lexicon, out WordDecomposition? lexicalized))
            return lexicalized ?? Empty(surface, normalized);

        SearchBounds bounds = new(6, 4096, 128, 128, 1);
        var applicationCandidates = new BoundedGraphCandidatePool(bounds.MaxApplicationCandidates);
        CompoundBridgeSignature? bridgeSignature = null;
        PathCandidate? bestBridge = null;
        bool bridgeProofCompatible = true;
        int bridgeProofCount = 0;
        SuffixBridgeRelation? suffixRelation = null;
        foreach (PathCandidate candidate in FindApplicationDecompositions(normalized, lexicon))
        {
            if (binary is not null) return Refuse(); // Observe before pool drop.
            if (candidate.EdgePath.Length != 2)
            {
                applicationCandidates.Add(candidate);
                continue;
            }
            if (++bridgeProofCount > bounds.MaxExpandedStates)
            {
                bridgeProofCompatible = false;
                break;
            }
            CompoundBridgeSignature current = GetCompoundBridgeSignature(candidate);
            if (bridgeSignature is null)
                bridgeSignature = current;
            else if (bridgeSignature.Value != current)
                bridgeProofCompatible = false;
            if (_edges[candidate.EdgePath[0]].Kind == EdgeKind.Suffix)
            {
                if (!TryGetSuffixBridgeRelation(_edges[candidate.EdgePath[0]], out SuffixBridgeRelation relation))
                    bridgeProofCompatible = false;
                else if (suffixRelation is null)
                    suffixRelation = relation;
                else
                    bridgeProofCompatible &= suffixRelation.Value == relation;
            }
            if (bestBridge is null || CandidateOrder.Compare(candidate, bestBridge) < 0)
                bestBridge = candidate;
            applicationCandidates.Add(candidate);
        }

        var graphCandidates = new BoundedGraphCandidatePool(bounds.MaxGraphCandidates);
        var productiveCandidates = search is null ? null : new BoundedGraphCandidatePool(2048);
        ConditionalSupportInventory? conditionalSupport = null;
        (string Base, string Edge)? ordinaryProof = null;
        bool ordinaryProofCompatible = true;
        int ordinaryProofCount = 0;
        bool oldProof = false, oldDepthLimited = false;
        bool hardRefusal = false;
        WordDecomposition Refuse() { hardRefusal = true; search?.Refuse(); return Empty(surface, normalized); }
        bool AddConditionalCandidate(PathCandidate candidate)
        {
            bool supportIncomplete = false;
            if (productiveCandidates is null)
                return false;
            string[] fullTags = candidate.ConditionalFullTag is string selectedTag
                ? [selectedTag]
                : candidate.ConditionalTerminalEdge >= 0
                    ? _conditionalFullTags[candidate.ConditionalTerminalEdge]
                    : candidate.EdgePath.Length > 0 ? _conditionalFullTags[candidate.EdgePath[^1]] : [];
            bool added = false;
            foreach (string fullTag in fullTags)
            {
                bool preparedOk = _conditionalProof.TryPrepare(candidate, fullTag, lexicon,
                    ref conditionalSupport, graphBudget, out PathCandidate? conditional, out supportIncomplete);
                if (preparedOk)
                {
                    productiveCandidates.Add(conditional! with { PublicProjection = ToPublic(conditional!, lexicon) });
                    added = true;
                }
                else if (supportIncomplete)
                    search!.Incomplete = true;
            }
            return added;
        }
        void AddGraphCandidate(PathCandidate candidate)
        {
            if (productiveCandidates is not null && candidate.FromStateId is not null && candidate.ToStateId is not null &&
                candidate.EdgePath.All(index => _edges[index].Origin != EdgeOrigin.Application && Approved(_edges[index])))
            {
                candidate = candidate with { Projection = ProjectMorphology(candidate, lexicon) };
                MorphPolicy.Result projection = candidate.Projection!.Value;
                if (projection.HasRoot)
                {
                    search!.Incomplete |= projection.Outcome == MorphPolicy.Outcome.MetadataMissing;
                    if (projection.Outcome == MorphPolicy.Outcome.Licensed)
                        productiveCandidates.Add(candidate with { PublicProjection = ToPublic(candidate, lexicon) });
                }
            }
            oldProof = true;
            if (candidate.EdgePath.Any(index => _edges[index].Origin == EdgeOrigin.Paradigm))
            {
                bool equivalentSuffix = suffixRelation is { } relation && bridgeSignature is { } signature &&
                    TryMatchEquivalentSuffixRepresentation(candidate, signature, relation, lexicon);
                if (++ordinaryProofCount > bounds.MaxExpandedStates ||
                    candidate.EdgePath.Length != 1 && !equivalentSuffix)
                    ordinaryProofCompatible = false;
                else if (candidate.EdgePath.Length == 1)
                {
                    (string Base, string Edge) current =
                        (candidate.Base, _edges[candidate.EdgePath[0]].Id);
                    if (ordinaryProof is null)
                        ordinaryProof = current;
                    else
                        ordinaryProofCompatible &= ordinaryProof.Value == current;
                }
            }
            graphCandidates.Add(candidate);
        }
        var nodes = new List<SearchNode>();
        var frontier = new Queue<int>();
        // Pending proofs reference existing nodes; they do not occupy the proven-result pool.
        List<int>? deferredConditionalNodes = search is null ? null : [];
        int expanded = 0;
        bool structuralDepthLimited = false;
        void SeedConditionalBases()
        {
            if (search is not null && !FindExactSelfReadings(lexicon, normalized).Any())
            {
                // CompilePrograms keeps every state's programs contiguous.
                string? seededState = null;
                foreach (ConditionalProgram program in _conditionalSeeds.Match(normalized, graphBudget))
                {
                    if (program.StateId == seededState)
                        continue;
                    bool canStart = false;
                    foreach (ConditionalBranch branch in program.Branches)
                    {
                        if (!CanStartConditionalBase(branch, normalized, graphBudget))
                            continue;
                        canStart = true;
                        break;
                    }
                    if (!canStart)
                        continue;
                    string stateId = program.StateId;
                    seededState = stateId;
                    if (nodes.Count >= bounds.MaxExpandedStates)
                    {
                        search.Incomplete = true;
                        return;
                    }
                    graphBudget?.Take();
                    nodes.Add(new SearchNode(normalized, StateConstraint.Exact(stateId), -1, -1, 0, normalized, true));
                    int seedIndex = nodes.Count - 1;
                    deferredConditionalNodes!.Add(seedIndex);
                    frontier.Enqueue(seedIndex);
                }
            }
        }
        void ExpandFrontier(bool conditionalPhase)
        {
            while (frontier.Count > 0 && expanded < bounds.MaxExpandedStates)
            {
                if (graphBudget is not null && !graphBudget.TryTake())
                {
                    if (search is not null) search.Incomplete = true;
                    break;
                }
                int nodeIndex = frontier.Dequeue();
                SearchNode node = nodes[nodeIndex];
                expanded++;
                if (node.Depth >= bounds.MaxDepth)
                {
                    oldDepthLimited = true;
                    if (search is null) continue;
                }
                foreach (int edgeIndex in GetIncomingEdges(node.State))
                {
                    graphBudget?.Take();
                    Edge edge = _edges[edgeIndex];
                    if (edge.Trust < ReviewTrust.Reviewed ||
                        edge.BlockedSurfaces.Contains(node.Surface, StringComparer.Ordinal) ||
                        !AllowsPrefix(nodeIndex, edgeIndex, nodes, TraversalDirection.Reverse) ||
                        !edge.StateBinding.TryTraverse(
                            TraversalDirection.Reverse,
                            node.State,
                            out StateConstraint previousConstraint))
                    {
                        continue;
                    }
                    foreach (string previousSurface in ApplyOperations(node.Surface, edge, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget))
                    {
                        if (edge.BlockedSurfaces.Contains(previousSurface, StringComparer.Ordinal) ||
                            HasIncompatibleExactPrefixHistory(edge, previousSurface, previousConstraint, lexicon))
                        {
                            continue;
                        }
                        foreach (StateRefinement refined in RefineAgainstExactAnchors(
                                     previousSurface,
                                     previousConstraint,
                                     lexicon))
                        {
                            if (HasVisited(nodeIndex, refined.Constraint, previousSurface, nodes))
                                continue;
                            if (refined.HasExactAnchor && HasReversePathExactOutputConflict(
                                    nodes,
                                    refined.Constraint,
                                    nodeIndex,
                                    edgeIndex,
                                    normalized,
                                    lexicon))
                            {
                                continue;
                            }
                            if (node.Depth >= bounds.MaxDepth)
                            {
                                search!.Incomplete = true;
                                structuralDepthLimited = true;
                                continue;
                            }
                            if (nodes.Count >= bounds.MaxExpandedStates)
                            {
                                if (!conditionalPhase)
                                    throw new InvalidDataException("Reverse graph candidates exceed the graph search-state bound.");
                                search!.Incomplete = true;
                                return;
                            }
                            graphBudget?.Take();
                            nodes.Add(new SearchNode(
                                previousSurface,
                                refined.Constraint,
                                nodeIndex,
                                edgeIndex,
                                node.Depth + 1,
                                previousSurface,
                                node.ConditionalBaseSeed));
                            int previousIndex = nodes.Count - 1;
                            if (node.ConditionalBaseSeed)
                            {
                                if (refined.Constraint.IsExact && !refined.HasExactAnchor)
                                    deferredConditionalNodes!.Add(previousIndex);
                            }
                            else if (refined.HasExactAnchor)
                                AddGraphCandidate(CreatePathCandidate(nodes, previousIndex, forward: false, budget: graphBudget));
                            else if (refined.Constraint.IsExact)
                            {
                                deferredConditionalNodes?.Add(previousIndex);
                            }
                            frontier.Enqueue(previousIndex);
                        }
                    }
                }
            }
        }
        WordDecomposition FinishOrdinaryDiscovery()
        {
            if (binaryApplicable && (frontier.Count > 0 || oldDepthLimited)) return Refuse();
            if (binary is not null) return oldProof ? Refuse() : ToDecomposition(surface, normalized, binary);
            PathCandidate[]? ranked = Rank(applicationCandidates.Values.Concat(graphCandidates.Values)).ToArray();
            ranked = ResolveCompoundParadigmRootAmbiguity(
                ranked, bridgeSignature, bestBridge, ordinaryProof,
                bridgeProofCompatible && ordinaryProofCompatible);
            if (ranked is null)
                return Refuse();
            search?.Complete(Rank(productiveCandidates!.Values).Select(candidate => candidate.PublicProjection!),
                productiveCandidates.Lost || graphCandidates.Lost || applicationCandidates.Lost || frontier.Count > 0);
            if (search is not null && (frontier.Count > 0 || structuralDepthLimited))
                return Empty(surface, normalized);
            bool pure = search is not null && ranked.Length > 0 && ranked.All(candidate =>
                !candidate.HasReviewedApplication && candidate.Projection?.Outcome == MorphPolicy.Outcome.Licensed);
            ranked = ResolveLexicalizedParadigmRootAmbiguity(ranked, graphBudget);
            if (ranked is null)
                return pure ? Empty(surface, normalized) : Refuse();
            if (ranked
                    .Where(candidate => candidate.EdgePath.Any(index => _edges[index].Origin == EdgeOrigin.Paradigm))
                    .Select(candidate => candidate.Base)
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .Count() > 1)
            {
                return pure ? Empty(surface, normalized) : Refuse();
            }
            PathCandidate? selected = ranked.FirstOrDefault();
            if (selected is null)
                return Empty(surface, normalized);
            WordDecomposition decomposition = ToDecomposition(surface, normalized, selected);
            decomposition = ProjectReviewedInflectedApplication(decomposition, normalized, selected, lexicon);
            return ProjectReviewedComposedInflectedApplication(decomposition, normalized, selected, lexicon);
        }

        WordDecomposition? ordinaryResult = null;
        try
        {
            (string Lemma, string StateId, int Edge)[] orderedParadigmSeeds = _paradigms.Reverse(normalized, budget: graphBudget)
                .Take(bounds.MaxExpandedStates + 1).Distinct()
                .OrderBy(item => item.StateId, StringComparer.Ordinal)
                .ThenBy(item => item.Lemma, StringComparer.Ordinal)
                .ThenBy(item => _edges[item.Edge].Id, StringComparer.Ordinal)
                .ToArray();
            if (orderedParadigmSeeds.Length > bounds.MaxExpandedStates)
                throw new InvalidDataException("Paradigm reverse candidates exceed the graph search-state bound.");
            foreach ((string lemma, string stateId, int edgeIndex) in orderedParadigmSeeds)
            {
                Edge paradigm = _edges[edgeIndex];
                if (!HasParadigmBaseAuthority(paradigm, lemma, lexicon, allowOov: true))
                    continue;
                foreach (StateRefinement refined in RefineAgainstExactAnchors(
                             lemma,
                             StateConstraint.Exact(stateId),
                             lexicon))
                {
                    if (refined.HasExactAnchor && HasReversePathExactOutputConflict(
                            nodes,
                            refined.Constraint,
                            parentIndex: -1,
                            edgeIndex,
                            normalized,
                            lexicon))
                    {
                        continue;
                    }
                    if (nodes.Count >= bounds.MaxExpandedStates)
                        throw new InvalidDataException("Paradigm reverse candidates exceed the graph search-state bound.");
                    graphBudget?.Take();
                    nodes.Add(new SearchNode(lemma, refined.Constraint, -1, edgeIndex, 1, lemma));
                    int index = nodes.Count - 1;
                    if (refined.HasExactAnchor)
                        AddGraphCandidate(CreatePathCandidate(nodes, index, forward: false, budget: graphBudget));
                    else if (refined.Constraint.IsExact)
                    {
                        deferredConditionalNodes?.Add(index);
                    }
                    frontier.Enqueue(index);
                }
            }
            foreach (int edgeIndex in _genericEdges)
            {
                graphBudget?.Take();
                Edge edge = _edges[edgeIndex];
                if (edge.Trust < ReviewTrust.Reviewed ||
                    edge.BlockedSurfaces.Contains(normalized, StringComparer.Ordinal))
                {
                    continue;
                }
                if (!edge.StateBinding.TryTraverse(
                        TraversalDirection.Reverse,
                        edge.StateBinding.OutputConstraint,
                        out StateConstraint previousConstraint))
                {
                    continue;
                }
                foreach (string previousSurface in ApplyOperations(normalized, edge, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget))
                {
                    if (edge.BlockedSurfaces.Contains(previousSurface, StringComparer.Ordinal))
                        continue;
                    foreach (StateRefinement refined in RefineAgainstExactAnchors(
                                 previousSurface,
                                 previousConstraint,
                                 lexicon))
                    {
                        if (HasIncompatibleExactPrefixHistory(edge, previousSurface, refined.Constraint, lexicon))
                            continue;
                        if (refined.HasExactAnchor && HasReversePathExactOutputConflict(
                                nodes,
                                refined.Constraint,
                                parentIndex: -1,
                                edgeIndex,
                                normalized,
                                lexicon))
                        {
                            continue;
                        }
                        if (nodes.Count >= bounds.MaxExpandedStates)
                            throw new InvalidDataException("Reverse graph seeds exceed the graph search-state bound.");
                        graphBudget?.Take();
                        nodes.Add(new SearchNode(previousSurface, refined.Constraint, -1, edgeIndex, 1, previousSurface));
                        int index = nodes.Count - 1;
                        if (refined.HasExactAnchor)
                            AddGraphCandidate(CreatePathCandidate(nodes, index, forward: false, budget: graphBudget));
                        frontier.Enqueue(index);
                    }
                }
            }

            ExpandFrontier(conditionalPhase: false);
            ordinaryResult = FinishOrdinaryDiscovery();
        }
        catch (GraphSearchBudget.Exhausted)
        {
            if (search is not null) search.Incomplete = true;
        }
        if (ordinaryResult is null)
        {
            search?.Complete(Rank(productiveCandidates!.Values).Select(candidate => candidate.PublicProjection!), true);
            return Empty(surface, normalized);
        }
        if (search is null || hardRefusal || binary is not null)
            return ordinaryResult;

        // Ordinary replay and ambiguity are final before optional donor work can exhaust the same budget.
        try
        {
            SeedConditionalBases();
            ExpandFrontier(conditionalPhase: true);
            foreach (int index in deferredConditionalNodes!)
            {
                if (nodes[index].ConditionalBaseSeed)
                    AddConditionalBaseCandidates(nodes, index, normalized, lexicon,
                        candidate => { AddConditionalCandidate(candidate); });
                else
                    AddConditionalCandidate(CreatePathCandidate(nodes, index, forward: false, budget: graphBudget) with { HasExactRoot = false });
            }
        }
        catch (GraphSearchBudget.Exhausted)
        {
            search.Incomplete = true;
        }
        catch (BinaryProofBudget.Failure failure) when (failure.Reason == BinaryProofBudget.Outcome.WorkExhausted)
        {
            search.Incomplete = true;
        }
        search.Complete(Rank(productiveCandidates!.Values).Select(candidate => candidate.PublicProjection!),
            productiveCandidates.Lost || graphCandidates.Lost || applicationCandidates.Lost || frontier.Count > 0);
        return ordinaryResult;
    }

    private PathCandidate[]? ResolveCompoundParadigmRootAmbiguity(
        PathCandidate[] ranked,
        CompoundBridgeSignature? bridgeSignature,
        PathCandidate? bestBridge,
        (string Base, string Edge)? ordinaryProof,
        bool proofComplete)
    {
        if (bridgeSignature is null)
            return ranked;
        if (!proofComplete || bestBridge is null ||
            ordinaryProof is { } ordinary &&
            (ordinary.Base != bridgeSignature.Value.Intermediate ||
             ordinary.Edge != bridgeSignature.Value.ParadigmEdge))
            return null;
        CompoundBridgeSignature signature = bridgeSignature.Value;
        return Rank(ranked.Concat([bestBridge])
                .Where(candidate => !IsDirectCompoundParadigmRepresentation(candidate, signature)))
            .ToArray();
    }

    private CompoundBridgeSignature GetCompoundBridgeSignature(PathCandidate candidate)
        => new(candidate.Base, _edges[candidate.EdgePath[0]].SurfaceKeys![0],
            _edges[candidate.EdgePath[^1]].StateBinding.FromStateId!,
            _edges[candidate.EdgePath[^1]].RequiredBaseFullTag!, _edges[candidate.EdgePath[^1]].Id);

    private bool IsDirectCompoundParadigmRepresentation(PathCandidate candidate, CompoundBridgeSignature signature) =>
        candidate.EdgePath.Length == 1 && candidate.Base == signature.Intermediate &&
        _edges[candidate.EdgePath[0]].Origin == EdgeOrigin.Paradigm &&
        _edges[candidate.EdgePath[0]].Id == signature.ParadigmEdge;

    private PathCandidate[]? ResolveLexicalizedParadigmRootAmbiguity(PathCandidate[] ranked, GraphSearchBudget? budget = null)
    {
        PathCandidate[] paradigmCandidates = ranked
            .Where(candidate => candidate.EdgePath.Any(index => _edges[index].Origin == EdgeOrigin.Paradigm))
            .ToArray();
        if (paradigmCandidates
                .Select(candidate => candidate.Base)
                .Distinct(StringComparer.Ordinal)
                .Take(2)
                .Count() <= 1)
        {
            return ranked;
        }

        PathCandidate[] direct = paradigmCandidates
            .Where(candidate =>
                candidate.EdgePath.Length == 1 &&
                _edges[candidate.EdgePath[0]].Origin == EdgeOrigin.Paradigm &&
                _edges[candidate.EdgePath[0]].Kind == EdgeKind.Paradigm)
            .Take(2)
            .ToArray();
        if (direct.Length != 1)
            return null;

        string directBase = direct[0].Base;
        int terminalEdge = direct[0].EdgePath[0];
        Edge terminal = _edges[terminalEdge];
        if (!Approved(terminal))
            return null;
        foreach (PathCandidate candidate in ranked.Where(item =>
                     !string.Equals(item.Base, directBase, StringComparison.Ordinal)))
        {
            if (candidate.EdgePath.Length < 2 || candidate.EdgePath[^1] != terminalEdge)
                return null;

            string replay = candidate.Base;
            foreach (int edgeIndex in candidate.EdgePath[..^1])
            {
                Edge edge = _edges[edgeIndex];
                if (edge.Origin != EdgeOrigin.Generic || edge.Kind != EdgeKind.Prefix ||
                !Approved(edge))
                {
                    return null;
                }
                string[] next = ApplyOperations(replay, edge, forward: true, budget: budget)
                    .Distinct(StringComparer.Ordinal)
                    .Take(2)
                    .ToArray();
                if (next.Length != 1)
                    return null;
                replay = next[0];
            }
            if (!string.Equals(replay, directBase, StringComparison.Ordinal))
                return null;
        }
        return ranked.Where(candidate => string.Equals(candidate.Base, directBase, StringComparison.Ordinal)).ToArray();
    }

    private IEnumerable<PathCandidate> FindApplicationCompositions(
        string baseLemma,
        IRuntimeLexicon lexicon)
    {
        if (!_applicationEdgesByLemma.TryGetValue(baseLemma, out int[]? edgeIndices))
            yield break;
        foreach (int edgeIndex in edgeIndices)
        {
            Edge edge = _edges[edgeIndex];
            if (!Approved(edge))
                continue;
            if (!IsAdmissibleProductiveCompound(edge, baseLemma, lexicon))
                continue;
            string output = edge.SurfaceKeys![0];
            if (edge.BlockedSurfaces.Contains(output, StringComparer.Ordinal))
                continue;
            yield return CreateApplicationCandidate(edgeIndex, edge, baseLemma, output);
            if (!TryGetExactApplicationOutputAuthority(edge, lexicon, out string stateId, out string fullTag) ||
                !_paradigmEdgesByState.TryGetValue(stateId, out int[]? paradigmIndices))
                continue;
            foreach (int paradigmIndex in paradigmIndices)
            {
                Edge paradigm = _edges[paradigmIndex];
                string? anchor = null;
                if (edge.Kind == EdgeKind.Suffix &&
                    (!TryFindExactBridgeApplicationBase(edge, lexicon, out anchor) ||
                     !string.Equals(anchor, baseLemma, StringComparison.Ordinal)))
                    continue;
                if (!Approved(paradigm) ||
                    paradigm.RequiredBaseFullTag != fullTag ||
                    !IsAppendOnlyParadigm(paradigm, output, out string form, out _, (lexicon as GraphWorkLexicon)?.Budget) ||
                    !IsEligibleApplicationParadigmBridge(edgeIndex, baseLemma, output, form, lexicon, anchor) ||
                    !HasCompatibleExactInflectedOutput(form, output, stateId, paradigm, lexicon))
                    continue;
                yield return CreateApplicationParadigmCandidate(
                    edgeIndex, edge, paradigmIndex, paradigm, baseLemma, form);
            }
        }
    }

    private IEnumerable<PathCandidate> FindApplicationDecompositions(string normalized, IRuntimeLexicon lexicon)
    {
        if (_applicationEdgesBySurface.TryGetValue(normalized, out int[]? edgeIndices))
        {
            foreach (int edgeIndex in edgeIndices)
            {
                Edge edge = _edges[edgeIndex];
                if (edge.Trust < ReviewTrust.Reviewed ||
                    edge.BlockedSurfaces.Contains(normalized, StringComparer.Ordinal) ||
                    !IsAdmissibleProductiveCompound(edge, null, lexicon))
                    continue;
                string[] bases = FindExactApplicationBases(edge.Kind == EdgeKind.Compound && edge.GenerationEligible ?
                    GetSlots(edge).Where(slot => slot.Role == "compound_left") : GetSlots(edge), lexicon);
                if (bases.Length == 1)
                    yield return CreateApplicationCandidate(edgeIndex, edge, bases[0], normalized);
            }
        }
        foreach (int paradigmIndex in _paradigms.ReverseCandidateEdges(normalized))
        {
            Edge paradigm = _edges[paradigmIndex];
            if (!Approved(paradigm) ||
                paradigm.OperationCount is < 1 or > 2)
                continue;
            SurfaceOperation tail = _operations[paradigm.OperationOffset + paradigm.OperationCount - 1];
            if (tail.Kind != OperationKind.AppendLiteral)
                continue;
            string ending = _literals[tail.OperandIndex];
            if (ending.Length == 0 || normalized.Length <= ending.Length ||
                !normalized.EndsWith(ending, StringComparison.Ordinal))
                continue;
            string intermediate = normalized[..^ending.Length];
            if (!IsAppendOnlyParadigm(paradigm, intermediate, out string form, out _, (lexicon as GraphWorkLexicon)?.Budget) ||
                form != normalized ||
                !_applicationEdgesBySurface.TryGetValue(intermediate, out int[]? applicationIndices))
                continue;
            string stateId = paradigm.StateBinding.FromStateId!;
            foreach (int applicationIndex in applicationIndices)
            {
                Edge application = _edges[applicationIndex];
                string? baseLemma = null;
                if (application.Kind == EdgeKind.Compound)
                {
                    string[] bases = FindExactApplicationBases(GetSlots(application).Where(slot => slot.Role == "compound_left"), lexicon);
                    if (bases.Length == 1) baseLemma = bases[0];
                }
                else
                {
                    if (!Approved(application) ||
                        application.ApplicationDecision is not { Transparency: "transparent", BlockReason: null } ||
                        application.SurfaceKeys is not { Length: 1 } keys || keys[0] != intermediate ||
                        application.BlockedSurfaces.Contains(intermediate, StringComparer.Ordinal) ||
                        application.BlockedSurfaces.Contains(normalized, StringComparer.Ordinal) ||
                        !TryFindExactBridgeApplicationBase(application, lexicon, out baseLemma))
                        continue;
                }
                if (baseLemma is null ||
                    !IsEligibleApplicationParadigmBridge(applicationIndex, baseLemma, intermediate, normalized, lexicon, baseLemma) ||
                    !TryGetExactApplicationOutputAuthority(application, lexicon, out string outputState, out string fullTag) ||
                    outputState != stateId || fullTag != paradigm.RequiredBaseFullTag ||
                    !HasCompatibleExactInflectedOutput(normalized, intermediate, stateId, paradigm, lexicon))
                    continue;
                yield return CreateApplicationParadigmCandidate(
                    applicationIndex, application, paradigmIndex, paradigm, baseLemma, normalized);
            }
        }
    }

    private bool TryDecomposeLexicalizedApplication(
        string surface,
        string normalized,
        IRuntimeLexicon lexicon,
        out WordDecomposition? decomposition)
    {
        decomposition = null;
        if (!_applicationEdgesBySurface.TryGetValue(normalized, out int[]? edgeIndices))
            return false;
        int[] lexicalized = edgeIndices
            .Where(index => _edges[index].ApplicationDecision?.Transparency == "lexicalized")
            .ToArray();
        if (lexicalized.Length == 0)
            return false;
        if (edgeIndices.Length != 1 || lexicalized.Length != 1)
            return true;

        Edge edge = _edges[lexicalized[0]];
        ApplicationDecision decision = edge.ApplicationDecision!.Value;
        InputSlot[] anchors = GetSlots(edge).Where(slot => slot.IsLexicalAnchor).ToArray();
        WordReading[] exactOutput = lexicon.LookupExactNormalized(normalized, int.MaxValue)
            .Where(reading => string.Equals(
                BulgarianTextNormalizer.NormalizeKey(reading.Form), normalized, StringComparison.Ordinal))
            .ToArray();
        if (edge.Trust != ReviewTrust.Blocked || string.IsNullOrWhiteSpace(decision.BlockReason) ||
            decision.OutputPartOfSpeech is null || anchors.Length == 0 ||
            anchors.Any(slot => !HasExactSelfReading(lexicon, NormalizeLemmaSlot(slot.NodeId))) ||
            exactOutput.Length == 0 || exactOutput.Any(reading =>
                !string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Lemma), normalized, StringComparison.Ordinal) ||
                TryCreateStateId(reading.PartOfSpeech) is not string stateId ||
                !string.Equals(StatePartOfSpeech(stateId), decision.OutputPartOfSpeech, StringComparison.Ordinal)))
        {
            return true;
        }

        decomposition = new WordDecomposition(
            surface,
            normalized,
            null,
            _segmentation.BuildApplicationSegments(normalized, edge),
            edge.ApplicationId,
            edge.RuleId,
            LexicalizedEvidenceConfidence,
            CollectEvidence([edge], edge.Id),
            _sourceId)
        {
            Transparency = decision.Transparency,
            ProductiveGeneration = false
        };
        return true;
    }

    private PathCandidate CreateApplicationCandidate(
        int edgeIndex,
        Edge edge,
        string publicBase,
        string word) =>
        new(
            word,
            publicBase,
            null,
            null,
            [edgeIndex],
            edge.ApplicationIds,
            edge.Trust,
            edge.SupportCount,
            false,
            true,
            edge.Id);

    private static PathCandidate CreateApplicationParadigmCandidate(
        int applicationIndex, Edge application, int paradigmIndex, Edge paradigm,
        string publicBase, string word) => new(
            word, publicBase, null, null, [applicationIndex, paradigmIndex], application.ApplicationIds,
            LeastTrust(application.Trust, paradigm.Trust), application.SupportCount + paradigm.SupportCount,
            false, true, application.Id + ">" + paradigm.Id);

    private PathCandidate CreatePathCandidate(
        IReadOnlyList<SearchNode> nodes,
        int nodeIndex,
        bool forward,
        int terminalEdge = -1,
        string? terminalSurface = null, GraphSearchBudget? budget = null)
    {
        List<int> path = [];
        int current = nodeIndex;
        while (current >= 0)
        {
            SearchNode node = nodes[current];
            if (node.Edge >= 0)
                path.Add(node.Edge);
            current = node.Parent;
        }
        if (forward)
        {
            path.Reverse();
            if (terminalEdge >= 0)
                path.Add(terminalEdge);
        }
        string baseLemma = forward ? nodes[nodeIndex].Base : nodes[nodeIndex].Surface;
        string word = forward ? terminalSurface ?? nodes[nodeIndex].Surface : ReplayPath(baseLemma, path, budget);
        string? fromStateId = forward
            ? RootStateId(nodes, nodeIndex)
            : nodes[nodeIndex].State.ExactStateId ??
              throw new InvalidDataException("A reverse graph candidate must terminate at an exact typed anchor.");
        string? toStateId = fromStateId;
        if (path.Count == 0)
            throw new InvalidOperationException("Sequence contains no elements");
        ReviewTrust trust = _edges[path[0]].Trust;
        int support = 0;
        bool exactBindings = true;
        bool rootFeaturesPreserved = fromStateId is not null;
        List<string>? applications = null;
        string[] ids = new string[path.Count];
        for (int index = 0; index < path.Count; index++)
        {
            Edge edge = _edges[path[index]];
            if (rootFeaturesPreserved && edge.Origin != EdgeOrigin.Paradigm &&
                (!edge.StateBinding.TryTraverse(TraversalDirection.Forward, StateConstraint.Exact(fromStateId!), out StateConstraint rootNext) ||
                 rootNext.ExactStateId != fromStateId))
                rootFeaturesPreserved = false;
            if (toStateId is not null)
                toStateId = edge.StateBinding.TryTraverse(TraversalDirection.Forward,
                    StateConstraint.Exact(toStateId), out StateConstraint nextState) ? nextState.ExactStateId : null;
            if (edge.Trust < trust) trust = edge.Trust;
            support = checked(support + edge.SupportCount);
            exactBindings &= !edge.StateBinding.PreservesMatchingState;
            if (edge.ApplicationIds.Length > 0) (applications ??= []).AddRange(edge.ApplicationIds);
            ids[index] = edge.Id;
        }
        return new PathCandidate(
            word, baseLemma, fromStateId, toStateId, path.ToArray(),
            applications?.Distinct(StringComparer.Ordinal).ToArray() ?? [],
            trust, support, exactBindings, false, string.Join('>', ids))
        { RootFeaturesPreserved = rootFeaturesPreserved, HasExactRoot = true };
    }

    private void AddConditionalBaseCandidates(
        IReadOnlyList<SearchNode> nodes,
        int nodeIndex,
        string target,
        IRuntimeLexicon lexicon,
        Action<PathCandidate> add)
    {
        SearchNode node = nodes[nodeIndex];
        if (node.State.ExactStateId is not string stateId)
            return;
        foreach (ConditionalProgram program in _conditionalPrograms.Where(item => item.StateId == stateId))
        {
            // Distinct compiled branches keep distinct donor authority.
            foreach (ConditionalBranch branch in program.Branches)
            {
                (lexicon as GraphWorkLexicon)?.Budget.Take();
                Transform transform = _transforms[branch.TransformIndex];
                if (!transform.CanApplyForward(node.Surface))
                    continue;
                PathCandidate candidate = node.Edge < 0
                    ? ConditionalZeroCandidate(node.Surface, target, [], program, branch)
                    : CreatePathCandidate(nodes, nodeIndex, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget) with
                    {
                        HasExactRoot = false,
                        RootFeaturesPreserved = true,
                        ConditionalProgramId = program.RuleId,
                        ConditionalBranchId = branch.Id,
                        ConditionalBaseFullTag = program.BaseTag,
                        ConditionalCellId = "base",
                        ConditionalFullTag = program.BaseTag
                    };
                add(candidate);
            }
        }
    }

    private PathCandidate ConditionalZeroCandidate(
        string root,
        string word,
        int[] path,
        ConditionalProgram program,
        ConditionalBranch branch)
    {
        string[] ids = path.Select(index => _edges[index].Id).ToArray();
        return new PathCandidate(
            word,
            root,
            program.StateId,
            program.StateId,
            path,
            [],
            path.Select(index => _edges[index].Trust).DefaultIfEmpty(branch.Trust).Min(),
            path.Select(index => _edges[index].SupportCount).DefaultIfEmpty(branch.SupportCount).Sum(),
            path.All(index => !_edges[index].StateBinding.PreservesMatchingState),
            false,
            "conditional-zero|" + program.RuleId + "|base|" + string.Join('>', ids))
        {
            HasExactRoot = false,
            RootFeaturesPreserved = true,
            ConditionalProgramId = program.RuleId,
            ConditionalBranchId = branch.Id,
            ConditionalBaseFullTag = program.BaseTag,
            ConditionalCellId = "base",
            ConditionalFullTag = program.BaseTag
        };
    }

    private bool CanStartConditionalBase(ConditionalBranch branch, string surface, GraphSearchBudget? budget)
    {
        budget?.Take();
        return _transforms[branch.TransformIndex].CanApplyForward(surface);
    }

    private bool IsConditionalProgram(Edge edge) => ConditionalRootProof.IsConditionalProgram(edge, _transforms);

    internal static IEnumerable<PathCandidate> Rank(IEnumerable<PathCandidate> candidates, CandidateRanking ranking = CandidateRanking.Inverse)
    {
        PathCandidate[] ordered = candidates
            .DistinctBy(item => item.Identity)
            .OrderBy(item => item, OrderFor(ranking))
            .ToArray();
        // Forward depth precedes authority; diversity stays within each selected tier.
        foreach (var tier in ordered.GroupBy(candidate => TierFor(candidate, ranking)))
        {
            var groups = new HashSet<CandidateGroupKey>();
            var yielded = new HashSet<CandidateKey>();
            foreach (PathCandidate candidate in tier)
            {
                if (groups.Add(candidate.GroupIdentity))
                {
                    yielded.Add(candidate.Identity);
                    yield return candidate;
                }
            }
            foreach (PathCandidate candidate in tier)
            {
                if (yielded.Add(candidate.Identity))
                    yield return candidate;
            }
        }
    }

    private static int CompareCandidates(PathCandidate left, PathCandidate right)
    {
        int comparison = left.IsConditional.CompareTo(right.IsConditional);
        if (comparison != 0)
            return comparison;
        comparison = ((int)right.Trust).CompareTo((int)left.Trust);
        if (comparison != 0)
            return comparison;
        comparison = right.HasReviewedApplication.CompareTo(left.HasReviewedApplication);
        if (comparison != 0)
            return comparison;
        comparison = right.HasOnlyExactStateBindings.CompareTo(left.HasOnlyExactStateBindings);
        if (comparison != 0)
            return comparison;
        comparison = right.SupportCount.CompareTo(left.SupportCount);
        if (comparison != 0)
            return comparison;
        comparison = left.EdgePath.Length.CompareTo(right.EdgePath.Length);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Word, right.Word);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.Base, right.Base);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.FromStateId, right.FromStateId);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.ToStateId, right.ToStateId);
        if (comparison != 0)
            return comparison;
        comparison = StringComparer.Ordinal.Compare(left.ConditionalFullTag, right.ConditionalFullTag);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.PathId, right.PathId);
    }

    internal static bool Approved(Edge edge) => edge.GenerationEligible && edge.Trust == ReviewTrust.Accepted;

    private WordDerivation ToPublic(PathCandidate candidate, IRuntimeLexicon lexicon)
    {
        IEnumerable<Edge> edges = candidate.EdgePath.Select(index => _edges[index]);
        MorphPolicy.Result morphology = candidate.IsConditional
            ? new(MorphPolicy.Outcome.Incompatible, null, [], false)
            : candidate.Projection ?? ProjectMorphology(candidate, lexicon);
        WordDerivation result = new(
            candidate.Word,
            candidate.Base,
            candidate.FromStateId,
            candidate.ToStateId,
            BuildSegments(candidate, (lexicon as GraphWorkLexicon)?.Budget),
            edges.Select(item => item.Id).ToArray(),
            edges.Select(item => item.RuleId).OfType<string>()
                .Concat(candidate.ConditionalProgramId is string program ? [program] : [])
                .Distinct(StringComparer.Ordinal).ToArray(),
            candidate.ApplicationIds.ToArray(),
            Confidence(candidate),
            CollectEvidence(edges, candidate.PathId, candidate),
            _sourceId);
        if (candidate.IsConditional)
        {
            Edge? terminal = candidate.ConditionalTerminalEdge >= 0
                ? _edges[candidate.ConditionalTerminalEdge]
                : candidate.EdgePath.Length > 0 ? _edges[candidate.EdgePath[^1]] : null;
            bool terminalInPath = candidate.EdgePath.Length > 0 &&
                _edges[candidate.EdgePath[^1]].Origin == EdgeOrigin.Paradigm;
            string derivedLemma = terminalInPath
                ? ReplayPath(candidate.Base, candidate.EdgePath[..^1], (lexicon as GraphWorkLexicon)?.Budget)
                : candidate.EdgePath.Length == 0 ? candidate.Base : ReplayPath(candidate.Base, candidate.EdgePath, (lexicon as GraphWorkLexicon)?.Budget);
            return result with
            {
                DerivedLemma = derivedLemma,
                FullTags = candidate.ConditionalFullTag is string selectedTag
                    ? [selectedTag]
                    : terminal?.AllowedFullTags?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                IsConditional = true,
                ConditionalRootProgramId = candidate.ConditionalProgramId,
                ConditionalRootBaseFullTag = candidate.ConditionalBaseFullTag,
                ConditionalRootSupportCount = candidate.ConditionalSupportCount,
                ConditionalSupportingLemmas = candidate.SupportingLemmas.ToArray(),
                UnmetExactSelfReadingRequirements = candidate.UnmetRequirements
            };
        }
        return morphology.Outcome == MorphPolicy.Outcome.Licensed
            ? result with { DerivedLemma = morphology.Lemma, FullTags = morphology.Tags }
            : result;
    }

    private MorphPolicy.Result ProjectMorphology(PathCandidate candidate, IRuntimeLexicon lexicon)
    {
        if (candidate.Binary is not null || candidate.ToStateId is null)
            return new(MorphPolicy.Outcome.Incompatible, null, [], false);
        Edge last = _edges[candidate.EdgePath[^1]];
        string lemma = last.Origin == EdgeOrigin.Paradigm ? ReplayPath(candidate.Base, candidate.EdgePath[..^1], (lexicon as GraphWorkLexicon)?.Budget) : candidate.Word;
        string[] rootTags = FindExactSelfReadings(lexicon, candidate.Base)
            .Where(reading => TryCreateStateId(reading.PartOfSpeech) == candidate.FromStateId)
            .Select(reading => reading.PartOfSpeech!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        bool terminal = last.Origin == EdgeOrigin.Paradigm;
        bool preservesRootFeatures = candidate.RootFeaturesPreserved;
        string[] terminalTags = terminal ? last.AllowedFullTags ?? [] : [];
        MorphPolicy.Program[] programs = terminal ? [] : GetParadigmEdges(candidate.ToStateId).Select(index =>
        {
            Edge edge = _edges[index];
            return new MorphPolicy.Program(edge.RequiredBaseFullTag, Approved(edge), edge.ParadigmStemTransform);
        }).Concat(_invariantMorphologyPrograms.GetValueOrDefault(candidate.ToStateId) ?? []).ToArray();
        return MorphPolicy.Evaluate(new(lemma, candidate.EdgePath.All(index => Approved(_edges[index])),
            terminal, preservesRootFeatures, rootTags, last.RequiredBaseFullTag,
            terminal ? last.ParadigmStemTransform : null, terminalTags, programs),
            transform => { (lexicon as GraphWorkLexicon)?.Budget.Take(); return _transforms[transform].CanApplyForward(lemma); });
    }

    private WordDecomposition ToDecomposition(string surface, string normalized, PathCandidate candidate)
    {
        IEnumerable<Edge> edges = candidate.EdgePath.Select(index => _edges[index]);
        Edge application = edges.LastOrDefault(item => item.Origin == EdgeOrigin.Application);
        bool hasApplication = application.Origin == EdgeOrigin.Application;
        ApplicationDecision? decision = hasApplication &&
            application.ApplicationDecision?.Transparency is "transparent" or "lexicalized"
            ? application.ApplicationDecision
            : null;
        StructuralDecision? structural = !hasApplication
            ? TryGetStructuralDecision(candidate.EdgePath)
            : null;
        return new WordDecomposition(
            surface,
            normalized,
            candidate.Base,
            BuildSegments(candidate),
            candidate.ApplicationIds.LastOrDefault(),
            edges.LastOrDefault(item => item.Origin != EdgeOrigin.Paradigm).RuleId ??
                edges.Select(item => item.RuleId).LastOrDefault(item => item is not null),
            Confidence(candidate),
            CollectEvidence(edges, candidate.PathId, candidate),
            _sourceId)
        {
            FromState = candidate.FromStateId,
            ToState = candidate.ToStateId,
            Transparency = decision?.Transparency ?? structural?.Transparency,
            ProductiveGeneration = decision is not null
                ? application.GenerationEligible
                : structural?.ProductiveGeneration
        };
    }

    private StructuralDecision? TryGetStructuralDecision(int[] path)
    {
        StructuralDecision? structural = null;
        int structuralCount = 0;
        foreach (int edgeIndex in path)
        {
            Edge edge = _edges[edgeIndex];
            if (edge.Origin == EdgeOrigin.Application)
                return null;
            if (TryGetStructuralDecision(edgeIndex, out StructuralDecision decision))
            {
                structural = decision;
                structuralCount++;
                continue;
            }
            if (edge.Origin != EdgeOrigin.Paradigm || !Approved(edge))
                return null;
        }

        return structuralCount == 1 ? structural : null;
    }

    private bool TryGetStructuralDecision(int edgeIndex, out StructuralDecision decision)
    {
        if (_structuralDecisions is not null)
            foreach ((int edge, StructuralDecision value) in _structuralDecisions)
                if (edge == edgeIndex)
                {
                    decision = value;
                    return true;
                }
        decision = default;
        return false;
    }

    private WordDecomposition ProjectReviewedInflectedApplication(
        WordDecomposition decomposition,
        string normalized,
        PathCandidate selected,
        IRuntimeLexicon lexicon)
    {
        if (!_applicationEdgesBySurface.ContainsKey(selected.Base))
            return decomposition;

        var identities = new List<(string Lemma, string StateId, int Edge)>();
        for (int edgeIndex = 0; edgeIndex < _edges.Length; edgeIndex++)
        {
            Edge edge = _edges[edgeIndex];
            if (edge.Origin != EdgeOrigin.Paradigm || edge.Kind != EdgeKind.Paradigm ||
                !Approved(edge) ||
                edge.StateBinding.FromStateId is not string reverseStateId)
                continue;
            foreach (string reverseLemma in ApplyOperations(normalized, edge, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget))
                if (HasExactProjectionState(edge, reverseLemma, reverseStateId, lexicon) &&
                    HasExactProjectionOutput(edge, normalized, reverseLemma, reverseStateId, lexicon))
                    identities.Add((reverseLemma, reverseStateId, edgeIndex));
        }

        (string Lemma, string StateId, int Edge)[] distinct = identities.Distinct().ToArray();
        if (distinct.Length != 1 || selected.EdgePath.Length != 1 ||
            distinct[0].Edge != selected.EdgePath[0] || distinct[0].Lemma != selected.Base ||
            distinct[0].StateId != selected.FromStateId || distinct[0].StateId != selected.ToStateId)
            return decomposition;

        (string lemma, string stateId, int paradigmIndex) = distinct[0];
        Edge paradigm = _edges[paradigmIndex];
        if (!TryGetDirectEnding(paradigm, lemma, normalized, out string ending, (lexicon as GraphWorkLexicon)?.Budget) ||
            !_applicationEdgesBySurface.TryGetValue(lemma, out int[]? applicationIndices))
            return decomposition;

        int[] compatible = applicationIndices.Where(index =>
        {
            Edge edge = _edges[index];
            return edge.Origin == EdgeOrigin.Application && edge.Kind == EdgeKind.Suffix &&
                edge.Trust >= ReviewTrust.Reviewed && edge.ApplicationId is not null && edge.RuleId is not null &&
                edge.ApplicationDecision is { Transparency: "transparent", BlockReason: null } decision &&
                decision.OutputPartOfSpeech == StatePartOfSpeech(stateId) && edge.SurfaceKeys is { Length: > 0 } &&
                edge.SurfaceKeys[0] == lemma && !edge.BlockedSurfaces.Contains(lemma, StringComparer.Ordinal);
        }).ToArray();
        if (compatible.Length != 1)
            return decomposition;

        Edge application = _edges[compatible[0]];
        InputSlot[] slots = GetSlots(application).ToArray();
        InputSlot[] anchors = slots.Where(slot => slot.IsLexicalAnchor).ToArray();
        InputSlot[] morphSlots = slots.Where(slot => !slot.IsLexicalAnchor).ToArray();
        if (anchors.Length == 0 || anchors.Any(slot => slot.Role != "base" ||
                !HasExactSelfReading(lexicon, NormalizeLemmaSlot(slot.NodeId))) || morphSlots.Length != 2 ||
            morphSlots.Any(slot => !TryFindMorph(slot.NodeId, out _)))
            return decomposition;

        Morph[] stemMorphs = morphSlots.Select(slot => _morphs[_morphIndexById[slot.NodeId]])
            .Where(morph => morph.Kind == "stem").ToArray();
        Morph[] suffixMorphs = morphSlots.Select(slot => _morphs[_morphIndexById[slot.NodeId]])
            .Where(morph => morph.Kind == "derivational_suffix").ToArray();
        if (stemMorphs.Length != 1 || suffixMorphs.Length != 1 ||
            morphSlots.Single(slot => slot.NodeId == stemMorphs[0].Id).Role != "stem" ||
            morphSlots.Single(slot => slot.NodeId == suffixMorphs[0].Id).Role != "suffix")
            return decomposition;

        Morph stem = stemMorphs[0];
        Morph suffix = suffixMorphs[0];
        if (!lemma.EndsWith(suffix.Text, StringComparison.Ordinal) || lemma.Length <= suffix.Text.Length)
            return decomposition;
        string surfaceStem = lemma[..^suffix.Text.Length];
        MorphSegment[] segments = ToSegments([
            new Piece(surfaceStem, stem.Text, MorphKind.Stem, stem.Id),
            new Piece(suffix.Text, suffix.Text, MorphKind.DerivationalSuffix, suffix.Id),
            new Piece(ending, ending, MorphKind.InflectionalEnding, paradigm.Id)
        ]);
        if (segments.Any(segment => segment.Surface.Length == 0) ||
            string.Concat(segments.Select(segment => segment.Surface)) != normalized)
            return decomposition;

        return decomposition with
        {
            Segments = segments,
            ApplicationId = application.ApplicationId,
            RuleId = application.RuleId,
            EvidenceCodes = decomposition.EvidenceCodes.Concat(application.Evidence)
                .Append($"reviewed_morpheme_projection={application.ApplicationId}|{paradigm.Id}")
                .Distinct(StringComparer.Ordinal).ToArray(),
            Transparency = "transparent",
            ProductiveGeneration = false
        };
    }

    private WordDecomposition ProjectReviewedComposedInflectedApplication(
        WordDecomposition decomposition,
        string normalized,
        PathCandidate selected,
        IRuntimeLexicon lexicon)
    {
        if (selected.EdgePath.Length != 2 || selected.EdgePath.Distinct().Count() != 2 ||
            lexicon.LookupExactNormalized(normalized, int.MaxValue).Any())
            return decomposition;

        var identities = new List<(string Lemma, string StateId, int Prefix, int Paradigm,
            int Application, string Residual, string Ending, Morph Stem, Morph Suffix)>();
        if (!_preservingEdgesByPartOfSpeech.TryGetValue("noun", out int[]? prefixIndices))
            return decomposition;
        if (!_paradigmEdgesByPartOfSpeech.TryGetValue("noun", out int[]? paradigmIndices))
            return decomposition;
        foreach (int prefixIndex in prefixIndices)
        {
            Edge prefix = _edges[prefixIndex];
            if (prefix.Origin != EdgeOrigin.Generic || prefix.Kind != EdgeKind.Prefix ||
                !Approved(prefix) ||
                prefix.StateBinding.PreserveMatchingPartOfSpeech != "noun" ||
                prefix.BlockedSurfaces.Contains(normalized, StringComparer.Ordinal))
                continue;

            foreach (string residual in ApplyOperations(normalized, prefix, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget))
            {
                if (prefix.BlockedSurfaces.Contains(residual, StringComparer.Ordinal))
                    continue;
                foreach (int paradigmIndex in paradigmIndices)
                {
                    Edge paradigm = _edges[paradigmIndex];
                    if (paradigm.Origin != EdgeOrigin.Paradigm || paradigm.Kind != EdgeKind.Paradigm ||
                        !Approved(paradigm) ||
                        paradigm.StateBinding.FromStateId is not string stateId ||
                        StatePartOfSpeech(stateId) != "noun")
                        continue;

                    foreach (string lemma in ApplyOperations(residual, paradigm, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget))
                    {
                        if (!HasExactComposedProjectionState(paradigm, lemma, stateId, lexicon) ||
                            !HasExactProjectionOutput(paradigm, residual, lemma, stateId, lexicon) ||
                            !TryGetDirectEnding(paradigm, lemma, residual, out string ending, (lexicon as GraphWorkLexicon)?.Budget) ||
                            !_applicationEdgesBySurface.TryGetValue(lemma, out int[]? applicationIndices))
                            continue;

                        foreach (int applicationIndex in applicationIndices)
                        {
                            Edge application = _edges[applicationIndex];
                            if (application.Origin != EdgeOrigin.Application || application.Kind != EdgeKind.Suffix ||
                                application.Trust < ReviewTrust.Reviewed || application.ApplicationId is null ||
                                application.RuleId is null || application.SurfaceKeys is not { Length: 1 } ||
                                application.SurfaceKeys[0] != lemma ||
                                application.BlockedSurfaces.Contains(lemma, StringComparer.Ordinal) ||
                                application.ApplicationDecision is not { Transparency: "transparent", BlockReason: null } decision ||
                                decision.OutputPartOfSpeech != StatePartOfSpeech(stateId))
                                continue;

                            InputSlot[] slots = GetSlots(application).ToArray();
                            InputSlot[] anchors = slots.Where(slot => slot.IsLexicalAnchor).ToArray();
                            InputSlot[] morphSlots = slots.Where(slot => !slot.IsLexicalAnchor).ToArray();
                            if (anchors.Length == 0 || anchors.Any(slot => slot.Role != "base" ||
                                    !HasExactSelfReading(lexicon, NormalizeLemmaSlot(slot.NodeId))) ||
                                morphSlots.Length != 2 || morphSlots.Any(slot => !TryFindMorph(slot.NodeId, out _)))
                                continue;

                            Morph[] stems = morphSlots.Select(slot => _morphs[_morphIndexById[slot.NodeId]])
                                .Where(morph => morph.Kind == "stem").ToArray();
                            Morph[] suffixes = morphSlots.Select(slot => _morphs[_morphIndexById[slot.NodeId]])
                                .Where(morph => morph.Kind == "derivational_suffix").ToArray();
                            if (stems.Length != 1 || suffixes.Length != 1 ||
                                morphSlots.Single(slot => slot.NodeId == stems[0].Id).Role != "stem" ||
                                morphSlots.Single(slot => slot.NodeId == suffixes[0].Id).Role != "suffix" ||
                                !lemma.EndsWith(suffixes[0].Text, StringComparison.Ordinal) ||
                                lemma.Length <= suffixes[0].Text.Length)
                                continue;
                            identities.Add((lemma, stateId, prefixIndex, paradigmIndex, applicationIndex,
                                residual, ending, stems[0], suffixes[0]));
                        }
                    }
                }
            }
        }

        var distinct = identities.DistinctBy(item => (item.Lemma, item.StateId,
            _edges[item.Prefix].Id, _edges[item.Paradigm].Id,
            _edges[item.Application].Id, _edges[item.Application].ApplicationId)).ToArray();
        if (distinct.Length != 1)
            return decomposition;
        var identity = distinct[0];
        if (identity.Lemma != selected.Base || identity.StateId != selected.FromStateId ||
            identity.StateId != selected.ToStateId || !selected.EdgePath.Contains(identity.Prefix) ||
            !selected.EdgePath.Contains(identity.Paradigm))
            return decomposition;

        Edge applicationEdge = _edges[identity.Application];
        MorphSegment[] prefixSegments = decomposition.Segments
            .Where(segment => segment.Kind == MorphKind.Prefix).ToArray();
        if (prefixSegments.Length != 1 || prefixSegments[0].Start != 0 ||
            prefixSegments[0].Surface + identity.Residual != normalized)
            return decomposition;
        MorphSegment prefixSegment = prefixSegments[0];
        string surfaceStem = identity.Lemma[..^identity.Suffix.Text.Length];
        MorphSegment[] segments = ToSegments([
            new Piece(prefixSegment.Surface, prefixSegment.Canonical, MorphKind.Prefix, prefixSegment.EvidenceId),
            new Piece(surfaceStem, identity.Stem.Text, MorphKind.Stem, identity.Stem.Id),
            new Piece(identity.Suffix.Text, identity.Suffix.Text, MorphKind.DerivationalSuffix, identity.Suffix.Id),
            new Piece(identity.Ending, identity.Ending, MorphKind.InflectionalEnding, _edges[identity.Paradigm].Id)
        ]);
        if (segments.Any(segment => segment.Surface.Length == 0) ||
            string.Concat(segments.Select(segment => segment.Surface)) != normalized)
            return decomposition;

        return decomposition with
        {
            Segments = segments,
            ApplicationId = applicationEdge.ApplicationId,
            EvidenceCodes = decomposition.EvidenceCodes.Concat(applicationEdge.Evidence)
                .Append($"reviewed_composed_morpheme_projection={applicationEdge.ApplicationId}|" +
                    $"{_edges[identity.Prefix].Id}|{_edges[identity.Paradigm].Id}")
                .Distinct(StringComparer.Ordinal).ToArray(),
            Transparency = "transparent",
            ProductiveGeneration = false
        };
    }

    private static bool HasExactComposedProjectionState(
        Edge edge,
        string lemma,
        string stateId,
        IRuntimeLexicon lexicon)
    {
        WordReading[] readings = lexicon.LookupExactNormalized(lemma, int.MaxValue).ToArray();
        return readings.Length > 0 && edge.AllowedFullTags is { Length: > 0 } &&
            readings.Any(reading => reading.PartOfSpeech == edge.RequiredBaseFullTag) && readings.All(reading =>
            BulgarianTextNormalizer.NormalizeKey(reading.Form) == lemma &&
            BulgarianTextNormalizer.NormalizeKey(reading.Lemma) == lemma &&
            (reading.PartOfSpeech == edge.RequiredBaseFullTag ||
                edge.AllowedFullTags.Contains(reading.PartOfSpeech, StringComparer.Ordinal)) &&
            TryCreateStateId(reading.PartOfSpeech) == stateId);
    }

    private static bool HasExactProjectionState(
        Edge edge,
        string lemma,
        string stateId,
        IRuntimeLexicon lexicon)
    {
        WordReading[] readings = FindExactSelfReadings(lexicon, lemma).ToArray();
        return readings.Any(reading => reading.PartOfSpeech == edge.RequiredBaseFullTag) &&
            readings.All(reading => TryCreateStateId(reading.PartOfSpeech) == stateId);
    }

    private static bool HasExactProjectionOutput(
        Edge edge,
        string normalized,
        string lemma,
        string stateId,
        IRuntimeLexicon lexicon)
    {
        WordReading[] readings = lexicon.LookupExactNormalized(normalized, int.MaxValue).ToArray();
        return readings.Length > 0 && edge.AllowedFullTags is { Length: > 0 } && readings.All(reading =>
            BulgarianTextNormalizer.NormalizeKey(reading.Form) == normalized &&
            BulgarianTextNormalizer.NormalizeKey(reading.Lemma) == lemma &&
            TryCreateStateId(reading.PartOfSpeech) == stateId &&
            edge.AllowedFullTags.Contains(reading.PartOfSpeech, StringComparer.Ordinal));
    }

    private bool TryGetDirectEnding(Edge edge, string lemma, string normalized, out string ending, GraphSearchBudget? budget = null)
    {
        return IsAppendOnlyParadigm(edge, lemma, out string replay, out ending, budget) &&
            string.Equals(replay, normalized, StringComparison.Ordinal);
    }

    private bool IsAppendOnlyParadigm(Edge edge, string lemma, out string form, out string ending, GraphSearchBudget? budget = null) =>
        _surfaceOperations.IsAppendOnlyParadigm(edge, lemma, out form, out ending, budget);

    private MorphSegment[] BuildSegments(PathCandidate candidate, GraphSearchBudget? budget = null)
    {
        if (candidate.Binary is { } binary)
        {
            MorphSegment[] prefix = BuildSegments(candidate with { EdgePath = [candidate.EdgePath[0]], Binary = null });
            MorphSegment[] right = BuildSegments(candidate with
            { Base = binary.Right, Word = candidate.Word[(candidate.Base.Length + 2)..], EdgePath = candidate.EdgePath[1..], Binary = null });
            return ToSegments(prefix.Concat(right).Select(segment =>
                new Piece(segment.Surface, segment.Canonical, segment.Kind, segment.EvidenceId)).ToArray());
        }
        return _segmentation.Build(candidate.Base, candidate.Word, candidate.EdgePath, budget);
    }

    private static MorphSegment[] ToSegments(IReadOnlyList<Piece> pieces) => GraphSegmentation.ToSegments(pieces);

    private string ApplyPathForward(string baseLemma, IReadOnlyList<int> path) => ReplayPath(baseLemma, path, null);

    private string ReplayPath(string baseLemma, IReadOnlyList<int> path, GraphSearchBudget? budget)
    {
        string current = baseLemma;
        foreach (int edgeIndex in path)
        {
            Edge edge = _edges[edgeIndex];
            current = ApplyOperations(current, edge, forward: true, budget: budget).SingleOrDefault() ??
                throw new InvalidDataException(
                    $"Edge '{edge.Id}' cannot replay an accepted reverse graph path over '{current}'.");
        }
        return current;
    }

    private IEnumerable<string> ApplyOperations(string surface, Edge edge, bool forward,
        string[]? forwardStem = null, GraphSearchBudget? budget = null) =>
        _surfaceOperations.Apply(surface, edge, forward, forwardStem, budget);

    private bool AllowsPrefix(
        int nodeIndex,
        int edgeIndex,
        IReadOnlyList<SearchNode> nodes,
        TraversalDirection direction)
    {
        Edge edge = _edges[edgeIndex];
        if (edge.Kind != EdgeKind.Prefix)
            return true;
        int current = nodeIndex;
        while (current >= 0)
        {
            int priorEdge = nodes[current].Edge;
            if (priorEdge >= 0 && _edges[priorEdge].Kind == EdgeKind.Prefix)
            {
                Edge prior = _edges[priorEdge];
                Edge inner = direction == TraversalDirection.Forward ? prior : edge;
                Edge outer = direction == TraversalDirection.Forward ? edge : prior;
                if (!AllowsPrefixPair(inner, outer))
                    return false;
            }
            current = nodes[current].Parent;
        }
        return true;
    }

    private static bool AllowsPrefixPair(Edge inner, Edge outer)
    {
        if (inner.Id == outer.Id)
            return false;
        if (inner.PrefixClass != PrefixClass.Degree && outer.PrefixClass != PrefixClass.Degree)
            return true;
        return inner.PrefixClass == PrefixClass.Negative && outer.PrefixClass == PrefixClass.Degree;
    }

    private static bool HasVisited(
        int nodeIndex,
        StateConstraint state,
        string surface,
        IReadOnlyList<SearchNode> nodes)
    {
        int current = nodeIndex;
        while (current >= 0)
        {
            SearchNode node = nodes[current];
            if (node.State == state &&
                string.Equals(node.Surface, surface, StringComparison.Ordinal))
                return true;
            current = node.Parent;
        }
        return false;
    }

    private int[] GetOutgoingEdges(StateConstraint state) => state.ExactStateId is null
        ? []
        : GetStateEdges(_outgoingEdges, state.ExactStateId, state.PartOfSpeech);

    private int[] GetIncomingEdges(StateConstraint state) => state.ExactStateId is null
        ? GetStateEdges(_incomingEdgesByToPartOfSpeech, state.PartOfSpeech, state.PartOfSpeech)
        : GetStateEdges(_incomingEdges, state.ExactStateId, state.PartOfSpeech);

    private int[] GetStateEdges(
        IReadOnlyDictionary<string, int[]> exactIndex,
        string stateId,
        string partOfSpeech)
    {
        exactIndex.TryGetValue(stateId, out int[]? exact);
        _preservingEdgesByPartOfSpeech.TryGetValue(partOfSpeech, out int[]? preserving);
        if (exact is null || exact.Length == 0)
            return preserving ?? [];
        if (preserving is null || preserving.Length == 0)
            return exact;
        return OrderEdgeIndices(exact.Concat(preserving).Distinct(), _edges);
    }

    private int[] GetParadigmEdges(string stateId) =>
        _paradigmEdgesByState.TryGetValue(stateId, out int[]? edges) ? edges : [];

    private IEnumerable<StateRefinement> RefineAgainstExactAnchors(
        string surface,
        StateConstraint constraint,
        IRuntimeLexicon lexicon)
    {
        string[] exactStates = FindExactSelfReadingStates(lexicon, surface, out bool hasExactSelfReading);
        if (!hasExactSelfReading)
            return [new StateRefinement(constraint, false)];
        return exactStates
            .Where(constraint.MatchesExactState)
            .Select(stateId => new StateRefinement(StateConstraint.Exact(stateId), true))
            .ToArray();
    }

    private static bool HasConflictingExactOutput(
        IRuntimeLexicon lexicon,
        string surface,
        StateConstraint expectedState,
        Edge edge)
    {
        WordReading[] exact = FindExactSelfReadings(lexicon, surface).ToArray();
        if (exact.Length == 0)
            return false;
        return edge.AllowedFullTags is { Length: > 0 }
            ? !exact.Any(reading => edge.AllowedFullTags.Contains(reading.PartOfSpeech, StringComparer.Ordinal))
            : !exact.Select(reading => TryCreateStateId(reading.PartOfSpeech))
                .OfType<string>()
                .Any(expectedState.MatchesExactState);
    }

    private static bool HasParadigmBaseAuthority(
        Edge edge,
        string surface,
        IRuntimeLexicon lexicon,
        bool allowOov)
    {
        WordReading[] exact = FindExactSelfReadings(lexicon, surface).ToArray();
        return exact.Length == 0
            ? allowOov
            : exact.Any(reading => string.Equals(
                reading.PartOfSpeech,
                edge.RequiredBaseFullTag,
                StringComparison.Ordinal));
    }

    private bool HasReversePathExactOutputConflict(
        IReadOnlyList<SearchNode> nodes,
        StateConstraint exactRootState,
        int parentIndex,
        int edgeIndex,
        string finalSurface,
        IRuntimeLexicon lexicon)
    {
        StateConstraint currentState = exactRootState;
        int currentParent = parentIndex;
        int currentEdge = edgeIndex;
        while (currentEdge >= 0)
        {
            Edge edge = _edges[currentEdge];
            if (!edge.StateBinding.TryTraverse(
                    TraversalDirection.Forward,
                    currentState,
                    out StateConstraint outputState))
            {
                return true;
            }
            string outputSurface = currentParent < 0 ? finalSurface : nodes[currentParent].Surface;
            if (HasConflictingExactOutput(lexicon, outputSurface, outputState, edge))
                return true;

            currentState = outputState;
            if (currentParent < 0)
                break;
            SearchNode parent = nodes[currentParent];
            currentEdge = parent.Edge;
            currentParent = parent.Parent;
        }
        return false;
    }

    private bool HasIncompatibleExactPrefixHistory(
        Edge edge,
        string surface,
        StateConstraint outputConstraint,
        IRuntimeLexicon lexicon)
    {
        if (edge.Kind != EdgeKind.Prefix)
            return false;
        // Reviewed generic edges establish prefix history; blocked surfaces cannot veto another path.
        foreach (Edge prior in _edges.Where(candidate =>
                     candidate.Kind == EdgeKind.Prefix &&
                     candidate.Origin == EdgeOrigin.Generic &&
                     candidate.Trust >= ReviewTrust.Reviewed &&
                     !candidate.BlockedSurfaces.Contains(surface, StringComparer.Ordinal)))
        {
            foreach (string previousSurface in ApplyOperations(surface, prior, forward: false, budget: (lexicon as GraphWorkLexicon)?.Budget))
            {
                if (prior.BlockedSurfaces.Contains(previousSurface, StringComparer.Ordinal))
                    continue;
                foreach (string previousStateId in FindAnchorStates(lexicon, previousSurface))
                {
                    if (prior.StateBinding.TryTraverse(
                            TraversalDirection.Forward,
                            StateConstraint.Exact(previousStateId),
                            out StateConstraint resolved) &&
                        outputConstraint.Matches(resolved) &&
                        !AllowsPrefixPair(prior, edge))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private bool IsAddressableState(string stateId)
    {
        if ((_outgoingEdges.TryGetValue(stateId, out int[]? outgoing) && outgoing.Length > 0) ||
            (_incomingEdges.TryGetValue(stateId, out int[]? incoming) && incoming.Length > 0) ||
            (_paradigmEdgesByState.TryGetValue(stateId, out int[]? paradigms) && paradigms.Length > 0))
        {
            return true;
        }
        return _preservingEdgesByPartOfSpeech.ContainsKey(StatePartOfSpeech(stateId));
    }

    private static string RootStateId(IReadOnlyList<SearchNode> nodes, int nodeIndex)
    {
        int current = nodeIndex;
        while (nodes[current].Parent >= 0)
            current = nodes[current].Parent;
        return nodes[current].State.ExactStateId ??
            throw new InvalidDataException("A forward graph path must begin at an exact typed anchor.");
    }

    private string[] FindAnchorStates(IRuntimeLexicon lexicon, string normalizedLemma) =>
        FindExactSelfReadingStates(lexicon, normalizedLemma, out _)
            .Where(IsAddressableState)
            .ToArray();

    private static string[] FindExactSelfReadingStates(
        IRuntimeLexicon lexicon,
        string normalizedLemma,
        out bool hasExactSelfReading)
    {
        WordReading[] exactSelfReadings = FindExactSelfReadings(lexicon, normalizedLemma).ToArray();
        hasExactSelfReading = exactSelfReadings.Length > 0;
        if (!hasExactSelfReading) return [];
        return exactSelfReadings
            .Select(reading => TryCreateStateId(reading.PartOfSpeech))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IEnumerable<WordReading> FindExactSelfReadings(IRuntimeLexicon lexicon, string normalizedLemma)
    {
        IReadOnlyList<WordReading> readings = lexicon.LookupExactNormalized(normalizedLemma, int.MaxValue);
        return readings.Count == 0 ? [] : Filter(readings, normalizedLemma);

        static IEnumerable<WordReading> Filter(IReadOnlyList<WordReading> rows, string lemma) => rows.Where(reading =>
            string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Form), lemma, StringComparison.Ordinal) &&
            string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Lemma), lemma, StringComparison.Ordinal));
    }

    private static bool HasExactSelfReading(IRuntimeLexicon lexicon, string normalizedLemma) =>
        FindExactSelfReadings(lexicon, normalizedLemma).Any();

    private static bool TryGetExactApplicationOutputAuthority(
        Edge application,
        IRuntimeLexicon lexicon,
        out string stateId,
        out string fullTag)
    {
        stateId = fullTag = string.Empty;
        if (application.SurfaceKeys is not { Length: 1 } ||
            application.ApplicationDecision?.OutputPartOfSpeech is not string outputPartOfSpeech)
            return false;
        string output = application.SurfaceKeys[0];
        IReadOnlyList<WordReading> readings = lexicon.LookupExactNormalized(output, int.MaxValue);
        if (readings.Count == 0)
            return false;
        string? exactTag = null;
        string? exactState = null;
        foreach (WordReading reading in readings)
        {
            if (reading.PartOfSpeech is not string tag ||
                BulgarianTextNormalizer.NormalizeKey(reading.Form) != output ||
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma) != output)
                return false;
            if (exactTag is null)
            {
                exactTag = tag;
                exactState = TryCreateStateId(tag);
                if (exactState is null || StatePartOfSpeech(exactState) != outputPartOfSpeech)
                    return false;
            }
            else if (!string.Equals(exactTag, tag, StringComparison.Ordinal))
                return false;
        }
        stateId = exactState!;
        fullTag = exactTag!;
        return true;
    }

    private static bool HasCompatibleExactInflectedOutput(
        string surface,
        string lemma,
        string stateId,
        Edge paradigm,
        IRuntimeLexicon lexicon)
    {
        IReadOnlyList<WordReading> readings = lexicon.LookupExactNormalized(surface, int.MaxValue);
        foreach (WordReading reading in readings)
            if (!string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Form), surface, StringComparison.Ordinal) ||
                !string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Lemma), lemma, StringComparison.Ordinal) ||
                TryCreateStateId(reading.PartOfSpeech) != stateId ||
                paradigm.AllowedFullTags is not { } tags ||
                !tags.Contains(reading.PartOfSpeech, StringComparer.Ordinal))
                return false;
        return true;
    }

    private static string[] FindExactApplicationBases(IEnumerable<InputSlot> slots, IRuntimeLexicon lexicon) => slots
        .Where(slot => slot.IsLexicalAnchor)
        .Select(slot => NormalizeLemmaSlot(slot.NodeId))
        .Where(lemma => lexicon.LookupExactNormalized(lemma, int.MaxValue).Any(reading =>
            string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Form), lemma, StringComparison.Ordinal) &&
            string.Equals(BulgarianTextNormalizer.NormalizeKey(reading.Lemma), lemma, StringComparison.Ordinal)))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private bool TryFindExactBridgeApplicationBase(Edge edge, IRuntimeLexicon lexicon, out string baseLemma)
    {
        baseLemma = string.Empty;
        int anchorCount = 0, baseCount = 0;
        InputSlot anchor = default, baseSlot = default;
        foreach (InputSlot slot in GetSlots(edge))
        {
            if (slot.IsLexicalAnchor) { anchorCount++; anchor = slot; }
            if (slot.Role == "base") { baseCount++; baseSlot = slot; }
        }
        if (anchorCount != 1 || baseCount != 1 || anchor != baseSlot)
            return false;
        baseLemma = NormalizeLemmaSlot(baseSlot.NodeId);
        return HasExactSelfReading(lexicon, baseLemma);
    }

    private bool IsAdmissibleProductiveCompound(Edge edge, string? queriedBase, IRuntimeLexicon lexicon)
    {
        if (edge.Kind != EdgeKind.Compound || !edge.GenerationEligible) return true;
        int leftCount = 0, rightCount = 0;
        string? leftLemma = null, rightLemma = null;
        foreach (InputSlot slot in GetSlots(edge))
        {
            if (slot.Role == "compound_left" && ++leftCount == 1 && slot.IsLexicalAnchor)
                leftLemma = NormalizeLemmaSlot(slot.NodeId);
            else if (slot.Role == "compound_right" && ++rightCount == 1 && slot.IsLexicalAnchor)
                rightLemma = NormalizeLemmaSlot(slot.NodeId);
        }
        return leftCount == 1 && rightCount == 1 && leftLemma is not null && rightLemma is not null &&
            (queriedBase is null || string.Equals(queriedBase, leftLemma, StringComparison.Ordinal)) &&
            HasExactSelfReading(lexicon, leftLemma) && HasExactSelfReading(lexicon, rightLemma);
    }

    private bool IsEligibleApplicationParadigmBridge(
        int edgeIndex, string root, string intermediate, string finalSurface, IRuntimeLexicon lexicon,
        string? verifiedAnchor = null)
    {
        Edge edge = _edges[edgeIndex];
        bool shape = edge.Origin == EdgeOrigin.Application &&
            (edge.Kind == EdgeKind.Compound || edge.Kind == EdgeKind.Suffix) &&
            Approved(edge) &&
            edge.ApplicationDecision is { Transparency: "transparent", BlockReason: null } &&
            edge.SurfaceKeys is { Length: 1 } keys && keys[0] == intermediate &&
            !edge.BlockedSurfaces.Contains(intermediate, StringComparer.Ordinal) &&
            !edge.BlockedSurfaces.Contains(finalSurface, StringComparer.Ordinal);
        if (!shape || edge.Kind == EdgeKind.Compound)
            return shape && (edge.Kind != EdgeKind.Compound || IsAdmissibleProductiveCompound(edge, root, lexicon));
        if (verifiedAnchor is null && !TryFindExactBridgeApplicationBase(edge, lexicon, out verifiedAnchor))
            return false;
        return string.Equals(verifiedAnchor, root, StringComparison.Ordinal) &&
            edge.ApplicationDecision!.Value.OutputPartOfSpeech is not null &&
            TryGetSuffixBridgeRelation(edge, out _);
    }

    private bool TryGetSuffixBridgeRelation(Edge edge, out SuffixBridgeRelation relation)
    {
        relation = default;
        if (edge.Kind != EdgeKind.Suffix || edge.RuleId is null)
            return false;
        IEnumerable<string> morphIds = edge.Origin == EdgeOrigin.Application
            ? GetSlots(edge).Where(slot => !slot.IsLexicalAnchor && TryFindMorph(slot.NodeId, out Morph morph) &&
                morph.Kind == "derivational_suffix").Select(slot => slot.NodeId)
            : _operations.Skip(edge.OperationOffset).Take(edge.OperationCount)
                .Where(operation => operation.Kind == OperationKind.AppendMorph)
                .Select(operation => _morphs[operation.OperandIndex])
                .Where(morph => morph.Kind == "derivational_suffix").Select(morph => morph.Id);
        string[] suffixes = morphIds.Distinct(StringComparer.Ordinal).ToArray();
        if (suffixes.Length != 1)
            return false;
        relation = new(edge.RuleId, suffixes[0]);
        return true;
    }

    private bool TryMatchEquivalentSuffixRepresentation(
        PathCandidate candidate,
        CompoundBridgeSignature signature,
        SuffixBridgeRelation relation,
        IRuntimeLexicon lexicon)
    {
        if (candidate.EdgePath.Length != 2 || candidate.Base != signature.LeftBase ||
            candidate.EdgePath[1] < 0 || _edges[candidate.EdgePath[1]].Id != signature.ParadigmEdge)
            return false;
        Edge transition = _edges[candidate.EdgePath[0]];
        if (transition.Origin != EdgeOrigin.Generic || transition.Kind != EdgeKind.Suffix ||
            !Approved(transition) ||
            !TryGetSuffixBridgeRelation(transition, out SuffixBridgeRelation transitionRelation) ||
            transitionRelation != relation || !HasExactSelfReading(lexicon, candidate.Base))
            return false;
        string[] replay = ApplyOperations(candidate.Base, transition, forward: true, budget: (lexicon as GraphWorkLexicon)?.Budget)
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return replay.Length == 1 && replay[0] == signature.Intermediate &&
            _edges[candidate.EdgePath[1]].StateBinding.FromStateId == signature.StateId &&
            _edges[candidate.EdgePath[1]].RequiredBaseFullTag == signature.BaseFullTag;
    }

    private static string NormalizeLemmaSlot(string nodeId) =>
        BulgarianTextNormalizer.NormalizeKey(nodeId["lemma:".Length..]);

    internal static string StatePartOfSpeech(string stateId) => GraphState.GetPartOfSpeech(stateId);

    internal static string GetStatePartOfSpeech(string stateId)
    {
        ValidateStateId(stateId);
        return StatePartOfSpeech(stateId);
    }

    internal static string? TryCreateStateId(string? fullTag)
    {
        string tag = fullTag?.Trim() ?? string.Empty;
        int colon = tag.IndexOf(':');
        if (colon <= 0 || colon == tag.Length - 1)
            return null;
        string pos = tag[..colon] switch
        {
            "Съществително" => "noun",
            "Прилагателно" => "adjective",
            "Глагол" => "verb",
            "Бройно_Числително" or "Редно_Числително" => "numeral",
            "Лично_местоимение" => "personal_pronoun",
            "Притежателно_местоимение" or "Въпросително_местоимение" or
            "Показателно_местоимение" or "Относително_местоимение" or
            "Обобщително_местоимение" or "Неопределително_местоимение" or
            "Отрицателно_местоимение" => "pronoun",
            "Наречие" => "adverb",
            "Съюз" => "conjunction",
            "Междуметие" => "interjection",
            "Частица" => "particle",
            "Предлог" => "preposition",
            "Име" => "given_name",
            "Фамилия" => "family_name",
            "Град" => "city_name",
            _ => string.Empty
        };
        if (pos.Length == 0)
            return null;
        int comma = tag.IndexOf(',', colon + 1);
        string paradigm = (comma < 0 ? tag[(colon + 1)..] : tag[(colon + 1)..comma])
            .Trim()
            .ToLowerInvariant();
        return IsCanonicalParadigm(paradigm) ? $"state:{pos}:{paradigm}" : null;
    }

    private ArraySegment<InputSlot> GetSlots(Edge edge) =>
        new(_slots, edge.SlotOffset, edge.SlotCount);

    private bool TryFindMorph(string id, out Morph morph)
    {
        if (!_morphIndexById.TryGetValue(id, out int index))
        {
            morph = default;
            return false;
        }

        morph = _morphs[index];
        return true;
    }


    private WordDecomposition Empty(string surface, string normalized) => new(
        surface,
        normalized,
        null,
        [],
        null,
        null,
        0,
        ["no_word_formation_candidate"],
        _sourceId);

    private static string[] CollectEvidence(IEnumerable<Edge> edges, string pathId, PathCandidate? candidate = null)
    {
        var collected = new List<string>(edges.Sum(edge => edge.Evidence.Length) + 1);
        foreach (Edge edge in edges) collected.AddRange(edge.Evidence);
        collected.Add("path=" + pathId);
        IEnumerable<string> evidence = collected.Distinct(StringComparer.Ordinal);
        if (candidate?.Binary is { } binary) evidence = evidence.Concat([
            "binary_left=" + candidate.Base, "binary_right=" + binary.Right,
            "binary_left_base_full_tag=" + binary.LeftTag, "binary_right_base_full_tag=" + binary.RightTag,
            "binary_right_program=" + binary.Program, "binary_output_full_tag=" + binary.FullTag]);
        return evidence.ToArray();
    }

    private static double Confidence(PathCandidate candidate)
    {
        double trust = candidate.Trust == ReviewTrust.Accepted ? 0.9 : 0.8;
        if (!candidate.HasReviewedApplication)
            trust -= 0.05;
        return trust;
    }

    internal static ReviewTrust LeastTrust(params ReviewTrust[] values) => values.Min();

    internal static MorphKind MapMorphKind(string kind) => kind switch
    {
        "prefix" => MorphKind.Prefix,
        "root" => MorphKind.Root,
        "interfix" => MorphKind.Interfix,
        "derivational_suffix" => MorphKind.DerivationalSuffix,
        "inflectional_ending" => MorphKind.InflectionalEnding,
        "stem" => MorphKind.Stem,
        _ => throw new InvalidDataException($"Unsupported word-formation morph kind '{kind}'.")
    };

    internal static void ValidateStateId(string value)
    {
        string[] parts = value.Split(':');
        if (parts.Length != 3 || !string.Equals(parts[0], "state", StringComparison.Ordinal) ||
            !IsCanonicalStatePartOfSpeech(parts[1]) || !IsCanonicalParadigm(parts[2]))
        {
            throw new InvalidDataException(
                $"Graph state '{value}' must use a semantic paradigm identity in canonical 'state:pos:paradigm' form.");
        }
    }

    private static bool IsCanonicalStatePartOfSpeech(string value) => value is
        "noun" or "adjective" or "verb" or "adverb" or "given_name" or "family_name" or "city_name" or
        "conjunction" or "interjection" or "particle" or "preposition" or
        "numeral" or "personal_pronoun" or "pronoun";

    private static bool IsCanonicalParadigm(string value) => value.Length > 0 &&
        value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9');

    internal readonly record struct SearchBounds(
        int MaxDepth,
        int MaxExpandedStates,
        int MaxGraphCandidates,
        int MaxApplicationCandidates,
        int MaxCandidatesReturned)
    {
        internal static SearchBounds ForLimit(int limit) =>
            new(6, Math.Max(128, limit * 32), Math.Min(2048, limit * 8), 128, limit);
    }

    internal readonly record struct Morph(string Id, string Kind, string Text, ReviewTrust Trust);

    internal readonly record struct InputSlot(string Role, string NodeId, bool IsLexicalAnchor);

    internal readonly record struct SurfaceOperation(OperationKind Kind, int OperandIndex);

    internal readonly record struct Edge(
        string Id,
        EdgeKind Kind,
        EdgeOrigin Origin,
        PrefixClass PrefixClass,
        StateBinding StateBinding,
        int SlotOffset,
        int SlotCount,
        int OperationOffset,
        int OperationCount,
        string? RuleId,
        string? ApplicationId,
        string[]? SurfaceKeys,
        ApplicationDecision? ApplicationDecision,
        ReviewTrust Trust,
        bool GenerationEligible,
        int SupportCount,
        string[] ApplicationIds,
        string[] BlockedSurfaces,
        string[] Evidence,
        string? RequiredBaseFullTag,
        string[]? AllowedFullTags,
        int ParadigmStemTransform = -1)
    {
        internal string? ParadigmBranchId { get; init; }
    }

    internal readonly record struct ApplicationDecision(
        string Transparency,
        string? BlockReason,
        string? OutputPartOfSpeech);

    internal readonly record struct StructuralDecision(
        string Transparency,
        bool ProductiveGeneration);

    internal enum InputFinalCharacterClass
    {
        None,
        BulgarianConsonant
    }

    internal enum InputPreTailCharacterClass
    {
        None,
        BulgarianConsonant,
        NotBulgarianConsonant,
        BulgarianConsonantExceptDt
    }

    private readonly record struct SearchNode(
        string Surface,
        StateConstraint State,
        int Parent,
        int Edge,
        int Depth,
        string Base,
        bool ConditionalBaseSeed = false);

    private readonly record struct StateRefinement(StateConstraint Constraint, bool HasExactAnchor);

    internal sealed record ConditionalProgram(
        string StateId,
        string RuleId,
        string BaseTag,
        ConditionalBranch[] Branches);

    internal sealed record ConditionalBranch(
        string Id,
        int TransformIndex,
        ReviewTrust Trust,
        int SupportCount);

    internal sealed record PathCandidate(
        string Word,
        string Base,
        string? FromStateId,
        string? ToStateId,
        int[] EdgePath,
        string[] ApplicationIds,
        ReviewTrust Trust,
        int SupportCount,
        bool HasOnlyExactStateBindings,
        bool HasReviewedApplication,
        string PathId)
    {
        internal WordDerivation? PublicProjection { get; init; }
        internal BinaryPath? Binary { get; init; }
        internal MorphPolicy.Result? Projection { get; init; }
        internal bool RootFeaturesPreserved { get; init; }
        internal bool HasExactRoot { get; init; } = true;
        internal bool IsConditional { get; init; }
        internal string? ConditionalProgramId { get; init; }
        internal string? ConditionalBranchId { get; init; }
        internal string? ConditionalBaseFullTag { get; init; }
        internal string? ConditionalFullTag { get; init; }
        internal string? ConditionalCellId { get; init; }
        internal int ConditionalTerminalEdge { get; init; } = -1;
        internal int ConditionalSupportCount { get; init; }
        internal string[] SupportingLemmas { get; init; } = [];
        internal ExactSelfReadingRequirement[] UnmetRequirements { get; init; } = [];
        internal CandidateKey Identity => new(Word, Base, FromStateId, ToStateId, PathId, ConditionalFullTag);

        internal CandidateGroupKey GroupIdentity =>
            new(Word, Base, FromStateId, ToStateId, HasReviewedApplication, ConditionalFullTag);
    }

    private readonly record struct CompoundBridgeSignature(
        string LeftBase,
        string Intermediate,
        string StateId,
        string BaseFullTag,
        string ParadigmEdge);

    private readonly record struct SuffixBridgeRelation(string RuleId, string SuffixMorphId);

    internal readonly record struct CandidateKey(
        string Word,
        string Base,
        string? FromStateId,
        string? ToStateId,
        string PathId,
        string? ConditionalFullTag);

    internal readonly record struct CandidateGroupKey(
        string Word,
        string Base,
        string? FromStateId,
        string? ToStateId,
        bool HasReviewedApplication,
        string? ConditionalFullTag);

    internal enum EdgeKind { Prefix, Suffix, Compound, Paradigm }

    internal enum EdgeOrigin { Generic, Application, Paradigm }

    internal enum PrefixClass { None, Negative, Degree, Intensifier }

    internal enum OperationKind { Transform, PrependMorph, AppendMorph, AppendLiteral, EmitLiteral }

    internal enum TransformKind { Identity, TailReplace, TailDelete, VariantSetNormalize, LastOccurrenceReplace, PenultimateDelete }

    internal enum TraversalDirection { Forward, Reverse }

    internal readonly record struct GenerationObservation(
        string Form,
        string FullTag,
        int RetainedOriginalPrefixLength)
    {
        internal int EdgeIndex { get; init; }
    }

    internal sealed record BinaryPath(string Right, string LeftTag, string RightTag, string Program, string FullTag);
    private readonly record struct BinaryResult(BinaryProofBudget.Outcome Outcome, PathCandidate[] Candidates);

    internal enum ReviewTrust { Blocked, Unreviewed, Reviewed, Accepted }
}
