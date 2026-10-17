namespace BgLang.Core;

/// <summary>A reverse or identity proposal awaiting ordinary donor authority and complete replay.</summary>
internal sealed record MorphologyProposal(string Lemma, string BaseFullTag, string FullTag, string RuleId);
