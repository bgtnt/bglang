using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Content-bound lexical identity independent of migration provenance.</summary>
internal static class LexicalIdentity
{
    private const string LexemeSchema = "bglang.lexeme.v1";
    private const string ReadingSchema = "bglang.reading.v1";

    internal static IEqualityComparer<WordReading> ReadingEquality { get; } =
        new SemanticReadingEqualityComparer();

    internal static IEqualityComparer<WordForm> FormEquality { get; } =
        new SemanticFormEqualityComparer();

    internal static string CreateLexemeKey(
        string lemma,
        string? productiveRuleId,
        IEnumerable<WordReading> paradigm)
    {
        ArgumentNullException.ThrowIfNull(paradigm);
        string[] rows = paradigm
            .Select(reading => string.Join('\u001f',
                BulgarianTextNormalizer.NormalizeKey(reading.Form),
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
                reading.PartOfSpeech ?? string.Empty))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(row => row, StringComparer.Ordinal)
            .ToArray();
        if (rows.Length == 0)
            throw new InvalidDataException("A lexeme identity requires at least one semantic reading.");

        string paradigmHash = HashLengthPrefixed(rows);
        return "lexeme-v1:" + HashLengthPrefixed(
            [
                LexemeSchema,
                BulgarianTextNormalizer.NormalizeKey(lemma),
                productiveRuleId ?? string.Empty,
                paradigmHash
            ]);
    }

    internal static string CreateReadingKey(string lexemeKey, WordReading reading)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lexemeKey);
        ArgumentNullException.ThrowIfNull(reading);
        return "reading-v1:" + HashLengthPrefixed(
            [
                ReadingSchema,
                lexemeKey,
                BulgarianTextNormalizer.NormalizeKey(reading.Form),
                BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
                reading.PartOfSpeech ?? string.Empty,
                BitConverter.DoubleToInt64Bits(reading.Score).ToString("x16", CultureInfo.InvariantCulture)
            ]);
    }

    internal static int CompareReadings(WordReading left, WordReading right)
    {
        int comparison = right.Score.CompareTo(left.Score);
        if (comparison != 0)
            return comparison;
        comparison = string.Compare(
            BulgarianTextNormalizer.NormalizeKey(left.Form),
            BulgarianTextNormalizer.NormalizeKey(right.Form),
            StringComparison.Ordinal);
        if (comparison != 0)
            return comparison;
        comparison = string.Compare(
            BulgarianTextNormalizer.NormalizeKey(left.Lemma),
            BulgarianTextNormalizer.NormalizeKey(right.Lemma),
            StringComparison.Ordinal);
        return comparison != 0
            ? comparison
            : string.Compare(
                left.PartOfSpeech ?? string.Empty,
                right.PartOfSpeech ?? string.Empty,
                StringComparison.Ordinal);
    }

    internal static string HashLengthPrefixed(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] utf8 = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            Span<byte> prefix = stackalloc byte[32];
            foreach (string value in values)
            {
                if (!Utf8Formatter.TryFormat(value.Length, prefix, out int prefixLength))
                    throw new InvalidOperationException("Could not format identity field length.");
                prefix[prefixLength++] = (byte)':';
                hash.AppendData(prefix[..prefixLength]);

                int byteCount = Encoding.UTF8.GetByteCount(value);
                if (byteCount > utf8.Length)
                {
                    byte[] expanded = ArrayPool<byte>.Shared.Rent(byteCount);
                    ArrayPool<byte>.Shared.Return(utf8);
                    utf8 = expanded;
                }
                int written = Encoding.UTF8.GetBytes(value.AsSpan(), utf8);
                hash.AppendData(utf8.AsSpan(0, written));
                hash.AppendData("\n"u8);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(utf8);
        }
    }

    private sealed class SemanticReadingEqualityComparer : IEqualityComparer<WordReading>
    {
        public bool Equals(WordReading? left, WordReading? right) =>
            ReferenceEquals(left, right) ||
            left is not null && right is not null &&
            BitConverter.DoubleToInt64Bits(left.Score) == BitConverter.DoubleToInt64Bits(right.Score) &&
            string.Equals(
                BulgarianTextNormalizer.NormalizeKey(left.Form),
                BulgarianTextNormalizer.NormalizeKey(right.Form),
                StringComparison.Ordinal) &&
            string.Equals(
                BulgarianTextNormalizer.NormalizeKey(left.Lemma),
                BulgarianTextNormalizer.NormalizeKey(right.Lemma),
                StringComparison.Ordinal) &&
            string.Equals(
                left.PartOfSpeech ?? string.Empty,
                right.PartOfSpeech ?? string.Empty,
                StringComparison.Ordinal);

        public int GetHashCode(WordReading reading) => HashCode.Combine(
            BulgarianTextNormalizer.NormalizeKey(reading.Form),
            BulgarianTextNormalizer.NormalizeKey(reading.Lemma),
            reading.PartOfSpeech ?? string.Empty,
            BitConverter.DoubleToInt64Bits(reading.Score));
    }

    private sealed class SemanticFormEqualityComparer : IEqualityComparer<WordForm>
    {
        public bool Equals(WordForm? left, WordForm? right) =>
            ReferenceEquals(left, right) ||
            left is not null && right is not null &&
            string.Equals(
                BulgarianTextNormalizer.NormalizeKey(left.Form),
                BulgarianTextNormalizer.NormalizeKey(right.Form),
                StringComparison.Ordinal) &&
            string.Equals(
                BulgarianTextNormalizer.NormalizeKey(left.Lemma),
                BulgarianTextNormalizer.NormalizeKey(right.Lemma),
                StringComparison.Ordinal) &&
            string.Equals(left.Tag ?? string.Empty, right.Tag ?? string.Empty, StringComparison.Ordinal);

        public int GetHashCode(WordForm form) => HashCode.Combine(
            BulgarianTextNormalizer.NormalizeKey(form.Form),
            BulgarianTextNormalizer.NormalizeKey(form.Lemma),
            form.Tag ?? string.Empty);
    }
}
