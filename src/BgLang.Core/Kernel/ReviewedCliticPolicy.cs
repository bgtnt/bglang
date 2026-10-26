namespace BgLang.Core;

/// <summary>Shared reviewed clitic membership and lexical-host boundaries.</summary>
internal static class ReviewedCliticPolicy
{
    private static readonly HashSet<string> NonHosts =
        new(["да", "не", "ще", "се", "си", "ми", "ти", "му", "и", "ѝ", "ни", "ви", "им",
             "ме", "те", "го", "я", "ги"], StringComparer.Ordinal);

    // A token may belong to both role sets (ни/ви); this is membership, not reading selection.
    internal static bool MatchesSlot(string classId, bool? isVariable, string token)
    {
        string word = BulgarianTextNormalizer.NormalizeKey(token);
        return classId switch
        {
            "short-dative-clitic" when isVariable == true => word is "ми" or "ти" or "му" or "ѝ" or "ни" or "ви" or "им",
            "short-accusative-clitic" when isVariable == true => word is "ме" or "те" or "го" or "я" or "ни" or "ви" or "ги",
            "reflexive-accusative-se" when isVariable == false => word == "се",
            _ => false
        };
    }

    internal static bool IsHostWord(string word) => !NonHosts.Contains(BulgarianTextNormalizer.NormalizeKey(word));
}
