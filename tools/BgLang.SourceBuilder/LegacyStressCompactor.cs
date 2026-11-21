using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using BgLang.Core;
using Cell = BgLang.Core.LexemeStressPattern.Cell;
using CaptureKind = BgLang.Core.LexemeStressPattern.CaptureKind;

namespace BgLang.SourceBuilder;

/// <summary>Offline lossless compaction; qualified complete-pattern fits are separate from captured evidence.</summary>
internal static class LegacyStressCompactor
{
    internal static object Compact(string sourcePath, string graphPath, string outputDirectory)
    {
        string target = NeutralSourceBuilder.PrepareTarget(outputDirectory);
        byte[] sourceBytes = File.ReadAllBytes(sourcePath), graphBytes = File.ReadAllBytes(graphPath);
        AuthoringDocument document = AuthoringDocument.Read(new UTF8Encoding(false, true).GetString(sourceBytes));
        using JsonDocument graphJson = JsonDocument.Parse(graphBytes);
        WordFormationAnalyzer graph = WordFormationAnalyzer.LoadSource(graphJson.RootElement,
            graphJson.RootElement.GetProperty("sourceId").GetString()!);
        long captured = 0, overrides = 0; int patterns = 0, models = 0, retainedModels = 0;
        Dictionary<string, int> fittingOutcomes = new(StringComparer.Ordinal);
        Dictionary<string, List<string>> fittingSamples = new(StringComparer.Ordinal);
        void Note(string reason, string entryId)
        {
            fittingOutcomes[reason] = fittingOutcomes.GetValueOrDefault(reason) + 1;
            if (!fittingSamples.TryGetValue(reason, out var ids)) fittingSamples.Add(reason, ids = []);
            if (ids.Count < 3) ids.Add(entryId);
        }
        List<object> modelBindings = [];
        AuthoringDocument.Entry[] entries = document.Entries.Select(entry =>
        {
            if (entry.StressPattern is not null) throw new InvalidDataException("Source is already compacted.");
            WordReading[] readings = entry.Lexeme.Kind == "productive"
                ? graph.GenerateLexicalReadings(entry.Lexeme.Lemma, entry.Lexeme.BaseFullTag!, entry.Lexeme.ProductiveRuleId!, entry.Lexeme.Score!.Value)
                : entry.Lexeme.Readings!.Select(row => row.ToWordReading()).ToArray();
            var legacy = entry.Stress.Where(row => row.Source is not null).ToArray();
            LexemeStressPattern? pattern = Compress(entry.EntryId, readings, legacy);
            captured += legacy.Length;
            if (pattern is not null) { patterns++; overrides += pattern.Cells.LongCount(cell => cell.VowelOrdinals is not null); }
            LexemeAccentParadigm? model = entry.AccentParadigm;
            var seed = legacy.SingleOrDefault(row => row.Form == entry.Lexeme.Lemma && row.FullTag == entry.Lexeme.BaseFullTag);
            string eligibility = entry.Lexeme.Kind != "productive" ? "not-productive" :
                seed is null ? "no-exact-captured-base" : seed.VowelOrdinals.Length != 1 ? "unclassified-base" :
                seed.Source!.Kind != "LegacyStored" ? "base-not-stored" :
                !BulgarianOrthography.IsWellFormedWord(entry.Lexeme.Lemma) ? "non-word-base" :
                entry.Lexeme.Lemma != BulgarianTextNormalizer.NormalizeKey(entry.Lexeme.Lemma) ? "noncanonical-base" :
                legacy.Length != readings.DistinctBy(row => (row.Form, row.PartOfSpeech)).Count() ? "incomplete-captured-inventory" : "eligible";
            if (model is not null) { retainedModels++; Note("retained-explicit-model", entry.EntryId); }
            else if (eligibility != "eligible") Note(eligibility, entry.EntryId);
            else
            {
                var proposed = new LexemeAccentParadigm(entry.Lexeme.Lemma, entry.Lexeme.BaseFullTag!, entry.Lexeme.ProductiveRuleId!,
                    entry.Lexeme.Lemma.Count(BulgarianOrthography.IsVowel) - 1 - seed!.VowelOrdinals[0],
                    "legacy-pattern-fit-v1:" + seed.Source!.SourceId[17..], []);
                try { proposed.Validate(readings, graph); }
                catch (InvalidDataException error) { throw new InvalidDataException("Unexpected eligible model binding failure: " + entry.EntryId + "/" + entry.Lexeme.Lemma + ": " + error.Message, error); }
                GeneratedStressParadigm prediction = proposed.Generate(entry.EntryId, entry.Lexeme.Lemma, graph, default);
                if (prediction.Forms.Count == legacy.Length && prediction.Forms.All(form =>
                    form.Status == StressStatus.Inferred && legacy.Any(row => row.Form == form.Form && row.FullTag == form.FullTag &&
                        row.VowelOrdinals.AsSpan().SequenceEqual(form.Marks.Select(mark => mark.VowelOrdinal).ToArray())))) model = proposed;
                Note(model is null ? "retained-prefix-does-not-explain-complete-pattern" : "fitted-qualified-model", entry.EntryId);
            }
            if (model is not null)
            {
                models++;
                modelBindings.Add(new { entry.EntryId, model.Lemma, model.RuleId, model.BaseFullTag, model.BaseVowelFromEnd,
                    model.EvidenceSourceId, authority = entry.AccentParadigm is null
                        ? "Legacy complete-pattern fit; explicit prototype selection yields qualified inference, not linguistic review."
                        : "Retained explicit authored model; not reclassified as a Legacy fit." });
            }
            return entry with { Stress = entry.Stress.Where(row => row.Source is null).ToArray(), StressPattern = pattern, AccentParadigm = model };
        }).ToArray();
        var report = new { schema = "bglang.legacy-stress-compaction.v1", entries = entries.Length, capturedRows = captured,
            patterns, ordinalExceptions = overrides, qualifiedPrototypeModels = models, fittedPrototypeModels = models - retainedModels, retainedModels, fittingOutcomes, fittingSamples,
            exactExpandedSourceFieldsCompared = true, modelBindings, actualTemp = Path.GetTempPath(),
            qualification = "Lossless captured-row parity and template predictions are separate. Missing compact cells remain Unknown; fitted models have no independent normative authority." };
        using OwnedStageDirectory stage = OwnedStageDirectory.Create(Path.GetDirectoryName(target)!);
        NeutralSourceBuilder.WriteNew(stage, AuthoringDocument.FileName, new AuthoringDocument(AuthoringDocument.AccentSchema, entries).Serialize());
        NeutralSourceBuilder.WriteNew(stage, NeutralDictionaryPackageLoader.WordFormationFileName, graphBytes);
        NeutralSourceBuilder.WriteNew(stage, "stress-compaction.json", JsonSerializer.SerializeToUtf8Bytes(report, NeutralSourceBuilder.JsonOptions));
        stage.Publish(target);
        return new { report.schema, report.entries, report.capturedRows, report.patterns, report.ordinalExceptions,
            report.qualifiedPrototypeModels, report.fittedPrototypeModels, report.retainedModels, report.fittingOutcomes, report.exactExpandedSourceFieldsCompared, report.actualTemp, report.qualification };
    }

    private static LexemeAnnotations.StressRow[] Expand(LexemeStressPattern pattern, string entryId, WordReading[] exact)
    {
        WordReading[] readings = LexemeStressPattern.CanonicalReadings(exact);
        pattern.Validate(entryId, readings);
        return pattern.Cells.Select(cell => pattern.Find(entryId, readings, cell.ReadingIndex)!).ToArray();
    }

    // Pass only Legacy rows. The parent keeps editorial Source==null Stress[] unchanged.
    internal static LexemeStressPattern? Compress(string parentEntryId, WordReading[] exactReadings,
        LexemeAnnotations.StressRow[] legacyRows)
    {
        if (legacyRows.Length == 0) return null;
        WordReading[] readings = LexemeStressPattern.CanonicalReadings(exactReadings);
        LexemeAnnotations.StressRow[] ordered = legacyRows.OrderBy(row => row.Form, StringComparer.Ordinal)
            .ThenBy(row => row.FullTag, StringComparer.Ordinal).ToArray();
        string sourceId = ordered[0].Source?.SourceId ?? throw new InvalidDataException("Editorial rows must remain in Stress[].");
        int[] fallback = LexemeStressPattern.Majority(ordered.Select(row => row.VowelOrdinals).ToArray());
        Cell[] cells = new Cell[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            LexemeAnnotations.StressRow row = ordered[i];
            LexemeAnnotations.StressSource source = row.Source ?? throw new InvalidDataException("Only Legacy rows are compressed.");
            if (source.SourceId != sourceId) throw new InvalidDataException("A pattern requires one exact source identity.");
            int index = Array.FindIndex(readings, reading => reading.Form == row.Form && reading.PartOfSpeech == row.FullTag);
            if (index < 0 || i > 0 && index <= cells[i - 1].ReadingIndex)
                throw new InvalidDataException("Legacy rows require unique exact owner/form/fullTag bindings.");
            CaptureKind kind = source.Kind switch
            {
                "LegacyStored" => CaptureKind.Stored,
                "LegacyCarried" => CaptureKind.Carried,
                "LegacyDisplayInferred" when source.Utf16Positions is null => CaptureKind.DisplayNull,
                "LegacyDisplayInferred" when source.Utf16Positions.Length == 0 => CaptureKind.DisplayEmpty,
                _ => throw new InvalidDataException("Unsupported captured Legacy provenance.")
            };
            cells[i] = new(index, source.WordId, kind)
            { VowelOrdinals = row.VowelOrdinals.AsSpan().SequenceEqual(fallback) ? null : (int[])row.VowelOrdinals.Clone() };
        }
        LexemeStressPattern result = new(sourceId,
            LexemeStressPattern.CreateBindingId(parentEntryId, sourceId, readings, cells), (int[])fallback.Clone(), cells);
        LexemeAnnotations.StressRow[] expanded = Expand(result, parentEntryId, exactReadings);
        for (int i = 0; i < ordered.Length; i++)
        {
            LexemeAnnotations.StressRow before = ordered[i], after = expanded[i];
            LexemeAnnotations.StressSource a = before.Source!, b = after.Source!;
            if (before.Form != after.Form || before.FullTag != after.FullTag ||
                !before.VowelOrdinals.AsSpan().SequenceEqual(after.VowelOrdinals) ||
                a.Kind != b.Kind || a.SourceId != b.SourceId || a.OwnerId != b.OwnerId || a.WordId != b.WordId ||
                !SameNullable(a.Utf16Positions, b.Utf16Positions) || !SameNullable(a.DisplayUtf16Positions, b.DisplayUtf16Positions))
                throw new InvalidDataException("Pattern compression must preserve all exact source and offset fields.");
        }
        return result;
    }


    private static bool SameNullable(int[]? left, int[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}
