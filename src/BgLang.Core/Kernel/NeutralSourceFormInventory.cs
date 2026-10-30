using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Content-bound stored forms grouped by neutral source lemma.</summary>
internal sealed class NeutralSourceFormInventory : IFormInventory
{
    private readonly IReadOnlyDictionary<string, WordForm[]> _formsByLemma;

    internal NeutralSourceFormInventory(IReadOnlyDictionary<string, WordForm[]> formsByLemma) =>
        _formsByLemma = formsByLemma;

    public IReadOnlyList<WordForm> GenerateForms(string lemma, int limit)
    {
        if (string.IsNullOrWhiteSpace(lemma) || limit <= 0)
            return [];

        string normalized = BulgarianTextNormalizer.NormalizeKey(lemma);
        return _formsByLemma.TryGetValue(normalized, out WordForm[]? forms)
            ? forms.AsSpan(0, Math.Min(limit, forms.Length)).ToArray()
            : [];
    }
}
