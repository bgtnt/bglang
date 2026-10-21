namespace BgLang.Contracts;

/// <summary>Issues in the explicit typed-reading-profile.v1 projection; nullable axes alone are not issues.</summary>
[Flags]
public enum ReadingProjectionIssues
{
    None = 0, FullInventoryUnavailable = 1, UnknownPartOfSpeech = 2,
    UnrecognizedFeatures = 4, ConflictingFeatures = 8, MissingLemma = 16
}

public sealed record WordReadingProfile(int TokenIndex, int Start, int Length,
    int DisplayedReadingCount, int? FullReadingCount, int? ProjectedGroupCount,
    ReadingProjectionIssues Issues, bool? ActualDisplayTruncated)
{
    public bool AssessmentCompleteForProjection => FullReadingCount.HasValue && Issues == ReadingProjectionIssues.None;
    public bool? ObservedProjectedAmbiguity => ProjectedGroupCount is { } count ? count > 1 : null;
}

public sealed record DocumentReadingProfile(long WordOccurrences, long KnownWordOccurrences,
    long UnknownWordOccurrences, long UnmeasuredKnownnessWordOccurrences,
    long DisplayedReadingOccurrences, long? FullReadingOccurrences,
    long CompleteExactInventoryWordOccurrences, long ProjectionAssessmentCompleteWordOccurrences,
    long? ObservedProjectedAmbiguityWordOccurrences, long ProjectionIssueWordOccurrences,
    long? DisplayTruncatedWordOccurrences,
    IReadOnlyList<KeyValuePair<MorphologicalPartOfSpeech, long>> DisplayedPosCounts,
    IReadOnlyList<KeyValuePair<string, long>> DisplayedLemmaCounts,
    IReadOnlyList<WordReadingProfile> Words)
{
    public string ProjectionVersion => "typed-reading-profile.v1";
    public double? KnownWordRatio => WordOccurrences == 0 || UnmeasuredKnownnessWordOccurrences > 0
        ? null : (double)KnownWordOccurrences / WordOccurrences;
    public bool AllWordAssessmentsCompleteForProjection => ProjectionAssessmentCompleteWordOccurrences == WordOccurrences;
    public bool ExactReadingEvidencePresent => KnownWordOccurrences > 0;
    // No Unambiguous/Unique/Selected/EntityCount or analyzer-availability property.
}

