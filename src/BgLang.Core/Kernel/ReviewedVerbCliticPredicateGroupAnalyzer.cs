using System.Security.Cryptography;
using System.Text;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Binds package-reviewed clitic groups without selecting a morphology reading.</summary>
internal static class ReviewedVerbCliticPredicateGroupAnalyzer
{
    private const string DiagnosticCode = "reviewed_verb_clitic_predicate_group";
    internal static IEnumerable<TextDiagnostic> Analyze(
        IReadOnlyList<TokenAnalysis> tokens,
        IReadOnlyList<SentenceSpan> sentences,
        IRuntimeLexicon lexicon,
        WordFormationAnalyzer wordFormation)
    {
        foreach (WordFormationAnalyzer.ReviewedVerbCliticRelationNode relation in
                 wordFormation.ReviewedVerbCliticRelations)
        {
            bool? compatible = null;
            bool postverbal = relation.Placement.Kind == "postverbal";
            for (int index = 0; index < tokens.Count; index++)
            {
                bool startsGroup = postverbal ? IsWord(tokens[index], relation.Decision.ObservedSurface) :
                    tokens[index].Kind == TokenKind.Word && ReviewedCliticPolicy.IsHostWord(tokens[index].Text);
                if (startsGroup &&
                    TryMatchFollowing(tokens, index, relation, postverbal, out int endIndex) &&
                    InOneSentence(tokens[index], tokens[endIndex], sentences) &&
                    (compatible ??= HasCompatibleClass(relation, lexicon, wordFormation)))
                {
                    yield return Diagnostic(tokens[index], tokens[endIndex], relation.Frame.Id);
                }
            }
        }
    }

    private static bool HasCompatibleClass(
        WordFormationAnalyzer.ReviewedVerbCliticRelationNode relation,
        IRuntimeLexicon lexicon,
        WordFormationAnalyzer wordFormation)
    {
        string surface = BulgarianTextNormalizer.NormalizeKey(relation.Decision.ObservedSurface);
        WordReading[] readings = lexicon.LookupExactNormalized(surface, int.MaxValue)
            .Where(reading => BulgarianTextNormalizer.NormalizeKey(reading.Form) == surface &&
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma) == relation.Frame.VerbLemma)
            .ToArray();
        if (relation.Frame.FrameKind == "AmbiguousOrNeedsSyntax" &&
            !string.Equals(surface, relation.Frame.VerbLemma, StringComparison.Ordinal))
        {
            return HasAcceptedExactCompanion(relation, readings, wordFormation);
        }

        return readings.Any(reading => HasAcceptedParadigmTag(relation, reading, wordFormation));
    }

    private static bool HasAcceptedExactCompanion(
        WordFormationAnalyzer.ReviewedVerbCliticRelationNode relation,
        IReadOnlyList<WordReading> readings,
        WordFormationAnalyzer wordFormation)
    {
        WordFormationAnalyzer.ReviewedVerbCliticFrameNode frame = relation.Frame;
        HashSet<string>? origins = EvidenceHashSet(frame.EvidenceIds, "origin-neutral-reading:");
        HashSet<string>? exact = EvidenceHashSet(relation.Decision.EvidenceIds, "exact-reading:");
        if (frame.CliticSignature != relation.Decision.CliticSignature ||
            origins is null || exact is null || !origins.SetEquals(exact) ||
            relation.Decision.EvidenceIds.Any(value =>
                value.StartsWith("generated-reading:", StringComparison.Ordinal)))
        {
            return false;
        }

        string suffix = "," + frame.CliticSignature;
        WordReading[] raw = readings.Where(reading =>
        {
            string rawTag = reading.PartOfSpeech ?? string.Empty;
            if (!rawTag.EndsWith(suffix, StringComparison.Ordinal))
                return false;
            string ordinaryTag = rawTag[..^suffix.Length];
            return ordinaryTag.Length > 0 &&
                WordFormationGraph.TryCreateStateId(ordinaryTag) == frame.VerbTypeStateId &&
                wordFormation.IsAcceptedParadigmBaseTag(
                    frame.VerbTypeStateId, frame.ProductiveParadigmRuleId, ordinaryTag);
        }).ToArray();
        if (raw.Length != 1)
            return false;

        Dictionary<string, int> matched = new(StringComparer.Ordinal);
        foreach (WordReading reading in readings)
        {
            string fullTag = reading.PartOfSpeech ?? string.Empty;
            if (!fullTag.Contains('+') &&
                string.Equals(reading.Form, raw[0].Form, StringComparison.Ordinal) &&
                string.Equals(reading.Lemma, raw[0].Lemma, StringComparison.Ordinal) &&
                WordFormationGraph.TryCreateStateId(fullTag) == frame.VerbTypeStateId &&
                wordFormation.IsAcceptedParadigmTag(
                    frame.VerbTypeStateId, frame.ProductiveParadigmRuleId, fullTag))
            {
                string hash = ComputeOriginReadingHash(
                    reading.Form, reading.Lemma, fullTag, frame.ProductiveParadigmRuleId);
                if (origins.Contains(hash))
                    matched[hash] = matched.GetValueOrDefault(hash) + 1;
            }
        }
        return matched.Count == origins.Count && matched.Values.All(count => count == 1);
    }

    private static bool HasAcceptedParadigmTag(
        WordFormationAnalyzer.ReviewedVerbCliticRelationNode relation,
        WordReading reading,
        WordFormationAnalyzer wordFormation)
    {
        WordFormationAnalyzer.ReviewedVerbCliticFrameNode frame = relation.Frame;
        string rawTag = reading.PartOfSpeech ?? string.Empty;
        if (frame.FrameKind != "AmbiguousOrNeedsSyntax" &&
            WordFormationGraph.TryCreateStateId(rawTag) == frame.VerbTypeStateId &&
            wordFormation.IsAcceptedParadigmTag(
                frame.VerbTypeStateId, frame.ProductiveParadigmRuleId, rawTag))
            return true;

        string suffix = "," + frame.CliticSignature;
        if (frame.CliticSignature != relation.Decision.CliticSignature ||
            !rawTag.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        string ordinaryTag = rawTag[..^suffix.Length];
        if (ordinaryTag.Length == 0 ||
            WordFormationGraph.TryCreateStateId(ordinaryTag) != frame.VerbTypeStateId ||
            !wordFormation.IsAcceptedParadigmBaseTag(
                frame.VerbTypeStateId, frame.ProductiveParadigmRuleId, ordinaryTag))
            return false;

        string? origin = SingleEvidenceHash(frame.EvidenceIds, "origin-neutral-reading:");
        string? generated = SingleEvidenceHash(
            relation.Decision.EvidenceIds, "generated-reading:");
        return origin is not null && origin == generated &&
            origin == ComputeOriginReadingHash(
                reading.Form, reading.Lemma, ordinaryTag, frame.ProductiveParadigmRuleId);
    }

    private static HashSet<string>? EvidenceHashSet(IEnumerable<string> evidenceIds, string prefix)
    {
        HashSet<string> hashes = new(StringComparer.Ordinal);
        foreach (string id in evidenceIds.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)))
        {
            string hash = id[prefix.Length..];
            if (!IsLowerHex(hash) || !hashes.Add(hash))
                return null;
        }
        return hashes.Count == 0 ? null : hashes;
    }

    private static string? SingleEvidenceHash(IEnumerable<string> evidenceIds, string prefix)
    {
        string[] values = evidenceIds.Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(value => value[prefix.Length..]).ToArray();
        return values.Length == 1 && IsLowerHex(values[0]) ? values[0] : null;
    }

    private static bool IsLowerHex(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ComputeOriginReadingHash(
        string form,
        string lemma,
        string fullTag,
        string ruleId)
    {
        string[] fields = ["origin-neutral-morphology-reading.v1", form, lemma, fullTag, ruleId];
        StringBuilder framed = new();
        foreach (string field in fields)
            framed.Append(field.Length).Append(':').Append(field).Append('|');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(framed.ToString())));
    }
    private static bool TryMatchFollowing(
        IReadOnlyList<TokenAnalysis> tokens,
        int startIndex,
        WordFormationAnalyzer.ReviewedVerbCliticRelationNode relation,
        bool postverbal,
        out int endIndex)
    {
        int cursor = startIndex;
        int slotCount = relation.Frame.Slots.Length;
        for (int index = 0; index < slotCount + (postverbal ? 0 : 1); index++)
        {
            cursor++;
            while (cursor < tokens.Count && tokens[cursor].Kind == TokenKind.Whitespace)
                cursor++;
            if (cursor >= tokens.Count || tokens[cursor].Kind != TokenKind.Word ||
                !(index < slotCount ? ReviewedCliticPolicy.MatchesSlot(relation.Frame.Slots[index].AllowedCliticIds[0],
                    relation.Frame.Slots[index].IsVariable, tokens[cursor].Text) :
                    IsWord(tokens[cursor], relation.Decision.ObservedSurface)))
            {
                endIndex = -1;
                return false;
            }
        }
        endIndex = cursor;
        return true;
    }

    private static bool IsWord(TokenAnalysis token, string expected) =>
        token.Kind == TokenKind.Word &&
        BulgarianTextNormalizer.NormalizeKey(token.Text) == BulgarianTextNormalizer.NormalizeKey(expected);

    private static bool InOneSentence(
        TokenAnalysis first,
        TokenAnalysis last,
        IReadOnlyList<SentenceSpan> sentences)
    {
        int end = last.Start + last.Length;
        return sentences.Any(sentence =>
            first.Start >= sentence.Start && end <= sentence.Start + sentence.Length);
    }

    private static TextDiagnostic Diagnostic(TokenAnalysis first, TokenAnalysis last, string ruleId) =>
        new(DiagnosticCode, first.Start, last.Start + last.Length - first.Start, ruleId);
}
