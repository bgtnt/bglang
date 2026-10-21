// Bounded selection overlay over source-aligned alternative hypotheses.
// Inputs are validated internal records tied to one complete request-local reading cache.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace BgLang.Core;

internal enum ContextOverlayRole { Adposition, Attribute, NominalContext, Predicate, Subject }
internal enum ContextOverlayStatus { Selected, Partial, Ambiguous, Truncated }
internal sealed record ContextOverlayRoleSelection(ContextOverlayRole Role, int TokenIndex,
    int Start, int Length, IReadOnlyList<int> AlternativeIndices, IReadOnlyList<string> EvidenceCodes);
internal sealed record ContextOverlayCandidate(int PredicateTokenIndex, bool Selectable,
    string? AuthorityLane, int Score, string CanonicalKey,
    IReadOnlyList<ContextOverlayRoleSelection> Roles, IReadOnlyList<string> EvidenceCodes);
internal sealed record ContextOverlayInput(bool HasTargetTopology, bool Truncated,
    string? TruncationEvidenceCode, IReadOnlyList<ContextOverlayCandidate> Candidates,
    IReadOnlyList<ContextOverlayRoleSelection> LocalRoleCandidates,
    IReadOnlyList<int> RoleAmbiguousTokenIndices);
internal sealed record ContextSelectionOverlay(ContextOverlayStatus Status, bool Truncated,
    int? SelectedPredicateTokenIndex, int? DiagnosticPredicateTokenIndex, int? CandidateCount,
    IReadOnlyList<ContextOverlayRoleSelection> Roles, IReadOnlyList<string> EvidenceCodes)
{
    // Source trace metadata, never a calibrated probability or the internal ranking score.
    internal int Score => 0;
    internal int ScoreMargin => 0;
}

internal static class ContextJointSelection
{
    private const string PrepLane = "closed_adposition_adjective_noun.v1";
    private const string PrepEvidence = "closed_adposition_phrase_typed_adjective_noun_agreement";
    private const string MismatchPreview = "closed_clause_pronoun_agreement_mismatch_diagnostic_preview";
    private const string MismatchRetained = "subject_predicate_agreement_mismatch_retained_for_diagnostic";
    // Historical source compatibility policy, not owner limits or a permanent linguistic rule.
    private const int MinimumScore = 40;
    private const int MinimumMargin = 8;

    internal static ContextSelectionOverlay? Select(ContextOverlayInput input,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!input.HasTargetTopology) return null;
        if (input.Truncated)
        {
            // The generator supplies the exact existing limit reason before attempting selection.
            if (input.TruncationEvidenceCode is not ("token_limit_reached" or "reading_limit_reached"))
                throw new ArgumentException("Truncation requires its existing source evidence code.", nameof(input));
            return new(ContextOverlayStatus.Truncated, true, null, null, null, [],
                [input.TruncationEvidenceCode]);
        }

        ContextOverlayCandidate[] ordered = input.Candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.PredicateTokenIndex)
            .ThenBy(candidate => candidate.CanonicalKey, StringComparer.Ordinal).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        ContextOverlayCandidate? best = ordered.FirstOrDefault();
        int runnerUp = ordered.Length > 1 ? ordered[1].Score : 0;
        int margin = best is null ? 0 : best.Score - runnerUp;
        bool supported = best is { Selectable: true } && best.Score >= MinimumScore &&
            (ordered.Length == 1 || margin >= MinimumMargin);

        List<ContextOverlayRoleSelection> selections = [];
        List<string> evidence = ["topology=contextual_morphosyntactic_role_competition",
            "candidate_hypothesis_count=" + ordered.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        foreach (ContextOverlayRoleSelection local in input.LocalRoleCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evidence.AddRange(local.EvidenceCodes.Select(code => "preview_local_evidence=" + code));
            if (local.Role == ContextOverlayRole.Attribute &&
                local.EvidenceCodes.Contains(PrepEvidence, StringComparer.Ordinal))
                selections.Add(local);
        }
        if (selections.Count > 0) evidence.Add("accepted_authority_lane=" + PrepLane);
        int? selectedPredicate = null;
        if (supported && best!.AuthorityLane is not null)
        {
            selections.AddRange(best.Roles);
            selectedPredicate = best.PredicateTokenIndex;
            evidence.Add("accepted_authority_lane=" + best.AuthorityLane);
            evidence.AddRange(best.EvidenceCodes);
        }
        else if (supported)
        {
            evidence.Add("predicate_hypothesis_preview_only");
            evidence.AddRange(best!.EvidenceCodes.Select(code => "preview_predicate_evidence=" + code));
        }
        else if (best is { Selectable: false })
            evidence.Add("best_predicate_hypothesis_role_ambiguous");

        List<ContextOverlayRoleSelection> merged = [];
        foreach (var group in selections.GroupBy(role => (role.Role, role.Start, role.Length)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int tokenIndex = group.First().TokenIndex;
            if (group.Any(role => role.TokenIndex != tokenIndex))
                throw new ArgumentException("One source span must identify one original token.", nameof(input));
            // Deduplicate repeated references to ONE index, never equal reading tuple values.
            merged.Add(new(group.Key.Role, tokenIndex, group.Key.Start, group.Key.Length,
                group.SelectMany(role => role.AlternativeIndices).Distinct().Order().ToArray(),
                group.SelectMany(role => role.EvidenceCodes).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray()));
        }
        merged = merged.OrderBy(role => role.Start)
            .ThenBy(role => RoleName(role.Role), StringComparer.Ordinal).ToList();
        bool unresolved = false;
        foreach (int tokenIndex in input.RoleAmbiguousTokenIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!merged.Any(role => role.TokenIndex == tokenIndex)) unresolved = true;
        }
        ContextOverlayStatus status = merged.Count == 0 ? ContextOverlayStatus.Ambiguous :
            unresolved ? ContextOverlayStatus.Partial : ContextOverlayStatus.Selected;
        if (merged.Count == 0) evidence.Add("insufficient_hypothesis_margin");
        string[] codes = evidence.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        bool diagnostic = selectedPredicate is null && status == ContextOverlayStatus.Ambiguous &&
            codes.Contains("predicate_hypothesis_preview_only", StringComparer.Ordinal) &&
            codes.Contains("preview_predicate_evidence=" + MismatchPreview, StringComparer.Ordinal) &&
            codes.Contains("preview_predicate_evidence=" + MismatchRetained, StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        return new(status, false, selectedPredicate, diagnostic ? best?.PredicateTokenIndex : null,
            ordered.Length, merged.ToArray(), codes);
    }

    private static string RoleName(ContextOverlayRole role) => role switch
    {
        ContextOverlayRole.Adposition => "adposition", ContextOverlayRole.Attribute => "attribute",
        ContextOverlayRole.NominalContext => "nominal-context", ContextOverlayRole.Predicate => "predicate",
        ContextOverlayRole.Subject => "subject", _ => throw new ArgumentOutOfRangeException(nameof(role))
    };
}
