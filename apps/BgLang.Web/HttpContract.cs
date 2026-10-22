using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using BgLang.Api;
using BgLang.Contracts;

namespace BgLang.Web;

internal static class HttpContract
{
    internal const int WordLength = 256;
    internal const int TextLength = 4096;
    internal const int DefaultResultLimit = 32;
    internal const int GenerateFormsDefaultLimit = 128;
    internal const int MaximumResultLimit = 256;
    internal const int TextWordAnalysisLimit = 32;
    internal const int QueryAnalysisLimit = 32;
    internal const int BodyBytes = 32 * 1024;
    internal const long DefaultResponseBytes = 32 * 1024 * 1024;
    internal const string RawFormat = "raw-v1";
    internal const int QueueCapacity = 4;
    internal const int RequestsPerMinute = 30;
    internal const int ClientCapacity = 1024;
    internal static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
    internal static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 32,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    internal static RuntimeVersions Versions() => new(
        VersionOf(typeof(HttpContract)), VersionOf(typeof(BulgarianLanguageSession)),
        VersionOf(typeof(PackageProvenance)), Environment.Version.ToString());

    private static string VersionOf(Type type) =>
        type.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? type.Assembly.GetName().Version?.ToString() ?? "unknown";
}

internal sealed record WebInput(string? Text, int? Limit = null, string? Query = null,
    string? EvidenceFormat = null, bool? IncludeUnknownContextProperNames = null, TextContextOptions? Context = null,
    StressReadingSelection? StressSelection = null, string? PrototypeEntryId = null);
internal sealed record RuntimeVersions(string Host, string PublicApi, string Contracts, string Dotnet);
internal sealed record ResultLimits(int? Requested, int? Effective, int? HostMaximum,
    bool HostLimitApplied, string Scope);
internal sealed record ResultCoverage(bool HostTruncated, bool PossibleTruncation, bool? Complete);
internal sealed record ApiEnvelope(string ApiVersion, RuntimeVersions Versions,
    PackageProvenance Package, ResultLimits Limits, ResultCoverage Coverage, object Data)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EvidenceFormat { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<IReadOnlyList<string>>? DonorSets { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<WordAnalysis>? WordAnalyses { get; init; }
}
internal sealed record ApiError(string Code, string Message)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResultCoverage? Coverage { get; init; }
}

internal sealed class ApiRequestException(int status, string code, string message) : Exception(message)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}
