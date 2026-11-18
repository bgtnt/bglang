namespace BgLang.Contracts;

/// <summary>The implemented rule catalog and the constructions examined for this text.</summary>
public sealed record TextGrammarEvidence(string Version, IReadOnlyList<TextGrammarRule> Rules,
    IReadOnlyList<TextGrammarMatch> Matches);

public sealed record TextGrammarRule(string Id, string DiagnosticCode, string Pattern, TextGrammarConstraint Constraint,
    string Explanation, string SourceSection, string SourceUrl);

public enum TextGrammarConstraint { NumberGender, PersonNumber, ArticlePlacement, MasculineArticle }
public enum TextGrammarAssessment { Compatible, Mismatch, Ambiguous, Unassessed }

/// <summary>Local coverage and alternatives over original UTF-16 spans. Repairs are independent proposals.</summary>
public sealed record TextGrammarMatch(string RuleId, int Start, int Length, IReadOnlyList<int> ControllerTokenIndices,
    IReadOnlyList<int> DependentTokenIndices, TextGrammarAssessment Assessment,
    IReadOnlyList<TextGrammarConflict> Conflicts, IReadOnlyList<TextGrammarRepair> Repairs, string? Reason);

/// <summary>Original complete reading references. ExpectedFrom names evidence for the controller or governor.</summary>
public sealed record TextGrammarConflict(int TokenIndex, IReadOnlyList<TextReadingReference> Actual,
    IReadOnlyList<TextReadingReference> ExpectedFrom);

public sealed record TextGrammarRepair(IReadOnlyList<TextGrammarEdit> Edits)
{
    /// <summary>Additional authority needed before choosing this locally admissible form.</summary>
    public IReadOnlyList<string> Qualifications { get; init; } = [];
}

public sealed record TextGrammarEdit(int TokenIndex, int Start, int Length, string Replacement);
