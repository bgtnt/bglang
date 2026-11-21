using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>Complete exact lookup snapshots, shared by raw Word occurrences in this response.</summary>
public sealed class TextReadingInventory
{
    public IReadOnlyList<TextReadingSet> Sets { get; }
    public IReadOnlyList<TextReadingOccurrence> Occurrences { get; }

    public TextReadingInventory(IReadOnlyList<TextReadingSet> sets,
        IReadOnlyList<TextReadingOccurrence> occurrences)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(occurrences);
        TextReadingSet[] setSnapshot = sets.ToArray();
        TextReadingOccurrence[] occurrenceSnapshot = occurrences.ToArray();
        if (setSnapshot.Any(set => set is null))
            throw new ArgumentException("A reading set cannot be null.", nameof(sets));
        int previous = -1;
        foreach (TextReadingOccurrence occurrence in occurrenceSnapshot)
        {
            if (occurrence.TokenIndex <= previous || occurrence.Start < 0 || occurrence.Length < 0 ||
                occurrence.Text is null || occurrence.Text.Length != occurrence.Length ||
                (uint)occurrence.ReadingSetIndex >= (uint)setSnapshot.Length)
                throw new ArgumentException("Invalid or unordered reading occurrence.", nameof(occurrences));
            previous = occurrence.TokenIndex;
        }
        Sets = Array.AsReadOnly(setSnapshot);
        Occurrences = Array.AsReadOnly(occurrenceSnapshot);
    }
}

/// <summary>Canonical lookup and optional fallback. Null support means not searched; empty means searched absent.</summary>
public sealed class TextReadingSet
{
    public string LookupText { get; }
    public string CanonicalKey { get; }
    public IReadOnlyList<TextReadingSnapshot> CanonicalReadings { get; }
    public string? SupportKey { get; }
    public IReadOnlyList<TextReadingSnapshot>? SupportReadings { get; }
    /// <summary>Length of the actual positional display prefix, independent of complete acquisition.</summary>
    public int DisplayedReadingCount { get; }
    public bool DisplayTruncated => ResolvedReadings.Count > DisplayedReadingCount;

    /// <summary>Complete canonical-first/support-fallback lookup result; contextual or stress-reading selection is a separate overlay.</summary>
    [JsonIgnore]
    public IReadOnlyList<TextReadingSnapshot> ResolvedReadings => SupportReadings ?? CanonicalReadings;

    [JsonConstructor]
    private TextReadingSet(string lookupText, string canonicalKey,
        IReadOnlyList<TextReadingSnapshot> canonicalReadings, string? supportKey,
        IReadOnlyList<TextReadingSnapshot>? supportReadings, int displayedReadingCount)
        : this(lookupText, canonicalKey, canonicalReadings, supportKey,
            supportReadings, (int?)displayedReadingCount)
    {
    }

    public TextReadingSet(string lookupText, string canonicalKey,
        IReadOnlyList<TextReadingSnapshot> canonicalReadings, string? supportKey,
        IReadOnlyList<TextReadingSnapshot>? supportReadings, int? displayedReadingCount = null)
    {
        ArgumentNullException.ThrowIfNull(lookupText);
        ArgumentNullException.ThrowIfNull(canonicalKey);
        ArgumentNullException.ThrowIfNull(canonicalReadings);
        if ((supportKey is null) != (supportReadings is null) ||
            (canonicalReadings.Count != 0 && supportReadings is not null))
            throw new ArgumentException("Invalid canonical/fallback lookup state.", nameof(supportReadings));
        TextReadingSnapshot[] canonical = canonicalReadings.ToArray();
        TextReadingSnapshot[]? support = supportReadings?.ToArray();
        if (canonical.Any(row => row.Raw is null) || support?.Any(row => row.Raw is null) == true)
            throw new ArgumentException("A reading snapshot must retain its raw reading.", nameof(canonicalReadings));
        LookupText = lookupText;
        CanonicalKey = canonicalKey;
        CanonicalReadings = Array.AsReadOnly(canonical);
        SupportKey = supportKey;
        SupportReadings = support is null ? null : Array.AsReadOnly(support);
        int resolvedCount = (support ?? canonical).Length;
        DisplayedReadingCount = displayedReadingCount ?? resolvedCount;
        if (DisplayedReadingCount < 0 || DisplayedReadingCount > resolvedCount)
            throw new ArgumentOutOfRangeException(nameof(displayedReadingCount));
    }
}

/// <summary>Whole raw tuple and the existing Features getter value; neither is a selected interpretation.</summary>
public readonly record struct TextReadingSnapshot(WordReading Raw, MorphologicalFeatures Features);

/// <summary>One raw Word token position, independent of cache lookup count. TokenIndex addresses TextAnalysis.Tokens.</summary>
public readonly record struct TextReadingOccurrence(
    int TokenIndex, string Text, int Start, int Length, int ReadingSetIndex);

/// <summary>Response-local original Word token and index in its complete resolved lookup route.</summary>
public readonly record struct TextReadingReference(int TokenIndex, int CompleteReadingIndex);
