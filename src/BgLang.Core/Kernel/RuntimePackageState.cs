using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Validated immutable state loaded from a runtime package.</summary>
internal sealed class RuntimePackageState : IDisposable
{
    private RuntimePackageState(
        IRuntimeLexicon lexicon,
        WordFormationAnalyzer wordFormation,
        PackageProvenance provenance,
        IReadOnlyDictionary<string, int[]>? lexicalStress = null)
    {
        Lexicon = lexicon;
        WordFormation = wordFormation;
        Provenance = provenance;
        LexicalStress = lexicalStress;
    }

    internal IRuntimeLexicon Lexicon { get; }

    internal WordFormationAnalyzer WordFormation { get; }

    internal PackageProvenance Provenance { get; }

    internal IReadOnlyDictionary<string, int[]>? LexicalStress { get; }

    internal static RuntimePackageState Open(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullManifestPath))
            throw new FileNotFoundException("Runtime package manifest was not found.", fullManifestPath);

        ValidateEntryPath(fullManifestPath);

        string? schemaVersion = StrictJson.ReadSchemaVersion(fullManifestPath, "runtime package manifest");
        if (schemaVersion is NeutralDictionaryPackageLoader.ManifestSchemaVersion or
            NeutralDictionaryPackageLoader.AnnotatedManifestSchemaVersion)
        {
            var loaded = NeutralDictionaryPackageLoader.Load(fullManifestPath);
            return new RuntimePackageState(loaded.Lexicon, loaded.WordFormation, loaded.Provenance, loaded.LexicalStress);
        }
        throw new InvalidDataException($"Unsupported runtime package schema '{schemaVersion}'.");
    }

    public void Dispose() => Lexicon.Dispose();

    private static void ValidateEntryPath(string manifestPath)
    {
        FileInfo manifest = new(manifestPath);
        if (manifest.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Runtime package manifest path is unsafe.");

        for (DirectoryInfo? directory = manifest.Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Runtime package manifest ancestry is unsafe.");
        }
    }

}
