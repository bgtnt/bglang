using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Finite compiled-cell proofs, owned by one graph and one immutable lexicon.</summary>
internal sealed class ConditionalSupportInventory
{
    private readonly Dictionary<Cell, string[]> _supports = [];
    private readonly object _sync = new();
    internal int Count { get { lock (_sync) return _supports.Count; } }

    // No input word or path belongs here. Base is a distinct endpoint of the
    // chosen branch; full tag remains part of each realized cell's authority.
    internal readonly record struct Cell(string State, string Program, string Branch,
        int TerminalEdge, bool Base, string BaseTag, string FullTag, char? FinalConsonant = null);

    internal bool TryGetCompleted(Cell cell, out string[]? supports)
    {
        lock (_sync) return _supports.TryGetValue(cell, out supports);
    }

    internal string[]? Get(Cell cell, string ending, IRuntimeLexicon lexicon,
        GraphSearchBudget? budget, Func<WordReading, string, bool> matchesCell)
    {
        lock (_sync)
        {
            if (_supports.TryGetValue(cell, out string[]? cached)) return cached;
            HashSet<string> donors = new(StringComparer.Ordinal);
            SuffixReadingCursor cursor = lexicon.OpenSuffixCursor(ending,
                new HashSet<string>(StringComparer.Ordinal) { cell.FullTag });
            while (cursor.TryRead(out WordReading reading, budget))
            {
                string lemma = BulgarianTextNormalizer.NormalizeKey(reading.Lemma);
                if (donors.Contains(lemma)) continue;
                if (!matchesCell(reading, lemma)) continue;
                // Routed/exact-only rows cannot donate ordinary morphology.
                IReadOnlyList<WordReading> self = lexicon.LookupOrdinaryExactNormalized(lemma, int.MaxValue);
                if (self.Any(row => BulgarianTextNormalizer.NormalizeKey(row.Form) == lemma &&
                        BulgarianTextNormalizer.NormalizeKey(row.Lemma) == lemma && row.PartOfSpeech == cell.BaseTag &&
                        WordFormationGraph.TryCreateStateId(row.PartOfSpeech) == cell.State))
                    donors.Add(lemma);
            }
            if (!cursor.Complete) return null;
            string[] result = donors.Order(StringComparer.Ordinal).ToArray();
            _supports.Add(cell, result);
            return result;
        }
    }
}
