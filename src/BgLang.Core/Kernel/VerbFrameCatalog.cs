using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgLang.Core;

internal enum VerbReflexiveUse
{
    None,
    OptionalSe,
    RequiredSe,
    OptionalSi,
    RequiredSi
}

internal enum VerbSubjectStatus
{
    ExplicitNominal,
    ExplicitClausalOrOther,
    ExplicitImpersonal,
    UnexpressedNonImpersonal
}

internal enum VerbComplementKind
{
    DirectObject,
    Prepositional,
    ClauseDa,
    ClauseChe,
    ClauseQuestion,
    ClauseRelative,
    ClauseOther,
    PredicativeAdjective,
    PredicativeParticiple,
    Adverbial,
    Other
}

internal sealed record VerbFrameProvenance(
    string SourceId,
    string SourceSha256,
    string Attribution,
    string License,
    string PackageIdentity,
    string LinkageScope);

internal sealed record VerbComplementSlot(
    VerbComplementKind Kind,
    string? Preposition,
    IReadOnlyList<string> SemanticConcepts);

internal sealed record VerbFrameAlternative(
    int BvflEntryIndex,
    string BvflRawLemma,
    string Definition,
    VerbReflexiveUse ReflexiveUse,
    IReadOnlyList<string> RequiredClitics,
    bool IsPhraseological,
    VerbSubjectStatus SubjectStatus,
    IReadOnlyList<string> SubjectConcepts,
    IReadOnlyList<VerbComplementSlot> Complements);

internal sealed record VerbFrameLexemeEntry(
    string LexemeKey,
    string Lemma,
    string LinkageKind,
    IReadOnlyList<VerbFrameAlternative> Frames)
{
    private static readonly FrozenSet<string> AnimateConcepts =
        new[] { "лице", "група от лица", "животно" }.ToFrozenSet(StringComparer.Ordinal);

    [JsonIgnore]
    internal bool HasAnyDirectObjectFrame =>
        Frames.Any(f => !f.IsPhraseological && f.Complements.Any(c => c.Kind == VerbComplementKind.DirectObject));

    [JsonIgnore]
    internal bool HasAnyAnimateDirectObjectFrame =>
        Frames.Any(f => !f.IsPhraseological && f.Complements.Any(c =>
            c.Kind == VerbComplementKind.DirectObject && c.SemanticConcepts.Any(AnimateConcepts.Contains)));

    [JsonIgnore]
    internal bool HasExplicitImpersonalFrame =>
        Frames.Any(f => !f.IsPhraseological && f.SubjectStatus == VerbSubjectStatus.ExplicitImpersonal);

    [JsonIgnore]
    internal double? NonReflexiveDirectObjectFrameRatio
    {
        get
        {
            int total = 0, withDirectObject = 0;
            foreach (VerbFrameAlternative f in Frames)
            {
                if (f.IsPhraseological) continue;
                if (f.ReflexiveUse is not (VerbReflexiveUse.None or VerbReflexiveUse.OptionalSi or VerbReflexiveUse.RequiredSi))
                    continue;
                if (f.RequiredClitics.Contains("се", StringComparer.Ordinal))
                    continue;
                total++;
                if (f.Complements.Any(c => c.Kind == VerbComplementKind.DirectObject))
                    withDirectObject++;
            }
            return total == 0 ? null : (double)withDirectObject / total;
        }
    }
}

internal sealed record VerbFrameCatalogData(
    string SchemaVersion,
    VerbFrameProvenance Provenance,
    IReadOnlyList<VerbFrameLexemeEntry> Entries)
{
    internal const string CurrentSchemaVersion = "bglang.verb-frames.v1";
}

/// <summary>
/// Optional immutable verb-valency catalog keyed by package lexemeKey and normalized lemma.
/// Absence of a verb or absence of a direct-object slot in one frame means no restriction, never prohibition.
/// </summary>
internal sealed class VerbFrameCatalog
{
    internal const double DefaultPostverbalDirectObjectMaxRatio = 0.10;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true
    };

    private readonly FrozenDictionary<string, VerbFrameLexemeEntry> _byLexemeKey;
    private readonly FrozenDictionary<string, VerbFrameLexemeEntry[]> _byLemma;

    private VerbFrameCatalog(
        VerbFrameCatalogData data,
        string catalogSha256,
        FrozenDictionary<string, VerbFrameLexemeEntry> byLexemeKey,
        FrozenDictionary<string, VerbFrameLexemeEntry[]> byLemma)
    {
        Data = data;
        CatalogSha256 = catalogSha256;
        _byLexemeKey = byLexemeKey;
        _byLemma = byLemma;
    }

    internal VerbFrameCatalogData Data { get; }

    internal string CatalogSha256 { get; }

    internal static VerbFrameCatalog Create(VerbFrameCatalogData data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);
        if (data.SchemaVersion != VerbFrameCatalogData.CurrentSchemaVersion || data.Provenance is null)
            throw new InvalidDataException("Unsupported verb-frame catalog schema or missing provenance.");

        RequireText(data.Provenance.SourceId);
        RequireText(data.Provenance.SourceSha256);
        RequireText(data.Provenance.Attribution);
        RequireText(data.Provenance.License);
        RequireText(data.Provenance.PackageIdentity);
        RequireText(data.Provenance.LinkageScope);

        Dictionary<string, VerbFrameLexemeEntry> byLexeme = new(data.Entries.Count, StringComparer.Ordinal);
        Dictionary<string, List<VerbFrameLexemeEntry>> byLemma = new(StringComparer.Ordinal);
        string? previousKey = null;

        foreach (VerbFrameLexemeEntry entry in data.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || entry.Frames is null || entry.Frames.Count == 0)
                throw new InvalidDataException("Verb-frame lexeme entry must contain at least one frame.");
            RequireText(entry.LexemeKey);
            RequireText(entry.Lemma);
            RequireText(entry.LinkageKind);

            string normLemma = BulgarianTextNormalizer.NormalizeKey(entry.Lemma);
            if (entry.Lemma != normLemma)
                throw new InvalidDataException($"Verb-frame lemma '{entry.Lemma}' must be normalized.");
            if (previousKey is not null && StringComparer.Ordinal.Compare(previousKey, entry.LexemeKey) >= 0)
                throw new InvalidDataException("Verb-frame entries must be unique and ordinally sorted by lexemeKey.");

            foreach (VerbFrameAlternative frame in entry.Frames)
            {
                if (frame is null || frame.BvflEntryIndex <= 0)
                    throw new InvalidDataException("Verb-frame alternative requires positive BvflEntryIndex.");
                RequireText(frame.BvflRawLemma);
                if (frame.Complements is null || frame.SubjectConcepts is null || frame.RequiredClitics is null)
                    throw new InvalidDataException("Verb-frame collections must be non-null.");
            }

            byLexeme.Add(entry.LexemeKey, entry);
            if (!byLemma.TryGetValue(normLemma, out List<VerbFrameLexemeEntry>? group))
                byLemma[normLemma] = group = [];
            group.Add(entry);
            previousKey = entry.LexemeKey;
        }

        using MemoryStream canonicalBytes = new();
        WriteData(data, canonicalBytes);
        string catalogSha256 = Convert.ToHexString(SHA256.HashData(canonicalBytes.GetBuffer().AsSpan(0, (int)canonicalBytes.Length))).ToLowerInvariant();

        return new(
            data,
            catalogSha256,
            byLexeme.ToFrozenDictionary(StringComparer.Ordinal),
            byLemma.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));
    }

    internal bool TryGetByLexemeKey(string lexemeKey, out VerbFrameLexemeEntry? entry) =>
        _byLexemeKey.TryGetValue(lexemeKey, out entry);

    internal IReadOnlyList<VerbFrameLexemeEntry> GetByLemma(string lemma)
    {
        string norm = BulgarianTextNormalizer.NormalizeKey(lemma);
        return _byLemma.TryGetValue(norm, out VerbFrameLexemeEntry[]? entries) ? entries : [];
    }

    internal static VerbFrameCatalog Read(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stream);
        VerbFrameCatalogData? data;
        try
        {
            data = JsonSerializer.Deserialize<VerbFrameCatalogData>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid verb-frame catalog JSON.", ex);
        }
        if (data is null)
            throw new InvalidDataException("Verb-frame catalog stream is empty.");
        return Create(data, cancellationToken);
    }

    internal void Write(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stream);
        WriteData(Data, stream);
    }

    private static void WriteData(VerbFrameCatalogData data, Stream stream)
    {
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true
        });
        JsonSerializer.Serialize(writer, data, JsonOptions);
        writer.Flush();
        stream.Write("\n"u8);
    }

    private static void RequireText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException("Required verb-frame metadata text is missing.");
    }
}
