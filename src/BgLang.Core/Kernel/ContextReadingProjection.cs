using System.Globalization;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Source-head categories and raw identity, separate from the shared typed feature parser.</summary>
internal sealed record ContextReadingProjection(ContextLanes.ReadingView Lane,
    SyntaxRoleCalculator.Projection Syntax, string Identity)
{
    internal static ContextReadingProjection Create(TextReadingSnapshot snapshot, string canonicalWord)
    {
        WordReading raw = snapshot.Raw;
        string? tag = raw.PartOfSpeech;
        int colon = tag?.IndexOf(':') ?? -1;
        string head = tag is null ? string.Empty : tag[..(colon >= 0 ? colon : tag.Length)].ToUpperInvariant();
        ContextLanes.Category category = string.IsNullOrWhiteSpace(tag) ? ContextLanes.Category.Unknown : head switch
        {
            "VERB" or "ГЛАГОЛ" => ContextLanes.Category.Verb,
            "PRON" or "PRONOUN" or "МЕСТОИМЕНИЕ" or "ВЪПРОСИТЕЛНО_МЕСТОИМЕНИЕ" or
            "ПОКАЗАТЕЛНО_МЕСТОИМЕНИЕ" or "ОТНОСИТЕЛНО_МЕСТОИМЕНИЕ" or "ОБОБЩИТЕЛНО_МЕСТОИМЕНИЕ" or
            "НЕОПРЕДЕЛИТЕЛНО_МЕСТОИМЕНИЕ" or "ОТРИЦАТЕЛНО_МЕСТОИМЕНИЕ" or "ЛИЧНО_МЕСТОИМЕНИЕ" or
            "ПРИТЕЖАТЕЛНО_МЕСТОИМЕНИЕ" => ContextLanes.Category.Pronoun,
            "NOUN" or "PROPN" or "СЪЩЕСТВИТЕЛНО" or "СОБСТВЕНО" => ContextLanes.Category.Noun,
            "ADJ" or "ПРИЛАГАТЕЛНО" => ContextLanes.Category.Adjective,
            "NUM" or "ЧИСЛИТЕЛНО" => ContextLanes.Category.Number,
            "ADP" or "PREP" or "PREPOSITION" or "ПРЕДЛОГ" => ContextLanes.Category.Adposition,
            "PART" or "ЧАСТИЦА" => ContextLanes.Category.Particle,
            _ => ContextLanes.Category.Other
        };
        bool adverb = head is "ADV" or "НАРЕЧИЕ";
        var lane = new ContextLanes.ReadingView(raw, snapshot.Features, category, adverb,
            adverb || head is "PART" or "ЧАСТИЦА", head is "CONJ" or "CCONJ" or "SCONJ" or "СЪЮЗ",
            !string.IsNullOrWhiteSpace(tag) && (tag.Contains("Лично", StringComparison.OrdinalIgnoreCase) || head is "PRON" or "PRONOUN"),
            head == "UNKNOWN" && StringComparer.Ordinal.Equals(raw.Lemma.Trim().ToLowerInvariant(), canonicalWord.Trim().ToLowerInvariant()));
        var syntax = new SyntaxRoleCalculator.Projection(SyntaxRoleCalculator.FromCurrentCategory(snapshot.Features.PartOfSpeech),
            snapshot.Features, BulgarianTextNormalizer.NormalizeKey(raw.Lemma));
        string identity = string.Concat(Atom(raw.Form), Atom(raw.Lemma), Atom(raw.PartOfSpeech),
            Atom(BitConverter.DoubleToInt64Bits(raw.Score).ToString("x16", CultureInfo.InvariantCulture)),
            Atom(raw.LegacyTypeId?.ToString(CultureInfo.InvariantCulture)), Atom(raw.LegacyBaseId?.ToString(CultureInfo.InvariantCulture)),
            Atom(raw.LegacyWordId?.ToString(CultureInfo.InvariantCulture)), Atom(raw.Source));
        return new(lane, syntax, identity);
    }

    private static string Atom(string? value) => value is null ? "-:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
}
