using System.Text.Json;

namespace BgLang.Public.Tests;

// Test-only construction over explicit normative seeds. No runtime dictionary output creates gold.
internal sealed record ArticleForm(string Id, string Lemma, string Full, string Short, string? Paradigm, string Authority);
internal sealed record ArticleSeeds(string Schema, string Partition, ArticleForm[] Nouns, ArticleForm[] Adjectives);
internal sealed record ArticleTarget(int Start, int Length, string Surface);
internal sealed record ArticleGeneratedCase(string Id, string PairId, string Partition, string Construction,
    string NounLemma, string? NounParadigm, string CarrierLemma, string Layout, string Frame, string Position,
    string Presentation, string NormativeExpectation, string InitialDeliveryScope, string ExpectedArticle,
    string Text, ArticleTarget Target, string? ExpectedReplacement, string ExpectedTextAfterRepair,
    string RuleAuthority, string[] FormAuthorities, string Qualification);

internal static class ArticleCaseFactory
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private sealed record Layout(string Id, string Kind, int Adjective = -1);
    private sealed record Frame(string Id, string Verb, string Prep);
    private static readonly Frame[] Frames = [new("govorya_za", "Говоря", "за"), new("mislya_za", "Мисля", "за"),
        new("pisha_na", "Пиша", "на"), new("tragvam_bez", "Тръгвам", "без")];
    private static readonly string[] Presentations = ["plain", "uppercase", "emoji", "repeated"];
    private const string Qualification = "Local article expectation only; package applicability and independent syntax support must be verified separately. Not a whole-sentence semantic correctness claim.";

    internal static IReadOnlyList<ArticleGeneratedCase> Generate(ArticleSeeds seeds)
    {
        if (seeds.Partition != "development") throw new ArgumentException("Only the declared development seeds are loaded during authoring.");
        List<Layout> layouts = [new("noun", "noun")];
        for (int a = 0; a < seeds.Adjectives.Length; a++)
        {
            layouts.Add(new("adj_" + seeds.Adjectives[a].Id, "single_attribute", a));
            foreach (string kind in new[] { "stacked", "comma", "conjunction" })
                layouts.Add(new(kind + "_" + seeds.Adjectives[a].Id, kind, a));
        }
        List<ArticleGeneratedCase> pp = [];
        foreach (ArticleForm noun in seeds.Nouns)
        foreach (Layout layout in layouts)
        foreach (Frame frame in Frames)
        foreach (string position in new[] { "postposed", "fronted" })
        foreach (string display in Presentations)
        foreach (bool wrong in new[] { false, true })
        {
            ArticleForm carrier = layout.Kind == "noun" ? noun : seeds.Adjectives[layout.Adjective];
            List<string> authorities = [noun.Authority, carrier.Authority];
            string tail = string.Empty;
            if (layout.Kind != "noun")
            {
                ArticleForm other = seeds.Adjectives[(layout.Adjective + 1) % seeds.Adjectives.Length];
                if (layout.Kind != "single_attribute") authorities.Add(other.Authority);
                string bridge = layout.Kind switch { "single_attribute" => " ", "stacked" => " " + other.Lemma + " ",
                    "comma" => ", " + other.Lemma + " ", _ => " и " + other.Lemma + " " };
                tail = bridge + noun.Lemma;
            }
            string before = position == "postposed" ? frame.Verb + " " + frame.Prep + " " : Capitalize(frame.Prep) + " ";
            string after = tail + (position == "postposed" ? "." : " " + frame.Verb.ToLowerInvariant() + ".");
            string pair = string.Join('/', seeds.Partition, noun.Id, layout.Id, frame.Id, position, display);
            var rendered = Render(before, after, wrong ? carrier.Full : carrier.Short, carrier.Short, display);
            pp.Add(new(pair + (wrong ? "/error" : "/correct"), pair, seeds.Partition, "prepositional_np", noun.Lemma,
                noun.Paradigm, carrier.Lemma, layout.Kind, frame.Id, position, display,
                wrong ? "MUST_WARN" : "MUST_ACCEPT_LOCALLY",
                position == "postposed" && layout.Kind is "noun" or "single_attribute" ? "REQUIRED_BASIC" : "DECLARED_EXTENSION_OR_CHALLENGE",
                "Short", rendered.Text, rendered.Target, wrong ? rendered.Repair : null, wrong ? rendered.Repaired : rendered.Text,
                "https://beron.mon.bg/rechnik/363", authorities.Distinct(StringComparer.Ordinal).ToArray(), Qualification));
        }
        List<ArticleGeneratedCase> result = [..pp];
        ArticleGeneratedCase[] basis = pp.Where(c => c.Frame == "govorya_za" && c.Position == "postposed" && c.Presentation == "plain").ToArray();
        foreach (ArticleGeneratedCase source in basis)
        foreach (string relation in new[] { "subject_copular", "copular_complement" })
        foreach (string display in Presentations)
        {
            ArticleGeneratedCase[] pair = basis.Where(c => c.PairId == source.PairId).ToArray();
            string full = pair.Single(c => c.NormativeExpectation == "MUST_WARN").Target.Surface;
            string shortForm = pair.Single(c => c.NormativeExpectation == "MUST_ACCEPT_LOCALLY").Target.Surface;
            bool wrong = source.Target.Surface == shortForm;
            string tail = source.Text[(source.Target.Start + source.Target.Length)..^1];
            string before = relation == "subject_copular" ? "Тук " : "Това е ";
            string after = tail + (relation == "subject_copular" ? " е познат." : ".");
            string id = string.Join('/', seeds.Partition, source.NounLemma, source.CarrierLemma, source.Layout, relation, display);
            var rendered = Render(before, after, source.Target.Surface, full, display);
            result.Add(source with { Id = id + (wrong ? "/error" : "/correct"), PairId = id, Construction = relation,
                Frame = relation, Position = "fixed_reviewed_template", Presentation = display,
                NormativeExpectation = wrong ? "MUST_WARN" : "MUST_ACCEPT_LOCALLY",
                InitialDeliveryScope = "DECLARE_EXISTING_OR_EXTENDED_FULL_ROLE_BEFORE_RUN", ExpectedArticle = "Full",
                Text = rendered.Text, Target = rendered.Target, ExpectedReplacement = wrong ? rendered.Repair : null,
                ExpectedTextAfterRepair = wrong ? rendered.Repaired : rendered.Text, RuleAuthority = "https://beron.mon.bg/rechnik/331" });
        }
        return result;
    }

    private static (string Text, ArticleTarget Target, string Repair, string Repaired) Render(
        string before, string after, string surface, string correct, string display)
    {
        string prefix = display == "repeated" ? before + correct + after + " " + before : before;
        if (display == "emoji") prefix = "🙂 " + prefix;
        if (display == "uppercase") { prefix = prefix.ToUpperInvariant(); after = after.ToUpperInvariant(); surface = surface.ToUpperInvariant(); correct = correct.ToUpperInvariant(); }
        return (prefix + surface + after, new(prefix.Length, surface.Length, surface), correct, prefix + correct + after);
    }

    internal static string Repair(ArticleGeneratedCase c, string replacement) => c.Text[..c.Target.Start] + replacement + c.Text[(c.Target.Start + c.Target.Length)..];
    internal static void Validate(IReadOnlyList<ArticleGeneratedCase> rows, string partition)
    {
        if (rows.Count != 2880 || rows.Any(c => c.Partition != partition) || rows.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            throw new ArgumentException("Incomplete, mixed-partition or duplicate generated fixture.");
        foreach (ArticleGeneratedCase c in rows)
            if (c.Target.Start < 0 || c.Target.Length <= 0 || c.Target.Start > c.Text.Length - c.Target.Length ||
                c.Text.Substring(c.Target.Start, c.Target.Length) != c.Target.Surface ||
                (c.ExpectedReplacement is null ? c.Text : Repair(c, c.ExpectedReplacement)) != c.ExpectedTextAfterRepair)
                throw new ArgumentException("Invalid original range/repair: " + c.Id);
        foreach (var pair in rows.GroupBy(c => c.PairId, StringComparer.Ordinal))
        {
            if (pair.Count() != 2) throw new ArgumentException("Incomplete pair: " + pair.Key);
            ArticleGeneratedCase valid = pair.Single(c => c.NormativeExpectation == "MUST_ACCEPT_LOCALLY");
            ArticleGeneratedCase wrong = pair.Single(c => c.NormativeExpectation == "MUST_WARN");
            if (valid.Text != wrong.ExpectedTextAfterRepair || valid.Target.Start != wrong.Target.Start || valid.Text == wrong.Text)
                throw new ArgumentException("Broken minimal pair: " + pair.Key);
        }
    }

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
