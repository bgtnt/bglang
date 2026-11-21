using BgLang.Contracts;
using BgLang.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass]
public sealed class TextConstructionProjectorBoundaryTests
{
    private const string Adjective = "Прилагателно:76,Женски";
    private const string Noun = "Съществително:41,Женски";
    private const string Conjunction = "Съюз:189";
    private const string Particle = "Частица:189";

    [TestMethod]
    public void Connector_after_nominal_phrase_is_rejected()
    {
        var source = Fixture("голяма книга и", Word("голяма", Adjective), Word("книга", Noun), Word("и", Conjunction));
        var analysis = AddPhrase(source, 1, [0], [2]);

        Assert.ThrowsExactly<ArgumentException>(() => TextConstructionProjector.Project(analysis, CancellationToken.None));
    }

    [TestMethod]
    public void Connector_before_nominal_phrase_is_rejected()
    {
        var source = Fixture("и голяма книга", Word("и", Conjunction), Word("голяма", Adjective), Word("книга", Noun));
        var analysis = AddPhrase(source, 2, [1], [0]);

        Assert.ThrowsExactly<ArgumentException>(() => TextConstructionProjector.Project(analysis, CancellationToken.None));
    }

    [TestMethod]
    public void Connector_inside_nominal_phrase_preserves_all_original_readings()
    {
        var source = Fixture("голяма и нова книга.", Word("голяма", Adjective), Word("и", Conjunction, Particle),
            Word("нова", Adjective), Word("книга", Noun), new(".", TokenKind.Punctuation, []));
        var analysis = AddPhrase(source, 3, [0, 2], [1]);

        var sentence = TextConstructionProjector.Project(analysis, CancellationToken.None).Sentences.Single();
        var slot = sentence.Slots.Single(candidate => candidate.Symbol == "NP");

        Assert.AreEqual((0, 19), (slot.Start, slot.Length));
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, slot.TokenIndices.ToArray());
        CollectionAssert.AreEqual(new[] { "modifier_coordination" }, slot.InternalLinks.ToArray());
        TextReadingReference[] expected = [new(0, 0), new(1, 0), new(1, 1), new(2, 0), new(3, 0)];
        CollectionAssert.AreEqual(expected, slot.Readings.ToArray());
        Assert.IsTrue(slot.HasNonNominalAlternatives);
        Assert.IsTrue(slot.HasAmbiguity);
        CollectionAssert.AreEqual(new[] { "NP", "P:." }, sentence.Slots.Select(candidate => candidate.Symbol).ToArray());
    }

    [TestMethod]
    public void Connector_index_outside_tokens_is_rejected()
    {
        var source = Fixture("голяма книга.", Word("голяма", Adjective), Word("книга", Noun), new(".", TokenKind.Punctuation, []));
        var analysis = AddPhrase(source, 1, [0], [99]);

        Assert.ThrowsExactly<ArgumentException>(() => TextConstructionProjector.Project(analysis, CancellationToken.None));
    }

    private static TextAnalysis AddPhrase(TextAnalysis source, int head, int[] modifiers, int[] connectors)
    {
        int start = source.Tokens[modifiers[0]].Start;
        int end = source.Tokens[head].Start + source.Tokens[head].Length;
        var members = modifiers.Select(i => new NominalMemberSupport(i, NominalAgreementAssessment.Compatible, [new(i, 0)], [])).ToArray();
        var phrase = new TextNominalPhrase("external:np", start, end - start, head, modifiers, connectors,
            NominalAgreementAssessment.Compatible, [new(new(head, 0), null, members)]);
        return source with { NominalConstructions = new("external:nominal:v1", true, null, [phrase], []) };
    }

    private static TextAnalysis Fixture(string text, params TokenSpec[] specifications)
    {
        List<TokenAnalysis> tokens = [];
        List<TextReadingSet> sets = [];
        List<TextReadingOccurrence> occurrences = [];
        int cursor = 0;
        foreach (var spec in specifications)
        {
            int start = text.IndexOf(spec.Text, cursor, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Invalid fixture token: " + spec.Text);
            int tokenIndex = tokens.Count;
            tokens.Add(new(spec.Text, spec.Kind, start, spec.Text.Length, null));
            if (spec.Kind == TokenKind.Word)
            {
                var rows = spec.Tags.Select(tag =>
                {
                    var raw = new WordReading(spec.Text, spec.Text, tag, 1, null, null, null, "test-fixture");
                    return new TextReadingSnapshot(raw, raw.Features);
                }).ToArray();
                occurrences.Add(new(tokenIndex, spec.Text, start, spec.Text.Length, sets.Count));
                sets.Add(new(spec.Text, spec.Text, rows, null, null, 0));
            }
            cursor = start + spec.Text.Length;
        }
        return new(text, tokens) { Sentences = [new(0, text.Length)], ReadingInventory = new(sets, occurrences) };
    }

    private static TokenSpec Word(string text, params string[] tags) => new(text, TokenKind.Word, tags);

    private sealed record TokenSpec(string Text, TokenKind Kind, string[] Tags);
}
