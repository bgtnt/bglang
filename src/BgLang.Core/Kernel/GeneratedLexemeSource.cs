using System.Globalization;
using System.Text.Json;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Metadata-bound productive declaration, validated again by complete graph replay.</summary>
internal sealed record GeneratedLexemeSource(
    string SchemaVersion,
    string LexemeKey,
    string Lemma,
    string ProductiveRuleId,
    string BaseFullTag,
    double Score,
    int GeneratedReadingCount,
    string GeneratedReadingsSha256)
{
    internal const string Schema = "bglang-neutral-lexeme.v3";

    internal static GeneratedLexemeSource Read(string json, int lineNumber)
    {
        string label = $"Generated lexeme line {lineNumber}";
        GeneratedLexemeSource row = StrictJson.ReadText<GeneratedLexemeSource>(json, label, root =>
        {
            string[] strings = ["schemaVersion", "lexemeKey", "lemma", "productiveRuleId", "baseFullTag", "generatedReadingsSha256"];
            StrictJson.RequireObject(root, label, [.. strings, "score", "generatedReadingCount"]);
            foreach (string field in strings) StrictJson.RequireString(root, field, label);
            StrictJson.RequireProperty(root, "score", JsonValueKind.Number, label);
            StrictJson.RequireProperty(root, "generatedReadingCount", JsonValueKind.Number, label);
        });
        if (row.SchemaVersion != Schema || string.IsNullOrWhiteSpace(row.Lemma) ||
            row.Lemma != BulgarianTextNormalizer.NormalizeKey(row.Lemma) ||
            !IsDeclaredLemma(row.Lemma) ||
            !Canonical(row.ProductiveRuleId) || !Canonical(row.BaseFullTag) ||
            !double.IsFinite(row.Score) || row.Score <= 0 || row.GeneratedReadingCount is < 1 or > 256 ||
            row.GeneratedReadingsSha256.Length != 64 || row.GeneratedReadingsSha256.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new InvalidDataException($"{label} has invalid productive metadata.");
        if (row.LexemeKey != row.CalculateKey())
            throw new InvalidDataException($"{label} lexemeKey does not match its declaration.");
        return row;
    }

    internal IEnumerable<string> IdentityFields() =>
        [BaseFullTag, ScoreBits(Score), GeneratedReadingCount.ToString(CultureInfo.InvariantCulture), GeneratedReadingsSha256];

    internal string CalculateKey() => "lexeme-v3:" + LexicalIdentity.HashLengthPrefixed(
        new[] { "bglang.generated-lexeme.v1", Schema, Lemma, ProductiveRuleId }.Concat(IdentityFields()));

    internal WordReading[] Materialize(WordFormationAnalyzer graph)
    {
        WordReading[] readings = graph.GenerateLexicalReadings(Lemma, BaseFullTag, ProductiveRuleId, Score);
        if (readings.Length != GeneratedReadingCount || ReadingDigest(readings) != GeneratedReadingsSha256)
            throw new InvalidDataException("Generated lexeme complete replay does not match its declared count/digest.");
        return readings;
    }

    internal static string ReadingDigest(WordReading[] readings)
    {
        IEnumerable<string> Fields()
        {
            yield return "bglang.generated-readings.v1";
            yield return readings.Length.ToString(CultureInfo.InvariantCulture);
            foreach (WordReading reading in readings.OrderBy(r => r.Form, StringComparer.Ordinal)
                         .ThenBy(r => r.Lemma, StringComparer.Ordinal).ThenBy(r => r.PartOfSpeech, StringComparer.Ordinal)
                         .ThenBy(r => ScoreBits(r.Score), StringComparer.Ordinal))
            {
                yield return reading.Form;
                yield return reading.Lemma;
                yield return reading.PartOfSpeech!;
                yield return ScoreBits(reading.Score);
            }
        }
        return LexicalIdentity.HashLengthPrefixed(Fields());
    }

    private static bool IsDeclaredLemma(string lemma)
    {
        for (int index = 0; index < lemma.Length; index++)
        {
            if (BulgarianOrthography.IsLowercaseLetterOrStressedVowel(lemma[index])) continue;
            if (lemma[index] != '-' || index == 0 || index == lemma.Length - 1 ||
                !BulgarianOrthography.IsLowercaseLetterOrStressedVowel(lemma[index - 1]) ||
                !BulgarianOrthography.IsLowercaseLetterOrStressedVowel(lemma[index + 1])) return false;
        }
        return lemma.Length > 0;
    }

    private static string ScoreBits(double score) => BitConverter.DoubleToInt64Bits(score).ToString("x16", CultureInfo.InvariantCulture);
    private static bool Canonical(string value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim();
}
