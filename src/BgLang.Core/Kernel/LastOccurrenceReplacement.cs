namespace BgLang.Core;

/// <summary>One ordinal character replacement in the pre-tail body, with exact inverse replay.</summary>
internal sealed record LastOccurrenceReplacement(char From, char To, int MinimumIndex, int MinimumFollowingLength)
{
    internal bool TryApply(string input, string fromTail, string toTail, out string result, out int match)
    {
        result = string.Empty;
        match = -1;
        if (!input.EndsWith(fromTail, StringComparison.Ordinal)) return false;
        int length = input.Length - fromTail.Length;
        if (length == 0) return false;
        match = input.LastIndexOf(From, length - 1, length);
        if (match < MinimumIndex || length - match - 1 < MinimumFollowingLength) return false;
        result = input[..match] + To + input[(match + 1)..length] + toTail;
        return true;
    }

    internal IEnumerable<string> Reverse(string output, string fromTail, string toTail, GraphSearchBudget budget)
    {
        if (!output.EndsWith(toTail, StringComparison.Ordinal)) yield break;
        int length = output.Length - toTail.Length;
        for (int index = 0; index < length; index++)
        {
            // Rejected positions are work too; never publish a silently capped inverse.
            budget.Take();
            if (output[index] != To) continue;
            // Debit reconstruction, the last-index scan and replay before allocating either string.
            long replayWork = 2L * length + fromTail.Length + output.Length;
            if (replayWork > int.MaxValue || !budget.TryTake((int)replayWork))
                throw new GraphSearchBudget.Exhausted();
            string input = output[..index] + From + output[(index + 1)..length] + fromTail;
            if (TryApply(input, fromTail, toTail, out string replay, out int selected) &&
                selected == index && string.Equals(replay, output, StringComparison.Ordinal))
                yield return input;
        }
    }
}
