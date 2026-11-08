using BgLang.Contracts;
using BgLang.Tests.Shared;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Public.Tests;

[TestClass, DoNotParallelize]
public sealed class ContinuationWordStartPackageTests
{
    [TestMethod]
    [DataRow("Аз чета.", 3, 4, TextContinuationTarget.Finite, TextContinuationTarget.Finite, "Аз ", ".")]
    [DataRow("Аз чета.", 3, 4, TextContinuationTarget.Auto, TextContinuationTarget.Finite, "Аз ", ".")]
    [DataRow("Нова книга.", 5, 5, TextContinuationTarget.Noun, TextContinuationTarget.Noun, "Нова ", ".")]
    [DataRow("Нова книга.", 5, 5, TextContinuationTarget.Auto, TextContinuationTarget.Noun, "Нова ", ".")]
    [DataRow("😀. Аз че́та.", 7, 5, TextContinuationTarget.Finite, TextContinuationTarget.Finite, "😀. Аз ", ".")]
    public void Word_start_replaces_the_original_token_with_the_selected_form(string text, int caret, int length,
        TextContinuationTarget requested, TextContinuationTarget resolved, string before, string after)
    {
        using var session = GraphStressAcceptance.Open();
        var options = new TextContextOptions { Continuation = new(caret) { Target = requested } };
        var analysis = session.AnalyzeText(text, false, options, default);
        Assert.AreEqual(text, analysis.Text);
        Assert.IsNotNull(analysis.Continuation);
        var evidence = analysis.Continuation;
        Assert.IsTrue(evidence.Available, evidence.UnavailableReason);
        Assert.AreEqual(resolved, evidence.Target);
        Assert.AreEqual((caret, caret, length, ""), (evidence.Caret, evidence.Start, evidence.Length, evidence.Prefix));
        Assert.IsNotEmpty(evidence.Suggestions);
        string form = evidence.Suggestions[0].Form;
        Assert.AreEqual(before + form + after, text[..evidence.Start] + form + text[(evidence.Start + evidence.Length)..]);
    }

    [TestMethod]
    public void Word_start_rejects_a_replacement_ending_inside_a_grapheme()
    {
        using var session = GraphStressAcceptance.Open();
        const string text = "Аз че\u200Dта.";
        var analysis = session.AnalyzeText(text, false, new TextContextOptions { Continuation = new(3) }, default);
        Assert.AreEqual(text, analysis.Text);
        Assert.IsNotNull(analysis.Continuation);
        Assert.IsFalse(analysis.Continuation.Available);
        Assert.AreEqual("finite_edit_span_unsupported", analysis.Continuation.UnavailableReason);
        Assert.IsEmpty(analysis.Continuation.Suggestions);
    }
}
