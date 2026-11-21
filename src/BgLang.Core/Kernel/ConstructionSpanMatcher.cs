using System;
using System.Collections.Generic;
using System.Threading;
using BgLang.Contracts;

namespace BgLang.Core;

// Owner membership and the selected lookup route are supplied by the request acquisition layer.
internal static class ConstructionSpanMatcher
{
    internal sealed record BoundCandidate(int CandidateIndex, int AnchorTokenIndex,
        int CompleteReadingIndex, WordReading Reading, WordConstructionCandidate Candidate);

    internal static IReadOnlyList<WordConstructionMatch> Find(
        string text, IReadOnlyList<TokenAnalysis> tokens, IReadOnlyList<SentenceSpan> sentences,
        IReadOnlyList<IReadOnlyList<WordReading>> completeReadings,
        IReadOnlyList<BoundCandidate> candidates, CancellationToken cancellationToken,
        IReadOnlyList<string?>? resolvedKeys = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(sentences);
        ArgumentNullException.ThrowIfNull(completeReadings);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();
        if (completeReadings.Count != tokens.Count || resolvedKeys is not null && resolvedKeys.Count != tokens.Count)
            throw new ArgumentException("Complete readings and route keys must be token-aligned.");
        var keys = new string?[tokens.Count];
        var sentenceIds = new int[tokens.Count];
        int sentenceIndex = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TokenAnalysis token = tokens[i];
            if (token.Start < 0 || token.Start > text.Length || token.Length < 0 || token.Length > text.Length - token.Start)
                throw new ArgumentException("A token is outside the original text.", nameof(tokens));
            if (!text.AsSpan(token.Start, token.Length).SequenceEqual(token.Text.AsSpan()) ||
                i > 0 && token.Start < tokens[i - 1].Start + tokens[i - 1].Length)
                throw new ArgumentException("Tokens do not preserve the ordered raw source spans.", nameof(tokens));
            sentenceIds[i] = -1;
            if (token.Kind == TokenKind.Word)
                keys[i] = resolvedKeys is null ? BulgarianTextNormalizer.NormalizeKey(token.Text) : resolvedKeys[i];
            while (sentenceIndex < sentences.Count &&
                   (long)sentences[sentenceIndex].Start + sentences[sentenceIndex].Length <= token.Start)
                sentenceIndex++;
            if (sentenceIndex < sentences.Count && token.Start >= sentences[sentenceIndex].Start &&
                (long)token.Start + token.Length <= (long)sentences[sentenceIndex].Start + sentences[sentenceIndex].Length)
                sentenceIds[i] = sentenceIndex;
        }
        var matches = new List<WordConstructionMatch>();
        foreach (BoundCandidate bound in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WordConstructionCandidate candidate = bound.Candidate;
            int anchor = bound.AnchorTokenIndex;
            int basePart = candidate.BasePartIndex;
            if (bound.CandidateIndex < 0 || anchor < 0 || anchor >= tokens.Count ||
                keys[anchor] is not string anchorKey || anchorKey.Length == 0 || sentenceIds[anchor] < 0 ||
                candidate.Parts.Count < 2 || basePart < 0 || basePart >= candidate.Parts.Count)
                continue;
            IReadOnlyList<WordReading> readings = completeReadings[anchor];
            if (bound.CompleteReadingIndex < 0 || bound.CompleteReadingIndex >= readings.Count ||
                !ReferenceEquals(readings[bound.CompleteReadingIndex], bound.Reading) ||
                !ReferenceEquals(candidate.BaseForm.Raw, bound.Reading) ||
                !string.Equals(BulgarianTextNormalizer.NormalizeKey(bound.Reading.Form), anchorKey, StringComparison.Ordinal) ||
                !string.Equals(BulgarianTextNormalizer.NormalizeKey(candidate.Parts[basePart]), anchorKey, StringComparison.Ordinal))
                continue;
            int first = anchor - basePart;
            if (first < 0 || candidate.Parts.Count > tokens.Count - first)
                continue;
            bool matched = true;
            for (int part = 0; part < candidate.Parts.Count; part++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int index = first + part;
                string partKey = BulgarianTextNormalizer.NormalizeKey(candidate.Parts[part]);
                if (partKey.Length == 0 || sentenceIds[index] != sentenceIds[anchor] ||
                    !string.Equals(keys[index], partKey, StringComparison.Ordinal) ||
                    (part > 0 && !OnlyWhitespace(text, tokens[index - 1].Start + tokens[index - 1].Length,
                        tokens[index].Start, cancellationToken)))
                {
                    matched = false;
                    break;
                }
            }
            if (!matched) continue;
            int start = tokens[first].Start;
            TokenAnalysis last = tokens[first + candidate.Parts.Count - 1];
            matches.Add(new(bound.CandidateIndex, start, last.Start + last.Length - start,
                new TextReadingReference(anchor, bound.CompleteReadingIndex)));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(matches.ToArray());
    }

    private static bool OnlyWhitespace(string text, int start, int end, CancellationToken cancellationToken)
    {
        if (start >= end) return false;
        for (int i = start; i < end; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!char.IsWhiteSpace(text[i])) return false;
        }
        return true;
    }
}
