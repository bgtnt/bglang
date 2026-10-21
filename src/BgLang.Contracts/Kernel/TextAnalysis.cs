namespace BgLang.Contracts;

/// <summary>Bulgarian tokenization with typed analysis for every word token.</summary>
public sealed record TextAnalysis(
    string Text,
    IReadOnlyList<TokenAnalysis> Tokens)
{
    private static readonly PhoneticEvidence EmptyPhonetics = new([], [], []);

    /// <summary>Source-aligned sentence boundaries derived from the token stream.</summary>
    public IReadOnlyList<SentenceSpan> Sentences { get; init; } = [];

    /// <summary>Bounded phonetic evidence derived from the same source-aligned word tokens.</summary>
    public PhoneticEvidence Phonetics { get; init; } = EmptyPhonetics;

    /// <summary>Source-aligned diagnostics and candidate evidence from explicitly supported text rules.</summary>
    public IReadOnlyList<TextDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Exact package owners and source-declared relation-target phrase occurrences.</summary>
    public LexicalTextEvidence Lexical { get; init; } = new(false, [], []);

    /// <summary>Null means complete exact acquisition is unavailable; a present inventory records every acquired Word occurrence.</summary>
    public TextReadingInventory? ReadingInventory { get; init; }

    /// <summary>Descriptive metrics over these raw tokens and sentence spans.</summary>
    public TextMetrics? Metrics { get; init; }

    /// <summary>Advisory bounded ordinary-index alternatives, preserving source positions and search disposition.</summary>
    public TextSpellingEvidence? Spelling { get; init; }

    /// <summary>Occurrence counts and explicit typed projection coverage over the complete reading inventory.</summary>
    public DocumentReadingProfile? Profile { get; init; }

    /// <summary>Ambiguity-preserving exact candidates and explicitly enabled contextual name evidence.</summary>
    public ProperNameEvidence? ProperNames { get; init; }
    /// <summary>Bounded contextual alternatives over the same complete inventory.</summary>
    public TextContextEvidence? Context { get; init; }

    /// <summary>Basic and explicitly enabled additional source role candidates.</summary>
    public TextSyntaxEvidence? Syntax { get; init; }

    /// <summary>Bounded diagnostics with explicit disabled and unavailable checks.</summary>
    public TextValidationEvidence? Validation { get; init; }

    /// <summary>Opt-in candidate constructions over exact owner-bound readings; null means not requested.</summary>
    public TextWordConstructionEvidence? WordConstructions { get; init; }
}
