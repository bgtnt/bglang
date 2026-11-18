namespace BgLang.Core;

/// <summary>Shared reviewed clitic membership and lexical-host boundaries.</summary>
internal static class ReviewedCliticPolicy
{
    // A token may belong to both role sets (ни/ви); this is membership, not reading selection.
    internal static bool MatchesSlot(string classId, bool? isVariable, string token)
    {
        string word = BulgarianTextNormalizer.NormalizeKey(token);
        return classId switch
        {
            "short-dative-clitic" when isVariable == true =>
                BulgarianFunctionWords.HasAny(word, FunctionWordClasses.ShortDativeClitic),
            "short-accusative-clitic" when isVariable == true =>
                BulgarianFunctionWords.HasAny(word, FunctionWordClasses.ShortAccusativeClitic),
            "reflexive-accusative-se" when isVariable == false =>
                BulgarianFunctionWords.HasAny(word, FunctionWordClasses.ReflexiveAccusativeSe),
            _ => false
        };
    }

    internal static bool IsHostWord(string word) =>
        !BulgarianFunctionWords.HasAny(BulgarianTextNormalizer.NormalizeKey(word), FunctionWordClasses.CliticNonHost);
}
