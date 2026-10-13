using System.Diagnostics.Tracing;

namespace BgLang.Core;

/// <summary>Coarse opt-in boundaries for measuring the actual public package-open path.</summary>
[EventSource(Name = "BgLang-PackageLoad")]
internal sealed class PackageLoadEvents : EventSource
{
    internal static readonly PackageLoadEvents Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Phase(string phase)
    {
        if (IsEnabled()) WriteEvent(1, phase);
    }
}
