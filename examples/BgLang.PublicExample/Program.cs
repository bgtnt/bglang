using System;
using System.Threading;
using BgLang.Api;
using BgLang.Contracts;

if (args.Length != 1)
    throw new ArgumentException("Pass the path to a complete package manifest.json.");

using var language = BulgarianLanguageSession.Open(args[0]);
Console.WriteLine($"Package: {language.Provenance.PackageId}");

foreach (WordReading reading in language.Lookup("книгата", LookupMode.Exact, 16))
    Console.WriteLine($"{reading.Form}\t{reading.Lemma}\t{reading.PartOfSpeech}");

WordAnalysis word = language.AnalyzeWord("книгата", 32, CancellationToken.None);
Console.WriteLine($"Normalized: {word.NormalizedWord}; known: {word.IsKnown}");
Console.WriteLine($"Graph incomplete: {word.GraphSearchIncomplete}; graph clipped: {word.GraphCandidatesTruncated}");
Console.WriteLine($"Morphology work/evidence limited: {word.CandidateSearchLimited}/{word.CandidateEvidenceLimited}");
Console.WriteLine($"Known-base work limited: {word.KnownBaseSearchLimited}");
Console.WriteLine($"Base-type work/evidence limited: {word.BaseTypeSearchLimited}/{word.BaseTypeEvidenceLimited}");

foreach (WordForm form in language.GenerateForms("книга", 256))
    Console.WriteLine($"{form.Form}\t{form.Tag}");

TextAnalysis text = language.AnalyzeText("Книгата е на масата.", CancellationToken.None);
Console.WriteLine($"Tokens: {text.Tokens.Count}; diagnostics: {text.Diagnostics.Count}");
