namespace BgLang.Core;

/// <summary>Bounded complete proofs over source-owned donor keys in one immutable graph.</summary>
internal sealed class OrdinaryBaseProofCache(WordFormationGraph graph)
{
    private readonly object _gate = new();
    private readonly byte[] _next = new byte[64];
    private Entry[]? _entries;

    internal bool IsValid(string lemma, string tag, string rule, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int bucket = (HashCode.Combine(lemma, tag, rule) & 63) * 4;
        lock (_gate)
        {
            if (TryGet(bucket, lemma, tag, rule, out bool found)) return found;
        }
        bool valid;
        try { graph.GenerateLexicalTuples(lemma, tag, rule, cancellationToken: cancellationToken); valid = true; }
        catch (InvalidDataException) { valid = false; }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (TryGet(bucket, lemma, tag, rule, out bool found)) return found;
            _entries ??= new Entry[256];
            int next = _next[bucket / 4];
            _entries[bucket + next] = new(lemma, tag, rule, valid);
            _next[bucket / 4] = (byte)((next + 1) & 3);
        }
        return valid;
    }

    private bool TryGet(int bucket, string lemma, string tag, string rule, out bool valid)
    {
        if (_entries is not null)
            for (int slot = bucket; slot < bucket + 4; slot++)
            {
                Entry entry = _entries[slot];
                if (entry.Lemma == lemma && entry.Tag == tag && entry.Rule == rule)
                { valid = entry.Valid; return true; }
            }
        valid = false;
        return false;
    }

    private readonly record struct Entry(string? Lemma, string? Tag, string? Rule, bool Valid);
}
