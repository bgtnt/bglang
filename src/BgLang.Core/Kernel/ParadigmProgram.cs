using GenerationObservation = BgLang.Core.WordFormationGraph.GenerationObservation;

namespace BgLang.Core;

/// <summary>One ordinary paradigm over shared surface operations, without graph traversal.</summary>
internal sealed class ParadigmProgram(string stateId, ParadigmProgram.Cell[] cells, GraphSurfaceOperations surfaceOperations,
    ParadigmProgram.Kind kind = ParadigmProgram.Kind.Inflecting)
{
    internal string StateId => stateId;
    internal int CellCount => cells.Length;
    internal Cell GetCell(int index) => cells[index];

    internal bool MatchesCell(int cellIndex, string lemma, string fullTag, string surface)
    {
        Cell cell = cells[cellIndex];
        return cell.Approved && cell.FullTags.Contains(fullTag, StringComparer.Ordinal) &&
            surfaceOperations.Apply(lemma, cell.OperationOffset, cell.OperationCount,
                forward: true, forwardStem: null, budget: null, cell.GuardTransformIndex).Contains(surface, StringComparer.Ordinal);
    }

    internal HashSet<(string Form, string Tag)> Execute(
        string lemma, string baseFullTag, Action<GenerationObservation>? observe, int maximumObservations,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HashSet<(string Form, string Tag)> seen = kind == Kind.Lexical ? [] : [(lemma, baseFullTag)];
        Dictionary<int, (bool Matches, string[]? Stem)> applicable = [];
        List<GenerationObservation>? observations = observe is null || kind == Kind.Invariant ? null : [];
        bool invariantBaseEmitted = false;
        foreach (Cell cell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Lemma/no-op stays in its guarded branch.
            if (!applicable.TryGetValue(cell.GuardTransformIndex, out var branch))
            {
                IEnumerable<string> stems = surfaceOperations.ApplyTransform(lemma, cell.GuardTransformIndex);
                applicable.Add(cell.GuardTransformIndex,
                    branch = (stems.Any(), stems is string[] { Length: 1 } single ? single : null));
            }
            if (!branch.Matches)
                continue;
            if (!cell.Approved)
                throw new InvalidDataException("Generated lexeme has an applicable unaccepted paradigm branch.");
            string[]? stem = cell.ReuseGuardStem ? branch.Stem : null;
            foreach (string form in surfaceOperations.Apply(
                lemma, cell.OperationOffset, cell.OperationCount, forward: true, stem, budget: null))
            foreach (string tag in cell.FullTags)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (kind == Kind.Invariant && form == lemma && tag == baseFullTag)
                    invariantBaseEmitted = true;
                if (observations is not null)
                {
                    if (observations.Count >= maximumObservations)
                        throw new BinaryProofBudget.Failure(BinaryProofBudget.Outcome.ProofOverflow);
                    observations.Add(new(form, tag, surfaceOperations.RetainedOriginalPrefixLength(
                        lemma, form, cell.OperationOffset, cell.OperationCount)) { EdgeIndex = cell.OriginalEdgeIndex });
                }
                if (!seen.Add((form, tag))) continue;
                if (seen.Count > 256)
                    throw new InvalidDataException("Generated lexeme exceeds the complete 256-reading bound.");
            }
        }
        if (kind == Kind.Invariant
                ? !invariantBaseEmitted || seen.Any(tuple => tuple.Form != lemma)
                : !seen.Any(tuple => tuple.Form != lemma))
            throw new InvalidDataException("Generated lexeme has no applicable non-lemma-surface output.");
        if (observations is not null)
            foreach (GenerationObservation observation in observations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observe!(observation);
            }
        cancellationToken.ThrowIfCancellationRequested();
        return seen;
    }

    internal readonly record struct Cell(
        int OriginalEdgeIndex,
        int GuardTransformIndex,
        int OperationOffset,
        int OperationCount,
        bool ReuseGuardStem,
        bool Approved,
        bool Reviewed,
        string[] FullTags);

    internal enum Kind { Inflecting, Invariant, Lexical }
}
