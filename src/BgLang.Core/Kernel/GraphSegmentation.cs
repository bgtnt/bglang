using BgLang.Contracts;
using static BgLang.Core.WordFormationGraph;
using Transform = BgLang.Core.GraphSurfaceOperations.Transform;

namespace BgLang.Core;

/// <summary>Source-aligned segment projection over the existing compiled graph operations.</summary>
internal sealed class GraphSegmentation(Morph[] morphs, Transform[] transforms, string[] literals,
    InputSlot[] slots, SurfaceOperation[] operations, Edge[] edges, IReadOnlyDictionary<string, int> morphIndex,
    GraphSurfaceOperations surfaceOperations)
{
    internal MorphSegment[] Build(string baseLemma, string word, int[] path, GraphSearchBudget? budget)
    {
        Edge first = path.Length > 0 ? edges[path[0]] : default;
        if (path.Length == 1 && first.Origin == EdgeOrigin.Application)
            return BuildApplicationSegments(word, first);
        if (path.Length == 2 && first.Origin == EdgeOrigin.Application &&
            edges[path[1]].Origin == EdgeOrigin.Paradigm && first.SurfaceKeys is { Length: 1 } output &&
            TryGetDirectEnding(edges[path[1]], output[0], word, out string bridgeEnding, budget))
        {
            MorphSegment[] applicationSegments = BuildApplicationSegments(output[0], first);
            var bridgePieces = applicationSegments.Select(segment => new Piece(
                segment.Surface, segment.Canonical, segment.Kind, segment.EvidenceId)).ToList();
            bridgePieces.Add(new Piece(bridgeEnding, bridgeEnding, MorphKind.InflectionalEnding, edges[path[1]].Id));
            MorphSegment[] result = ToSegments(bridgePieces);
            if (string.Concat(result.Select(segment => segment.Surface)) == word)
                return result;
        }

        var pieces = new List<Piece> { new(baseLemma, baseLemma, MorphKind.Stem, null) };
        foreach (int edgeIndex in path)
        {
            Edge edge = edges[edgeIndex];
            for (int index = 0; index < edge.OperationCount; index++)
            {
                budget?.Take();
                SurfaceOperation operation = operations[edge.OperationOffset + index];
                if (operation.Kind == OperationKind.Transform)
                {
                    Transform transform = transforms[operation.OperandIndex];
                    string before = string.Concat(pieces.Select(item => item.Surface));
                    string after = transform.Apply(before, forward: true).SingleOrDefault() ??
                        throw new InvalidDataException(
                            $"Transform '{transform.Id}' cannot replay an accepted graph path over '{before}'.");
                    ApplyChangedTail(pieces, before, after, transform.Id);
                }
                else if (operation.Kind is OperationKind.PrependMorph or OperationKind.AppendMorph)
                {
                    Morph morph = morphs[operation.OperandIndex];
                    Piece piece = new(morph.Text, morph.Text, MapMorphKind(morph.Kind), morph.Id);
                    if (operation.Kind == OperationKind.PrependMorph)
                        pieces.Insert(0, piece);
                    else
                        pieces.Add(piece);
                }
                else if (operation.Kind == OperationKind.AppendLiteral)
                {
                    string ending = literals[operation.OperandIndex];
                    pieces.Add(new Piece(ending, ending, MorphKind.InflectionalEnding, edge.Id));
                }
            }
        }
        return ToSegments(pieces);
    }

    internal MorphSegment[] BuildApplicationSegments(string word, Edge edge)
    {
        string baseSurface = word;
        ArraySegment<InputSlot> inputSlots = GetSlots(edge);
        var pieces = new List<Piece>();
        string? mechanical = edge.Evidence
            .FirstOrDefault(item => item.StartsWith("mechanical_split=", StringComparison.Ordinal));
        string? surfaceStem = edge.ApplicationDecision?.SurfaceStem;
        if (surfaceStem is not null)
        {
            InputSlot stemSlot = inputSlots.Single(slot => !slot.IsLexicalAnchor && slot.Role == "stem");
            InputSlot suffixSlot = inputSlots.Single(slot => !slot.IsLexicalAnchor && slot.Role == "suffix");
            if (!TryFindMorph(stemSlot.NodeId, out Morph stem) || !TryFindMorph(suffixSlot.NodeId, out Morph suffix))
                throw new InvalidDataException("Reviewed surface alignment lost its typed morph identity.");
            pieces.Add(new Piece(surfaceStem, stem.Text, MorphKind.Stem, stem.Id));
            pieces.Add(new Piece(suffix.Text, suffix.Text, MorphKind.DerivationalSuffix, suffix.Id));
        }
        else if (mechanical is not null)
        {
            foreach (string part in mechanical["mechanical_split=".Length..]
                         .Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                string normalized = BulgarianTextNormalizer.NormalizeKey(part);
                Morph? morph = FindSlotMorph(inputSlots, normalized);
                pieces.Add(new Piece(
                    normalized,
                    normalized,
                    morph is null ? MorphKind.Stem : MapMorphKind(morph.Value.Kind),
                    morph?.Id));
            }
            if (!string.Equals(
                    string.Concat(pieces.Select(item => item.Surface)),
                    baseSurface,
                    StringComparison.Ordinal))
            {
                pieces.Clear();
            }
        }
        else
        {
            int cursor = 0;
            foreach (InputSlot slot in inputSlots)
            {
                if (slot.IsLexicalAnchor || !TryFindMorph(slot.NodeId, out Morph morph))
                    continue;
                int index = baseSurface.IndexOf(morph.Text, cursor, StringComparison.Ordinal);
                if (index < 0)
                    continue;
                if (index > cursor)
                    pieces.Add(new Piece(baseSurface[cursor..index], baseSurface[cursor..index], MorphKind.Stem, null));
                pieces.Add(new Piece(morph.Text, morph.Text, MapMorphKind(morph.Kind), morph.Id));
                cursor = index + morph.Text.Length;
            }
            if (cursor < baseSurface.Length)
                pieces.Add(new Piece(baseSurface[cursor..], baseSurface[cursor..], MorphKind.Stem, null));
        }
        if (pieces.Count == 0)
            pieces.Add(new Piece(baseSurface, baseSurface, MorphKind.Stem, null));
        if (!string.Equals(
                string.Concat(pieces.Select(item => item.Surface)),
                baseSurface,
                StringComparison.Ordinal))
        {
            pieces.Clear();
            pieces.Add(new Piece(baseSurface, baseSurface, MorphKind.Stem, null));
        }
        return ToSegments(pieces);
    }

    private static void ApplyChangedTail(List<Piece> pieces, string before, string after, string evidenceId)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return;
        int common = 0;
        int maximum = Math.Min(before.Length, after.Length);
        // Keep every leading piece that ends at or before the first changed offset so that
        // morph boundaries recorded by earlier edges on the same path survive the rewrite.
        while (common < maximum && before[common] == after[common])
            common++;

        int kept = 0;
        int keptLength = 0;
        while (kept < pieces.Count && keptLength + pieces[kept].Surface.Length <= common)
        {
            keptLength += pieces[kept].Surface.Length;
            kept++;
        }

        int changedPieceCount = pieces.Count - kept;
        MorphKind changedKind = changedPieceCount == 1 ? pieces[kept].Kind : MorphKind.Stem;
        pieces.RemoveRange(kept, changedPieceCount);
        if (keptLength < after.Length)
            pieces.Add(new Piece(after[keptLength..], before[keptLength..], changedKind, evidenceId));
    }

    internal static MorphSegment[] ToSegments(IReadOnlyList<Piece> pieces)
    {
        var result = new MorphSegment[pieces.Count];
        int start = 0;
        for (int index = 0; index < pieces.Count; index++)
        {
            Piece piece = pieces[index];
            result[index] = new MorphSegment(
                piece.Surface,
                piece.Canonical,
                piece.Kind,
                start,
                piece.Surface.Length,
                piece.EvidenceId);
            start += piece.Surface.Length;
        }
        return result;
    }

    internal readonly record struct Piece(
        string Surface,
        string Canonical,
        MorphKind Kind,
        string? EvidenceId);

    private Morph? FindSlotMorph(IEnumerable<InputSlot> slots, string surface)
    {
        foreach (InputSlot slot in slots)
        {
            if (!slot.IsLexicalAnchor && TryFindMorph(slot.NodeId, out Morph morph) && morph.Text == surface)
                return morph;
        }
        return null;
    }

    private ArraySegment<InputSlot> GetSlots(Edge edge) =>
        new(slots, edge.SlotOffset, edge.SlotCount);

    private bool TryFindMorph(string id, out Morph morph)
    {
        if (!morphIndex.TryGetValue(id, out int index))
        {
            morph = default;
            return false;
        }

        morph = morphs[index];
        return true;
    }


    private bool TryGetDirectEnding(Edge edge, string lemma, string normalized, out string ending, GraphSearchBudget? budget) =>
        surfaceOperations.IsAppendOnlyParadigm(edge, lemma, out string replay, out ending, budget) && replay == normalized;
}
