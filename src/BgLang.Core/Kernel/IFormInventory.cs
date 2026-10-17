using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>Package-bound stored-form inventory independent of source provenance.</summary>
internal interface IFormInventory
{
    IReadOnlyList<WordForm> GenerateForms(string lemma, int limit);
}
