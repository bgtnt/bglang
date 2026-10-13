namespace BgLang.Core;

internal static class PathMorphologyPolicy
{
    internal enum Outcome { Licensed, Incompatible, MetadataMissing }

    internal readonly record struct Result(Outcome Outcome, string? Lemma, string[] Tags, bool HasRoot);
    internal readonly record struct Program(string? BaseTag, bool Approved, int TransformId);

    internal readonly record struct Input(
        string Lemma, bool Approved, bool Terminal, bool PreservesRootFeatures,
        string[] RootTags, string? TerminalBaseTag, int? TerminalTransformId,
        string[] TerminalTags, Program[] Programs);

    internal static Result Evaluate(Input input, Func<int, bool> transformMatches)
    {
        bool hasRoot = input.RootTags.Length > 0;
        bool rootMatches = !input.Terminal || !input.PreservesRootFeatures ||
            input.RootTags.Contains(input.TerminalBaseTag!, StringComparer.Ordinal);
        if (!input.Approved || input.Terminal && (input.TerminalTransformId is null ||
            !transformMatches(input.TerminalTransformId.Value) || !rootMatches))
            return new(Outcome.Incompatible, input.Lemma, [], hasRoot);
        if (input.Terminal)
        {
            string[] tags = input.TerminalTags.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return new(tags.Length == 0 ? Outcome.MetadataMissing : Outcome.Licensed, input.Lemma, tags, hasRoot);
        }
        string[] baseTags = input.Programs
            .Where(program => program.Approved && transformMatches(program.TransformId) && program.BaseTag is not null &&
                (!input.PreservesRootFeatures || input.RootTags.Contains(program.BaseTag, StringComparer.Ordinal)))
            .Select(program => program.BaseTag!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        bool declared = input.Programs.Any(program => program.Approved && program.BaseTag is not null);
        return new(baseTags.Length > 0 ? Outcome.Licensed : declared ? Outcome.Incompatible : Outcome.MetadataMissing,
            input.Lemma, baseTags, hasRoot);
    }
}
