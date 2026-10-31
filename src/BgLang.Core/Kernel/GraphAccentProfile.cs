using System.Text.Json;
using System.Text.Json.Serialization;

namespace BgLang.Core;

// An explicit source policy, not an inference from a surface operation or prefix class.
// The first supported policy assesses lexical-anchor transport only, never secondary stress.
internal sealed record GraphAccentProfile(string Id, string Kind, string[] TransitionIds,
    string ReviewState, string[] EvidenceIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MorphVowelOrdinal = null)
{
    internal const string PreserveLexicalAnchor = "preserve_lexical_anchor";
    internal const string DominantMorphNucleus = "dominant_morph_nucleus";

    internal static void ValidateJson(JsonElement root)
    {
        if (!root.TryGetProperty("graphAccentProfiles", out JsonElement rows)) return;
        if (rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("graphAccentProfiles must be an array.");
        foreach (JsonElement row in rows.EnumerateArray())
        {
            StrictJson.RequireObject(row, "Graph accent profile",
                ["id", "kind", "transitionIds", "reviewState", "evidenceIds"], ["morphVowelOrdinal"]);
            if (row.TryGetProperty("morphVowelOrdinal", out JsonElement ordinal) &&
                (ordinal.ValueKind != JsonValueKind.Number || !ordinal.TryGetInt32(out _)))
                throw new InvalidDataException("Morph vowel ordinal must be an integer.");
            foreach (string name in new[] { "id", "kind", "reviewState" })
                StrictJson.RequireString(row, name, "Graph accent profile");
            foreach (string name in new[] { "transitionIds", "evidenceIds" })
            {
                JsonElement values = row.GetProperty(name);
                if (values.ValueKind != JsonValueKind.Array ||
                    values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("Graph accent profile references must be string arrays.");
            }
        }
    }

    internal static void Validate(GraphAccentProfile[]? profiles,
        WordFormationAnalyzer.TypedTransitionNode[] transitions, HashSet<string> evidenceIds)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> transitionIds = transitions.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        foreach (GraphAccentProfile profile in profiles ?? [])
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || profile.Id != profile.Id.Trim() ||
                !ids.Add(profile.Id) || profile.Kind is not (PreserveLexicalAnchor or DominantMorphNucleus) ||
                profile.ReviewState != "accepted" ||
                (profile.Kind == DominantMorphNucleus ? profile.MorphVowelOrdinal is not >= 0 : profile.MorphVowelOrdinal is not null))
                throw new InvalidDataException("Graph accent profiles require unique IDs and an accepted supported policy.");
            References(profile.TransitionIds, transitionIds);
            References(profile.EvidenceIds, evidenceIds);
        }
    }

    private static void References(string[]? values, HashSet<string> known)
    {
        if (values is not { Length: > 0 } || values.Any(value => value is null || !known.Contains(value)) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("Graph accent policy requires nonempty, unique, known references.");
    }
}
