using System.Collections.Immutable;
using System.Globalization;
using BgLang.Contracts;
using Rows = BgLang.Core.MorphologyEvidenceProjection.Rows;

namespace BgLang.Core;

/// <summary>One request-local binding of the context and role calculators to complete response snapshots.</summary>
internal static class TextContextAnalyzer
{
    private static readonly string[] AdditionalGroupIds =
    [
        "unknown-preverbal-attribute", "closed-register-title-attribute", "closed-adverbials",
        "nominal-possessive-clitic", "preposed-object-clitic", "special-preposed-dual-and-suppression",
        "closed-da-short-clitic-subpredicate", "closed-postverbal-clitic-subject", "closed-dative-speech-subpredicate"
    ];

    internal static (TextContextEvidence Context, TextSyntaxEvidence Syntax) Calculate(
        TextAnalysis analysis, TextContextOptions options, CancellationToken cancellationToken,
        MorphologyEvidenceProjection.View? sharedMorphology = null)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        MorphologyEvidenceProjection.View morphology = sharedMorphology ?? MorphologyEvidenceProjection.Create(analysis, cancellationToken);
        if (morphology.UnavailableReason is { } unavailable) return Unavailable(unavailable, options);
        var byToken = morphology.ByToken;
        var orderedTokens = morphology.OrderedTokens;
        var contextSentences = new List<TextContextSentence>();
        var syntaxSentences = new List<TextSyntaxSentence>();
        int[] groupCounts = new int[AdditionalGroupIds.Length];
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var laneWords = new List<ContextLanes.TokenView>();
            var exactLaneWords = new List<ContextLanes.TokenView>();
            var scopeEvidence = new List<string>();
            var roleWords = ImmutableArray.CreateBuilder<SyntaxRoleCalculator.Word>();
            foreach (var entry in orderedTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TokenAnalysis token = analysis.Tokens[entry.Index];
                if (token.Start < sentence.Start || (long)token.Start + token.Length > (long)sentence.Start + sentence.Length) continue;
                Rows r = entry.Rows;
                laneWords.Add(new(entry.Index, token, r.Canonical, r.Lanes, true) { UsesGraph = r.UsesGraph });
                exactLaneWords.Add(new(entry.Index, token, r.Canonical, r.UsesGraph ? [] : r.Lanes, true));
                if (r.GraphUnavailableReason is { } reason) scopeEvidence.Add("graph_token=" + entry.Index + ":" + reason);
                roleWords.Add(new(entry.Index, token.Start, token.Length, r.Canonical, r.Raw, r.Syntax, true)
                { GraphTags = r.UsesGraph ? r.Syntax.Select(x => x.Features.RawTag!).ToImmutableArray() : [] });
            }
            ContextOverlayInput input = ContextLanes.Calculate(analysis.Text, sentence, exactLaneWords, CanonicalKey, cancellationToken);
            ContextOverlayInput? productive = ContextLanes.CalculateProductive(analysis.Text, sentence, laneWords,
                CanonicalKey, cancellationToken, out string? graphReason);
            if (productive is not null) input = productive;
            if (graphReason is not null) scopeEvidence.Add(graphReason);
            ContextSelectionOverlay? selected = ContextJointSelection.Select(input, cancellationToken);
            TextContextRoleSelection MapRole(ContextOverlayRoleSelection role) => new((TextContextRole)role.Role,
                role.TokenIndex, role.Start, role.Length, References(role.TokenIndex, role.AlternativeIndices), Freeze(role.EvidenceCodes))
                { GraphReadings = GraphReferences(role.TokenIndex, role.AlternativeIndices) };
            var candidates = input.Candidates.Select(candidate => new TextContextCandidate(candidate.PredicateTokenIndex,
                candidate.Selectable, candidate.AuthorityLane, candidate.Score, candidate.CanonicalKey,
                Freeze(candidate.Roles.Select(MapRole)), Freeze(candidate.EvidenceCodes)));
            contextSentences.Add(new(sentence.Start, sentence.Length, !input.Truncated,
                selected is null ? null : (TextContextSelectionStatus)selected.Status,
                selected?.SelectedPredicateTokenIndex, selected?.DiagnosticPredicateTokenIndex,
                Freeze(candidates), Freeze(input.LocalRoleCandidates.Select(MapRole)),
                selected is null ? [] : Freeze(selected.Roles.Select(MapRole)),
                Freeze((selected?.EvidenceCodes ?? []).Concat(scopeEvidence))));
            ImmutableArray<SyntaxRoleCalculator.Word> words = roleWords.ToImmutable();
            SyntaxRoleCalculator.Result result;
            if (productive is not null)
            {
                var results = new List<SyntaxRoleCalculator.Result>();
                foreach (ContextOverlayCandidate candidate in productive.Candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var route = (candidate.EvidenceCodes.Contains(ContextLanes.ProductiveGraphMismatch, StringComparer.Ordinal) ||
                        candidate.EvidenceCodes.Contains(ContextLanes.ProductiveAttributeMismatch, StringComparer.Ordinal))
                        ? SyntaxRoleCalculator.Route.ProductiveGraphMismatch : candidate.Selectable
                            ? SyntaxRoleCalculator.Route.ProductiveGraph : SyntaxRoleCalculator.Route.ProductiveGraphPreview;
                    var provenance = candidate.Roles.Select(role => new SyntaxRoleCalculator.Support(role.TokenIndex,
                        role.AlternativeIndices.ToImmutableArray(), byToken[role.TokenIndex].UsesGraph
                            ? SyntaxRoleCalculator.Basis.LicensedGraphMorphology : SyntaxRoleCalculator.Basis.TypedFeatures)).ToImmutableArray();
                    int predicateWord = -1, subjectWord = -1;
                    int subjectToken = candidate.Roles.Single(role => role.Role == ContextOverlayRole.Subject).TokenIndex;
                    for (int i = 0; i < words.Length; i++)
                    {
                        if (words[i].TokenIndex == candidate.PredicateTokenIndex) predicateWord = i;
                        if (words[i].TokenIndex == subjectToken) subjectWord = i;
                    }
                    results.Add(SyntaxRoleCalculator.Calculate(analysis.Text, sentence.Start, sentence.Length, words,
                        options.IncludeAdditionalRoleHeuristics, new(route, predicateWord, provenance)
                        { SubjectWordIndex = subjectWord }, cancellationToken));
                }
                result = new(SyntaxRoleCalculator.Status.Calculated, words,
                    results.SelectMany(x => x.Candidates).ToImmutableArray(),
                    results.Count == 0 ? new(SyntaxRoleCalculator.Route.Abstained, -1, []) : results[0].Resolution);
            }
            else
            {
                // Unsupported graph topology retains the old exact-only source scan, not a broader graph parser.
                words = words.Select(word => word.UsesGraph ? word with { Projection = [], GraphTags = [] } : word).ToImmutableArray();
                SyntaxRoleCalculator.Route route = selected is null ? SyntaxRoleCalculator.Route.SourceScan :
                    selected.SelectedPredicateTokenIndex is not null ? SyntaxRoleCalculator.Route.Selected :
                    selected.DiagnosticPredicateTokenIndex is not null ? SyntaxRoleCalculator.Route.ClosedThirdPersonMismatchPreview :
                    SyntaxRoleCalculator.Route.Abstained;
                int? predicateToken = selected?.SelectedPredicateTokenIndex ?? selected?.DiagnosticPredicateTokenIndex;
                int predicateWord = -1;
                for (int i = 0; i < words.Length; i++) if (words[i].TokenIndex == predicateToken) predicateWord = i;
                var provenance = selected is null ? ImmutableArray<SyntaxRoleCalculator.Support>.Empty : selected.Roles
                    .Select(role => new SyntaxRoleCalculator.Support(role.TokenIndex, role.AlternativeIndices.ToImmutableArray(),
                        SyntaxRoleCalculator.Basis.TypedFeatures)).ToImmutableArray();
                result = SyntaxRoleCalculator.Calculate(analysis.Text, sentence.Start, sentence.Length,
                    words, options.IncludeAdditionalRoleHeuristics, new(route, predicateWord, provenance), cancellationToken);
            }
            TextSyntaxSupport MapSupport(SyntaxRoleCalculator.Support support) => new(support.TokenIndex,
                support.Basis.ToString(), References(support.TokenIndex, support.ReadingIndices))
                { GraphReadings = support.Basis == SyntaxRoleCalculator.Basis.LicensedGraphMorphology
                    ? GraphReferences(support.TokenIndex, support.ReadingIndices) : null };
            var roles = new List<TextSyntaxCandidate>();
            foreach (SyntaxRoleCalculator.Candidate candidate in result.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                roles.Add(new((TextSyntaxRole)candidate.Role, candidate.Rule.ToString(), candidate.Start, candidate.Length,
                    Freeze(candidate.Targets.Select(MapSupport)), Freeze(candidate.Context.Select(MapSupport))));
                if (Group(candidate.Rule) is int group) groupCounts[group]++;
            }
            int? resultPredicate = roles.FirstOrDefault(candidate => candidate.Role == TextSyntaxRole.Predicate)
                is { Targets.Count: > 0 } main ? main.Targets[0].TokenIndex : null;
            syntaxSentences.Add(new(sentence.Start, sentence.Length, result.Status.ToString(), result.Resolution.Route.ToString(),
                resultPredicate, Freeze(roles)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (new("bounded-context.v2", true, null, Freeze(contextSentences)),
            new("bounded-syntax-roles.v2", true, null, Freeze(syntaxSentences), options.IncludeAdditionalRoleHeuristics ?
                Freeze(AdditionalGroupIds.Select((id, i) => new TextSyntaxGroupStatus(id, true, null, groupCounts[i]))) : null));

        IReadOnlyList<TextReadingReference> References(int token, IEnumerable<int> members)
        {
            if (!byToken.TryGetValue(token, out Rows? bound)) throw new InvalidOperationException("Support token is absent.");
            if (bound.UsesGraph) return [];
            var refs = new List<TextReadingReference>();
            foreach (int member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)member >= (uint)bound.Raw.Length) throw new InvalidOperationException("Support reading is outside complete inventory.");
                refs.Add(new(token, member));
            }
            return Freeze(refs);
        }
        IReadOnlyList<TextGraphReadingReference>? GraphReferences(int token, IEnumerable<int> members)
        {
            if (!byToken.TryGetValue(token, out Rows? bound) || !bound.UsesGraph) return null;
            var refs = new List<TextGraphReadingReference>();
            foreach (int member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)member >= (uint)bound.Graph.Count) throw new InvalidOperationException("Graph support is outside original cells.");
                var cell = bound.Graph[member];
                refs.Add(new(token, cell.CandidateIndex, cell.FullTagIndex));
            }
            return refs.Count == 0 ? null : Freeze(refs);
        }
        string CanonicalKey(IReadOnlyList<ContextOverlayRoleSelection> roles) => string.Join('|', roles.Select(role =>
            RoleName(role.Role) + ":" + role.Start.ToString(CultureInfo.InvariantCulture) + ":" +
            string.Join(',', role.AlternativeIndices.Select(index => byToken[role.TokenIndex].Identities[index]))));
    }

    internal static IReadOnlyList<TextDiagnostic> GraphAgreementDiagnostics(TextContextEvidence context, CancellationToken ct)
    {
        var output = new List<TextDiagnostic>();
        foreach (TextContextSentence sentence in context.Sentences)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var group in sentence.Candidates.Where(candidate =>
                candidate.AuthorityLane is ContextLanes.ProductiveGraphLane or ContextLanes.ProductiveNominalLane &&
                candidate.EvidenceCodes.Any(code => code is ContextLanes.ProductiveGraphMismatch or ContextLanes.ProductiveAttributeMismatch))
                .GroupBy(candidate => (candidate.PredicateTokenIndex, candidate.AuthorityLane,
                    Attribute: candidate.EvidenceCodes.FirstOrDefault(code => code.StartsWith("attribute_mismatch_token=", StringComparison.Ordinal)))))
            {
                TextContextRoleSelection[] roles = group.SelectMany(candidate => candidate.Roles).ToArray();
                bool attributeMismatch = group.Key.Attribute is not null;
                int attributeToken = attributeMismatch ? int.Parse(group.Key.Attribute!.AsSpan("attribute_mismatch_token=".Length), CultureInfo.InvariantCulture) : -1;
                TextContextRoleSelection[] relation = roles.Where(role => role.Role == TextContextRole.Subject ||
                    (attributeMismatch ? role.Role == TextContextRole.Attribute && role.TokenIndex == attributeToken : role.Role == TextContextRole.Predicate)).ToArray();
                TextContextRoleSelection? target = relation.Where(role => role.GraphReadings is { Count: > 0 }).OrderBy(role => role.Start).FirstOrDefault();
                if (target is null) throw new InvalidOperationException("Productive mismatch needs graph support in its actual relation.");
                output.Add(new(attributeMismatch ? ContextLanes.ProductiveAttributeMismatch : ContextLanes.ProductiveGraphMismatch,
                    target.Start, target.Length, group.Key.AuthorityLane!)
                {
                    Readings = Freeze(relation.SelectMany(role => role.Readings).Distinct()
                        .OrderBy(reference => reference.TokenIndex).ThenBy(reference => reference.CompleteReadingIndex)),
                    GraphReadings = Freeze(relation.SelectMany(role => role.GraphReadings ?? []).Distinct()
                        .OrderBy(reference => reference.TokenIndex).ThenBy(reference => reference.GraphCandidateIndex).ThenBy(reference => reference.FullTagIndex))
                });
            }
        }
        return Freeze(output);
    }

    private static string RoleName(ContextOverlayRole role) => role switch
    {
        ContextOverlayRole.Adposition => "adposition", ContextOverlayRole.Attribute => "attribute",
        ContextOverlayRole.NominalContext => "nominal-context", ContextOverlayRole.Predicate => "predicate",
        ContextOverlayRole.Subject => "subject", _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
    private static int? Group(SyntaxRoleCalculator.Rule rule) => rule switch
    {
        SyntaxRoleCalculator.Rule.UnknownAttributeShape => 0, SyntaxRoleCalculator.Rule.RegisterTitle => 1,
        SyntaxRoleCalculator.Rule.Zadneshkom or SyntaxRoleCalculator.Rule.KrivoLyavo or SyntaxRoleCalculator.Rule.KadeKade or
            SyntaxRoleCalculator.Rule.Directional or SyntaxRoleCalculator.Rule.Zamalko => 2,
        SyntaxRoleCalculator.Rule.NominalPossessive => 3,
        SyntaxRoleCalculator.Rule.FutureClitic or SyntaxRoleCalculator.Rule.DaClitic or SyntaxRoleCalculator.Rule.ReflexiveClitic or
            SyntaxRoleCalculator.Rule.ImmediateClitic => 4,
        SyntaxRoleCalculator.Rule.ImpersonalDoshya => 5, SyntaxRoleCalculator.Rule.DaSubpredicate => 6,
        SyntaxRoleCalculator.Rule.PostverbalChaka => 7, SyntaxRoleCalculator.Rule.DativeSpeech => 8, _ => null
    };
    private static (TextContextEvidence, TextSyntaxEvidence) Unavailable(string reason, TextContextOptions options) =>
        (new("bounded-context.v2", false, reason, []), new("bounded-syntax-roles.v2", false, reason, [],
            options.IncludeAdditionalRoleHeuristics ? Freeze(AdditionalGroupIds.Select(id => new TextSyntaxGroupStatus(id, false, reason, 0))) : null));
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
