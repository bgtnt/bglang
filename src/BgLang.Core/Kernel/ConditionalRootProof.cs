using System.Runtime.CompilerServices;
using BgLang.Contracts;
using static BgLang.Core.WordFormationGraph;
using Transform = BgLang.Core.GraphSurfaceOperations.Transform;

namespace BgLang.Core;

/// <summary>Conditional path authority over compiled branches and ordinary donor cells.</summary>
internal sealed class ConditionalRootProof(Edge[] _edges, Transform[] _transforms, ConditionalProgram[] _conditionalPrograms,
    ConditionalWeakTable<IRuntimeLexicon, ConditionalSupportInventory> _conditionalSupport, GraphSurfaceOperations _operations)
{
    internal bool TryPrepare(
        PathCandidate candidate,
        string fullTag,
        IRuntimeLexicon lexicon,
        ref ConditionalSupportInventory? inventory,
        GraphSearchBudget? graphBudget,
        out PathCandidate? prepared,
        out bool supportIncomplete)
    {
        prepared = null;
        supportIncomplete = false;
        if (candidate.HasExactRoot) return false;
        foreach (int index in candidate.EdgePath)
            if (!Approved(_edges[index]) || _edges[index].Origin == EdgeOrigin.Application ||
                _edges[index].Kind == EdgeKind.Compound) return false;
        if (candidate.FromStateId is not string stateId || !candidate.RootFeaturesPreserved) return false;

        bool baseCell = string.Equals(candidate.ConditionalCellId, "base", StringComparison.Ordinal);
        ConditionalProgram? program = null;
        if (candidate.ConditionalProgramId is string programId)
            foreach (ConditionalProgram item in _conditionalPrograms)
                if (item.StateId == stateId && item.RuleId == programId && item.BaseTag == candidate.ConditionalBaseFullTag)
                { program = item; break; }
        ConditionalBranch? branch = null;
        if (baseCell && program is not null)
            foreach (ConditionalBranch item in program.Branches)
                if (candidate.ConditionalBranchId is null || string.Equals(item.Id, candidate.ConditionalBranchId, StringComparison.Ordinal))
                { branch = item; break; }
        int terminalIndex = candidate.ConditionalTerminalEdge >= 0
            ? candidate.ConditionalTerminalEdge
            : candidate.EdgePath.Length > 0 && _edges[candidate.EdgePath[^1]].Origin == EdgeOrigin.Paradigm
                ? candidate.EdgePath[^1] : -1;
        if (!baseCell && terminalIndex < 0)
            return false;
        Edge terminal = terminalIndex >= 0 ? _edges[terminalIndex] : default;
        string? baseTag = baseCell ? program?.BaseTag : terminal.RequiredBaseFullTag;
        if (baseTag is null || program is null && baseCell ||
            !baseCell && (terminal.Origin != EdgeOrigin.Paradigm || terminal.AllowedFullTags is not { Length: > 0 } ||
                !string.Equals(terminal.StateBinding.FromStateId, stateId, StringComparison.Ordinal)))
            return false;

        if (!baseCell && !IsConditionalProgram(terminal, _transforms))
            return false;
        string partOfSpeech = StatePartOfSpeech(stateId);
        int prefixCount = baseCell ? candidate.EdgePath.Length : Math.Max(0, candidate.EdgePath.Length - 1);
        for (int index = 0; index < prefixCount; index++)
        {
            Edge prefix = _edges[candidate.EdgePath[index]];
            if (prefix.Origin != EdgeOrigin.Generic || prefix.Kind != EdgeKind.Prefix ||
                prefix.StateBinding.PreserveMatchingPartOfSpeech != partOfSpeech) return false;
        }

        List<(string Surface, string Edge)>? missingPrefixes = null;
        string current = candidate.Base;
        WordReading[] rootExact = FindExactSelfReadings(lexicon, current).ToArray();
        if (ConflictsWithBaseTag(rootExact, baseTag))
            return false;
        bool rootMissing = rootExact.Length == 0;
        for (int index = 0; index < candidate.EdgePath.Length; index++)
        {
            Edge edge = _edges[candidate.EdgePath[index]];
            if (edge.Origin == EdgeOrigin.Generic && edge.Kind == EdgeKind.Prefix)
            {
                WordReading[] exact = FindExactSelfReadings(lexicon, current).ToArray();
                if (ConflictsWithBaseTag(exact, baseTag))
                    return false;
                if (exact.Length == 0)
                    (missingPrefixes ??= []).Add((current, edge.Id));
            }
            else if (edge.Origin == EdgeOrigin.Paradigm)
            {
                WordReading[] exact = FindExactSelfReadings(lexicon, current).ToArray();
                if (ConflictsWithBaseTag(exact, baseTag))
                    return false;
            }
            string? next = null;
            foreach (string value in _operations.Apply(current, edge, true, null, graphBudget))
            {
                if (next is null) next = value;
                else if (!string.Equals(next, value, StringComparison.Ordinal)) return false;
            }
            if (next is null) return false;
            current = next;
        }

        int transformIndex = baseCell ? branch?.TransformIndex ?? -1 : terminal.ParadigmStemTransform;
        if (transformIndex < 0) return false;
        Transform transform = _transforms[transformIndex];
        char? finalConsonant = null;
        if (transform.Kind == TransformKind.PenultimateDelete)
        {
            graphBudget?.Take();
            if (candidate.Base.Length == 0 || !BulgarianOrthography.IsConsonant(candidate.Base[^1]) ||
                baseCell && !transform.CanApplyForward(candidate.Base)) return false;
            finalConsonant = candidate.Base[^1];
        }

        IRuntimeLexicon inventoryOwner = lexicon is GraphWorkLexicon work ? work.Source : lexicon;
        inventory ??= _conditionalSupport.GetValue(inventoryOwner, static _ => new ConditionalSupportInventory());
        string programRule = (program?.RuleId ?? terminal.RuleId)!;
        string branchId = (baseCell ? branch?.Id : terminal.ParadigmBranchId ?? terminal.Id)!;
        var cell = new ConditionalSupportInventory.Cell(stateId, programRule,
            branchId, terminalIndex, baseCell, baseTag, fullTag, finalConsonant);
        // Null or empty literal endings require a complete payload walk, not a rejection.
        string ending = baseCell && finalConsonant is char captured
            ? transform.From + captured
            : baseCell && transform.Kind != TransformKind.VariantSetNormalize
                ? transform.From : baseCell ? "" : _operations.FinalReverseEnding(terminal) ?? "";
        if (!inventory.TryGetCompleted(cell, out string[]? proven))
            proven = ReadSupport(inventory, cell, ending, lexicon, graphBudget, baseCell, transform, finalConsonant, terminal);
        if (proven is null) { supportIncomplete = true; return false; }
        // The shared proof is input-independent. Self cannot contribute a donor.
        string[] supporting = Array.IndexOf(proven, candidate.Base) < 0 ? proven
            : ExcludeSelf(proven, candidate.Base);
        if (supporting.Length < 2) return false;

        List<ExactSelfReadingRequirement> unmet = [];
        if (rootMissing) unmet.Add(new(candidate.Base, baseTag, "program|" + programRule + "|base"));
        if (missingPrefixes is not null)
            foreach (var missing in missingPrefixes) unmet.Add(new(missing.Surface, baseTag, missing.Edge));
        prepared = candidate with
        {
            ConditionalProgramId = programRule,
            ConditionalBranchId = branchId,
            ConditionalBaseFullTag = baseTag,
            ConditionalTerminalEdge = terminalIndex,
            ConditionalFullTag = fullTag,
            IsConditional = true,
            ConditionalSupportCount = supporting.Length,
            SupportingLemmas = supporting,
            UnmetRequirements = unmet.Distinct().ToArray()
        };
        return true;

        static bool ConflictsWithBaseTag(WordReading[] readings, string tag)
        {
            if (readings.Length == 0) return false;
            foreach (WordReading reading in readings)
                if (string.Equals(reading.PartOfSpeech, tag, StringComparison.Ordinal)) return false;
            return true;
        }

        static string[] ExcludeSelf(string[] donors, string root) => donors.Where(lemma => lemma != root).ToArray();
    }

    private string[]? ReadSupport(ConditionalSupportInventory inventory, ConditionalSupportInventory.Cell cell,
        string ending, IRuntimeLexicon lexicon, GraphSearchBudget? graphBudget, bool baseCell,
        Transform transform, char? finalConsonant, Edge terminal)
    {
        return inventory.Get(cell, ending, lexicon, graphBudget, (reading, lemma) =>
        {
            if (baseCell)
            {
                graphBudget?.Take();
                return BulgarianTextNormalizer.NormalizeKey(reading.Form) == lemma &&
                    (finalConsonant is not char required || lemma.Length > 0 && lemma[^1] == required) &&
                    transform.CanApplyForward(lemma);
            }
            if (finalConsonant is char requiredFinal)
            {
                graphBudget?.Take();
                if (lemma.Length == 0 || lemma[^1] != requiredFinal) return false;
            }
            return _operations.Apply(lemma, terminal, true, null, graphBudget)
                .Contains(BulgarianTextNormalizer.NormalizeKey(reading.Form), StringComparer.Ordinal);
        });
    }

    internal static bool IsConditionalProgram(Edge edge, Transform[] transforms)
    {
        if (edge.RequiredBaseFullTag is null || edge.AllowedFullTags is not { Length: > 0 } ||
            edge.ParadigmStemTransform is not (>= 0 and < int.MaxValue))
            return false;
        Transform transform = transforms[edge.ParadigmStemTransform];
        if (transform.ExactInput is not null) return false;
        if (transform.Kind == TransformKind.PenultimateDelete)
            return transform.From.Length == 1 && !char.IsSurrogate(transform.From[0]) && transform.To.Length == 0 &&
                transform.MinimumInputLength >= 3 && transform.FinalCharacterClass == InputFinalCharacterClass.BulgarianConsonant;
        if (transform.Kind == TransformKind.LastOccurrenceReplace)
            return transform.LastOccurrence is not null && transform.MinimumInputLength >= transform.From.Length;
        return transform.Kind is (TransformKind.TailReplace or TransformKind.TailDelete or
            TransformKind.VariantSetNormalize) && transform.From.Length > 0 &&
            transform.MinimumInputLength >= transform.From.Length;
    }


    internal static ConditionalProgram[] CompilePrograms(Edge[] edges, Transform[] transforms)
    {
        return edges.Select((edge, index) => (edge, index))
            .Where(item => item.edge.Origin == EdgeOrigin.Paradigm && Approved(item.edge) &&
                           IsConditionalProgram(item.edge, transforms))
            .GroupBy(item => (item.edge.StateBinding.FromStateId!, item.edge.RuleId!, item.edge.RequiredBaseFullTag!))
            .Select(group =>
            {
                (Edge edge, int index) first = group.First();
                ConditionalBranch[] branches = group
                    .Select(item => new ConditionalBranch(
                        item.edge.ParadigmBranchId ?? item.edge.Id,
                        item.edge.ParadigmStemTransform,
                        item.edge.Trust,
                        item.edge.SupportCount))
                    .DistinctBy(item => item.Id, StringComparer.Ordinal)
                    .OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
                return new ConditionalProgram(first.edge.StateBinding.FromStateId!, first.edge.RuleId!,
                    first.edge.RequiredBaseFullTag!, branches);
            })
            .OrderBy(item => item.StateId, StringComparer.Ordinal)
            .ThenBy(item => item.RuleId, StringComparer.Ordinal)
            .ThenBy(item => item.BaseTag, StringComparer.Ordinal).ToArray();
    }
}
