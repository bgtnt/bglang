using System.Diagnostics;
using System.Diagnostics.Tracing;
using BgLang.Api;

namespace BgLang.SourceBuilder;

/// <summary>One public Open in the caller's fresh process; no queries or forced GC enter its measurements.</summary>
internal static class PackageOpenMeasurement
{
    internal static object Run(string manifest)
    {
        using var listener = new PhaseListener();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long beforeWorking = process.WorkingSet64, beforePeak = process.PeakWorkingSet64;
        long started = Stopwatch.GetTimestamp();
        using BulgarianLanguageSession session = BulgarianLanguageSession.Open(manifest);
        long finished = Stopwatch.GetTimestamp();
        process.Refresh();
        long peak = process.PeakWorkingSet64, working = process.WorkingSet64, privateBytes = process.PrivateMemorySize64;
        double total = Stopwatch.GetElapsedTime(started, finished).TotalMilliseconds;
        var phases = listener.Durations();
        return new
        {
            schemaVersion = "bglang-package-open-measurement.v1",
            processId = Environment.ProcessId,
            manifest = Path.GetFullPath(manifest),
            session.Provenance,
            openMilliseconds = total,
            phaseMilliseconds = phases,
            outsideLoaderMilliseconds = total - phases.Values.Sum(),
            peakWorkingSetBytes = peak,
            workingSetAfterOpenBytes = working,
            privateBytesAfterOpen = privateBytes,
            beforeOpenWorkingSetBytes = beforeWorking,
            beforeOpenPeakWorkingSetBytes = beforePeak,
            runtime = Environment.Version.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            note = "Fresh process only; OS file cache is not cleared. Read includes strict parsing, hashing and source identity; generate includes complete replay digest validation; materialize includes reading validation/order and form inventory; index includes forward/suffix/routed indexes. Phase comparison is direct for v3 sources; v1/v2 also materialize during read. Totals include EventSource initialization/callback overhead, without an estimated correction. No query, forced GC or output serialization is inside Open."
        };
    }

    private sealed class PhaseListener : EventListener
    {
        private readonly List<(string Phase, long Tick)> _points = [];

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "BgLang-PackageLoad") EnableEvents(source, EventLevel.Informational);
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId == 1 && args.Payload is { Count: 1 } && args.Payload[0] is string phase)
                _points.Add((phase, Stopwatch.GetTimestamp()));
        }

        internal Dictionary<string, double> Durations()
        {
            string[] expected = ["read", "graph", "generate", "materialize", "validate", "index", "complete"];
            if (!_points.Select(point => point.Phase).SequenceEqual(expected))
                throw new InvalidDataException("Expected one fully instrumented loader call; phase evidence is incomplete.");
            Dictionary<string, double> result = new(StringComparer.Ordinal);
            for (int index = 0; index < _points.Count - 1; index++)
                result.Add(_points[index].Phase, Stopwatch.GetElapsedTime(_points[index].Tick, _points[index + 1].Tick).TotalMilliseconds);
            return result;
        }
    }
}
