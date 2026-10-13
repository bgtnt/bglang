using static BgLang.Core.WordFormationGraph;

namespace BgLang.Core;

/// <summary>Shared forward/reverse surface operations. Single-valued programs avoid LINQ pipelines.</summary>
internal sealed class GraphSurfaceOperations(Morph[] morphs, GraphSurfaceOperations.Transform[] transforms,
    string[] literals, SurfaceOperation[] operations)
{
    internal IEnumerable<string> Apply(string surface, Edge edge, bool forward,
        string[]? forwardStem, GraphSearchBudget? budget) =>
        Apply(surface, edge.OperationOffset, edge.OperationCount, forward, forwardStem, budget,
            edge.ParadigmStemTransform);

    internal IEnumerable<string> Apply(string surface, int operationOffset, int operationCount, bool forward,
        string[]? forwardStem, GraphSearchBudget? budget, int guardTransformIndex = -1)
    {
        // Lemma-source cells omit the stem operation but retain its whole branch guard.
        bool separateGuard = guardTransformIndex >= 0 && !(operationCount > 0 &&
            operations[operationOffset] is { Kind: OperationKind.Transform } guard &&
            guard.OperandIndex == guardTransformIndex);
        if (separateGuard && forward && !MatchesGuard(surface, guardTransformIndex, budget)) return [];
        int first = forwardStem is null ? 0 : 1;
        bool single = forwardStem is null || forwardStem.Length == 1;
        for (int step = first; single && step < operationCount; step++)
        {
            SurfaceOperation op = operations[operationOffset + step];
            if (op.Kind == OperationKind.Transform &&
                (transforms[op.OperandIndex].Kind == TransformKind.VariantSetNormalize ||
                 !forward && transforms[op.OperandIndex].Kind == TransformKind.LastOccurrenceReplace))
                single = false;
        }
        if (single)
        {
            string current = forwardStem is null ? surface : forwardStem[0];
            for (int step = first; step < operationCount; step++)
            {
                SurfaceOperation op = operations[forward ? operationOffset + step
                    : operationOffset + operationCount - 1 - step];
                budget?.Take();
                if (!TrySingle(current, op, forward, out current)) return [];
            }
            return current.Length == 0 || separateGuard && !forward &&
                !MatchesGuard(current, guardTransformIndex, budget) ? [] : new[] { current };
        }
        IEnumerable<string> values = forwardStem ?? new[] { surface };
        for (int step = first; step < operationCount; step++)
        {
            SurfaceOperation op = operations[forward ? operationOffset + step
                : operationOffset + operationCount - 1 - step];
            values = Expand(values, op, forward, budget);
        }
        IEnumerable<string> results = values.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal);
        return separateGuard && !forward ? GuardReverseResults(results, guardTransformIndex, budget) : results;
    }

    private bool MatchesGuard(string lemma, int transformIndex, GraphSearchBudget? budget)
    {
        budget?.Take();
        return transforms[transformIndex].CanApplyForward(lemma);
    }

    private IEnumerable<string> GuardReverseResults(IEnumerable<string> lemmas, int transformIndex, GraphSearchBudget? budget)
    {
        foreach (string lemma in lemmas)
            if (MatchesGuard(lemma, transformIndex, budget)) yield return lemma;
    }

    internal IEnumerable<string> ApplyTransform(string surface, int transformIndex) =>
        transforms[transformIndex].Apply(surface, forward: true);

    internal int RetainedOriginalPrefixLength(string lemma, string form, int operationOffset, int operationCount)
    {
        if (operationCount == 0)
            return lemma.Length;
        if (operationCount == 1 && operations[operationOffset].Kind == OperationKind.EmitLiteral)
            return 0;
        if (operationCount > 2)
            throw new InvalidDataException("Stress carry requires at most one stem transform and one literal append.");

        SurfaceOperation first = operations[operationOffset];
        if (first.Kind != OperationKind.Transform && first.Kind != OperationKind.AppendLiteral)
            throw new InvalidDataException("Stress carry requires a stem transform followed by a literal append.");
        if (operationCount == 2 &&
            (first.Kind != OperationKind.Transform ||
             operations[operationOffset + 1].Kind != OperationKind.AppendLiteral))
            throw new InvalidDataException("Stress carry requires a stem transform followed by a literal append.");

        int appendLength = operationCount == 2
            ? literals[operations[operationOffset + 1].OperandIndex].Length
            : first.Kind == OperationKind.AppendLiteral ? literals[first.OperandIndex].Length : 0;
        if (appendLength > form.Length)
            throw new InvalidDataException("Stress carry literal append is longer than the generated surface.");
        string stem = appendLength == 0 ? form : form[..^appendLength];
        Transform transform = first.Kind == OperationKind.Transform ? transforms[first.OperandIndex] : default;
        int prefix;
        if (first.Kind != OperationKind.Transform || transform.Kind == TransformKind.Identity)
            prefix = lemma.Length;
        else if (transform.Kind == TransformKind.VariantSetNormalize)
        {
            prefix = -1;
            foreach (string variant in transform.Variants)
            {
                if (!lemma.EndsWith(variant, StringComparison.Ordinal)) continue;
                string expected = variant.Length == 0 ? lemma + transform.To : lemma[..^variant.Length] + transform.To;
                if (string.Equals(expected, stem, StringComparison.Ordinal))
                {
                    prefix = lemma.Length - variant.Length;
                    break;
                }
            }
            if (prefix < 0)
                throw new InvalidDataException("Stress carry could not identify the matched variant tail.");
        }
        else if (transform.Kind == TransformKind.PenultimateDelete)
        {
            if (!transform.TryPenultimateDelete(lemma, true, out string expected) || expected != stem)
                throw new InvalidDataException("Stress carry could not replay the penultimate deletion.");
            prefix = lemma.Length - 2;
        }
        else if (transform.Kind == TransformKind.LastOccurrenceReplace)
        {
            if (transform.LastOccurrence is not { } last ||
                !last.TryApply(lemma, transform.From, transform.To, out string expected, out int match) || expected != stem)
                throw new InvalidDataException("Stress carry could not replay the last-occurrence transform.");
            prefix = last.From != last.To ? match : transform.From == transform.To ? lemma.Length : lemma.Length - transform.From.Length;
        }
        else
        {
            string from = transform.From;
            if (from.Length > 0 && !lemma.EndsWith(from, StringComparison.Ordinal))
                throw new InvalidDataException("Stress carry transform does not match the source lemma tail.");
            prefix = lemma.Length - from.Length;
            string expected = from.Length == 0 ? lemma + transform.To : lemma[..prefix] + transform.To;
            if (!string.Equals(expected, stem, StringComparison.Ordinal))
                throw new InvalidDataException("Stress carry transform output does not match the generated stem.");
        }
        if (prefix < 0 || prefix > lemma.Length || !form.StartsWith(lemma[..prefix], StringComparison.Ordinal))
            throw new InvalidDataException("Stress carry output does not retain the calculated source prefix.");
        return prefix;
    }

    private IEnumerable<string> Expand(IEnumerable<string> values, SurfaceOperation op, bool forward, GraphSearchBudget? budget)
    {
        foreach (string value in values)
            foreach (string result in ApplyOne(value, op, forward, budget)) yield return result;
    }

    internal IEnumerable<string> ApplyOne(string surface, SurfaceOperation op, bool forward, GraphSearchBudget? budget = null)
    {
        budget?.Take();
        if (op.Kind == OperationKind.Transform &&
            (transforms[op.OperandIndex].Kind == TransformKind.VariantSetNormalize ||
             !forward && transforms[op.OperandIndex].Kind == TransformKind.LastOccurrenceReplace))
            return transforms[op.OperandIndex].Apply(surface, forward, budget);
        return TrySingle(surface, op, forward, out string result) ? new[] { result } : [];
    }

    private bool TrySingle(string surface, SurfaceOperation op, bool forward, out string result)
    {
        result = "";
        if (op.Kind == OperationKind.EmitLiteral)
        {
            if (!forward) return false;
            result = literals[op.OperandIndex];
            return result.Length > 0;
        }
        if (op.Kind == OperationKind.Transform)
        {
            Transform transform = transforms[op.OperandIndex];
            if (transform.Kind == TransformKind.PenultimateDelete)
                return transform.TryPenultimateDelete(surface, forward, out result);
            if ((forward || transform.Kind == TransformKind.Identity) && !transform.AppliesToInput(surface)) return false;
            if (transform.Kind == TransformKind.Identity) { result = surface; return true; }
            if (transform.Kind == TransformKind.LastOccurrenceReplace)
                return forward && transform.LastOccurrence is { } last &&
                    last.TryApply(surface, transform.From, transform.To, out result, out _);
            string from = forward ? transform.From : transform.To;
            string to = forward ? transform.To : transform.From;
            if (!surface.EndsWith(from, StringComparison.Ordinal)) return false;
            result = surface[..(surface.Length - from.Length)] + to;
            return result.Length > 0 && (forward || transform.AppliesToInput(result));
        }
        string affix = op.Kind == OperationKind.AppendLiteral ? literals[op.OperandIndex] : morphs[op.OperandIndex].Text;
        bool prefix = op.Kind == OperationKind.PrependMorph;
        if (forward) { result = prefix ? affix + surface : surface + affix; return true; }
        if (surface.Length <= affix.Length || !(prefix ? surface.StartsWith(affix, StringComparison.Ordinal)
                : surface.EndsWith(affix, StringComparison.Ordinal))) return false;
        result = prefix ? surface[affix.Length..] : surface[..(surface.Length - affix.Length)];
        return true;
    }
    internal readonly record struct Transform(
        string Id,
        TransformKind Kind,
        string From,
        string To,
        string[] Variants,
        ReviewTrust Trust,
        int MinimumInputLength,
        InputFinalCharacterClass FinalCharacterClass,
        InputPreTailCharacterClass PreTailCharacterClass,
        string? ExcludedInputTail,
        LastOccurrenceReplacement? LastOccurrence = null,
        string? ExactInput = null)
    {
        internal IEnumerable<string> Apply(string surface, bool forward, GraphSearchBudget? budget = null)
        {
            if (Kind == TransformKind.PenultimateDelete)
                return TryPenultimateDelete(surface, forward, out string deleted) ? [deleted] : [];
            if (Kind == TransformKind.Identity)
                return AppliesToInput(surface) ? new[] { surface } : [];
            if (forward && !AppliesToInput(surface))
                return [];
            if (Kind == TransformKind.LastOccurrenceReplace)
            {
                if (LastOccurrence is not { } last) return [];
                return forward
                    ? last.TryApply(surface, From, To, out string result, out _) ? [result] : []
                    : last.Reverse(surface, From, To, budget ?? new GraphSearchBudget(4096)).Where(AppliesToInput);
            }
            if (Kind == TransformKind.VariantSetNormalize)
            {
                string canonical = To;
                string[] variants = Variants;
                IEnumerable<string> results = forward
                    ? variants.SelectMany(variant => ReplaceTail(surface, variant, canonical))
                    : variants.SelectMany(variant => ReplaceTail(surface, canonical, variant));
                return forward ? results : results.Where(AppliesToInput);
            }
            string from = forward ? From : To;
            string to = forward ? To : From;
            IEnumerable<string> replacements = ReplaceTail(surface, from, to);
            return forward
                ? replacements
                : replacements.Where(AppliesToInput);
        }

        internal bool AppliesToInput(string surface) =>
            (ExactInput is null || string.Equals(surface, ExactInput, StringComparison.Ordinal)) &&
            surface.Length >= MinimumInputLength &&
            (ExcludedInputTail is null || !surface.EndsWith(ExcludedInputTail, StringComparison.Ordinal)) &&
            (FinalCharacterClass == InputFinalCharacterClass.None ||
             surface.Length > 0 && BulgarianOrthography.IsConsonant(surface[^1])) &&
            (PreTailCharacterClass == InputPreTailCharacterClass.None ||
             From.Length > 0 && surface.Length > From.Length &&
             surface.EndsWith(From, StringComparison.Ordinal) &&
             MatchesPreTail(surface[surface.Length - From.Length - 1]));

        private bool MatchesPreTail(char value) => PreTailCharacterClass switch
        {
            InputPreTailCharacterClass.BulgarianConsonant => BulgarianOrthography.IsConsonant(value),
            InputPreTailCharacterClass.NotBulgarianConsonant => !BulgarianOrthography.IsConsonant(value),
            InputPreTailCharacterClass.BulgarianConsonantExceptDt => value is not ('д' or 'т') && BulgarianOrthography.IsConsonant(value),
            _ => false
        };

        internal bool CanApplyForward(string surface)
        {
            if (!AppliesToInput(surface))
                return false;
            if (Kind == TransformKind.Identity) return true;
            if (Kind == TransformKind.PenultimateDelete)
                return surface.Length >= 2 && surface[^2] == From[0] && !char.IsSurrogate(surface[^1]);
            if (Kind == TransformKind.LastOccurrenceReplace)
                return LastOccurrence is { } last && last.TryApply(surface, From, To, out _, out _);
            if (Kind == TransformKind.VariantSetNormalize)
            {
                foreach (string variant in Variants)
                    if (surface.EndsWith(variant, StringComparison.Ordinal) && surface.Length - variant.Length + To.Length > 0)
                        return true;
                return false;
            }
            return surface.EndsWith(From, StringComparison.Ordinal) && surface.Length - From.Length + To.Length > 0;
        }

        // One fixed position, with the original whole guard replayed after inverse insertion.
        internal bool TryPenultimateDelete(string surface, bool forward, out string result)
        {
            result = "";
            if (forward)
            {
                if (!CanApplyForward(surface)) return false;
                result = surface.Remove(surface.Length - 2, 1);
                return true;
            }
            if (surface.Length == 0 || char.IsSurrogate(surface[^1])) return false;
            result = surface.Insert(surface.Length - 1, From);
            return CanApplyForward(result);
        }

        private static string[] ReplaceTail(string surface, string from, string to)
        {
            if (from.Length > 0 && !surface.EndsWith(from, StringComparison.Ordinal))
                return [];
            string result = from.Length == 0 ? surface + to : surface[..^from.Length] + to;
            return result.Length == 0 ? [] : [result];
        }
    }

    internal bool IsAppendOnlyParadigm(
        Edge edge,
        string lemma,
        out string form,
        out string ending,
        GraphSearchBudget? budget = null)
    {
        form = ending = string.Empty;
        if (edge.OperationCount is < 1 or > 2)
            return false;
        SurfaceOperation tail = operations[edge.OperationOffset + edge.OperationCount - 1];
        if (tail.Kind != OperationKind.AppendLiteral || (ending = literals[tail.OperandIndex]).Length == 0)
            return false;
        if (edge.OperationCount == 1 && edge.ParadigmStemTransform >= 0 &&
            !MatchesGuard(lemma, edge.ParadigmStemTransform, budget))
            return false;
        if (edge.OperationCount == 2)
        {
            SurfaceOperation stem = operations[edge.OperationOffset];
            if (stem.Kind != OperationKind.Transform ||
                !ApplyOne(lemma, stem, forward: true, budget).SequenceEqual([lemma], StringComparer.Ordinal))
                return false;
        }
        form = lemma + ending;
        return !string.Equals(form, lemma, StringComparison.Ordinal);
    }

    internal string? FinalReverseEnding(Edge edge) => FinalReverseEnding(edge.OperationOffset, edge.OperationCount);

    internal string? FinalReverseEnding(int operationOffset, int operationCount)
    {
        if (operationCount == 0) return null;
        SurfaceOperation last = operations[operationOffset + operationCount - 1];
        return last.Kind == OperationKind.AppendLiteral ? literals[last.OperandIndex] : null;
    }
}
