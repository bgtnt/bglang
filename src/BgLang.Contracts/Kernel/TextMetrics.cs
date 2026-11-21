namespace BgLang.Contracts;

/// <summary>Descriptive counts and ratios over raw tokens; no quality, correctness or confidence score.</summary>
public sealed record TextMetrics(
    int TokenCount,                         // token.count: every emitted token
    int WordCount,                          // word.count: all Word occurrences
    int SentenceCount,                      // sentence.count: supplied sentence spans
    double AverageWordsPerSentence,         // avg.words.per.sentence: W / max(1, S)
    double AverageWordLength,               // avg.word.length: raw UTF-16 Word lengths / W
    double TypeTokenRatio,                  // type.token.ratio: raw OrdinalIgnoreCase types / W
    int PunctuationCount,                   // punctuation.count: only Punctuation tokens
    int NormalizedRepetitionExtraCount,     // repetition.normalized.extra.count: all repeats after first
    double UnknownWordRatio);               // unknown.word.ratio: exact-unknown non-abbreviations / W
