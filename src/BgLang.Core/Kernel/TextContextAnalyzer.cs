using System.Collections.Immutable;
using System.Globalization;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>One request-local binding of the context and role calculators to complete response snapshots.</summary>
internal static class TextContextAnalyzer
{
    private sealed record Rows(IReadOnlyList<ContextLanes.ReadingView> Lanes,
        ImmutableArray<WordReading> Raw, ImmutableArray<SyntaxRoleCalculator.Projection> Syntax,
        IReadOnlyList<string> Identities, string Canonical);
    private static readonly string[] AdditionalGroupIds =
    [
        "unknown-preverbal-attribute", "closed-register-title-attribute", "closed-adverbials",
        "nominal-possessive-clitic", "preposed-object-clitic", "special-preposed-dual-and-suppression",
        "closed-da-short-clitic-subpredicate", "closed-postverbal-clitic-subject", "closed-dative-speech-subpredicate"
    ];

    internal static (TextContextEvidence Context, TextSyntaxEvidence Syntax) Calculate(
        TextAnalysis analysis, TextContextOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        cancellationToken.ThrowIfCancellationRequested();
        if (analysis.ReadingInventory is not { } inventory) return Unavailable("reading-inventory-unavailable", options);
        var occurrences = inventory.Occurrences.ToDictionary(x => x.TokenIndex);
        var sets = new Dictionary<int, Rows>();
        var byToken = new Dictionary<int, Rows>();
        var orderedTokens = new List<(int Index, Rows Rows)>();
        for (int i = 0; i < analysis.Tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = analysis.Tokens[i];
            if (token.Kind != TokenKind.Word) continue;
            if (!occurrences.TryGetValue(i, out TextReadingOccurrence occurrence))
                return Unavailable("word-reading-occurrence-missing", options);
            if (occurrence.Start != token.Start || occurrence.Length != token.Length ||
                !StringComparer.Ordinal.Equals(occurrence.Text, token.Text) || token.Start < 0 || token.Length < 1 ||
                (long)token.Start + token.Length > analysis.Text.Length ||
                !analysis.Text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text))
                throw new ArgumentException("Context occurrence is not the original Word span.", nameof(analysis));
            if (!sets.TryGetValue(occurrence.ReadingSetIndex, out Rows? rows))
            {
                TextReadingSet set = inventory.Sets[occurrence.ReadingSetIndex];
                string canonical = set.SupportReadings is { Count: > 0 } ? set.SupportKey! : set.CanonicalKey;
                if (string.IsNullOrWhiteSpace(canonical)) return Unavailable("word-canonical-key-empty", options);
                var projections = new List<ContextReadingProjection>();
                foreach (TextReadingSnapshot snapshot in set.ResolvedReadings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    projections.Add(ContextReadingProjection.Create(snapshot, canonical));
                }
                rows = new(Array.AsReadOnly(projections.Select(x => x.Lane).ToArray()),
                    projections.Select(x => x.Lane.Reading).ToImmutableArray(),
                    projections.Select(x => x.Syntax).ToImmutableArray(),
                    Array.AsReadOnly(projections.Select(x => x.Identity).ToArray()), canonical);
                sets.Add(occurrence.ReadingSetIndex, rows);
            }
            byToken.Add(i, rows);
            orderedTokens.Add((i, rows));
        }
        if (occurrences.Count != byToken.Count)
            throw new ArgumentException("Inventory contains a non-Word or absent occurrence.", nameof(analysis));
        var contextSentences = new List<TextContextSentence>();
        var syntaxSentences = new List<TextSyntaxSentence>();
        int[] groupCounts = new int[AdditionalGroupIds.Length];
        foreach (SentenceSpan sentence in analysis.Sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var laneWords = new List<ContextLanes.TokenView>();
            var roleWords = ImmutableArray.CreateBuilder<SyntaxRoleCalculator.Word>();
            foreach (var entry in orderedTokens)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TokenAnalysis token = analysis.Tokens[entry.Index];
                if (token.Start < sentence.Start || (long)token.Start + token.Length > (long)sentence.Start + sentence.Length) continue;
                Rows r = entry.Rows;
                laneWords.Add(new(entry.Index, token, r.Canonical, r.Lanes, true));
                roleWords.Add(new(entry.Index, token.Start, token.Length, r.Canonical, r.Raw, r.Syntax, true));
            }
            ContextOverlayInput input = ContextLanes.Calculate(analysis.Text, sentence, laneWords, CanonicalKey, cancellationToken);
            ContextSelectionOverlay? selected = ContextJointSelection.Select(input, cancellationToken);
            TextContextRoleSelection MapRole(ContextOverlayRoleSelection role) => new((TextContextRole)role.Role,
                role.TokenIndex, role.Start, role.Length, References(role.TokenIndex, role.AlternativeIndices), Freeze(role.EvidenceCodes));
            var candidates = input.Candidates.Select(candidate => new TextContextCandidate(candidate.PredicateTokenIndex,
                candidate.Selectable, candidate.AuthorityLane, candidate.Score, candidate.CanonicalKey,
                Freeze(candidate.Roles.Select(MapRole)), Freeze(candidate.EvidenceCodes)));
            contextSentences.Add(new(sentence.Start, sentence.Length, !input.Truncated,
                selected is null ? null : (TextContextSelectionStatus)selected.Status,
                selected?.SelectedPredicateTokenIndex, selected?.DiagnosticPredicateTokenIndex,
                Freeze(candidates), Freeze(input.LocalRoleCandidates.Select(MapRole)),
                selected is null ? [] : Freeze(selected.Roles.Select(MapRole)), selected is null ? [] : Freeze(selected.EvidenceCodes)));
            ImmutableArray<SyntaxRoleCalculator.Word> words = roleWords.ToImmutable();
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
            SyntaxRoleCalculator.Result result = SyntaxRoleCalculator.Calculate(analysis.Text, sentence.Start, sentence.Length,
                words, options.IncludeAdditionalRoleHeuristics, new(route, predicateWord, provenance), cancellationToken);
            TextSyntaxSupport MapSupport(SyntaxRoleCalculator.Support support) => new(support.TokenIndex,
                support.Basis.ToString(), References(support.TokenIndex, support.ReadingIndices));
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
        return (new("bounded-context.v1", true, null, Freeze(contextSentences)),
            new("bounded-syntax-roles.v1", true, null, Freeze(syntaxSentences), options.IncludeAdditionalRoleHeuristics ?
                Freeze(AdditionalGroupIds.Select((id, i) => new TextSyntaxGroupStatus(id, true, null, groupCounts[i]))) : null));

        IReadOnlyList<TextReadingReference> References(int token, IEnumerable<int> members)
        {
            if (!byToken.TryGetValue(token, out Rows? bound)) throw new InvalidOperationException("Support token is absent.");
            var refs = new List<TextReadingReference>();
            foreach (int member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((uint)member >= (uint)bound.Raw.Length) throw new InvalidOperationException("Support reading is outside complete inventory.");
                refs.Add(new(token, member));
            }
            return Freeze(refs);
        }
        string CanonicalKey(IReadOnlyList<ContextOverlayRoleSelection> roles) => string.Join('|', roles.Select(role =>
            RoleName(role.Role) + ":" + role.Start.ToString(CultureInfo.InvariantCulture) + ":" +
            string.Join(',', role.AlternativeIndices.Select(index => byToken[role.TokenIndex].Identities[index]))));
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
        (new("bounded-context.v1", false, reason, []), new("bounded-syntax-roles.v1", false, reason, [],
            options.IncludeAdditionalRoleHeuristics ? Freeze(AdditionalGroupIds.Select(id => new TextSyntaxGroupStatus(id, false, reason, 0))) : null));
    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
