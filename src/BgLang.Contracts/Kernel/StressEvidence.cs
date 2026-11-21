using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>Evidence origin; observation and Legacy provenance do not imply linguistic review.</summary>
public enum StressOrigin { InputObservation, SingleVowelInference, Editorial, ReviewedExact, LegacyStored, LegacyCarried, LegacyDisplayInferred, DerivationalRulePrediction }

public enum StressStatus { Unknown, Supported, Inferred, Ambiguous, Incomplete, Conflict, Unclassified, InvalidInput }

/// <summary>Written mark shape. Grave/acute observations do not establish primary/secondary roles.</summary>
public enum StressMarkKind { Unspecified, Grave, Acute, Primary }

public enum StressSelectionStatus { NotRequested, Matched, NotConnected }

/// <summary>Zero-based vowel ordinal and absolute UTF-16 position in the original query text.</summary>
public sealed record StressMark(int VowelOrdinal, int Utf16Position, StressMarkKind Kind);

/// <summary>Marks belonging to one coherent payload, never a union of homograph alternatives.</summary>
public sealed record StressRealization(StressOrigin Origin, StressStatus Status,
    IReadOnlyList<StressMark> Marks, string? SourceId)
{
    /// <summary>Original offsets in the exact Legacy form, when captured from that Word object.</summary>
    public IReadOnlyList<int>? LegacyUtf16Positions { get; init; }

    /// <summary>Positions recovered from captured Legacy display, separately from a null/empty offset getter.</summary>
    public IReadOnlyList<int>? LegacyDisplayUtf16Positions { get; init; }
}

public sealed record StressOwnerEvidence(string EntryId, string LexemeKey,
    IReadOnlyList<StressRealization> Realizations);

/// <summary>One row of the complete exact inventory; storage duplicates and source order are retained.</summary>
public sealed record StressReadingEvidence(int CompleteReadingIndex, WordReading Reading,
    IReadOnlyList<StressOwnerEvidence> Owners, IReadOnlyList<StressRealization> ReviewedEvidence);

public sealed record StressTokenEvidence(int TokenIndex, string Text, int Start, int Length,
    StressStatus Status, IReadOnlyList<StressRealization> Observations,
    IReadOnlyList<StressRealization> Inferences, IReadOnlyList<StressReadingEvidence> Readings,
    string RenderedText);

/// <summary>Caller selection must match the acquired token, exact opaque owner, form and raw full tag.</summary>
public sealed record StressReadingSelection([property: JsonRequired] int TokenIndex,
    [property: JsonRequired] string EntryId, [property: JsonRequired] string Form,
    [property: JsonRequired] string? FullTag);

/// <summary>Lexical stress evidence and conservative rendering; this does not predict sentence prosody.</summary>
public sealed record StressEvidence(string Text, string RenderedText, bool OwnersAvailable,
    StressSelectionStatus SelectionStatus, IReadOnlyList<StressTokenEvidence> Tokens);

/// <summary>One complete graph-generated form. Positions refer to this generated form; all assignments are qualified predictions.</summary>
public sealed record GeneratedStressForm(string Form, string FullTag, StressStatus Status, IReadOnlyList<StressMark> Marks);

/// <summary>Explicit lexical prototype selection; no POS-only assignment or fabricated captured Legacy coordinates.</summary>
public sealed record GeneratedStressParadigm(bool Available, string PrototypeEntryId, string? PrototypeLemma,
    string Lemma, string? RuleId, string? EvidenceSourceId, string? GraphSourceId, IReadOnlyList<GeneratedStressForm> Forms);

/// <summary>Accent transition attached to an actual licensed derivation; unresolved transitions stay Unknown.</summary>
public sealed record DerivationalStressEvidence(WordDecomposition Decomposition, StressStatus Status, StressRealization? Realization);
