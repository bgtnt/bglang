using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass]
public sealed class TextMatchProjectionTests
{
    [TestMethod]
    public void Literal_whole_token_matching_is_case_sensitive_and_stored_lemma_matching_includes_inflection()
    {
        WordAnalysis query = Word("книга", Reading("книга", "книга", "base-tag", "query-reading"));
        TextAnalysis text = new("Книга книга книгата книгари",
        [
            Token("Книга", 0, Word("Книга", Reading("Книга", "книга", "capital-tag"))),
            Token("книга", 6, Word("книга", Reading("книга", "книга", "base-tag"))),
            Token("книгата", 12, Word("книгата", Reading("книгата", "книга", "article-tag", "article-reading"))),
            Token("книгари", 20, Word("книгари", Reading("книгари", "книгар", "different-lemma")))
        ]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(text, result.TextAnalysis);
        Assert.AreEqual("книга", result.Query);
        Assert.AreEqual(3, result.Matches.Count, "A prefix is not a whole-token literal or lemma match.");
        Assert.IsFalse(result.Matches[0].ExactMatch, "Capitalization is significant only for literal matching.");
        Assert.IsTrue(result.Matches[1].ExactMatch);
        Assert.IsFalse(result.Matches[2].ExactMatch);
        Assert.AreEqual(12, result.Matches[2].Start);
        Assert.AreEqual(7, result.Matches[2].Length);
        Assert.AreEqual("книгата", result.Matches[2].Text);
        LemmaMatch inflected = result.Matches[2].StoredLemmaMatches.Single();
        Assert.AreEqual("книга", inflected.Lemma);
        Assert.AreEqual("query-reading", inflected.QueryEvidence.Single().Source);
        Assert.AreEqual("article-reading", inflected.TokenEvidence.Single().Source);
        CollectionAssert.AreEqual(new[] { "article-tag" }, inflected.TokenEvidence.Single().Tags.ToArray());
        Assert.AreEqual(0, result.Matches[2].EvidenceLemmaMatches.Count);
        Assert.IsFalse(result.Matches[2].Unknown);
    }

    [TestMethod]
    public void Every_ambiguous_query_lemma_can_match_without_selecting_a_contextual_winner()
    {
        WordAnalysis query = Word("пила",
            Reading("пила", "пила", "noun-tag", "noun-query"),
            Reading("пила", "пия", "verb-tag", "verb-query"));
        TextAnalysis text = new("пили пилата",
        [
            Token("пили", 0, Word("пили", Reading("пили", "пия", "verb-plural", "verb-token"))),
            Token("пилата", 5, Word("пилата", Reading("пилата", "пила", "noun-article", "noun-token")))
        ]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.IsTrue(result.QueryAmbiguous);
        CollectionAssert.AreEqual(new[] { "пила", "пия" }, result.QueryLemmaEvidence.Select(item => item.Lemma).ToArray());
        Assert.AreEqual(2, result.Matches.Count);
        Assert.AreEqual("пия", result.Matches[0].StoredLemmaMatches.Single().Lemma);
        Assert.AreEqual("verb-query", result.Matches[0].StoredLemmaMatches.Single().QueryEvidence.Single().Source);
        Assert.AreEqual("пила", result.Matches[1].StoredLemmaMatches.Single().Lemma);
        Assert.AreEqual("noun-query", result.Matches[1].StoredLemmaMatches.Single().QueryEvidence.Single().Source);
        Assert.IsTrue(result.Matches[0].Ambiguous);
        Assert.IsTrue(result.Matches[1].Ambiguous);
        Assert.IsFalse(result.Matches[0].ExactMatch);
        Assert.IsFalse(result.Matches[1].ExactMatch);
    }

    [TestMethod]
    public void Unknown_exact_surface_matches_without_becoming_a_dictionary_reading()
    {
        WordAnalysis query = Word("щщ");
        WordAnalysis unknown = Word("щщ");
        TextAnalysis text = new("щщ щщщ", [Token("щщ", 0, unknown), Token("щщщ", 3, Word("щщщ"))]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        TextMatch match = result.Matches.Single();
        Assert.IsTrue(match.ExactMatch);
        Assert.IsTrue(match.Unknown);
        Assert.AreEqual(0, match.StoredLemmaMatches.Count);
        Assert.AreEqual(0, match.EvidenceLemmaMatches.Count);
        Assert.AreEqual(0, result.QueryLemmaEvidence.Count);
        Assert.AreSame(unknown, result.TextAnalysis.Tokens[match.TokenIndex].Analysis);
        Assert.IsFalse(match.Conditional);
        Assert.IsFalse(match.Incomplete);
    }

    [TestMethod]
    public void Graph_matches_use_derived_lemma_instead_of_the_root_base()
    {
        WordAnalysis query = Word("супермравка") with
        { GraphCandidates = [Path("супермравка", "мравка", "супермравка", source: "query-path")] };
        TextAnalysis text = new("мравката супермравките",
        [
            Token("мравката", 0, Word("мравката", Reading("мравката", "мравка"))),
            Token("супермравките", 9, Word("супермравките") with
            { GraphCandidates = [Path("супермравките", "мравка", "супермравка", source: "token-path")] })
        ]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.AreEqual("супермравка", result.QueryLemmaEvidence.Single().Lemma);
        TextMatch match = result.Matches.Single();
        Assert.AreEqual(1, match.TokenIndex);
        Assert.AreEqual("супермравките", match.Text);
        Assert.AreEqual(0, match.StoredLemmaMatches.Count);
        LemmaMatch evidence = match.EvidenceLemmaMatches.Single();
        Assert.AreEqual("супермравка", evidence.Lemma);
        Assert.AreEqual(LemmaEvidenceKind.GraphPath, evidence.QueryEvidence.Single().Kind);
        Assert.AreEqual("query-path", evidence.QueryEvidence.Single().Source);
        Assert.AreEqual("token-path", evidence.TokenEvidence.Single().Source);
        Assert.IsTrue(match.Unknown);
        Assert.IsFalse(match.Conditional);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    public void Missing_derived_lemma_never_promotes_a_graph_root_to_a_lemma(string? derivedLemma)
    {
        WordAnalysis graph = Word("супермравка") with
        { GraphCandidates = [Path("супермравка", "мравка", derivedLemma, true)] };
        WordAnalysis stored = Word("мравка", Reading("мравка", "мравка"));
        TextMatchResult queryGraph = TextMatchProjection.Find(graph, new("мравка", [Token("мравка", 0, stored)]));
        TextMatchResult tokenGraph = TextMatchProjection.Find(stored, new("супермравка", [Token("супермравка", 0, graph)]));

        Assert.AreEqual(0, queryGraph.QueryLemmaEvidence.Count);
        Assert.AreEqual(0, queryGraph.Matches.Count);
        Assert.AreEqual(0, tokenGraph.Matches.Count);
        Assert.AreSame(graph, queryGraph.QueryAnalysis);
        Assert.AreSame(graph, tokenGraph.TextAnalysis.Tokens[0].Analysis);
        Assert.IsTrue(queryGraph.QueryAnalysis.GraphCandidates.Single().IsConditional);
        Assert.IsTrue(tokenGraph.TextAnalysis.Tokens[0].Analysis!.GraphCandidates.Single().IsConditional);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Matched_conditional_query_or_token_evidence_remains_a_hypothesis(bool conditionalQuery)
    {
        WordAnalysis query = Word("новак") with
        { GraphCandidates = [Path("новак", "нов", "новак", conditionalQuery, "query-path")] };
        WordAnalysis token = Word("новаците") with
        { GraphCandidates = [Path("новаците", "нов", "новак", !conditionalQuery, "token-path")] };

        TextAnalysis text = new("новаците", [Token("новаците", 0, token)]);
        TextMatchResult result = TextMatchProjection.Find(query, text);
        TextMatch match = result.Matches.Single();

        Assert.IsTrue(match.Conditional);
        Assert.IsTrue(match.Unknown);
        Assert.AreEqual(0, match.StoredLemmaMatches.Count);
        LemmaMatch evidence = match.EvidenceLemmaMatches.Single();
        Assert.AreEqual("новак", evidence.Lemma);
        Assert.AreEqual(conditionalQuery, evidence.QueryEvidence.Single().IsConditional);
        Assert.AreEqual(!conditionalQuery, evidence.TokenEvidence.Single().IsConditional);
        Assert.AreEqual("query-path", evidence.QueryEvidence.Single().Source);
        Assert.AreEqual("token-path", evidence.TokenEvidence.Single().Source);
        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(text, result.TextAnalysis);
        Assert.AreEqual(conditionalQuery, result.QueryAnalysis.GraphCandidates.Single().IsConditional);
        Assert.AreEqual(!conditionalQuery, result.TextAnalysis.Tokens[0].Analysis!.GraphCandidates.Single().IsConditional);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Unrelated_conditional_query_or_token_lemma_does_not_mark_a_stored_match_conditional(bool conditionalQuery)
    {
        WordAnalysis query = Word("книга", Reading("книга", "книга"));
        WordAnalysis token = Word("книгата", Reading("книгата", "книга"));
        WordDerivation unrelated = Path(conditionalQuery ? "книга" : "книгата", "друг", "друга", true, "unrelated-conditional");
        if (conditionalQuery) query = query with { GraphCandidates = [unrelated] };
        else token = token with { GraphCandidates = [unrelated] };
        TextAnalysis text = new("книгата", [Token("книгата", 0, token)]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        TextMatch match = result.Matches.Single();
        Assert.AreEqual("книга", match.StoredLemmaMatches.Single().Lemma);
        Assert.AreEqual(0, match.EvidenceLemmaMatches.Count);
        Assert.IsFalse(match.Conditional, "The conditional lemma 'друга' did not participate in this match.");
        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(token, result.TextAnalysis.Tokens[0].Analysis);
        WordAnalysis conditionalAnalysis = conditionalQuery ? result.QueryAnalysis : result.TextAnalysis.Tokens[0].Analysis!;
        Assert.AreSame(unrelated, conditionalAnalysis.GraphCandidates.Single());
        Assert.IsTrue(conditionalAnalysis.GraphCandidates.Single().IsConditional);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Conditional_path_without_derived_lemma_remains_raw_evidence_without_a_conditional_literal_match(bool conditionalQuery)
    {
        WordAnalysis query = Word("новак");
        WordAnalysis token = Word("новак");
        WordDerivation raw = Path("новак", "нов", null, true, "conditional-without-lemma");
        if (conditionalQuery) query = query with { GraphCandidates = [raw] };
        else token = token with { GraphCandidates = [raw] };
        TextAnalysis text = new("новак", [Token("новак", 0, token)]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        TextMatch match = result.Matches.Single();
        Assert.IsTrue(match.ExactMatch);
        Assert.IsTrue(match.Unknown);
        Assert.IsFalse(match.Conditional);
        Assert.AreEqual(0, match.StoredLemmaMatches.Count);
        Assert.AreEqual(0, match.EvidenceLemmaMatches.Count);
        Assert.AreEqual(0, result.QueryLemmaEvidence.Count);
        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(text, result.TextAnalysis);
        WordAnalysis conditionalAnalysis = conditionalQuery ? result.QueryAnalysis : result.TextAnalysis.Tokens[0].Analysis!;
        Assert.AreSame(raw, conditionalAnalysis.GraphCandidates.Single());
        Assert.IsTrue(conditionalAnalysis.GraphCandidates.Single().IsConditional);
        Assert.IsNull(conditionalAnalysis.GraphCandidates.Single().DerivedLemma);
    }

    [TestMethod]
    [DataRow("GraphSearchIncomplete")]
    [DataRow("GraphCandidatesTruncated")]
    [DataRow("CandidateSearchLimited")]
    [DataRow("CandidateEvidenceLimited")]
    [DataRow("KnownBaseSearchLimited")]
    [DataRow("BaseTypeSearchLimited")]
    [DataRow("BaseTypeEvidenceLimited")]
    public void Each_public_limit_flag_on_either_analysis_marks_matches_incomplete(string flag)
    {
        WordAnalysis clean = Word("книга", Reading("книга", "книга"));
        WordAnalysis limited = flag switch
        {
            "GraphSearchIncomplete" => clean with { GraphSearchIncomplete = true },
            "GraphCandidatesTruncated" => clean with { GraphCandidatesTruncated = true },
            "CandidateSearchLimited" => clean with { CandidateSearchLimited = true },
            "CandidateEvidenceLimited" => clean with { CandidateEvidenceLimited = true },
            "KnownBaseSearchLimited" => clean with { KnownBaseSearchLimited = true },
            "BaseTypeSearchLimited" => clean with { BaseTypeSearchLimited = true },
            "BaseTypeEvidenceLimited" => clean with { BaseTypeEvidenceLimited = true },
            _ => throw new ArgumentOutOfRangeException(nameof(flag))
        };
        TextMatchResult queryLimited = TextMatchProjection.Find(limited, new("книга", [Token("книга", 0, clean)]));
        TextMatchResult tokenLimited = TextMatchProjection.Find(clean, new("книга", [Token("книга", 0, limited)]));

        Assert.IsTrue(queryLimited.Matches.Single().Incomplete, "Query: " + flag);
        Assert.IsTrue(tokenLimited.Matches.Single().Incomplete, "Token: " + flag);
        Assert.AreSame(limited, queryLimited.QueryAnalysis);
        Assert.AreSame(limited, tokenLimited.TextAnalysis.Tokens[0].Analysis);
        Assert.IsFalse(TextMatchProjection.Find(clean, new("книга", [Token("книга", 0, clean)])).Matches.Single().Incomplete);
    }

    [TestMethod]
    public void Every_query_candidate_lane_and_shorter_child_supplies_its_own_lemma_and_source_not_donor_lemmas()
    {
        WordAnalysis query = Word("нова") with
        {
            Candidates = [Candidate("нова", "нов", "morph-tag", "morph-source")],
            KnownBaseCandidates = [Candidate("нова", "корен", "known-tag", "known-source")],
            BaseTypeCandidates = [Candidate("нова", "новак", "base-tag", "base-source") with
            { ShorterEndingCandidates = [Candidate("нова", "новица", "shorter-tag", "shorter-source")] }]
        };
        TextAnalysis text = new("нови корени новаци новици донори",
        [
            Token("нови", 0, Word("нови", Reading("нови", "нов"))),
            Token("корени", 5, Word("корени", Reading("корени", "корен"))),
            Token("новаци", 12, Word("новаци", Reading("новаци", "новак"))),
            Token("новици", 19, Word("новици", Reading("новици", "новица"))),
            Token("донори", 26, Word("донори", Reading("донори", "донор")))
        ]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.AreEqual(4, result.Matches.Count, "Supporting donor lemmas do not identify the candidate's lemma.");
        CollectionAssert.AreEqual(new[] { "корен", "нов", "новак", "новица" }, result.QueryLemmaEvidence.Select(item => item.Lemma).ToArray());
        LemmaEvidence morphology = result.Matches[0].EvidenceLemmaMatches.Single().QueryEvidence.Single();
        Assert.AreEqual(LemmaEvidenceKind.MorphologyCandidate, morphology.Kind);
        Assert.AreEqual("morph-source", morphology.Source);
        LemmaEvidence known = result.Matches[1].EvidenceLemmaMatches.Single().QueryEvidence.Single();
        Assert.AreEqual(LemmaEvidenceKind.KnownBaseCandidate, known.Kind);
        Assert.AreEqual("known-source", known.Source);
        LemmaEvidence baseType = result.Matches[2].EvidenceLemmaMatches.Single().QueryEvidence.Single();
        Assert.AreEqual(LemmaEvidenceKind.BaseTypeCandidate, baseType.Kind);
        Assert.AreEqual("base-source", baseType.Source);
        LemmaEvidence shorter = result.Matches[3].EvidenceLemmaMatches.Single().QueryEvidence.Single();
        Assert.AreEqual(LemmaEvidenceKind.BaseTypeCandidate, shorter.Kind);
        Assert.AreEqual("shorter-source", shorter.Source);
        CollectionAssert.AreEqual(new[] { "shorter-tag" }, shorter.Tags.ToArray());
        Assert.IsTrue(result.Matches.All(match => match.StoredLemmaMatches.Count == 0));
        Assert.IsTrue(result.Matches.All(match => match.EvidenceLemmaMatches.Single().TokenEvidence.Single().Kind == LemmaEvidenceKind.StoredReading));
    }

    [TestMethod]
    [DataRow("MorphologyCandidate")]
    [DataRow("KnownBaseCandidate")]
    [DataRow("BaseTypeCandidate")]
    [DataRow("BaseTypeShorterChild")]
    public void Token_candidate_lanes_match_stored_query_without_becoming_stored_matches(string lane)
    {
        WordAnalysis query = Word("основа", Reading("основа", "основа"));
        MorphologyCandidate evidence = Candidate("основите", "основа", "token-tag", "token-source");
        WordAnalysis token = lane switch
        {
            "MorphologyCandidate" => Word("основите") with { Candidates = [evidence] },
            "KnownBaseCandidate" => Word("основите") with { KnownBaseCandidates = [evidence] },
            "BaseTypeCandidate" => Word("основите") with { BaseTypeCandidates = [evidence] },
            "BaseTypeShorterChild" => Word("основите") with
            { BaseTypeCandidates = [Candidate("основите", "друга", "parent-tag", "parent-source") with { ShorterEndingCandidates = [evidence] }] },
            _ => throw new ArgumentOutOfRangeException(nameof(lane))
        };

        TextMatch match = TextMatchProjection.Find(query, new("основите", [Token("основите", 0, token)])).Matches.Single();

        Assert.AreEqual(0, match.StoredLemmaMatches.Count);
        Assert.IsTrue(match.Unknown);
        Assert.IsFalse(match.ExactMatch);
        LemmaMatch common = match.EvidenceLemmaMatches.Single();
        Assert.AreEqual("основа", common.Lemma);
        Assert.AreEqual(LemmaEvidenceKind.StoredReading, common.QueryEvidence.Single().Kind);
        LemmaEvidence tokenEvidence = common.TokenEvidence.Single();
        Assert.AreEqual(lane == "BaseTypeShorterChild" ? "BaseTypeCandidate" : lane, tokenEvidence.Kind.ToString());
        Assert.AreEqual("token-source", tokenEvidence.Source);
        CollectionAssert.AreEqual(new[] { "token-tag" }, tokenEvidence.Tags.ToArray());
    }

    [TestMethod]
    public void Stored_and_hypothetical_evidence_for_the_same_lemma_remain_separate()
    {
        WordAnalysis query = Word("новак", Reading("новак", "новак", source: "query-stored")) with
        { GraphCandidates = [Path("новак", "нов", "новак", source: "query-graph")] };
        WordAnalysis token = Word("новаци", Reading("новаци", "новак", source: "token-stored")) with
        { GraphCandidates = [Path("новаци", "нов", "новак", true, "token-graph")] };

        TextMatch match = TextMatchProjection.Find(query, new("новаци", [Token("новаци", 0, token)])).Matches.Single();

        LemmaMatch stored = match.StoredLemmaMatches.Single();
        Assert.AreEqual("query-stored", stored.QueryEvidence.Single().Source);
        Assert.AreEqual("token-stored", stored.TokenEvidence.Single().Source);
        LemmaMatch hypotheses = match.EvidenceLemmaMatches.Single();
        CollectionAssert.AreEqual(new[] { "query-stored", "query-graph" }, hypotheses.QueryEvidence.Select(item => item.Source).ToArray());
        CollectionAssert.AreEqual(new[] { "token-stored", "token-graph" }, hypotheses.TokenEvidence.Select(item => item.Source).ToArray());
        Assert.IsTrue(hypotheses.TokenEvidence[1].IsConditional);
        Assert.IsTrue(match.Conditional);
        Assert.IsFalse(match.Unknown);
    }

    [TestMethod]
    public void Utf16_offsets_preserve_emoji_cyrillic_crlf_stress_and_abbreviation_source_spelling()
    {
        WordAnalysis abbreviation = new("т.н.", "т.н.", [Reading("т.н.", "т.н.")], null);
        TextAnalysis text = new("🙂 Книга\r\nт. н. книга\u0301",
        [
            new("🙂", TokenKind.Symbol, 0, 2, null),
            Token("Книга", 3, Word("Книга", Reading("Книга", "книга"))),
            Token("т. н.", 10, abbreviation),
            Token("книга\u0301", 16, Word("книга\u0301", Reading("книга", "книга")))
        ]);
        WordAnalysis query = Word("книга", Reading("книга", "книга"));

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.AreEqual(22, text.Text.Length);
        Assert.AreEqual(2, result.Matches.Count);
        Assert.AreEqual(3, result.Matches[0].Start);
        Assert.AreEqual(5, result.Matches[0].Length);
        Assert.AreEqual(16, result.Matches[1].Start);
        Assert.AreEqual(6, result.Matches[1].Length);
        Assert.IsFalse(result.Matches[0].ExactMatch);
        Assert.IsFalse(result.Matches[1].ExactMatch);
        Assert.AreEqual("Книга", text.Text.Substring(result.Matches[0].Start, result.Matches[0].Length));
        Assert.AreEqual("книга\u0301", text.Text.Substring(result.Matches[1].Start, result.Matches[1].Length));
        WordAnalysis abbreviationQuery = Word("т. н.", Reading("т.н.", "т.н."));
        TextMatchResult abbreviationResult = TextMatchProjection.Find(abbreviationQuery, text);
        TextMatch match = abbreviationResult.Matches.Single();
        Assert.IsTrue(match.ExactMatch, "Literal spelling comes from token.Text, not its canonical analysis.Word.");
        Assert.AreEqual(10, match.Start);
        Assert.AreEqual(5, match.Length);
        Assert.AreEqual("т. н.", match.Text);
        Assert.AreSame(abbreviation, abbreviationResult.TextAnalysis.Tokens[match.TokenIndex].Analysis);
        Assert.AreSame(abbreviationQuery, abbreviationResult.QueryAnalysis);
        Assert.AreSame(text, abbreviationResult.TextAnalysis);
    }

    [TestMethod]
    [DataRow(-1, 5, "книга")]
    [DataRow(0, 0, "книга")]
    [DataRow(0, 6, "книга")]
    [DataRow(1, 5, "книга")]
    [DataRow(0, 4, "книга")]
    [DataRow(0, 5, "друга")]
    public void Malformed_word_offsets_are_rejected_even_when_no_lemma_matches(int start, int length, string surface)
    {
        TextAnalysis text = new("книга", [new(surface, TokenKind.Word, start, length, Word(surface))]);

        Assert.Throws<InvalidOperationException>(() => TextMatchProjection.Find(Word("несъвпадаща"), text));
    }

    [TestMethod]
    public void More_than_32_matching_tokens_are_retained_in_source_order()
    {
        WordAnalysis query = Word("книга", Reading("книга", "книга"));
        TokenAnalysis[] tokens = Enumerable.Range(0, 40).Select(index => Token("книга", index * 6, query)).ToArray();
        TextAnalysis text = new(string.Join(" ", Enumerable.Repeat("книга", 40)), tokens);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.AreEqual(40, result.Matches.Count, "The public per-word evidence bound is not a token-match bound.");
        Assert.AreEqual(0, result.Matches[0].TokenIndex);
        Assert.AreEqual(0, result.Matches[0].Start);
        Assert.AreEqual(39, result.Matches[^1].TokenIndex);
        Assert.AreEqual(234, result.Matches[^1].Start);
        Assert.AreEqual(5, result.Matches[^1].Length);
        Assert.IsTrue(result.Matches.All(match => match.ExactMatch));
        Assert.IsTrue(result.Matches.All(match => match.StoredLemmaMatches.Count == 1));
        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(text, result.TextAnalysis);
    }

    [TestMethod]
    public void Comparison_rows_follow_word_view_lane_order_without_reranking_within_a_lane()
    {
        WordAnalysis query = Word("форма", Reading("форма", "stored")) with
        {
            GraphCandidates = [Path("форма", "root", "graph")],
            Candidates = [Candidate("форма", "morph-first", "first-tag", "first-source"),
                Candidate("форма", "morph-second", "second-tag", "second-source")],
            KnownBaseCandidates = [Candidate("форма", "known", "known-tag", "known-source")],
            BaseTypeCandidates = [Candidate("форма", "base", "base-tag", "base-source")]
        };
        TextAnalysis text = new("форма", [Token("форма", 0, Word("форма"))]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        CollectionAssert.AreEqual(new[] { "stored", "known", "morph-first", "morph-second", "base", "graph" },
            result.QueryLemmaEvidence.Select(row => row.Lemma).ToArray());
        CollectionAssert.AreEqual(new[] { LemmaEvidenceKind.StoredReading, LemmaEvidenceKind.KnownBaseCandidate,
            LemmaEvidenceKind.MorphologyCandidate, LemmaEvidenceKind.MorphologyCandidate,
            LemmaEvidenceKind.BaseTypeCandidate, LemmaEvidenceKind.GraphPath },
            result.QueryLemmaEvidence.Select(row => row.Kind).ToArray());
        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(text, result.TextAnalysis);
    }

    [TestMethod]
    public void Exact_repeated_presentation_rows_collapse_in_query_and_token_without_changing_raw_analyses()
    {
        MorphologyCandidate row = Candidate("форма", "лема", "tag", "source");
        WordAnalysis query = Word("форма") with
        { Candidates = [row, row with { }, row with { ShorterEndingCandidates = [row with { }] }] };
        WordAnalysis token = Word("форми") with { Candidates = [row with { }, row with { }, row with { }] };
        TextAnalysis text = new("форми", [Token("форми", 0, token)]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.AreEqual(1, result.QueryLemmaEvidence.Count);
        TextMatch match = result.Matches.Single();
        LemmaMatch hypothesis = match.EvidenceLemmaMatches.Single();
        Assert.AreEqual(1, hypothesis.QueryEvidence.Count);
        Assert.AreEqual(1, hypothesis.TokenEvidence.Count);
        Assert.AreEqual(0, match.StoredLemmaMatches.Count);
        Assert.IsTrue(match.Unknown);
        Assert.IsFalse(match.Conditional);
        Assert.IsFalse(match.Incomplete);
        Assert.IsFalse(match.Ambiguous);
        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(token, result.TextAnalysis.Tokens[0].Analysis);
        Assert.AreEqual(3, result.QueryAnalysis.Candidates.Count);
        Assert.AreEqual(1, result.QueryAnalysis.Candidates[2].ShorterEndingCandidates.Count);
        Assert.AreEqual(3, result.TextAnalysis.Tokens[0].Analysis!.Candidates.Count);
    }

    [TestMethod]
    [DataRow("lemma")]
    [DataRow("kind")]
    [DataRow("tag")]
    [DataRow("tag-order")]
    [DataRow("tag-count")]
    [DataRow("source")]
    [DataRow("conditional")]
    public void Distinct_tag_provenance_kind_lemma_and_conditional_rows_are_never_merged(string difference)
    {
        WordDerivation first = Path("форма", "root", "лема", source: "source") with { FullTags = ["tag-a", "tag-b"] };
        WordDerivation second = first with { FullTags = ["tag-a", "tag-b"] };
        second = difference switch
        {
            "lemma" => second with { DerivedLemma = "друга" },
            "tag" => second with { FullTags = ["tag-a", "tag-c"] },
            "tag-order" => second with { FullTags = ["tag-b", "tag-a"] },
            "tag-count" => second with { FullTags = ["tag-a", "tag-b", ""] },
            "source" => second with { Source = "other-source" },
            "conditional" => second with { IsConditional = true },
            _ => second
        };
        WordAnalysis query = difference == "kind"
            ? Word("форма", Reading("форма", "лема", "tag-a", "source")) with
                { GraphCandidates = [first with { FullTags = ["tag-a"] }] }
            : Word("форма") with { GraphCandidates = [first, second] };
        TextAnalysis text = new("форма", [Token("форма", 0, Word("форма"))]);

        TextMatchResult result = TextMatchProjection.Find(query, text);

        Assert.AreEqual(2, result.QueryLemmaEvidence.Count, difference);
        Assert.AreSame(query, result.QueryAnalysis);
        Assert.AreSame(text, result.TextAnalysis);
        if (difference == "conditional") Assert.IsTrue(result.QueryLemmaEvidence.Any(row => row.IsConditional));
        if (difference == "source") CollectionAssert.AreEqual(new[] { "source", "other-source" },
            result.QueryLemmaEvidence.Select(row => row.Source).ToArray());
        if (difference == "kind") CollectionAssert.AreEqual(new[] { LemmaEvidenceKind.StoredReading, LemmaEvidenceKind.GraphPath },
            result.QueryLemmaEvidence.Select(row => row.Kind).ToArray());
    }

    private static WordAnalysis Word(string surface, params WordReading[] readings) => new(surface, surface, readings, null);

    private static WordReading Reading(string form, string lemma, string tag = "stored-tag", string source = "stored-source") =>
        new(form, lemma, tag, 1, null, null, null, source);

    private static TokenAnalysis Token(string surface, int start, WordAnalysis analysis) => new(surface, TokenKind.Word, start, surface.Length, analysis);

    private static WordDerivation Path(string surface, string root, string? derivedLemma, bool conditional = false, string source = "graph-source") =>
        new(surface, root, null, null, [], [], [], [], 1, [], source)
        { DerivedLemma = derivedLemma, FullTags = ["graph-tag"], IsConditional = conditional };

    private static MorphologyCandidate Candidate(string surface, string lemma, string tag, string source) =>
        new(surface, lemma, "candidate-base-tag", tag, "candidate-program", 1, ["донор"], source);
}
