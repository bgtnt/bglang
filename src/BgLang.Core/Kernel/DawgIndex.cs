using System.Buffers;
using System.Runtime.CompilerServices;
using BgLang.Contracts;

namespace BgLang.Core;

/// <summary>
/// Immutable minimal DAWG adapted from the archive's ranked DawgWordIndex.
/// Terminal ranks address contiguous payload groups without storing output ids on graph nodes.
/// </summary>
internal sealed class DawgIndex
{
    private static readonly IComparer<WordReading> ReadingComparer = WordReadingOrder.Comparer;
    private static readonly Comparer<FuzzyCandidate> FuzzyCandidateComparer =
        Comparer<FuzzyCandidate>.Create(CompareFuzzyCandidates);
    private static readonly Comparer<FuzzyCandidate> WorstFuzzyCandidateComparer =
        Comparer<FuzzyCandidate>.Create((left, right) => CompareFuzzyCandidates(right, left));
    private readonly DawgNode[] _nodes;
    private readonly int _rootIndex;
    private readonly WordReading[] _payloads;
    private readonly int[] _payloadStarts;
    private readonly int[] _basePayloadPositions;
    private readonly Dictionary<string, int[]>? _suffixTagPositions;
    private readonly BlockRankedRangeSelector? _rangeSelector;

    internal DawgIndex(IEnumerable<WordReading> readings, bool allowEmpty = false)
        : this(readings, reverseKeys: false, allowEmpty)
    {
    }

    private DawgIndex(IEnumerable<WordReading> readings, bool reverseKeys, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(readings);

        CandidateGroup[] groups = readings
            .Where(reading => !string.IsNullOrWhiteSpace(reading.Form))
            .GroupBy(
                reading => CreateKey(reading.Form, reverseKeys),
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CandidateGroup(
                group.Key,
                group.Distinct(LexicalIdentity.ReadingEquality)
                    .OrderBy(reading => reading, ReadingComparer)
                    .ToArray()))
            .ToArray();
        if (groups.Length == 0 && !allowEmpty)
            throw new InvalidDataException("Runtime lexicon must contain at least one reading.");

        _payloadStarts = new int[groups.Length + 1];
        for (int i = 0; i < groups.Length; i++)
            _payloadStarts[i + 1] = checked(_payloadStarts[i] + groups[i].Readings.Length);

        _payloads = new WordReading[_payloadStarts[^1]];
        for (int i = 0; i < groups.Length; i++)
            groups[i].Readings.CopyTo(_payloads, _payloadStarts[i]);
        // Positions retain the existing payload order and point into the shared readings.
        _suffixTagPositions = reverseKeys ? Enumerable.Range(0, _payloads.Length)
            .Where(i => _payloads[i].PartOfSpeech is not null)
            .GroupBy(i => _payloads[i].PartOfSpeech!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal) : null;
        // Share the reversed graph and final payloads; forward/routed indexes need no base inventory.
        _basePayloadPositions = reverseKeys ? Enumerable.Range(0, _payloads.Length).Where(i =>
            BulgarianTextNormalizer.NormalizeKey(_payloads[i].Form) ==
            BulgarianTextNormalizer.NormalizeKey(_payloads[i].Lemma)).ToArray() : [];
        if (_payloads.Length >= 512)
            _rangeSelector = new BlockRankedRangeSelector(_payloads);

        MutableNode root = BuildMinimalGraph(groups);
        List<DawgNode> frozen = [];
        _rootIndex = Freeze(root, frozen, new Dictionary<MutableNode, int>(ReferenceEqualityComparer.Instance));
        _nodes = frozen.ToArray();
    }

    internal static DawgIndex CreateSuffixIndex(IEnumerable<WordReading> readings, bool allowEmpty = false) =>
        new(readings, reverseKeys: true, allowEmpty);

    internal IReadOnlyList<WordReading> AllReadings => _payloads;

    internal int WordCount => _payloadStarts.Length - 1;

    internal int NodeCount => _nodes.Length;

    internal IReadOnlyList<WordReading> LookupExact(string form, int limit)
    {
        if (string.IsNullOrWhiteSpace(form) || limit <= 0)
            return [];

        return LookupExactNormalized(BulgarianTextNormalizer.NormalizeKey(form), limit);
    }

    internal IReadOnlyList<WordReading> LookupExactNormalized(string normalizedForm, int limit)
    {
        if (normalizedForm.Length == 0 || limit <= 0)
            return [];

        TraversalResult result = Traverse(normalizedForm);
        if (result.NodeIndex < 0 || !_nodes[result.NodeIndex].IsTerminal)
            return [];

        (int start, int end) = GetPayloadRange(result.Rank);
        int count = Math.Min(limit, end - start);
        return _payloads.AsSpan(start, count).ToArray();
    }

    internal IReadOnlyList<WordReading> FindByPrefix(string prefix, int limit)
    {
        if (string.IsNullOrWhiteSpace(prefix) || limit <= 0)
            return [];

        return FindByNormalizedPrefix(BulgarianTextNormalizer.NormalizeKey(prefix), limit);
    }

    internal IReadOnlyList<WordReading> FindBySuffix(string suffix, int limit, IReadOnlySet<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(suffix) || limit <= 0)
            return [];

        return FindByNormalizedPrefix(CreateKey(suffix, reverse: true), limit, tags: tags);
    }

    internal SuffixReadingCursor OpenSuffixCursor(string suffix, IReadOnlySet<string>? tags = null)
    {
        // Only an exact ordinal HashSet can use tag positions; other filters keep their own semantics.
        int[]? positions = null;
        if (_suffixTagPositions is not null && tags is HashSet<string> requested &&
            requested.GetType() == typeof(HashSet<string>) &&
            ReferenceEquals(requested.Comparer, StringComparer.Ordinal))
        {
            if (requested.Count == 1)
            {
                string? tag = requested.First();
                if (tag is null || !_suffixTagPositions.TryGetValue(tag, out positions))
                    return new SuffixReadingCursor(Array.Empty<WordReading>());
            }
            else if (!requested.Any(tag => tag is not null && _suffixTagPositions.ContainsKey(tag)))
                return new SuffixReadingCursor(Array.Empty<WordReading>());
        }
        TraversalResult result = Traverse(CreateKey(suffix, reverse: true));
        if (result.NodeIndex < 0)
            return new SuffixReadingCursor(Array.Empty<WordReading>());
        int start = _payloadStarts[result.Rank];
        int end = _payloadStarts[result.Rank + _nodes[result.NodeIndex].TerminalCount];
        if (positions is not null)
        {
            start = PositionBound(positions, start);
            end = PositionBound(positions, end);
        }
        return new SuffixReadingCursor(_payloads, start, end, tags, positions);
    }

    internal SuffixReadingCursor OpenExactCursor(string form)
    {
        TraversalResult result = Traverse(form);
        if (result.NodeIndex < 0 || !_nodes[result.NodeIndex].IsTerminal)
            return new(Array.Empty<WordReading>());
        (int start, int end) = GetPayloadRange(result.Rank);
        return new(_payloads, start, end);
    }

    internal IReadOnlyList<WordReading> FindBaseBySuffix(string suffix, int limit) =>
        string.IsNullOrWhiteSpace(suffix) || limit <= 0 ? [] :
        FindByNormalizedPrefix(CreateKey(suffix, reverse: true), limit, baseOnly: true);

    internal IReadOnlyList<FuzzyMatch> FindSimilarNormalized(
        string normalizedQuery,
        int maxDistance,
        int limit)
    {
        if (normalizedQuery.Length == 0)
            return [];

        int rowWidth = normalizedQuery.Length + 1;
        int maxDepth = normalizedQuery.Length + maxDistance + 1;
        int[] rowBuffer = ArrayPool<int>.Shared.Rent(checked(rowWidth * (maxDepth + 1)));
        try
        {
            for (int index = 0; index <= normalizedQuery.Length; index++)
                rowBuffer[index] = index;

            PriorityQueue<FuzzyCandidate, FuzzyCandidate> matches = new(WorstFuzzyCandidateComparer);
            int childRank = _nodes[_rootIndex].IsTerminal ? 1 : 0;
            foreach (DawgEdge edge in _nodes[_rootIndex].Edges)
            {
                SearchSimilar(
                    edge.TargetIndex,
                    childRank,
                    edge.Symbol,
                    previousSymbol: '\0',
                    normalizedQuery,
                    rowBuffer,
                    rowWidth,
                    depth: 1,
                    maxDepth,
                    maxDistance,
                    limit,
                    matches);
                childRank += _nodes[edge.TargetIndex].TerminalCount;
            }

            return matches.UnorderedItems
                .Select(item => item.Element)
                .OrderBy(candidate => candidate, FuzzyCandidateComparer)
                .Select(candidate => new FuzzyMatch(
                    candidate.Form,
                    candidate.Distance,
                    candidate.Readings))
                .ToArray();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rowBuffer);
        }
    }

    private IReadOnlyList<WordReading> FindByNormalizedPrefix(string normalizedPrefix, int limit, bool baseOnly = false,
        IReadOnlySet<string>? tags = null)
    {
        TraversalResult result = Traverse(normalizedPrefix);
        if (result.NodeIndex < 0)
            return [];

        int start = _payloadStarts[result.Rank];
        int end = _payloadStarts[result.Rank + _nodes[result.NodeIndex].TerminalCount];
        if (baseOnly)
        {
            start = PositionBound(_basePayloadPositions, start);
            end = PositionBound(_basePayloadPositions, end);
        }
        // Selection scans partial blocks per winner; use it only for sufficiently sparse requests.
        if (!baseOnly && tags is null && end - start > Math.Max(256L, (long)limit * 16) && _rangeSelector is not null &&
            _rangeSelector.TrySelect(start, end, limit, out IReadOnlyList<WordReading> selected))
            return selected;
        List<WordReading> top = new(Math.Min(limit, end - start));
        for (int i = start; i < end; i++)
        {
            WordReading candidate = _payloads[baseOnly ? _basePayloadPositions[i] : i];
            if (tags is not null && (candidate.PartOfSpeech is not string tag || !tags.Contains(tag))) continue;
            if (top.Count == limit && ReadingComparer.Compare(candidate, top[^1]) >= 0)
                continue;

            int insertion = top.BinarySearch(candidate, ReadingComparer);
            insertion = insertion < 0 ? ~insertion : insertion;
            top.Insert(insertion, candidate);
            if (top.Count > limit)
                top.RemoveAt(top.Count - 1);
        }

        return top.ToArray();
    }

    private static int PositionBound(int[] positions, int value)
    {
        int index = Array.BinarySearch(positions, value);
        return index < 0 ? ~index : index;
    }

    private static string CreateKey(string form, bool reverse)
    {
        string normalized = BulgarianTextNormalizer.NormalizeKey(form);
        if (!reverse)
            return normalized;

        char[] characters = normalized.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }

    private static MutableNode BuildMinimalGraph(IReadOnlyList<CandidateGroup> groups)
    {
        MutableNode root = new();
        Dictionary<MutableNode, MutableNode> registry = new(new MutableNodeStructuralComparer());
        List<UncheckedTransition> uncheckedTransitions = [];
        string previousWord = string.Empty;

        foreach (CandidateGroup group in groups)
        {
            int commonPrefixLength = CommonPrefixLength(previousWord, group.NormalizedForm);
            MinimizeUncheckedTransitions(uncheckedTransitions, registry, commonPrefixLength);

            MutableNode current = commonPrefixLength == 0
                ? root
                : uncheckedTransitions[commonPrefixLength - 1].Child;
            for (int i = commonPrefixLength; i < group.NormalizedForm.Length; i++)
            {
                MutableNode child = new();
                int childIndex = current.AddChild(group.NormalizedForm[i], child);
                uncheckedTransitions.Add(new UncheckedTransition(current, childIndex, child));
                current = child;
            }

            current.IsTerminal = true;
            previousWord = group.NormalizedForm;
        }

        MinimizeUncheckedTransitions(uncheckedTransitions, registry, 0);
        return root;
    }

    private static void MinimizeUncheckedTransitions(
        List<UncheckedTransition> uncheckedTransitions,
        Dictionary<MutableNode, MutableNode> registry,
        int downTo)
    {
        int count = uncheckedTransitions.Count;
        if (downTo >= count)
            return;

        for (int i = count - 1; i >= downTo; i--)
        {
            UncheckedTransition transition = uncheckedTransitions[i];
            if (registry.TryGetValue(transition.Child, out MutableNode? equivalent))
                transition.Parent.ReplaceChild(transition.ChildIndex, equivalent);
            else
                registry.Add(transition.Child, transition.Child);
        }

        uncheckedTransitions.RemoveRange(downTo, count - downTo);
    }

    private static int Freeze(
        MutableNode root,
        List<DawgNode> nodes,
        Dictionary<MutableNode, int> nodeIndexes)
    {
        Stack<FreezeFrame> stack = [];
        stack.Push(new FreezeFrame(root, false));
        while (stack.Count > 0)
        {
            FreezeFrame frame = stack.Pop();
            MutableNode current = frame.Node;
            if (nodeIndexes.ContainsKey(current))
                continue;

            if (!frame.Expanded)
            {
                stack.Push(frame with { Expanded = true });
                for (int i = current.ChildCount - 1; i >= 0; i--)
                {
                    MutableNode child = current.GetChild(i).Child;
                    if (!nodeIndexes.ContainsKey(child))
                        stack.Push(new FreezeFrame(child, false));
                }

                continue;
            }

            DawgEdge[] edges = new DawgEdge[current.ChildCount];
            int terminalCount = current.IsTerminal ? 1 : 0;
            for (int i = 0; i < current.ChildCount; i++)
            {
                MutableEdge child = current.GetChild(i);
                int target = nodeIndexes[child.Child];
                edges[i] = new DawgEdge(child.Symbol, target);
                terminalCount += nodes[target].TerminalCount;
            }

            nodeIndexes.Add(current, nodes.Count);
            nodes.Add(new DawgNode(current.IsTerminal, terminalCount, edges));
        }

        return nodeIndexes[root];
    }

    private TraversalResult Traverse(string normalized)
    {
        int nodeIndex = _rootIndex;
        int rank = 0;
        foreach (char symbol in normalized)
        {
            DawgNode node = _nodes[nodeIndex];
            if (node.IsTerminal)
                rank++;

            int edgeIndex = FindEdgeIndex(node, symbol);
            if (edgeIndex < 0)
                return new TraversalResult(-1, 0);

            for (int i = 0; i < edgeIndex; i++)
                rank += _nodes[node.Edges[i].TargetIndex].TerminalCount;
            nodeIndex = node.Edges[edgeIndex].TargetIndex;
        }

        return new TraversalResult(nodeIndex, rank);
    }

    private void SearchSimilar(
        int nodeIndex,
        int rank,
        char symbol,
        char previousSymbol,
        string query,
        int[] rowBuffer,
        int rowWidth,
        int depth,
        int maxDepth,
        int maxDistance,
        int limit,
        PriorityQueue<FuzzyCandidate, FuzzyCandidate> matches)
    {
        if (depth > maxDepth)
            return;

        int previousOffset = (depth - 1) * rowWidth;
        int currentOffset = depth * rowWidth;
        rowBuffer[currentOffset] = rowBuffer[previousOffset] + 1;
        int minimum = rowBuffer[currentOffset];
        for (int index = 1; index <= query.Length; index++)
        {
            int insertion = rowBuffer[currentOffset + index - 1] + 1;
            int deletion = rowBuffer[previousOffset + index] + 1;
            int substitution = rowBuffer[previousOffset + index - 1] +
                (query[index - 1] == symbol ? 0 : 1);
            int value = Math.Min(Math.Min(insertion, deletion), substitution);
            if (depth > 1 &&
                index > 1 &&
                symbol == query[index - 2] &&
                previousSymbol == query[index - 1])
            {
                value = Math.Min(value, rowBuffer[(depth - 2) * rowWidth + index - 2] + 1);
            }

            rowBuffer[currentOffset + index] = value;
            minimum = Math.Min(minimum, value);
        }

        DawgNode node = _nodes[nodeIndex];
        int distance = rowBuffer[currentOffset + query.Length];
        if (node.IsTerminal && distance <= maxDistance)
            AddFuzzyCandidate(matches, rank, distance, limit);

        int pruningDistance = maxDistance;
        if (matches.Count == limit && matches.TryPeek(out FuzzyCandidate? worst, out _))
            pruningDistance = worst.Distance;
        if (minimum > pruningDistance)
            return;

        int childRank = rank + (node.IsTerminal ? 1 : 0);
        foreach (DawgEdge edge in node.Edges)
        {
            SearchSimilar(
                edge.TargetIndex,
                childRank,
                edge.Symbol,
                symbol,
                query,
                rowBuffer,
                rowWidth,
                depth + 1,
                maxDepth,
                maxDistance,
                limit,
                matches);
            childRank += _nodes[edge.TargetIndex].TerminalCount;
        }
    }

    private void AddFuzzyCandidate(
        PriorityQueue<FuzzyCandidate, FuzzyCandidate> matches,
        int rank,
        int distance,
        int limit)
    {
        (int start, int end) = GetPayloadRange(rank);
        double bestScore = _payloads[start].Score;
        string normalizedForm = BulgarianTextNormalizer.NormalizeKey(_payloads[start].Form);
        bool full = matches.Count >= limit;
        if (full)
        {
            matches.TryPeek(out FuzzyCandidate? worst, out _);
            if (CompareCandidateKeys(distance, bestScore, normalizedForm, worst!) >= 0)
                return;
        }

        WordReading[] readings = _payloads[start..end];
        string displayForm = readings
            .Select(reading => reading.Form)
            .FirstOrDefault(form => string.Equals(form, normalizedForm, StringComparison.Ordinal)) ??
            readings[0].Form;
        FuzzyCandidate candidate = new(
            normalizedForm,
            displayForm,
            distance,
            bestScore,
            readings);

        if (full)
            matches.Dequeue();
        matches.Enqueue(candidate, candidate);
    }

    private static int FindEdgeIndex(DawgNode node, char symbol)
    {
        int left = 0;
        int right = node.Edges.Length - 1;
        while (left <= right)
        {
            int middle = left + ((right - left) / 2);
            int comparison = node.Edges[middle].Symbol.CompareTo(symbol);
            if (comparison == 0)
                return middle;
            if (comparison < 0)
                left = middle + 1;
            else
                right = middle - 1;
        }

        return -1;
    }

    private (int Start, int End) GetPayloadRange(int rank)
    {
        if ((uint)rank >= (uint)(_payloadStarts.Length - 1))
            throw new InvalidDataException("DAWG terminal rank is outside the payload table.");
        return (_payloadStarts[rank], _payloadStarts[rank + 1]);
    }

    private static int CommonPrefixLength(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        int index = 0;
        while (index < length && left[index] == right[index])
            index++;
        return index;
    }

    private static int CompareFuzzyCandidates(FuzzyCandidate left, FuzzyCandidate right) =>
        CompareCandidateKeys(left.Distance, left.BestScore, left.NormalizedForm, right);

    /// <summary>Orders a not-yet-materialized candidate against a stored one on the same keys.</summary>
    private static int CompareCandidateKeys(
        int distance,
        double bestScore,
        string normalizedForm,
        FuzzyCandidate right)
    {
        int comparison = distance.CompareTo(right.Distance);
        if (comparison != 0)
            return comparison;
        comparison = right.BestScore.CompareTo(bestScore);
        return comparison != 0
            ? comparison
            : string.Compare(normalizedForm, right.NormalizedForm, StringComparison.Ordinal);
    }

    private sealed class MutableNode
    {
        private List<MutableEdge>? _children;

        internal bool IsTerminal { get; set; }

        internal int ChildCount => _children?.Count ?? 0;

        internal MutableEdge GetChild(int index) => _children![index];

        internal int AddChild(char symbol, MutableNode child)
        {
            _children ??= [];
            _children.Add(new MutableEdge(symbol, child));
            return _children.Count - 1;
        }

        internal void ReplaceChild(int index, MutableNode child) =>
            _children![index] = _children[index] with { Child = child };
    }

    private sealed class MutableNodeStructuralComparer : IEqualityComparer<MutableNode>
    {
        public bool Equals(MutableNode? left, MutableNode? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null || left.IsTerminal != right.IsTerminal || left.ChildCount != right.ChildCount)
                return false;

            for (int i = 0; i < left.ChildCount; i++)
            {
                MutableEdge leftEdge = left.GetChild(i);
                MutableEdge rightEdge = right.GetChild(i);
                if (leftEdge.Symbol != rightEdge.Symbol || !ReferenceEquals(leftEdge.Child, rightEdge.Child))
                    return false;
            }

            return true;
        }

        public int GetHashCode(MutableNode node)
        {
            HashCode hash = new();
            hash.Add(node.IsTerminal);
            for (int i = 0; i < node.ChildCount; i++)
            {
                MutableEdge edge = node.GetChild(i);
                hash.Add(edge.Symbol);
                hash.Add(RuntimeHelpers.GetHashCode(edge.Child));
            }

            return hash.ToHashCode();
        }
    }

    private sealed record DawgNode(bool IsTerminal, int TerminalCount, DawgEdge[] Edges);
    private readonly record struct DawgEdge(char Symbol, int TargetIndex);
    private readonly record struct MutableEdge(char Symbol, MutableNode Child);
    private readonly record struct UncheckedTransition(MutableNode Parent, int ChildIndex, MutableNode Child);
    private readonly record struct FreezeFrame(MutableNode Node, bool Expanded);
    private readonly record struct CandidateGroup(string NormalizedForm, WordReading[] Readings);
    private sealed record FuzzyCandidate(
        string NormalizedForm,
        string Form,
        int Distance,
        double BestScore,
        WordReading[] Readings);
    private readonly record struct TraversalResult(int NodeIndex, int Rank);
}
