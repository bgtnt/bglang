namespace BgLang.Contracts;

/// <summary>Explicit opt-ins for bounded context advice; all options are disabled by default.</summary>
public readonly record struct TextContextOptions
{
    public bool IncludeAdditionalRoleHeuristics { get; init; }
    public bool IncludeSubordinateCommaDiagnostics { get; init; }
    public bool IncludeFragmentDiagnostics { get; init; }
    public bool IncludeSpacingDiagnostics { get; init; }
    public bool IncludeWordConstructions { get; init; }
}
