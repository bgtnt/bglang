using static BgLang.Core.WordFormationGraph;

namespace BgLang.Core;

/// <summary>Typed state constraints and forward/reverse edge compatibility.</summary>
internal static class GraphState
{
    internal static string GetPartOfSpeech(string stateId)
    {
        int start = "state:".Length;
        int separator = stateId.IndexOf(':', start);
        return separator < 0 ? string.Empty : stateId[start..separator];
    }

    internal readonly record struct Binding(
        string? FromStateId,
        string? ToStateId,
        string? PreserveMatchingPartOfSpeech)
    {
        internal static Binding Exact(string fromStateId, string toStateId) =>
            new(fromStateId, toStateId, null);

        internal static Binding Preserve(string partOfSpeech) =>
            new(null, null, partOfSpeech);

        internal bool PreservesMatchingState => PreserveMatchingPartOfSpeech is not null;

        internal Constraint OutputConstraint => ToStateId is not null
            ? Constraint.Exact(ToStateId)
            : Constraint.PartOfSpeechOnly(PreserveMatchingPartOfSpeech!);

        internal bool TryTraverse(
            TraversalDirection direction,
            Constraint current,
            out Constraint next)
        {
            if (PreserveMatchingPartOfSpeech is not null)
            {
                if (string.Equals(PreserveMatchingPartOfSpeech, current.PartOfSpeech, StringComparison.Ordinal))
                {
                    next = current;
                    return true;
                }
                next = default;
                return false;
            }

            string required = direction == TraversalDirection.Forward ? FromStateId! : ToStateId!;
            if (current.ExactStateId is not null)
            {
                if (!string.Equals(current.ExactStateId, required, StringComparison.Ordinal))
                {
                    next = default;
                    return false;
                }
            }
            else if (direction == TraversalDirection.Forward ||
                     !string.Equals(current.PartOfSpeech, GetPartOfSpeech(required), StringComparison.Ordinal))
            {
                next = default;
                return false;
            }

            next = Constraint.Exact(
                direction == TraversalDirection.Forward ? ToStateId! : FromStateId!);
            return true;
        }
    }

    internal readonly record struct Constraint(string? ExactStateId, string PartOfSpeech)
    {
        internal bool IsExact => ExactStateId is not null;

        internal static Constraint Exact(string stateId) =>
            new(stateId, GetPartOfSpeech(stateId));

        internal static Constraint PartOfSpeechOnly(string partOfSpeech) =>
            new(null, partOfSpeech);

        internal bool MatchesExactState(string stateId) => ExactStateId is not null
            ? string.Equals(ExactStateId, stateId, StringComparison.Ordinal)
            : string.Equals(PartOfSpeech, GetPartOfSpeech(stateId), StringComparison.Ordinal);

        internal bool Matches(Constraint other) => ExactStateId is not null
            ? string.Equals(ExactStateId, other.ExactStateId, StringComparison.Ordinal)
            : string.Equals(PartOfSpeech, other.PartOfSpeech, StringComparison.Ordinal);
    }

}
