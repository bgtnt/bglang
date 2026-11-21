using System.Globalization;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>One source-head classification seam for exact rows and licensed graph tags.</summary>
internal sealed record ContextReadingProjection(ContextLanes.ReadingView Lane,
    SyntaxRoleCalculator.Projection Syntax, string Identity)
{
    internal static ContextReadingProjection Create(TextReadingSnapshot snapshot, string canonicalWord)
    {
        WordReading raw = snapshot.Raw;
        string identity = string.Concat(Atom(raw.Form), Atom(raw.Lemma), Atom(raw.PartOfSpeech),
            Atom(BitConverter.DoubleToInt64Bits(raw.Score).ToString("x16", CultureInfo.InvariantCulture)),
            Atom(raw.LegacyTypeId?.ToString(CultureInfo.InvariantCulture)), Atom(raw.LegacyBaseId?.ToString(CultureInfo.InvariantCulture)),
            Atom(raw.LegacyWordId?.ToString(CultureInfo.InvariantCulture)), Atom(raw.Source));
        return CreateParts(raw.Lemma, raw.PartOfSpeech, snapshot.Features, canonicalWord, identity, raw);
    }

    internal static ContextReadingProjection CreateGraph(WordDerivation path, int candidate, int tagIndex,
        MorphologicalFeatures features, string canonicalWord)
    {
        string identity = string.Concat("graph:", Atom(path.DerivedLemma), Atom(path.FullTags![tagIndex]), Atom(path.Source),
            Atom(path.Base), Atom(string.Join('\0', path.EdgeIds)), Atom(string.Join('\0', path.ApplicationIds)),
            candidate.ToString(CultureInfo.InvariantCulture), ":", tagIndex.ToString(CultureInfo.InvariantCulture));
        return CreateParts(path.DerivedLemma!, path.FullTags[tagIndex], features, canonicalWord, identity);
    }

    private static ContextReadingProjection CreateParts(string lemma, string? tag, MorphologicalFeatures features,
        string canonicalWord, string identity, WordReading? exactReading = null)
    {
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
        var lane = new ContextLanes.ReadingView(lemma, features, category, adverb,
            adverb || head is "PART" or "ЧАСТИЦА", head is "CONJ" or "CCONJ" or "SCONJ" or "СЪЮЗ",
            !string.IsNullOrWhiteSpace(tag) && (tag.Contains("Лично", StringComparison.OrdinalIgnoreCase) || head is "PRON" or "PRONOUN"),
            head == "UNKNOWN" && StringComparer.Ordinal.Equals(lemma.Trim().ToLowerInvariant(), canonicalWord.Trim().ToLowerInvariant()))
        { Reading = exactReading };
        var syntax = new SyntaxRoleCalculator.Projection(SyntaxRoleCalculator.FromCurrentCategory(features.PartOfSpeech),
            features, BulgarianTextNormalizer.NormalizeKey(lemma));
        return new(lane, syntax, identity);
    }

    private static string Atom(string? value) => value is null ? "-:" : value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value;
}
