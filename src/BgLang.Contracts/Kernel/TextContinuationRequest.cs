using System.Text.Json.Serialization;

namespace BgLang.Contracts;

/// <summary>Opt-in dictionary continuations at an original UTF-16 caret; finite remains the default.</summary>
public sealed record TextContinuationRequest(int Caret, int Limit = 12)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public TextContinuationTarget Target { get; init; }
}
