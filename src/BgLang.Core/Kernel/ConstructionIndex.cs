using System.Text.Json;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>
/// Immutable sorted-vector index. Exact/prefix lookup uses binary ranges; fuzzy retrieval
/// exhaustively measures token Levenshtein distance. Corpus absence supplies no correctness verdict.
/// Corpus extraction and aggregation belong outside this runtime component.
/// </summary>
public sealed class ConstructionIndex
{
    private static readonly string[] DataFields = ["schemaVersion", "provenance", "entries"];
    private static readonly string[] ProvenanceFields =
        ["corpusId", "extractionVersion", "sourceIdentity", "packageIdentity", "coverageScope"];
    private static readonly string[] EntryFields =
        ["symbols", "occurrenceCount", "documentFrequency", "viewFrequency", "witnesses", "witnessesTruncated"];
    private static readonly string[] WitnessFields =
        ["documentId", "viewId", "sentenceStart", "sentenceLength", "evidenceId"];

    private ConstructionIndex(ConstructionIndexData data) => Data = data;

    public ConstructionIndexData Data { get; }

    public static ConstructionIndex Create(ConstructionIndexData data,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);
        if (data.SchemaVersion != ConstructionIndexData.CurrentSchemaVersion || data.Provenance is null)
            throw new InvalidDataException("Unsupported construction index schema or missing provenance.");
        ConstructionIndexProvenance provenance = data.Provenance;
        RequireText(provenance.CorpusId);
        RequireText(provenance.ExtractionVersion);
        RequireText(provenance.SourceIdentity);
        RequireText(provenance.PackageIdentity);
        RequireText(provenance.CoverageScope);

        HashSet<(string Document, string View, int Start, int Length)> witnesses = [];
        ConstructionIndexEntry? previous = null;
        foreach (ConstructionIndexEntry entry in data.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is null || entry.Symbols.Count == 0)
                throw new InvalidDataException("Construction signatures must be nonempty.");
            foreach (string symbol in entry.Symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireText(symbol);
            }
            if (previous is not null && Compare(previous.Symbols, entry.Symbols, false, cancellationToken) >= 0)
                throw new InvalidDataException("Construction signatures must be unique and ordinally sorted.");
            if (entry.DocumentFrequency <= 0 || entry.ViewFrequency < entry.DocumentFrequency ||
                entry.OccurrenceCount < entry.ViewFrequency || entry.Witnesses.Count > entry.OccurrenceCount ||
                entry.WitnessesTruncated != (entry.Witnesses.Count < entry.OccurrenceCount))
                throw new InvalidDataException("Construction counts or witness truncation flag are inconsistent.");

            HashSet<string> documents = new(StringComparer.Ordinal);
            HashSet<(string Document, string View)> views = [];
            foreach (ConstructionWitness witness in entry.Witnesses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (witness is null || witness.SentenceStart < 0 || witness.SentenceLength <= 0 ||
                    (long)witness.SentenceStart + witness.SentenceLength > int.MaxValue)
                    throw new InvalidDataException("Construction witness span is invalid.");
                RequireText(witness.DocumentId);
                RequireText(witness.ViewId);
                RequireText(witness.EvidenceId);
                if (!witnesses.Add((witness.DocumentId, witness.ViewId, witness.SentenceStart, witness.SentenceLength)))
                    throw new InvalidDataException("Duplicate construction witness.");
                documents.Add(witness.DocumentId);
                views.Add((witness.DocumentId, witness.ViewId));
            }
            long newDocuments = entry.DocumentFrequency - documents.Count;
            long newViews = entry.ViewFrequency - views.Count;
            long missingOccurrences = entry.OccurrenceCount - entry.Witnesses.Count;
            if (documents.Count > entry.DocumentFrequency || views.Count > entry.ViewFrequency ||
                newDocuments > newViews || newViews > missingOccurrences ||
                (!entry.WitnessesTruncated &&
                    (documents.Count != entry.DocumentFrequency || views.Count != entry.ViewFrequency)))
                throw new InvalidDataException("Construction frequencies disagree with retained witnesses.");
            previous = entry;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(data);
    }

    public ConstructionQueryResult Query(IReadOnlyList<string> symbols, ConstructionQueryMode mode,
        int maxDistance = 1, int limit = 20, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(symbols);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (maxDistance < 0) throw new ArgumentOutOfRangeException(nameof(maxDistance));
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        string[] query = new string[symbols.Count];
        for (int i = 0; i < query.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string symbol = symbols[i];
            if (string.IsNullOrWhiteSpace(symbol))
                throw new ArgumentException("Query symbols must be nonempty.", nameof(symbols));
            query[i] = symbol;
        }

        if (mode != ConstructionQueryMode.EditDistance)
        {
            int start = Bound(query, afterPrefix: false, cancellationToken);
            int end = mode == ConstructionQueryMode.Prefix
                ? Bound(query, afterPrefix: true, cancellationToken)
                : start < Data.Entries.Count && Compare(Data.Entries[start].Symbols, query, false, cancellationToken) == 0
                    ? start + 1 : start;
            ConstructionIndexHit[] hits = new ConstructionIndexHit[Math.Min(limit, end - start)];
            for (int i = 0; i < hits.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hits[i] = new(Data.Entries[start + i], 0);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(hits, end - start, hits.Length < end - start);
        }

        // Reverse priorities retain only the requested best results; every candidate still counts.
        // Entry ordinals are the lexicographic tie-break, including proper-prefix signatures.
        PriorityQueue<ConstructionIndexHit, (int Distance, int Ordinal)> best = new();
        int total = 0;
        for (int i = 0; i < Data.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConstructionIndexEntry entry = Data.Entries[i];
            if (Math.Abs((long)entry.Symbols.Count - query.Length) > maxDistance) continue;
            int distance = Distance(query, entry.Symbols, cancellationToken);
            if (distance > maxDistance) continue;
            total++;
            if (limit == 0) continue;
            if (best.Count < limit) best.Enqueue(new(entry, distance), (-distance, -i));
            else if (distance < best.Peek().Distance)
                best.DequeueEnqueue(new(entry, distance), (-distance, -i));
        }
        ConstructionIndexHit[] selected = new ConstructionIndexHit[best.Count];
        for (int i = selected.Length - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selected[i] = best.Dequeue();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(selected, total, selected.Length < total);
    }

    public static ConstructionIndex Read(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using JsonDocument document = JsonDocument.ParseAsync(stream,
                cancellationToken: cancellationToken).GetAwaiter().GetResult();
            JsonElement root = document.RootElement;
            RequireProperties(root, DataFields, cancellationToken);
            JsonElement provenance = root.GetProperty("provenance");
            RequireProperties(provenance, ProvenanceFields, cancellationToken);
            ConstructionIndexProvenance source = new(
                Text(provenance, "corpusId"), Text(provenance, "extractionVersion"),
                Text(provenance, "sourceIdentity"), Text(provenance, "packageIdentity"),
                Text(provenance, "coverageScope"));
            List<ConstructionIndexEntry> entries = [];
            foreach (JsonElement entry in root.GetProperty("entries").EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                RequireProperties(entry, EntryFields, cancellationToken);
                List<string> symbols = [];
                foreach (JsonElement symbol in entry.GetProperty("symbols").EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    symbols.Add(symbol.GetString() ?? throw new InvalidDataException("Null construction symbol."));
                }
                List<ConstructionWitness> witnesses = [];
                foreach (JsonElement witness in entry.GetProperty("witnesses").EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireProperties(witness, WitnessFields, cancellationToken);
                    witnesses.Add(new(Text(witness, "documentId"), Text(witness, "viewId"),
                        witness.GetProperty("sentenceStart").GetInt32(),
                        witness.GetProperty("sentenceLength").GetInt32(), Text(witness, "evidenceId")));
                }
                entries.Add(new(symbols, entry.GetProperty("occurrenceCount").GetInt64(),
                    entry.GetProperty("documentFrequency").GetInt64(),
                    entry.GetProperty("viewFrequency").GetInt64(), witnesses,
                    entry.GetProperty("witnessesTruncated").GetBoolean()));
            }
            return Create(new(Text(root, "schemaVersion"), source, entries), cancellationToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
            FormatException or OverflowException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("Malformed construction index JSON.", exception);
        }
    }

    public void Write(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(stream);
        using Utf8JsonWriter writer = new(stream);
        writer.WriteStartObject();
        writer.WriteString("schemaVersion", Data.SchemaVersion);
        writer.WriteStartObject("provenance");
        writer.WriteString("corpusId", Data.Provenance.CorpusId);
        writer.WriteString("extractionVersion", Data.Provenance.ExtractionVersion);
        writer.WriteString("sourceIdentity", Data.Provenance.SourceIdentity);
        writer.WriteString("packageIdentity", Data.Provenance.PackageIdentity);
        writer.WriteString("coverageScope", Data.Provenance.CoverageScope);
        writer.WriteEndObject();
        writer.WriteStartArray("entries");
        foreach (ConstructionIndexEntry entry in Data.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            writer.WriteStartArray("symbols");
            foreach (string symbol in entry.Symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStringValue(symbol);
            }
            writer.WriteEndArray();
            writer.WriteNumber("occurrenceCount", entry.OccurrenceCount);
            writer.WriteNumber("documentFrequency", entry.DocumentFrequency);
            writer.WriteNumber("viewFrequency", entry.ViewFrequency);
            writer.WriteStartArray("witnesses");
            foreach (ConstructionWitness witness in entry.Witnesses)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteString("documentId", witness.DocumentId);
                writer.WriteString("viewId", witness.ViewId);
                writer.WriteNumber("sentenceStart", witness.SentenceStart);
                writer.WriteNumber("sentenceLength", witness.SentenceLength);
                writer.WriteString("evidenceId", witness.EvidenceId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteBoolean("witnessesTruncated", entry.WitnessesTruncated);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        cancellationToken.ThrowIfCancellationRequested();
        writer.Flush();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private int Bound(IReadOnlyList<string> query, bool afterPrefix, CancellationToken cancellationToken)
    {
        int low = 0, high = Data.Entries.Count;
        while (low < high)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int middle = low + (high - low) / 2;
            int order = Compare(Data.Entries[middle].Symbols, query, afterPrefix, cancellationToken);
            if (order < 0 || (afterPrefix && order == 0)) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int Compare(IReadOnlyList<string> left, IReadOnlyList<string> right,
        bool prefix, CancellationToken cancellationToken)
    {
        for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int order = string.CompareOrdinal(left[i], right[i]);
            if (order != 0) return order;
        }
        return prefix && left.Count >= right.Count ? 0 : left.Count.CompareTo(right.Count);
    }

    private static int Distance(IReadOnlyList<string> left, IReadOnlyList<string> right,
        CancellationToken cancellationToken)
    {
        if (left.Count > right.Count) (left, right) = (right, left);
        int[] previous = new int[checked(left.Count + 1)], current = new int[checked(left.Count + 1)];
        for (int j = 0; j < previous.Length; j++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            previous[j] = j;
        }
        for (int i = 0; i < right.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current[0] = i + 1;
            for (int j = 1; j < current.Length; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int substitution = previous[j - 1] + (string.Equals(left[j - 1], right[i], StringComparison.Ordinal) ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            (previous, current) = (current, previous);
        }
        return previous[^1];
    }

    private static void RequireText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Construction index text must be nonempty.");
    }

    private static string Text(JsonElement value, string name) =>
        value.GetProperty(name).GetString() ?? throw new InvalidDataException($"Null {name}.");

    private static void RequireProperties(JsonElement value, string[] names, CancellationToken cancellationToken)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected construction index object.");
        int seen = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = Array.IndexOf(names, property.Name);
            if (index < 0 || (seen & (1 << index)) != 0)
                throw new InvalidDataException($"Unknown or duplicate construction index property: {property.Name}.");
            seen |= 1 << index;
        }
        if (seen != (1 << names.Length) - 1) throw new InvalidDataException("Missing construction index property.");
    }
}
