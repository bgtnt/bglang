using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BgLang.Core;

namespace BgLang.SourceBuilder;

/// <summary>One typed editorial metadata correction; publication requires the exact predeclared bytes.</summary>
internal static class AuthoringStressOverlay
{
    private const string Schema = "bglang-authoring-stress-overlay.v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static Result Apply(string inputPath, string graphPath, string overlayPath, string outputDirectory)
    {
        string target = NeutralSourceBuilder.PrepareTarget(outputDirectory);
        Definition change = StrictJson.Read<Definition>(overlayPath, "Editorial stress overlay", root =>
        {
            StrictJson.RequireObject(root, "Editorial stress overlay", ["schemaVersion", "input", "graph", "entryId",
                "ownerSha256", "oldBindingId", "stress", "accentParadigm", "stressPattern", "result"]);
            if (StrictJson.RequireString(root, "schemaVersion", "Editorial stress overlay") != Schema)
                throw new InvalidDataException("Unsupported editorial stress overlay schema.");
            foreach (string field in new[] { "entryId", "ownerSha256", "oldBindingId" })
                StrictJson.RequireString(root, field, "Editorial stress overlay");
            foreach (string field in new[] { "input", "graph", "result" })
            {
                JsonElement pin = StrictJson.RequireProperty(root, field, JsonValueKind.Object, "Editorial stress overlay");
                StrictJson.RequireObject(pin, "Overlay pin", field == "result" ? ["bytes", "sha256", "ownerSha256"] : ["bytes", "sha256"]);
                StrictJson.RequireProperty(pin, "bytes", JsonValueKind.Number, "Overlay pin");
                StrictJson.RequireString(pin, "sha256", "Overlay pin");
                if (field == "result") StrictJson.RequireString(pin, "ownerSha256", "Overlay pin");
            }
            StrictJson.RequireProperty(root, "stress", JsonValueKind.Array, "Editorial stress overlay");
            StrictJson.RequireProperty(root, "accentParadigm", JsonValueKind.Object, "Editorial stress overlay");
            StrictJson.RequireProperty(root, "stressPattern", JsonValueKind.Object, "Editorial stress overlay");
        });
        byte[] source = File.ReadAllBytes(inputPath);
        Check(new(source.LongLength, Hash(source)), change.Input, "Original authoring input");
        Check(Measure(graphPath), change.Graph, "Graph input");
        AuthoringDocument document = AuthoringDocument.Read(Utf8.GetString(source));
        source = [];
        if (document.SchemaVersion != AuthoringDocument.AccentSchema)
            throw new InvalidDataException("A v5 authoring input is required.");
        AuthoringDocument.Entry original = document.Entries.Single(entry => entry.EntryId == change.EntryId);
        if (Hash(JsonSerializer.SerializeToUtf8Bytes(original, NeutralSourceBuilder.JsonOptions)) != change.OwnerSha256)
            throw new InvalidDataException("Owner preimage differs.");
        LexemeStressPattern originalPattern = original.StressPattern ?? throw new InvalidDataException("Original captured pattern is required.");
        if (originalPattern.BindingId != change.OldBindingId)
            throw new InvalidDataException("Original stress binding differs; no binding is recomputed.");
        AuthoringDocument.Entry revised = original with
        { Stress = change.Stress, AccentParadigm = change.AccentParadigm, StressPattern = change.StressPattern };
        AuthoringDocument.Read(Utf8.GetString(new AuthoringDocument(document.SchemaVersion, [revised]).Serialize()));
        if (revised.Stress.Any(row => row.Source is not null) || change.StressPattern.SourceId != originalPattern.SourceId)
            throw new InvalidDataException("Editorial rows or retained Legacy source provenance differ.");
        byte[] owner = JsonSerializer.SerializeToUtf8Bytes(revised, NeutralSourceBuilder.JsonOptions);
        if (Hash(owner) != change.Result.OwnerSha256) throw new InvalidDataException("Revised owner bytes differ.");
        byte[] derived = (document with { Entries = document.Entries.Select(entry => ReferenceEquals(entry, original) ? revised : entry).ToArray() }).Serialize();
        Pin expected = new(change.Result.Bytes, change.Result.Sha256);
        Check(new(derived.LongLength, Hash(derived)), expected, "Derived authoring input");
        using OwnedStageDirectory stage = OwnedStageDirectory.Create(Path.GetDirectoryName(target)!);
        NeutralSourceBuilder.WriteNew(stage, AuthoringDocument.FileName, derived);
        Check(Measure(Path.Combine(stage.Path, AuthoringDocument.FileName)), expected, "Written authoring input");
        stage.Publish(target);
        return new(Path.Combine(target, AuthoringDocument.FileName), expected.Bytes, expected.Sha256,
            change.EntryId, change.Result.OwnerSha256, false, Path.GetTempPath());
    }

    private static Pin Measure(string path)
    {
        using FileStream input = File.OpenRead(path);
        return new(input.Length, Convert.ToHexStringLower(SHA256.HashData(input)));
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Check(Pin actual, Pin expected, string label)
    {
        if (actual != expected) throw new InvalidDataException(label + " RAW bytes/SHA differ.");
    }

    internal sealed record Result(string SourcePath, long Bytes, string Sha256, string EntryId,
        string OwnerSha256, bool NativeBindingVerified, string ActualTempPath);
    private sealed record Pin(long Bytes, string Sha256);
    private sealed record OutputPin(long Bytes, string Sha256, string OwnerSha256);
    private sealed record Definition(string SchemaVersion, Pin Input, Pin Graph, string EntryId,
        string OwnerSha256, string OldBindingId, LexemeAnnotations.StressRow[] Stress,
        LexemeAccentParadigm AccentParadigm, LexemeStressPattern StressPattern, OutputPin Result);
}
