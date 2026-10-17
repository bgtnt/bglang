using static BgLang.Core.WordFormationGraph;

namespace BgLang.Core;

/// <summary>Bounded retention uses the same tiers and semantic diversity as final ranking.</summary>
internal sealed class BoundedGraphCandidatePool
{
    private readonly int _limit;
    private readonly CandidateRanking _ranking;
    private readonly IComparer<PathCandidate> _order;
    private readonly SortedSet<PathCandidate> _items;
    private readonly SortedSet<PathCandidate> _secondary;
    private readonly HashSet<CandidateKey> _keys = [];
    private readonly Dictionary<((int Depth, bool Conditional) Tier, CandidateGroupKey Group), PathCandidate> _primaryByGroup = [];

    internal BoundedGraphCandidatePool(int limit, CandidateRanking ranking = CandidateRanking.Inverse)
    {
        _limit = limit;
        _ranking = ranking;
        _order = OrderFor(ranking);
        _items = new(_order);
        _secondary = new(_order);
    }

    private ((int Depth, bool Conditional) Tier, CandidateGroupKey Group) GroupFor(PathCandidate candidate) =>
        (_ranking == CandidateRanking.Forward ? TierFor(candidate, _ranking) : default, candidate.GroupIdentity);

    internal IEnumerable<PathCandidate> Values => _items;
    internal bool Lost { get; private set; }

    internal void Add(PathCandidate candidate)
    {
        if (!_keys.Add(candidate.Identity))
            return;
        var group = GroupFor(candidate);
        if (!_primaryByGroup.TryGetValue(group, out PathCandidate? primary))
            _primaryByGroup[group] = candidate;
        else if (_order.Compare(candidate, primary) < 0)
        {
            _secondary.Add(primary);
            _primaryByGroup[group] = candidate;
        }
        else
            _secondary.Add(candidate);
        _items.Add(candidate);
        if (_items.Count <= _limit)
            return;
        Lost = true;
        PathCandidate worst = _items.Max!;
        if (_secondary.Max is { } secondary && TierFor(secondary, _ranking) == TierFor(worst, _ranking))
            worst = secondary;
        if (!_secondary.Remove(worst))
            _primaryByGroup.Remove(GroupFor(worst));
        _keys.Remove(worst.Identity);
        _items.Remove(worst);
    }
}

