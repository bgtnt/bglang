using System.Security.Cryptography;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Tests.Shared;

// Focused package acceptance source, compiled only into tests; the52-row oracle predates candidate runs.
internal static class EditorialStressAcceptance
{
    internal const string Target = "BGLANG_EDITORIAL_STRESS_MANIFEST";
    internal const string Required = "BGLANG_EDITORIAL_STRESS_REQUIRE_MANIFEST";
    internal const string ModeVariable = "BGLANG_EDITORIAL_STRESS_PROFILE";
    internal const string Owner = "legacy-base:6332288";
    private const string FixtureSha = "ef32bb63b1a3e565381f00a980913ef08de3f7cd155bed72e1b58681bc5cb2c3";
    private static readonly Lazy<JsonDocument> Document = new(() =>
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "data/editorial-stress-acceptance.v1.json"));
        Assert.AreEqual(FixtureSha, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        JsonDocument result = JsonDocument.Parse(bytes);
        Assert.AreEqual("bglang.editorial-stress-acceptance.v1", result.RootElement.GetProperty("schema").GetString());
        return result;
    });
    internal static JsonElement Root => Document.Value.RootElement;
    internal static JsonElement[] Rows => Root.GetProperty("rows").EnumerateArray().Select(row => row.Clone()).ToArray();
    internal static string Mode
    {
        get
        {
            Assert.AreEqual("1", Environment.GetEnvironmentVariable(Required), "The editorial package gate is mandatory; absence never skips.");
            string? value = Environment.GetEnvironmentVariable(ModeVariable);
            Assert.IsTrue(value is "Current" or "Baseline", "Select an explicit canonical acceptance profile.");
            return value!;
        }
    }
    internal static bool IsCurrent => Mode == "Current";
    internal static string Manifest
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(Target);
            Assert.IsFalse(string.IsNullOrWhiteSpace(value));
            Assert.IsTrue(value == value!.Trim() && Path.IsPathFullyQualified(value!));
            return Path.GetFullPath(value!);
        }
    }
    internal static void AssertBinding(string path, string mode)
    {
        Assert.IsTrue(mode is "Current" or "Baseline");
        JsonElement expected = Root.GetProperty("packages").GetProperty(mode);
        string directory = Path.GetDirectoryName(path)!;
        JsonElement[] files = expected.GetProperty("files").EnumerateArray().ToArray();
        CollectionAssert.AreEquivalent(files.Select(file => file.GetProperty("name").GetString()).ToArray(),
            Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).ToArray());
        foreach (JsonElement file in files)
        {
            string name = file.GetProperty("name").GetString()!;
            Assert.AreEqual(name, Path.GetFileName(name));
            string input = Path.Combine(directory, name);
            Assert.IsFalse(File.GetAttributes(input).HasFlag(FileAttributes.ReparsePoint));
            Assert.AreEqual(file.GetProperty("bytes").GetInt64(), new FileInfo(input).Length);
            using FileStream stream = File.OpenRead(input);
            Assert.AreEqual(file.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(stream)), $"Expected RAW SHA-256 for {name}: {file.GetProperty("sha256").GetString()}.");
        }
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.AreEqual(expected.GetProperty("packageId").GetString(), manifest.RootElement.GetProperty("packageId").GetString());
    }
    internal static BulgarianLanguageSession Open(bool requireCurrent = false)
    {
        if (requireCurrent) Assert.AreEqual("Current", Mode, "Current successor gates cannot accept Baseline.");
        string path = Manifest; AssertBinding(path, Mode);
        BulgarianLanguageSession session = BulgarianLanguageSession.Open(path);
        try
        {
            JsonElement expected = Root.GetProperty("packages").GetProperty(Mode);
            Assert.AreEqual(expected.GetProperty("packageId").GetString(), session.Provenance.PackageId);
            Assert.AreEqual(expected.GetProperty("sourceId").GetString(), session.Provenance.LexiconSource);
            Assert.AreEqual(expected.GetProperty("annotationsSourceId").GetString(), session.Provenance.AnnotationsSource);
            Assert.AreEqual("word-formation-source-v0", session.Provenance.WordFormationSource);
            return session;
        }
        catch { session.Dispose(); throw; }
    }
    internal static StressReadingSelection Selection(JsonElement row) => new(0, Owner,
        row.GetProperty("form").GetString()!, row.GetProperty("fullTag").GetString());
    internal static void AssertSelected(StressEvidence actual, JsonElement row)
    {
        string form = row.GetProperty("form").GetString()!, tag = row.GetProperty("fullTag").GetString()!;
        int original = row.GetProperty("legacyVowelOrdinal").GetInt32();
        JsonElement normative = row.GetProperty("normativeVowelOrdinal");
        int ordinal = IsCurrent && normative.ValueKind == JsonValueKind.Number ? normative.GetInt32() : original;
        bool editorial = IsCurrent && ordinal != original;
        StressOrigin origin = editorial ? StressOrigin.Editorial : row.GetProperty("legacyCaptureKind").GetString() == "Stored" ? StressOrigin.LegacyStored : StressOrigin.LegacyCarried;
        StressStatus status = origin == StressOrigin.LegacyCarried ? StressStatus.Inferred : StressStatus.Supported;
        Assert.AreEqual(form, actual.Text); Assert.IsTrue(actual.OwnersAvailable);
        Assert.AreEqual(StressSelectionStatus.Matched, actual.SelectionStatus);
        string rendered = IsCurrent && normative.ValueKind == JsonValueKind.Number ? row.GetProperty("normativeRendered").GetString()! : Render(form, original);
        Assert.AreEqual(rendered, actual.RenderedText);
        StressTokenEvidence token = actual.Tokens.Single();
        Assert.AreEqual(0, token.TokenIndex); Assert.AreEqual(form, token.Text); Assert.AreEqual(0, token.Start); Assert.AreEqual(form.Length, token.Length);
        Assert.AreEqual(status, token.Status);
        StressReadingEvidence[] readings = token.Readings.Where(value => value.Reading.Form == form && value.Reading.PartOfSpeech == tag && value.Owners.Any(owner => owner.EntryId == Owner)).ToArray();
        Assert.IsNotEmpty(readings);
        foreach (StressReadingEvidence reading in readings)
        {
            Assert.IsNull(reading.Reading.LegacyWordId); Assert.IsNull(reading.Reading.LegacyBaseId);
            Assert.AreEqual(Root.GetProperty("packages").GetProperty(Mode).GetProperty("sourceId").GetString(), reading.Reading.Source);
            StressRealization realization = reading.Owners.Single(owner => owner.EntryId == Owner).Realizations.Single();
            Assert.AreEqual(origin, realization.Origin); Assert.AreEqual(status, realization.Status);
            CollectionAssert.AreEqual(new[] { new StressMark(ordinal, Vowels(form)[ordinal], StressMarkKind.Unspecified) }, realization.Marks.ToArray());
            Assert.AreEqual(editorial ? Root.GetProperty("packages").GetProperty("Current").GetProperty("annotationsSourceId").GetString() : Root.GetProperty("legacySourceId").GetString(), realization.SourceId);
            if (editorial) Assert.IsNull(realization.LegacyUtf16Positions);
            else CollectionAssert.AreEqual(new[] { Vowels(form)[original] }, realization.LegacyUtf16Positions!.ToArray());
            Assert.IsNull(realization.LegacyDisplayUtf16Positions);
        }
    }
    internal static void AssertSelectedAll(BulgarianLanguageSession session)
    {
        Assert.HasCount(57, Rows); Assert.AreEqual(52, Rows.Count(row => row.GetProperty("normativeVowelOrdinal").ValueKind == JsonValueKind.Number));
        foreach (JsonElement row in Rows) AssertSelected(session.GetStressEvidence(row.GetProperty("form").GetString()!, Selection(row)), row);
    }
    internal static void AssertGenerated(GeneratedStressParadigm actual)
    {
        Assert.IsTrue(actual.Available); Assert.AreEqual(Owner, actual.PrototypeEntryId); Assert.AreEqual("чета", actual.PrototypeLemma); Assert.AreEqual("чета", actual.Lemma);
        Assert.AreEqual("productive.verb.type145ti.full_mini_forms.v0", actual.RuleId);
        Assert.AreEqual(Root.GetProperty(IsCurrent ? "currentModelSourceId" : "baselineModelSourceId").GetString(), actual.EvidenceSourceId);
        Assert.AreEqual("word-formation-source-v0", actual.GraphSourceId);
        CollectionAssert.AreEquivalent(Rows.Select(row => row.GetProperty("form").GetString() + "|" + row.GetProperty("fullTag").GetString()).ToArray(), actual.Forms.Select(form => form.Form + "|" + form.FullTag).ToArray());
        foreach (JsonElement row in Rows)
        {
            string text = row.GetProperty("form").GetString()!, tag = row.GetProperty("fullTag").GetString()!;
            GeneratedStressForm form = actual.Forms.Single(value => value.Form == text && value.FullTag == tag);
            JsonElement gold = row.GetProperty("normativeVowelOrdinal");
            // Missing official cells preserve inherited retained-base inference; these five are not normative gold.
            int ordinal = IsCurrent && gold.ValueKind == JsonValueKind.Number ? gold.GetInt32() : row.GetProperty("legacyVowelOrdinal").GetInt32();
            Assert.AreEqual(StressStatus.Inferred, form.Status);
            CollectionAssert.AreEqual(new[] { new StressMark(ordinal, Vowels(text)[ordinal], StressMarkKind.Unspecified) }, form.Marks.ToArray());
        }
    }
    internal static void AssertControls(BulgarianLanguageSession session)
    {
        const string noun = "legacy-base:1465984", nounTag = "Съществително:41,Женски";
        StressEvidence nounResult = session.GetStressEvidence("чета", new(0, noun, "чета", nounTag));
        Assert.AreEqual(StressSelectionStatus.Matched, nounResult.SelectionStatus); Assert.AreEqual("че\u0300та", nounResult.RenderedText);
        StressRealization nounMark = nounResult.Tokens.Single().Readings.Where(row => row.Reading.Form == "чета" && row.Reading.PartOfSpeech == nounTag).SelectMany(row => row.Owners).First(owner => owner.EntryId == noun).Realizations.Single();
        CollectionAssert.AreEqual(new[] { new StressMark(0, 1, StressMarkKind.Unspecified) }, nounMark.Marks.ToArray());
        foreach (string word in new[] { "чета", "чете" })
        {
            StressEvidence unselected = session.GetStressEvidence(word); Assert.AreEqual(StressSelectionStatus.NotRequested, unselected.SelectionStatus);
            Assert.AreEqual(IsCurrent ? word : Render(word, 0), unselected.RenderedText);
            if (IsCurrent) Assert.AreEqual(StressStatus.Ambiguous, unselected.Tokens.Single().Status);
        }
        JsonElement present = Rows.Single(row => row.GetProperty("form").GetString() == "чете" && row.GetProperty("fullTag").GetString() == "Глагол:145ti,3л.,Сегашно");
        StressEvidence padded = session.GetStressEvidence("😀 чете!", new(1, Owner, "чете", present.GetProperty("fullTag").GetString()));
        Assert.AreEqual(StressSelectionStatus.Matched, padded.SelectionStatus);
        Assert.AreEqual(IsCurrent ? "😀 чете\u0300!" : "😀 че\u0300те!", padded.RenderedText);
        StressTokenEvidence token = padded.Tokens.Single(); Assert.AreEqual(3, token.Start); Assert.AreEqual(4, token.Length);
        int position = IsCurrent ? 6 : 4;
        Assert.AreEqual(position, token.Readings.Where(row => row.Reading.PartOfSpeech == present.GetProperty("fullTag").GetString()).SelectMany(row => row.Owners).Single(owner => owner.EntryId == Owner).Realizations.Single().Marks.Single().Utf16Position);
        foreach (StressReadingSelection selection in new StressReadingSelection[]
        {
            new(0, " " + Owner, "чета", "Глагол:145ti,1л.,Сегашно"),
            new(0, Owner + "#", "чета", "Глагол:145ti,1л.,Сегашно"),
            new(0, Owner, "четат", "Глагол:145ti,1л.,Сегашно"),
            new(0, Owner, "чета", " Глагол:145ti,1л.,Сегашно"),
            new(0, Owner, "чета", null)
        })
        {
            StressEvidence wrong = session.GetStressEvidence("чета", selection);
            Assert.AreEqual(StressSelectionStatus.NotConnected, wrong.SelectionStatus); Assert.AreEqual("чета", wrong.RenderedText);
        }
        Assert.IsFalse(session.GenerateStressForms(" " + Owner, "чета").Available);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.AreEqual(cancellation.Token, Assert.Throws<OperationCanceledException>(() => session.GetStressEvidence("чета", cancellationToken: cancellation.Token)).CancellationToken);
        Assert.Throws<OperationCanceledException>(() => session.GenerateStressForms(Owner, "чета", cancellation.Token));
        AssertSelected(session.GetStressEvidence("чете", Selection(present)), present);
    }
    internal static void AssertAll(BulgarianLanguageSession session)
    {
        AssertSelectedAll(session); AssertGenerated(session.GenerateStressForms(Owner, "чета")); AssertControls(session);
    }
    private static string Render(string text, int ordinal) => text.Insert(Vowels(text)[ordinal] + 1, "\u0300");
    private static int[] Vowels(string text) => Enumerable.Range(0, text.Length).Where(i => "аеиоуъюяѝѐ".Contains(char.ToLowerInvariant(text[i]))).ToArray();
}
