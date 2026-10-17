namespace BgLang.Core;

/// <summary>Optional validated source capture for offline editing; never retained by a runtime session.</summary>
internal sealed record NeutralPackageSnapshot(byte[] Lexicon, byte[] WordFormation, byte[] Annotations);
