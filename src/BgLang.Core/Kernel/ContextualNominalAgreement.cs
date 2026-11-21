using BgLang.Contracts;

namespace BgLang.Core;

// Contextual agreement axes only: source readings, tags and reading indices remain unchanged.
internal static class ContextualNominalAgreement
{
    internal static bool HasLeaderCue(NominalStructuralProjection.Binding? phrase, CancellationToken ct = default) =>
        phrase is { Head.Word.CanonicalText: "глава" } && HasLeaderCue(phrase.Head.Word,
            phrase.Participants.SkipLast(1).Select(member => member.Word), ct);

    // ИБЕ, езикови справки №472: глава is masculine when it means a leader,
    // including държавен глава and църковен глава. A masculine title adjective
    // licenses this sense; an unrelated feminine head or a capitalized place name does not.
    internal static bool HasLeaderCue(ContextLanes.TokenView head,
        IEnumerable<ContextLanes.TokenView> modifiers, CancellationToken ct)
    {
        string surface = head.Token.Text;
        if (!head.Complete || head.UsesGraph || head.CanonicalText != "глава" ||
            surface.Length == 0 || !head.Readings.Any(IsLeaderSource)) return false;
        bool uppercase = char.IsUpper(surface[0]);
        if (uppercase && surface.Any(char.IsLower)) return false;
        foreach (var modifier in modifiers)
        {
            ct.ThrowIfCancellationRequested();
            if (!modifier.Complete || modifier.UsesGraph || uppercase && modifier.Token.Text.Any(char.IsLower)) continue;
            if (modifier.Readings.Any(row => row.Category == ContextLanes.Category.Adjective &&
                    row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Adjective,
                        Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Masculine,
                        HasConflictingFeatures: false, HasUnrecognizedFeatures: false } &&
                    BulgarianTextNormalizer.NormalizeKey(row.Lemma) is "държавен" or "църковен"))
                return true;
        }
        return false;
    }

    internal static MorphologicalFeatures Features(ContextLanes.ReadingView source, bool leader) =>
        leader && IsLeaderSource(source)
            ? source.Features with { Gender = MorphologicalGender.Masculine }
            : source.Features;

    internal static ContextLanes.ReadingView ForAgreement(ContextLanes.ReadingView source, bool leader) =>
        leader && IsLeaderSource(source) ? source with { Features = Features(source, leader) } : source;

    private static bool IsLeaderSource(ContextLanes.ReadingView row) =>
        row.Category == ContextLanes.Category.Noun &&
        BulgarianTextNormalizer.NormalizeKey(row.Lemma) == "глава" &&
        row.Features is { PartOfSpeech: MorphologicalPartOfSpeech.Noun, NameKind: null,
            Number: MorphologicalNumber.Singular, Gender: MorphologicalGender.Feminine,
            Article: MorphologicalArticle.Indefinite, HasConflictingFeatures: false,
            HasUnrecognizedFeatures: false };
}
