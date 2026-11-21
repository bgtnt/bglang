using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Lossless compression of admitted Legacy rows; missing cells stay unknown.</summary>
internal sealed record LexemeStressPattern(string SourceId, string BindingId, int[] DefaultVowelOrdinals, LexemeStressPattern.Cell[] Cells)
{
    [JsonConverter(typeof(JsonStringEnumConverter<CaptureKind>))]
    internal enum CaptureKind { Stored, Carried, DisplayNull, DisplayEmpty }

    internal sealed record Cell(int ReadingIndex, uint WordId, CaptureKind CaptureKind)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int[]? VowelOrdinals { get; init; }
    }

    // Invoke for the optional stressPattern object inside the existing strict parent reader.
    internal static void ValidateJson(JsonElement value)
    {
        StrictJson.RequireObject(value, "Stress pattern", ["sourceId", "bindingId", "defaultVowelOrdinals", "cells"]);
        ValidateSourceId(StrictJson.RequireString(value, "sourceId", "Stress pattern"));
        ValidateBindingId(StrictJson.RequireString(value, "bindingId", "Stress pattern"));
        int[] fallback = ReadOrdinals(value.GetProperty("defaultVowelOrdinals"));
        List<int[]> patterns = [];
        int previous = -1;
        foreach (JsonElement cell in StrictJson.RequireArray(value, "cells", "Stress pattern"))
        {
            StrictJson.RequireObject(cell, "Stress cell", ["readingIndex", "wordId", "captureKind"], ["vowelOrdinals"]);
            if (!StrictJson.RequireProperty(cell, "readingIndex", JsonValueKind.Number, "Stress cell").TryGetInt32(out int index) ||
                index <= previous ||
                !StrictJson.RequireProperty(cell, "wordId", JsonValueKind.Number, "Stress cell").TryGetUInt32(out _))
                throw new InvalidDataException("Stress cells require unique increasing nonnegative indices and uint Word IDs.");
            previous = index;
            if (StrictJson.RequireString(cell, "captureKind", "Stress cell") is not
                ("Stored" or "Carried" or "DisplayNull" or "DisplayEmpty"))
                throw new InvalidDataException("Unsupported stress capture kind.");
            int[] ordinals = cell.TryGetProperty("vowelOrdinals", out JsonElement item) ? ReadOrdinals(item) : fallback;
            if (item.ValueKind != JsonValueKind.Undefined && ordinals.AsSpan().SequenceEqual(fallback))
                throw new InvalidDataException("A default-equal ordinal override must be omitted.");
            patterns.Add(ordinals);
        }
        if (patterns.Count == 0 || !Majority(patterns).AsSpan().SequenceEqual(fallback))
            throw new InvalidDataException("Stress default must be the canonical most frequent nonempty ordinal array.");
    }

    // The catalog shares this exact canonical inventory per validated lexeme.
    internal void Validate(string parentEntryId, WordReading[] canonicalReadings)
    {
        _ = OwnerId(parentEntryId);
        ValidateSourceId(SourceId);
        ValidateBindingId(BindingId);
        ArgumentNullException.ThrowIfNull(canonicalReadings);
        for (int i = 1; i < canonicalReadings.Length; i++)
        {
            int form = StringComparer.Ordinal.Compare(canonicalReadings[i - 1].Form, canonicalReadings[i].Form);
            if (form > 0 || form == 0 && StringComparer.Ordinal.Compare(
                canonicalReadings[i - 1].PartOfSpeech, canonicalReadings[i].PartOfSpeech) >= 0)
                throw new InvalidDataException("Pattern inventory must be canonical distinct exact form/fullTag order.");
        }
        if (Cells is null || Cells.Length == 0 || DefaultVowelOrdinals is null)
            throw new InvalidDataException("A stress pattern needs explicit cells and a default.");
        ValidateOrdinals(DefaultVowelOrdinals);
        List<int[]> patterns = [];
        int previous = -1;
        foreach (Cell cell in Cells)
        {
            if (cell is null || cell.ReadingIndex <= previous || cell.ReadingIndex >= canonicalReadings.Length ||
                !Enum.IsDefined(cell.CaptureKind))
                throw new InvalidDataException("Stress cell index/order or capture kind is invalid.");
            previous = cell.ReadingIndex;
            int[] ordinals = cell.VowelOrdinals ?? DefaultVowelOrdinals;
            ValidateOrdinals(ordinals);
            if (cell.VowelOrdinals is not null && ordinals.AsSpan().SequenceEqual(DefaultVowelOrdinals))
                throw new InvalidDataException("A default-equal ordinal override must be omitted.");
            if (ordinals[^1] >= canonicalReadings[cell.ReadingIndex].Form.Count(IsVowel))
                throw new InvalidDataException("Stress ordinals are outside the exact reading's vowels.");
            patterns.Add(ordinals);
        }
        if (BindingId != CreateBindingId(parentEntryId, SourceId, canonicalReadings, Cells))
            throw new InvalidDataException("Stress pattern binding must match its exact owner and canonical form/fullTag inventory.");
        if (!Majority(patterns).AsSpan().SequenceEqual(DefaultVowelOrdinals))
            throw new InvalidDataException("Stress default is not the canonical most frequent cell pattern.");
    }

    // Call after Validate at package binding. No cell means unknown, regardless of the default.
    internal LexemeAnnotations.StressRow? Find(string parentEntryId, WordReading[] canonicalReadings, int readingIndex)
    {
        if ((uint)readingIndex >= (uint)canonicalReadings.Length)
            throw new InvalidDataException("Stress reading index is outside its exact owner inventory.");
        int low = 0, high = Cells.Length - 1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            Cell cell = Cells[middle];
            if (cell.ReadingIndex < readingIndex) { low = middle + 1; continue; }
            if (cell.ReadingIndex > readingIndex) { high = middle - 1; continue; }
            WordReading reading = canonicalReadings[readingIndex];
            int[] ordinals = cell.VowelOrdinals ?? DefaultVowelOrdinals;
            ValidateOrdinals(ordinals);
            int[] vowels = reading.Form.Select((c, index) => (c, index)).Where(p => IsVowel(p.c)).Select(p => p.index).ToArray();
            if (ordinals[^1] >= vowels.Length) throw new InvalidDataException("Stress ordinals exceed the exact form.");
            int[] offsets = ordinals.Select(ordinal => vowels[ordinal]).ToArray();
            bool display = cell.CaptureKind is CaptureKind.DisplayNull or CaptureKind.DisplayEmpty;
            string kind = cell.CaptureKind switch
            {
                CaptureKind.Stored => "LegacyStored",
                CaptureKind.Carried => "LegacyCarried",
                CaptureKind.DisplayNull or CaptureKind.DisplayEmpty => "LegacyDisplayInferred",
                _ => throw new InvalidDataException("Unsupported captured Legacy kind.")
            };
            return new(reading.Form, reading.PartOfSpeech, (int[])ordinals.Clone())
            {
                Source = new(kind, SourceId, OwnerId(parentEntryId), cell.WordId,
                    display ? cell.CaptureKind == CaptureKind.DisplayNull ? null : [] : offsets)
                { DisplayUtf16Positions = display ? offsets : null }
            };
        }
        return null;
    }

    internal static WordReading[] CanonicalReadings(WordReading[] readings)
    {
        ArgumentNullException.ThrowIfNull(readings);
        return readings.OrderBy(row => row.Form, StringComparer.Ordinal).ThenBy(row => row.PartOfSpeech, StringComparer.Ordinal)
            .DistinctBy(row => (row.Form, row.PartOfSpeech)).ToArray();
    }

    // Frozen at compaction, never refreshed by mutable authoring export/rebuild.
    internal static string CreateBindingId(string parentEntryId, string sourceId, WordReading[] canonicalReadings, Cell[] cells)
    {
        IEnumerable<string> Fields()
        {
            yield return "bglang.stress-pattern-binding.v1";
            yield return parentEntryId;
            yield return sourceId;
            yield return canonicalReadings.Length.ToString(CultureInfo.InvariantCulture);
            foreach (WordReading reading in canonicalReadings)
            {
                yield return reading.Form;
                yield return reading.PartOfSpeech is null ? "null" : "string";
                yield return reading.PartOfSpeech ?? string.Empty;
            }
            yield return cells.Length.ToString(CultureInfo.InvariantCulture);
            foreach (Cell cell in cells)
            {
                yield return cell.ReadingIndex.ToString(CultureInfo.InvariantCulture);
                yield return cell.WordId.ToString(CultureInfo.InvariantCulture);
            }
        }
        return LexicalIdentity.HashLengthPrefixed(Fields());
    }

    private static void ValidateBindingId(string value)
    {
        if (value is null || value.Length != 64 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Stress pattern requires its exact inventory binding.");
    }

    // Highest frequency; lexicographic ordinal-array minimum wins ties (shorter prefix first).
    internal static int[] Majority(IReadOnlyList<int[]> patterns)
    {
        if (patterns.Count == 0) throw new InvalidDataException("No stress cells.");
        foreach (int[] pattern in patterns) ValidateOrdinals(pattern);
        int[][] sorted = patterns.ToArray();
        Array.Sort(sorted, CompareOrdinals);
        int[] best = sorted[0];
        int count = 0, bestCount = 0;
        for (int i = 0; i < sorted.Length; i++)
        {
            count = i > 0 && sorted[i].AsSpan().SequenceEqual(sorted[i - 1]) ? count + 1 : 1;
            if (count > bestCount) { best = sorted[i]; bestCount = count; }
        }
        return best;
    }

    private static int CompareOrdinals(int[] left, int[] right)
    {
        for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
            if (left[i] != right[i]) return left[i].CompareTo(right[i]);
        return left.Length.CompareTo(right.Length);
    }

    private static int[] ReadOrdinals(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Stress ordinals require an array.");
        List<int> ordinals = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int ordinal))
                throw new InvalidDataException("Stress ordinals must be int32 values.");
            ordinals.Add(ordinal);
        }
        int[] result = ordinals.ToArray(); ValidateOrdinals(result); return result;
    }

    private static void ValidateOrdinals(int[] ordinals)
    {
        if (ordinals is null || ordinals.Length == 0) throw new InvalidDataException("Stress ordinals must be nonempty.");
        int previous = -1;
        foreach (int ordinal in ordinals)
        {
            if (ordinal <= previous) throw new InvalidDataException("Stress ordinals must be nonnegative and strictly increasing.");
            previous = ordinal;
        }
    }

    private static uint OwnerId(string parentEntryId)
    {
        const string prefix = "legacy-base:";
        if (parentEntryId is null || !parentEntryId.StartsWith(prefix, StringComparison.Ordinal) ||
            !uint.TryParse(parentEntryId.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out uint ownerId) ||
            parentEntryId != prefix + ownerId.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("Legacy stress requires the exact canonical parent owner EntryId.");
        return ownerId;
    }

    private static void ValidateSourceId(string value)
    {
        if (value is null || value.Length != 81 || !value.StartsWith("legacy-stress-v1:", StringComparison.Ordinal) ||
            value[17..].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Stress pattern requires the exact Legacy source identity.");
    }

    private static bool IsVowel(char c) => char.ToLowerInvariant(c) is 'а' or 'ъ' or 'о' or 'у' or 'е' or 'и' or 'ю' or 'я' or 'ѐ' or 'ѝ';
}
