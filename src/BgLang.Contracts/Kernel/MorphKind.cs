namespace BgLang.Contracts;

/// <summary>Typed word-formation segment roles emitted by the runtime analyzer.</summary>
public enum MorphKind
{
    Prefix,
    Root,
    Stem,
    DerivationalSuffix,
    Interfix,
    InflectionalEnding
}
