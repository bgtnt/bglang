namespace BgLang.Contracts;

/// <summary>Deterministic identity of the validated immutable runtime package.</summary>
public sealed record PackageProvenance(
    string PackageId,
    string SchemaVersion,
    string ManifestSha256,
    string LexiconSha256,
    string LexiconSource,
    string WordFormationSha256,
    string WordFormationSource)
{
    /// <summary>Hash of the optional editorial annotation component in manifest v2.</summary>
    public string? AnnotationsSha256 { get; init; }

    /// <summary>Semantic identity of optional editorial annotations, separate from lexical identity.</summary>
    public string? AnnotationsSource { get; init; }
}
