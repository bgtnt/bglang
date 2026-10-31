namespace BgLang.Contracts;

/// <summary>Source and measured coverage identities; corpus frequency is not a grammar verdict.</summary>
public sealed record ConstructionIndexProvenance(
    string CorpusId,
    string ExtractionVersion,
    string SourceIdentity,
    string PackageIdentity,
    string CoverageScope);

/// <summary>An original sentence span in one view of a source document.</summary>
public sealed record ConstructionWitness(
    string DocumentId,
    string ViewId,
    int SentenceStart,
    int SentenceLength,
    string EvidenceId);

/// <summary>A structural symbol vector with distinct occurrence, document and view counts.</summary>
public sealed class ConstructionIndexEntry
{
    public ConstructionIndexEntry(IReadOnlyList<string> symbols, long occurrenceCount,
        long documentFrequency, long viewFrequency, IReadOnlyList<ConstructionWitness> witnesses,
        bool witnessesTruncated)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(witnesses);
        Symbols = Array.AsReadOnly(symbols.ToArray());
        OccurrenceCount = occurrenceCount;
        DocumentFrequency = documentFrequency;
        ViewFrequency = viewFrequency;
        Witnesses = Array.AsReadOnly(witnesses.ToArray());
        WitnessesTruncated = witnessesTruncated;
    }

    public IReadOnlyList<string> Symbols { get; }
    public long OccurrenceCount { get; }
    public long DocumentFrequency { get; }
    public long ViewFrequency { get; }
    public IReadOnlyList<ConstructionWitness> Witnesses { get; }
    public bool WitnessesTruncated { get; }
}

/// <summary>Immutable input in strictly increasing ordinal symbol-vector order.</summary>
public sealed class ConstructionIndexData
{
    public const string CurrentSchemaVersion = "bglang-construction-index.v1";

    public ConstructionIndexData(string schemaVersion, ConstructionIndexProvenance provenance,
        IReadOnlyList<ConstructionIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        SchemaVersion = schemaVersion;
        Provenance = provenance;
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public string SchemaVersion { get; }
    public ConstructionIndexProvenance Provenance { get; }
    public IReadOnlyList<ConstructionIndexEntry> Entries { get; }
}

public enum ConstructionQueryMode { Exact, Prefix, EditDistance }

/// <summary>Distance is unit-cost Levenshtein over whole structural symbols, without transpositions.</summary>
public sealed record ConstructionIndexHit(ConstructionIndexEntry Entry, int Distance);

/// <summary>Truncated describes the result limit, independently of retained witness coverage.</summary>
public sealed class ConstructionQueryResult
{
    public ConstructionQueryResult(IReadOnlyList<ConstructionIndexHit> hits, int totalMatches,
        bool truncated)
    {
        ArgumentNullException.ThrowIfNull(hits);
        Hits = Array.AsReadOnly(hits.ToArray());
        TotalMatches = totalMatches;
        Truncated = truncated;
    }

    public IReadOnlyList<ConstructionIndexHit> Hits { get; }
    public int TotalMatches { get; }
    public bool Truncated { get; }
}
