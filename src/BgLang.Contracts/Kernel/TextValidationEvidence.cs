namespace BgLang.Contracts;

/// <summary>Bounded source diagnostics. A disabled optional check is null, not an evaluated empty result.</summary>
public sealed record TextValidationEvidence(string Version, bool Available, string? UnavailableReason,
    IReadOnlyList<TextValidationDiagnostic> Diagnostics,
    IReadOnlyList<TextValidationDiagnostic>? SpacingDiagnostics,
    IReadOnlyList<TextValidationDiagnostic>? SubordinateCommaDiagnostics,
    IReadOnlyList<TextValidationDiagnostic>? FragmentDiagnostics, IReadOnlyList<string> UnavailableChecks);

public sealed record TextValidationDiagnostic(string Code, int Start, int Length, string Basis,
    IReadOnlyList<TextReadingReference> Supports);
