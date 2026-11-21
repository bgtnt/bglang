namespace BgLang.Core;

/// <summary>Legacy lexical tag projection over an approved ordinary program; no surface clitic or graph authority.</summary>
internal sealed record LexicalCliticProjection(
    string Id, string RuleId, string BaseFullTag, string ParentRuleId, string ParentBaseFullTag,
    string CliticTagPart, string ReviewState, string[] EvidenceIds)
{
    internal string ProjectTag(string tag)
    {
        string[] parts = tag.Split(',');
        int insert = parts.Length;
        while (insert > 0 && parts[insert - 1] is "мн.ч." or "Членуван" or "Пълен") insert--;
        return string.Join(',', parts.Take(insert).Append(CliticTagPart).Concat(parts.Skip(insert)));
    }
}
