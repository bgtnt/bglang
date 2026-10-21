using System.Text.Json;
using System.Text.Json.Serialization;
using BgLang.Contracts;

namespace BgLang.Web;

/// <summary>Lossless response-local sharing of complete donors and original token analyses.</summary>
internal sealed class SharedDonorResponse
{
    internal const string Format = "shared-donors-v1";
    internal const string AnalysisFormat = "shared-analyses-v1";
    private readonly Dictionary<IReadOnlyList<string>, int> _ids;
    private readonly List<IReadOnlyList<string>> _sets = [];
    private readonly Dictionary<WordAnalysis, int> _analysisIds = new(ReferenceEqualityComparer.Instance);
    private readonly CancellationToken _cancellation;

    internal ApiEnvelope Envelope { get; }
    internal JsonSerializerOptions Json { get; }

    internal SharedDonorResponse(ApiEnvelope result, bool shareAnalyses = false,
        CancellationToken cancellation = default)
    {
        _cancellation = cancellation;
        _ids = new(new DonorEquality(cancellation));
        cancellation.ThrowIfCancellationRequested();
        List<WordAnalysis>? analyses = null;
        if (shareAnalyses && result.Data is TextAnalysis or TextMatchResult)
        {
            analyses = [];
            TextAnalysis text = result.Data is TextAnalysis value ? value : ((TextMatchResult)result.Data).TextAnalysis;
            foreach (TokenAnalysis token in text.Tokens)
            {
                cancellation.ThrowIfCancellationRequested();
                if (token.Analysis is { } word && _analysisIds.TryAdd(word, analyses.Count)) analyses.Add(word);
            }
        }
        // AnalyzeText already memoizes by ordinal surface; never infer equivalence from word/tag alone.
        var seen = new HashSet<WordAnalysis>(ReferenceEqualityComparer.Instance);
        foreach (WordAnalysis word in Words(result.Data))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!seen.Add(word)) continue;
            foreach (WordDerivation path in word.GraphCandidates) Add(path.ConditionalSupportingLemmas);
            foreach (MorphologyCandidate candidate in word.Candidates.Concat(word.KnownBaseCandidates)
                         .Concat(word.BaseTypeCandidates)) AddCandidate(candidate);
        }
        if (result.Data is IReadOnlyList<WordDerivation> paths)
            foreach (WordDerivation path in paths) Add(path.ConditionalSupportingLemmas);
        Envelope = result with { EvidenceFormat = shareAnalyses ? AnalysisFormat : Format,
            DonorSets = _sets, WordAnalyses = analyses };
        Json = new(HttpContract.Json);
        Json.Converters.Add(new PathConverter(this));
        Json.Converters.Add(new CandidateConverter(this));
        if (shareAnalyses) Json.Converters.Add(new TokenConverter(this));
    }

    private void Add(IReadOnlyList<string>? donors)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (donors is null || _ids.ContainsKey(donors)) return;
        _ids.Add(donors, _sets.Count);
        _sets.Add(donors);
    }

    private void AddCandidate(MorphologyCandidate candidate)
    {
        _cancellation.ThrowIfCancellationRequested();
        Add(candidate.SupportingLemmas);
        foreach (MorphologyCandidate shorter in candidate.ShorterEndingCandidates) AddCandidate(shorter);
    }

    private static IEnumerable<WordAnalysis> Words(object data)
    {
        switch (data)
        {
            case WordAnalysis word:
                yield return word;
                break;
            case TextAnalysis text:
                foreach (TokenAnalysis token in text.Tokens)
                    if (token.Analysis is { } analysis) yield return analysis;
                break;
            case TextMatchResult matches:
                yield return matches.QueryAnalysis;
                foreach (WordAnalysis analysis in Words(matches.TextAnalysis)) yield return analysis;
                break;
        }
    }

    private sealed class PathConverter(SharedDonorResponse response) : JsonConverter<WordDerivation>
    {
        public override WordDerivation Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException("Shared donors are a response-only representation.");

        public override void Write(Utf8JsonWriter writer, WordDerivation path, JsonSerializerOptions options)
        {
            response._cancellation.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            // Reuse the contract serializer so all other fields and JSON-ignore rules stay exact.
            JsonElement value = JsonSerializer.SerializeToElement(path with { ConditionalSupportingLemmas = null }, HttpContract.Json);
            foreach (JsonProperty property in value.EnumerateObject()) property.WriteTo(writer);
            if (path.ConditionalSupportingLemmas is { } donors)
                writer.WriteNumber("conditionalSupportingLemmasRef", response._ids[donors]);
            writer.WriteEndObject();
        }
    }

    private sealed class CandidateConverter(SharedDonorResponse response) : JsonConverter<MorphologyCandidate>
    {
        public override MorphologyCandidate Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException("Shared donors are a response-only representation.");

        public override void Write(Utf8JsonWriter writer, MorphologyCandidate candidate, JsonSerializerOptions options)
        {
            response._cancellation.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            JsonElement value = JsonSerializer.SerializeToElement(candidate with
                { SupportingLemmas = [], ShorterEndingCandidates = [] }, HttpContract.Json);
            foreach (JsonProperty property in value.EnumerateObject())
                if (property.Name is not ("supportingLemmas" or "shorterEndingCandidates")) property.WriteTo(writer);
            writer.WriteNumber("supportingLemmasRef", response._ids[candidate.SupportingLemmas]);
            writer.WritePropertyName("shorterEndingCandidates");
            JsonSerializer.Serialize(writer, candidate.ShorterEndingCandidates, options);
            writer.WriteEndObject();
        }
    }

    private sealed class TokenConverter(SharedDonorResponse response) : JsonConverter<TokenAnalysis>
    {
        public override TokenAnalysis Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException("Shared analyses are a response-only representation.");

        public override void Write(Utf8JsonWriter writer, TokenAnalysis token, JsonSerializerOptions options)
        {
            response._cancellation.ThrowIfCancellationRequested();
            writer.WriteStartObject();
            JsonElement value = JsonSerializer.SerializeToElement(token with { Analysis = null }, HttpContract.Json);
            foreach (JsonProperty property in value.EnumerateObject())
                if (property.Name != "analysis") property.WriteTo(writer);
            writer.WritePropertyName("analysisRef");
            if (token.Analysis is { } analysis) writer.WriteNumberValue(response._analysisIds[analysis]);
            else writer.WriteNullValue();
            writer.WriteEndObject();
        }
    }

    private sealed class DonorEquality(CancellationToken cancellation) : IEqualityComparer<IReadOnlyList<string>>
    {
        public bool Equals(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left is null || right is null || left.Count != right.Count) return false;
            for (int i = 0; i < left.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        public int GetHashCode(IReadOnlyList<string> donors)
        {
            var hash = new HashCode();
            foreach (string donor in donors)
            {
                cancellation.ThrowIfCancellationRequested();
                hash.Add(donor, StringComparer.Ordinal);
            }
            return hash.ToHashCode();
        }
    }
}
