namespace BgLang.Core;

/// <summary>Another exact generation binding for one approved ordinary program; no graph path.</summary>
internal sealed record OrdinaryParadigmAlias(
    string Id, string RuleId, string BaseFullTag, string TargetRuleId, string ReviewState, string[] EvidenceIds);
