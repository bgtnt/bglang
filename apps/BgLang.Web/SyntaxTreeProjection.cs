using BgLang.Contracts;
using System.Text.Json.Serialization;

namespace BgLang.Web;

/// <summary>Displays existing local evidence without selecting readings or constructing a second parse.</summary>
internal static class SyntaxTreeProjection
{
    internal static SyntaxTreeResult Build(TextAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        List<SyntaxTreeRole>[] roles = analysis.Tokens.Select(_ => new List<SyntaxTreeRole>()).ToArray();
        List<SyntaxTreeEdge> edges = [];
        List<string> reasons = ["local_evidence_only"];
        bool available = analysis.Context?.Available == true || analysis.Syntax?.Available == true || analysis.PredicateLinks?.Available == true ||
            analysis.NominalConstructions?.Available == true || analysis.Grammar is not null;
        AddAvailability("syntax", analysis.Syntax?.Available, analysis.Syntax?.UnavailableReason);
        AddAvailability("predicate", analysis.PredicateLinks?.Available, analysis.PredicateLinks?.UnavailableReason);
        AddAvailability("nominal", analysis.NominalConstructions?.Available, analysis.NominalConstructions?.UnavailableReason);
        if (analysis.Grammar is null) reasons.Add("grammar_not_requested");
        foreach (TextSyntaxGroupStatus group in analysis.Syntax?.AdditionalGroups ?? [])
            if (!group.Available) reasons.Add("syntax_group:" + group.GroupId + ":" + group.UnavailableReason);

        if (analysis.Syntax is { Available: true } syntax)
            for (int sentenceIndex = 0; sentenceIndex < syntax.Sentences.Count; sentenceIndex++)
            {
                TextSyntaxSentence sentence = syntax.Sentences[sentenceIndex];
                for (int candidateIndex = 0; candidateIndex < sentence.Candidates.Count; candidateIndex++)
                {
                    TextSyntaxCandidate candidate = sentence.Candidates[candidateIndex];
                    string alternative = $"syntax:{sentenceIndex}:{candidateIndex}";
                    foreach (TextSyntaxSupport target in candidate.Targets)
                        AddRole(target.TokenIndex, new(candidate.Role.ToString(), "candidate", "syntax",
                            candidate.RuleId, alternative, target, candidate.Context));
                    // Context supports a role candidate; it does not identify that role's syntactic governor.
                }
            }

        if (analysis.Context is { Available: true } context)
            foreach (TextContextSentence sentence in context.Sentences)
            {
                if (sentence.SelectedPredicateTokenIndex is not int predicateIndex ||
                    !sentence.SelectedRoles.Any(r => r.Role == TextContextRole.Predicate && r.TokenIndex == predicateIndex))
                    continue;
                foreach (TextContextRoleSelection subject in sentence.SelectedRoles.Where(r => r.Role == TextContextRole.Subject))
                    AddEdge(predicateIndex, subject.TokenIndex, "selected_subject", "selected", "context", null,
                        $"context:{sentence.Start}:{predicateIndex}", []);
            }

        if (analysis.PredicateLinks is { Available: true } predicate)
            for (int sentenceIndex = 0; sentenceIndex < predicate.Sentences.Count; sentenceIndex++)
            {
                TextPredicateSentence sentence = predicate.Sentences[sentenceIndex];
                foreach (TextPredicateOccurrence occurrence in sentence.Occurrences)
                    for (int readingIndex = 0; readingIndex < occurrence.Readings.Count; readingIndex++)
                    {
                        TextPredicateReading reading = occurrence.Readings[readingIndex];
                        string status = reading.Coverage == "competing_alternative" ? "alternative" :
                            reading.Coverage == "uncovered" ? "uncovered" :
                            reading.Coverage == "conflict_support" ? "conflict" : "candidate";
                        AddRole(occurrence.TokenIndex, new(reading.Kind, status, "predicate", null,
                            $"predicate-reading:{sentenceIndex}:{occurrence.TokenIndex}:{readingIndex}",
                            Support(occurrence.TokenIndex, reading.Reading, reading.GraphReading), [])
                            { Coverage = reading.Coverage });
                    }
                for (int linkIndex = 0; linkIndex < sentence.Links.Count; linkIndex++)
                {
                    TextPredicateLink link = sentence.Links[linkIndex];
                    string alternative = $"predicate:{sentenceIndex}:{linkIndex}";
                    TextSyntaxSupport[] supports = [link.Left, link.Right, .. link.Markers];
                    // Left/right preserve source order. In particular, future_marker is ще -> finite verb.
                    AddEdge(link.Left.TokenIndex, link.Right.TokenIndex, link.Kind, "candidate", "predicate", null,
                        alternative, supports);
                    foreach (TextSyntaxSupport marker in link.Markers)
                        AddEdge(link.Left.TokenIndex, marker.TokenIndex, link.Kind + "_marker", "candidate",
                            "predicate", null, alternative, supports);
                }
            }

        if (analysis.NominalConstructions is { Available: true } nominal)
            for (int phraseIndex = 0; phraseIndex < nominal.Phrases.Count; phraseIndex++)
            {
                TextNominalPhrase phrase = nominal.Phrases[phraseIndex];
                if (phrase.HeadSupports.Count == 0)
                {
                    string alternative = $"nominal:{phraseIndex}:unmeasured";
                    AddRole(phrase.HeadTokenIndex, new("nominal_head", "unassessed", "nominal", null,
                        alternative, null, []));
                    foreach (int modifier in phrase.ModifierTokenIndices)
                        AddEdge(phrase.HeadTokenIndex, modifier, "nominal_modifier", "unassessed", "nominal",
                            null, alternative, []);
                    continue;
                }
                for (int supportIndex = 0; supportIndex < phrase.HeadSupports.Count; supportIndex++)
                {
                    NominalHeadSupport head = phrase.HeadSupports[supportIndex];
                    string alternative = $"nominal:{phraseIndex}:{supportIndex}";
                    TextSyntaxSupport headSupport = Support(phrase.HeadTokenIndex, head.Reading, head.GraphReading);
                    AddRole(phrase.HeadTokenIndex, new("nominal_head", "candidate", "nominal", null,
                        alternative, headSupport, []) { ContextualGender = head.ContextualGender });
                    foreach (NominalMemberSupport member in head.Members)
                    {
                        TextSyntaxSupport memberSupport = new(member.TokenIndex, "nominal_member", member.Readings)
                            { GraphReadings = member.GraphReadings.Count == 0 ? null : member.GraphReadings };
                        AddRole(member.TokenIndex, new("nominal_modifier", Agreement(member.Assessment), "nominal",
                            null, alternative, memberSupport, [headSupport]));
                        AddEdge(phrase.HeadTokenIndex, member.TokenIndex, "nominal_modifier",
                            Agreement(member.Assessment), "nominal", null, alternative, [headSupport, memberSupport]);
                    }
                    foreach (int connector in phrase.ConnectorTokenIndices)
                    {
                        TextReadingReference[] readings = phrase.ConjunctionReadings?
                            .Where(row => row.TokenIndex == connector).ToArray() ?? [];
                        TextSyntaxSupport connectorSupport = new(connector, "nominal_connector", readings);
                        AddEdge(phrase.HeadTokenIndex, connector, "nominal_connector", "candidate", "nominal",
                            null, alternative, [headSupport, connectorSupport]);
                    }
                }
            }

        if (analysis.Grammar is { } grammar)
            for (int matchIndex = 0; matchIndex < grammar.Matches.Count; matchIndex++)
            {
                TextGrammarMatch match = grammar.Matches[matchIndex];
                string status = match.Assessment switch
                {
                    TextGrammarAssessment.Compatible => "compatible",
                    TextGrammarAssessment.Mismatch => "conflict",
                    TextGrammarAssessment.Ambiguous => "ambiguous",
                    _ => "unassessed"
                };
                // All pairs belong to one constraint match, not independently measured dependency edges.
                foreach (int controller in match.ControllerTokenIndices)
                    foreach (int dependent in match.DependentTokenIndices)
                        AddEdge(controller, dependent, "grammar_constraint", status, "grammar", match.RuleId,
                            $"grammar:{matchIndex}", []);
            }

        HashSet<int> linked = edges.SelectMany(edge => new[] { edge.From, edge.To }).ToHashSet();
        SyntaxTreeEditorialResult editorial = SyntaxTreeEditorialProjection.Build(analysis);
        // Both public streams are ordered by original offsets; advance once instead of rescanning every sentence.
        int sentenceCursor = 0;
        SyntaxTreeToken[] tokens = analysis.Tokens.Select((token, index) =>
        {
            if (token.Start < 0 || token.Length < 0 || token.Start > analysis.Text.Length - token.Length ||
                !analysis.Text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text.AsSpan()))
                throw new InvalidOperationException("Public token offsets do not match the original UTF-16 text.");
            int? sentence = null;
            while (sentenceCursor < analysis.Sentences.Count &&
                (long)analysis.Sentences[sentenceCursor].Start + analysis.Sentences[sentenceCursor].Length <= token.Start)
                sentenceCursor++;
            if (sentenceCursor < analysis.Sentences.Count && Contains(analysis.Sentences[sentenceCursor].Start,
                analysis.Sentences[sentenceCursor].Length, token.Start, token.Length)) sentence = sentenceCursor;
            return new SyntaxTreeToken(index, token.Text, token.Start, token.Length, sentence, token.Kind.ToString(),
                linked.Contains(index) ? "linked" : roles[index].Count > 0 ? "candidate" : "unlinked", roles[index])
            {
                Known = editorial.Words[index].Known, Pos = editorial.Words[index].Pos,
                Lemmas = editorial.Words[index].Lemmas, Tags = editorial.Words[index].Tags
            };
        }).ToArray();
        var predicateBySpan = analysis.PredicateLinks?.Sentences.ToLookup(row => (row.Start, row.Length));
        var syntaxBySpan = analysis.Syntax?.Sentences.ToLookup(row => (row.Start, row.Length));
        var unlinkedBySentence = tokens.Where(token => token.Kind == nameof(TokenKind.Word) &&
            !linked.Contains(token.Index)).ToLookup(token => token.Sentence);
        SyntaxTreeSentence[] sentences = analysis.Sentences.Select((sentence, index) =>
        {
            IEnumerable<string> uncovered = predicateBySpan?[(sentence.Start, sentence.Length)]
                .SelectMany(row => row.UncoveredReasons) ?? [];
            IEnumerable<string> syntaxStatus = syntaxBySpan?[(sentence.Start, sentence.Length)]
                .Select(row => "syntax_status:" + row.Status) ?? [];
            return new SyntaxTreeSentence(index, sentence.Start, sentence.Length, available ? "partial" : "unavailable",
                reasons.Concat(uncovered).Concat(syntaxStatus).Distinct(StringComparer.Ordinal).ToArray(),
                unlinkedBySentence[index].Select(token => token.Index).ToArray());
        }).ToArray();
        var grammarRules = analysis.Grammar?.Rules.ToLookup(rule => rule.Id, StringComparer.Ordinal);
        IEnumerable<SyntaxTreeDiagnostic> diagnostics = analysis.Diagnostics.Select(diagnostic =>
            new SyntaxTreeDiagnostic(diagnostic.Start, diagnostic.Length, diagnostic.Code, diagnostic.RuleId,
                diagnostic.RuleId is { } ruleId && grammarRules?[ruleId].FirstOrDefault() is { } rule
                    ? rule.Explanation : DiagnosticMessage(diagnostic.Code)));
        if (analysis.Validation is { Available: true } validation)
        {
            var checks = validation.Diagnostics.Concat(validation.SpacingDiagnostics ?? [])
                .Concat(validation.SubordinateCommaDiagnostics ?? []).Concat(validation.FragmentDiagnostics ?? []);
            diagnostics = diagnostics.Concat(checks.Select(d => new SyntaxTreeDiagnostic(d.Start, d.Length, d.Code,
                null, DiagnosticMessage(d.Code))));
        }
        return new("syntax-tree.v1", available ? "partial" : "unavailable", tokens, edges, sentences,
            diagnostics.DistinctBy(d => (d.Start, d.Length, d.Code)).ToArray()) { Suggestions = editorial.Suggestions, ConflictWarnings = editorial.ConflictWarnings };

        void AddRole(int index, SyntaxTreeRole role)
        {
            RequireIndex(index);
            roles[index].Add(role);
        }
        void AddEdge(int from, int to, string label, string status, string source, string? rule,
            string alternative, IReadOnlyList<TextSyntaxSupport> supports)
        {
            RequireIndex(from);
            RequireIndex(to);
            if (from != to) edges.Add(new(from, to, label, status, source, rule, alternative, supports));
        }
        void RequireIndex(int index)
        {
            if ((uint)index >= (uint)analysis.Tokens.Count)
                throw new InvalidOperationException("Public syntax evidence references an absent token occurrence.");
        }
        void AddAvailability(string source, bool? isAvailable, string? reason)
        {
            if (isAvailable != true) reasons.Add(source + ":" + (reason ?? "not_requested"));
        }
    }

    private static bool Contains(int start, int length, int childStart, int childLength) =>
        childStart >= start && (long)childStart + childLength <= (long)start + length;

    private static TextSyntaxSupport Support(int token, TextReadingReference? reading,
        TextGraphReadingReference? graphReading) => new(token, "original_reading_reference",
        reading is { } exact ? [exact] : []) { GraphReadings = graphReading is { } graph ? [graph] : null };

    private static string Agreement(NominalAgreementAssessment assessment) => assessment switch
    {
        NominalAgreementAssessment.Compatible => "compatible",
        NominalAgreementAssessment.Mismatch => "conflict",
        _ => "unassessed"
    };

    private static string DiagnosticMessage(string code) => code switch
    {
        "unseparated_finite_predications" => "Две лични глаголни форми без установена връзка или разделяне.",
        "spurious_comma_inside_compound_conjunction" => "Излишна запетая между частите на сложния съюз.",
        _ => "Езиковият анализ отчита диагностиката „" + code + "“."
    };
}

internal sealed record SyntaxTreeResult(string Version, string Status, IReadOnlyList<SyntaxTreeToken> Tokens,
    IReadOnlyList<SyntaxTreeEdge> Edges, IReadOnlyList<SyntaxTreeSentence> Sentences,
    IReadOnlyList<SyntaxTreeDiagnostic> Diagnostics)
{
    public IReadOnlyList<SyntaxTreeSuggestion> Suggestions { get; init; } = [];
    public IReadOnlyList<SyntaxTreeDiagnostic> ConflictWarnings { get; init; } = [];
}
internal sealed record SyntaxTreeToken(int Index, string Text, int Start, int Length, int? Sentence, string Kind,
    string Status, IReadOnlyList<SyntaxTreeRole> Roles)
{
    public bool? Known { get; init; }
    public IReadOnlyList<string> Pos { get; init; } = [];
    public IReadOnlyList<string> Lemmas { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
}
internal sealed record SyntaxTreeRole(string Role, string Status, string Source, string? RuleId, string Alternative,
    [property: JsonIgnore] TextSyntaxSupport? Support, [property: JsonIgnore] IReadOnlyList<TextSyntaxSupport> Context)
{
    public string? Coverage { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MorphologicalGender? ContextualGender { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<int>? ContextTokenIndices => Context.Count == 0 ? null : Context.Select(s => s.TokenIndex).Distinct();
}
internal sealed record SyntaxTreeEdge(int From, int To, string Label, string Status, string Source, string? RuleId,
    string Alternative, [property: JsonIgnore] IReadOnlyList<TextSyntaxSupport> Supports);
internal sealed record SyntaxTreeSentence(int Index, int Start, int Length, string Status,
    IReadOnlyList<string> Reasons, IReadOnlyList<int> UnlinkedTokenIndices);
internal sealed record SyntaxTreeDiagnostic(int Start, int Length, string Code, string? RuleId, string Message);
