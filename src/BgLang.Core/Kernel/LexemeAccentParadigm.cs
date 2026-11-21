using BgLang.Contracts;
using System.Text.Json;

namespace BgLang.Core;

/// <summary>An explicitly selected lexical accent class over the existing guarded morphology program.</summary>
internal sealed record LexemeAccentParadigm(string Lemma, string BaseFullTag, string RuleId,
    int BaseVowelFromEnd, string EvidenceSourceId, LexemeAccentParadigm.Cell[] Cells)
{
    internal enum Anchor { RetainedBase, WordVowelFromEnd, EndingVowel }
    internal sealed record Cell(string FullTag, Anchor Anchor, int Distance);

    internal static void ValidateJson(JsonElement value)
    {
        const string label = "Lexeme accent paradigm";
        StrictJson.RequireObject(value, label, ["lemma", "baseFullTag", "ruleId", "baseVowelFromEnd", "evidenceSourceId", "cells"]);
        foreach (string field in new[] { "lemma", "baseFullTag", "ruleId", "evidenceSourceId" })
            StrictJson.RequireString(value, field, label);
        if (!StrictJson.RequireProperty(value, "baseVowelFromEnd", JsonValueKind.Number, label).TryGetInt32(out _))
            throw new InvalidDataException("Accent anchor must be an integer.");
        foreach (JsonElement cell in StrictJson.RequireArray(value, "cells", label))
        {
            StrictJson.RequireObject(cell, label, ["fullTag", "anchor", "distance"]);
            StrictJson.RequireString(cell, "fullTag", label);
            if (!StrictJson.RequireProperty(cell, "anchor", JsonValueKind.Number, label).TryGetInt32(out int anchor) ||
                !Enum.IsDefined((Anchor)anchor) ||
                !StrictJson.RequireProperty(cell, "distance", JsonValueKind.Number, label).TryGetInt32(out int distance) || distance < 0)
                throw new InvalidDataException("Accent cell requires a supported typed anchor.");
        }
    }

    internal void Validate(WordReading[] inventory, WordFormationAnalyzer graph)
    {
        if (!BulgarianOrthography.IsWellFormedWord(Lemma) || Lemma != BulgarianTextNormalizer.NormalizeKey(Lemma) ||
            BaseVowelFromEnd < 0 || BaseVowelFromEnd >= Vowels(Lemma).Length ||
            string.IsNullOrWhiteSpace(EvidenceSourceId) || EvidenceSourceId != EvidenceSourceId.Trim() ||
            !inventory.Any(row => row.Form == Lemma && row.Lemma == Lemma && row.PartOfSpeech == BaseFullTag))
            throw new InvalidDataException("Accent paradigm requires its exact lexical base and explicit evidence.");
        var generated = graph.GenerateLexicalTuples(Lemma, BaseFullTag, RuleId);
        if (!generated.SetEquals(inventory.Select(row => (row.Form, row.PartOfSpeech!))))
            throw new InvalidDataException("Accent paradigm must bind the owner's complete exact guarded graph program.");
        string? previous = null;
        foreach (Cell cell in Cells)
        {
            if (!Enum.IsDefined(cell.Anchor) || cell.Distance < 0 || cell.Anchor == Anchor.RetainedBase && cell.Distance != 0 ||
                previous is not null && StringComparer.Ordinal.Compare(previous, cell.FullTag) >= 0 ||
                !generated.Any(row => row.Tag == cell.FullTag))
                throw new InvalidDataException("Accent cells require unique ordered generated tags and valid anchors.");
            previous = cell.FullTag;
        }
    }

    internal GeneratedStressParadigm Generate(string entryId, string lemma, WordFormationAnalyzer graph,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<WordFormationGraph.GenerationObservation> observations = [];
        var tuples = graph.GenerateAccentTuples(lemma, BaseFullTag, RuleId, observations.Add, cancellationToken);
        int[] sourceVowels = Vowels(lemma);
        int? sourceOrdinal = BaseVowelFromEnd < sourceVowels.Length ? sourceVowels.Length - 1 - BaseVowelFromEnd : null;
        var witnesses = observations.GroupBy(row => (row.Form, Tag: row.FullTag)).ToDictionary(group => group.Key, group => group.ToArray());
        List<GeneratedStressForm> forms = [];
        foreach (var tuple in tuples.OrderBy(row => row.Form, StringComparer.Ordinal).ThenBy(row => row.Tag, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] targetVowels = Vowels(tuple.Form);
            Cell? cell = Cells.SingleOrDefault(cell => cell.FullTag == tuple.Tag);
            int? ordinal = null;
            if (cell?.Anchor == Anchor.WordVowelFromEnd)
                ordinal = cell.Distance < targetVowels.Length ? targetVowels.Length - 1 - cell.Distance : null;
            else if (cell?.Anchor == Anchor.EndingVowel)
            {
                if (witnesses.TryGetValue(tuple, out var endingPaths) && endingPaths.Length > 0 &&
                    endingPaths.All(path => path.EndingStart is not null) &&
                    endingPaths.Select(path => path.EndingStart).Distinct().Count() == 1)
                {
                    int[] endingOrdinals = targetVowels.Select((position, index) => (position, index))
                        .Where(item => item.position >= endingPaths[0].EndingStart!.Value).Select(item => item.index).ToArray();
                    if (cell.Distance < endingOrdinals.Length) ordinal = endingOrdinals[cell.Distance];
                }
            }
            else if (sourceOrdinal is { } stress)
            {
                if (tuple.Form == lemma && tuple.Tag == BaseFullTag) ordinal = stress;
                else if (witnesses.TryGetValue(tuple, out var paths) && paths.Length > 0 &&
                    paths.All(path => sourceVowels[stress] < path.RetainedOriginalPrefixLength)) ordinal = stress;
            }
            StressMark[] marks = ordinal is { } value && (uint)value < (uint)targetVowels.Length
                ? [new(value, targetVowels[value], StressMarkKind.Unspecified)] : [];
            forms.Add(new(tuple.Form, tuple.Tag, marks.Length == 1 ? StressStatus.Inferred : StressStatus.Unknown,
                Array.AsReadOnly(marks)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(true, entryId, Lemma, lemma, RuleId, EvidenceSourceId, graph.SourceId, forms.AsReadOnly());
    }

    private static int[] Vowels(string text) => text.Select((value, position) => (value, position))
        .Where(item => BulgarianOrthography.IsVowel(item.value)).Select(item => item.position).ToArray();
}
