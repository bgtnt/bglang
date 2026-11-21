using GenerationObservation = BgLang.Core.WordFormationGraph.GenerationObservation;

namespace BgLang.Core;

/// <summary>Exact ordinary generation, inverse candidates and replay over the same program cells.</summary>
internal sealed class ParadigmRuntime
{
    private readonly Dictionary<(string BaseFullTag, string RuleId), ParadigmProgram> _programs;
    private readonly Dictionary<(string StateId, string RuleId), CellReference[]> _matchingCells;
    private readonly CellReference[] _reverseCells;
    private readonly ParadigmReverseIndex _reverseIndex;
    private readonly GraphSurfaceOperations _surfaceOperations;
    private readonly Dictionary<(string BaseFullTag, string RuleId), (LexicalCliticProjection Projection, ParadigmProgram Parent)> _lexicalProjections = [];

    internal ParadigmRuntime(
        IEnumerable<(string StateId, string BaseFullTag, string RuleId, ParadigmProgram.Cell Cell)> cells,
        GraphSurfaceOperations surfaceOperations, ProgramCells[]? additionalPrograms = null,
        LexicalCliticProjection[]? lexicalProjections = null, OrdinaryParadigmAlias[]? ordinaryAliases = null)
    {
        var rows = cells.ToArray();
        _surfaceOperations = surfaceOperations;
        _programs = rows.GroupBy(row => (row.BaseFullTag, row.RuleId))
            .ToDictionary(group => group.Key,
                group => new ParadigmProgram(group.First().StateId,
                    group.Select(row => row.Cell).ToArray(), surfaceOperations));
        // Same-surface readings complete the ordinary program, without becoming graph edges.
        foreach (ProgramCells extra in additionalPrograms ?? [])
            if (extra.Kind == ParadigmProgram.Kind.Inflecting)
            {
                var key = (extra.BaseFullTag, extra.RuleId);
                if (!_programs.TryGetValue(key, out ParadigmProgram? ordinary) || ordinary.StateId != extra.StateId)
                    throw new InvalidDataException("Supplementary cells require an ordinary program in the same state.");
                _programs[key] = new(extra.StateId,
                    [.. Enumerable.Range(0, ordinary.CellCount).Select(ordinary.GetCell), .. extra.Cells], surfaceOperations);
            }
        var references = _programs.Values.SelectMany(program => Enumerable.Range(0, program.CellCount)
                .Select(index => new CellReference(program, index)))
            .Where(reference => reference.Cell.OriginalEdgeIndex >= 0)
            .ToDictionary(reference => reference.Cell.OriginalEdgeIndex);
        // Replay follows the compiler's per-state order, including interleaved base-tag programs.
        _matchingCells = rows.GroupBy(row => (row.StateId, row.RuleId)).ToDictionary(group => group.Key,
            group => group.Select(row => references[row.Cell.OriginalEdgeIndex]).ToArray());
        // Reverse suffix buckets historically follow original edge indices, not forward ranking.
        _reverseCells = references.OrderBy(pair => pair.Key).Select(pair => pair.Value).ToArray();
        _reverseIndex = new(Enumerable.Range(0, _reverseCells.Length).Select(index =>
        {
            ParadigmProgram.Cell cell = _reverseCells[index].Cell;
            return (index, surfaceOperations.FinalReverseEnding(cell.OperationOffset, cell.OperationCount));
        }));
        // Bind the same instance after graph inventories are complete. Aliases cannot target aliases or lexical cells.
        if (ordinaryAliases is { Length: > 0 })
        {
            HashSet<(string BaseFullTag, string RuleId)> ordinaryBindings = _programs.Keys.ToHashSet();
            foreach (OrdinaryParadigmAlias alias in ordinaryAliases)
                if (alias.ReviewState != "accepted" || !ordinaryBindings.Contains((alias.BaseFullTag, alias.TargetRuleId)) ||
                    !_programs.TryGetValue((alias.BaseFullTag, alias.TargetRuleId), out ParadigmProgram? target) ||
                    Enumerable.Range(0, target.CellCount).Any(index => !target.GetCell(index).Approved) ||
                    !_programs.TryAdd((alias.BaseFullTag, alias.RuleId), target))
                    throw new InvalidDataException("Ordinary paradigm alias requires a unique approved ordinary target.");
        }
        // Lexical cells share execution, but never enter graph replay, reverse or OOV inventories.
        foreach (ProgramCells lexical in additionalPrograms ?? [])
            if (lexical.Kind != ParadigmProgram.Kind.Inflecting && !_programs.TryAdd((lexical.BaseFullTag, lexical.RuleId),
                    new(lexical.StateId, lexical.Cells, surfaceOperations, lexical.Kind)))
                throw new InvalidDataException("Lexical program must be the sole declaration for its base tag and rule.");
        // Bind only to ordinary programs. Projections never enter replay/reverse/OOV inventories.
        foreach (LexicalCliticProjection projection in lexicalProjections ?? [])
            if (!_programs.TryGetValue((projection.ParentBaseFullTag, projection.ParentRuleId), out ParadigmProgram? parent) ||
                _programs.ContainsKey((projection.BaseFullTag, projection.RuleId)) ||
                !_lexicalProjections.TryAdd((projection.BaseFullTag, projection.RuleId), (projection, parent)))
                throw new InvalidDataException("Lexical clitic projection requires one ordinary parent and a unique binding.");
    }

    internal HashSet<(string Form, string Tag)> Generate(
        string lemma, string baseFullTag, string ruleId,
        Action<GenerationObservation>? observe, int maximumObservations, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lexicalProjections.TryGetValue((baseFullTag, ruleId), out var projection))
        {
            HashSet<(string Form, string Tag)> tuples = projection.Parent.Execute(lemma,
                    projection.Projection.ParentBaseFullTag, observe: null, maximumObservations, cancellationToken)
                .Select(tuple => (tuple.Tag == projection.Projection.ParentBaseFullTag ? lemma : tuple.Form,
                    projection.Projection.ProjectTag(tuple.Tag))).ToHashSet();
            if (!tuples.Any(tuple => tuple.Form != lemma))
                throw new InvalidDataException("Lexical clitic projection requires actual non-lemma-surface output.");
            return tuples;
        }
        if (!_programs.TryGetValue((baseFullTag, ruleId), out ParadigmProgram? program))
            throw new InvalidDataException("Generated lexeme has no applicable non-lemma-surface output.");
        return program.Execute(lemma, baseFullTag, observe, maximumObservations, cancellationToken);
    }

    internal bool MatchesForm(string lemma, string stateId, string ruleId, string fullTag, string surface)
    {
        if (!_matchingCells.TryGetValue((stateId, ruleId), out CellReference[]? cells)) return false;
        foreach (CellReference reference in cells)
            if (reference.Program.MatchesCell(reference.Index, lemma, fullTag, surface)) return true;
        return false;
    }

    internal IEnumerable<int> ReverseCandidateEdges(string surface)
    {
        foreach (int index in _reverseIndex.Match(surface))
            yield return _reverseCells[index].Cell.OriginalEdgeIndex;
    }

    internal IEnumerable<(string Lemma, string StateId, int Edge)> Reverse(
        string surface, IReadOnlySet<string>? observedTags = null, bool productiveOnly = false, GraphSearchBudget? budget = null)
    {
        foreach (int index in _reverseIndex.Match(surface))
        {
            budget?.Take();
            CellReference reference = _reverseCells[index];
            ParadigmProgram.Cell cell = reference.Cell;
            if (!cell.Reviewed) continue;
            if ((productiveOnly || observedTags is not null) && !cell.Approved) continue;
            if (observedTags is not null && !cell.FullTags.Any(observedTags.Contains)) continue;
            foreach (string lemma in _surfaceOperations.Apply(surface, cell.OperationOffset, cell.OperationCount,
                         forward: false, forwardStem: null, budget, cell.GuardTransformIndex))
            {
                budget?.Take();
                yield return (lemma, reference.Program.StateId, cell.OriginalEdgeIndex);
            }
        }
    }

    private readonly record struct CellReference(ParadigmProgram Program, int Index)
    {
        internal ParadigmProgram.Cell Cell => Program.GetCell(Index);
    }

    internal readonly record struct ProgramCells(
        string StateId, string BaseFullTag, string RuleId, ParadigmProgram.Cell[] Cells, ParadigmProgram.Kind Kind);
}
