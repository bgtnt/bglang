// Five basic role routes and nine explicit optional source groups.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using BgLang.Contracts;

namespace BgLang.Core;

internal static class SyntaxRoleCalculator
{
    // Syntax rule categories; current-to-syntax mapping below is an explicit prospective contract.
    internal enum SourcePos { Other, Noun, Adjective, Numeral, ProperNoun, Pronoun, Verb, Adverb, Particle, Preposition, Conjunction }
    internal enum Role { Predicate, Subject, Object, Complement, PrepositionalObject, Attribute, Adverbial, Possessive }
    internal enum Basis { ExactCategory, SurfaceRule, WeakTitleCase, CompleteUnknown, TypedFeatures, LicensedGraphMorphology }
    internal enum Route { SourceScan, Selected, ClosedThirdPersonMismatchPreview, Abstained, ProductiveGraph, ProductiveGraphPreview, ProductiveGraphMismatch }
    internal enum Status { Calculated, IncompleteInventory, UnsupportedSelectedPredicate }
    internal enum Rule
    {
        BasicPredicate, BasicSubject, ShortMasculineObject, BasicObject, BasicComplement,
        BasicPrepositionalObject, UnknownAttributeShape, RegisterTitle, NominalPossessive,
        FutureClitic, DaClitic, ReflexiveClitic, ImmediateClitic, ImpersonalDoshya,
        DaSubpredicate, PostverbalChaka, DativeSpeech,
        Zadneshkom, KrivoLyavo, KadeKade, Directional, Zamalko, ProductiveGraphPredicate, ProductiveGraphSubject,
        ProductiveNominalPhrasePredicate, ProductiveNominalPhraseSubject, ProductiveNominalPhraseAttribute
    }
    // Projection is created once from the same immutable row by the shared adapter.
    // CanonicalLemma uses the existing lookup normalizer; this calculator never normalizes.
    internal sealed record Projection(SourcePos Pos, MorphologicalFeatures Features, string CanonicalLemma);
    internal sealed record Word(int TokenIndex, int Start, int Length, string Canonical,
        ImmutableArray<WordReading> Readings, ImmutableArray<Projection> Projection, bool Complete)
    {
        internal ImmutableArray<string> GraphTags { get; init; } = [];
        internal bool UsesGraph => !GraphTags.IsDefaultOrEmpty;
    }
    internal sealed record Support(int TokenIndex, ImmutableArray<int> ReadingIndices, Basis Basis);
    internal sealed record Resolution(Route Route, int PredicateWordIndex, ImmutableArray<Support> Provenance)
    {
        internal int SubjectWordIndex { get; init; } = -1;
    }
    internal sealed record Candidate(Role Role, Rule Rule, int Start, int Length,
        ImmutableArray<Support> Targets, ImmutableArray<Support> Context);
    internal sealed record Result(Status Status, ImmutableArray<Word> OriginalWords,
        ImmutableArray<Candidate> Candidates, Resolution Resolution);

    // Bind once in the shared adapter; no raw-tag parser and no ProperNoun-to-Noun collapse.
    // This explicit current mapping is not a claim of archive upstream equivalence.
    internal static SourcePos FromCurrentCategory(MorphologicalPartOfSpeech pos) => pos switch
    {
        MorphologicalPartOfSpeech.Noun => SourcePos.Noun,
        MorphologicalPartOfSpeech.Adjective => SourcePos.Adjective,
        MorphologicalPartOfSpeech.Numeral => SourcePos.Numeral,
        MorphologicalPartOfSpeech.ProperNoun => SourcePos.ProperNoun,
        MorphologicalPartOfSpeech.PersonalPronoun or MorphologicalPartOfSpeech.Pronoun => SourcePos.Pronoun,
        MorphologicalPartOfSpeech.Verb => SourcePos.Verb,
        MorphologicalPartOfSpeech.Adverb => SourcePos.Adverb,
        MorphologicalPartOfSpeech.Particle => SourcePos.Particle,
        MorphologicalPartOfSpeech.Preposition => SourcePos.Preposition,
        MorphologicalPartOfSpeech.Conjunction => SourcePos.Conjunction,
        _ => SourcePos.Other
    };

    // One opt-in switch mirrors the source option; it controls all nine named groups.
    internal static Result Calculate(string text, int sentenceStart, int sentenceLength,
        ImmutableArray<Word> words, bool includeContextualHints, Resolution resolution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(resolution);
        cancellationToken.ThrowIfCancellationRequested();
        if (words.IsDefault || sentenceStart < 0 || sentenceLength < 0 ||
            sentenceLength > text.Length || sentenceStart > text.Length - sentenceLength)
            throw new ArgumentException("Invalid sentence or immutable inventory.");
        var tokenIds = new HashSet<int>();
        int previousEnd = sentenceStart;
        bool complete = true;
        foreach (Word word in words)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (word is null || word.TokenIndex < 0 || !tokenIds.Add(word.TokenIndex) ||
                word.Start < previousEnd || word.Length <= 0 || word.Length > sentenceLength ||
                word.Start - sentenceStart > sentenceLength - word.Length ||
                word.Readings.IsDefault || word.Projection.IsDefault ||
                (word.UsesGraph ? word.GraphTags.Length : word.Readings.Length) != word.Projection.Length || word.Canonical is null)
                throw new ArgumentException("Invalid original token/projection binding.");
            previousEnd = word.Start + word.Length;
            complete &= word.Complete;
            for (int j = 0; j < word.Projection.Length; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!word.UsesGraph && word.Readings[j] is null || word.Projection[j] is null ||
                    word.Projection[j].CanonicalLemma is null ||
                    !StringComparer.Ordinal.Equals(word.UsesGraph ? word.GraphTags[j] : word.Readings[j].PartOfSpeech, word.Projection[j].Features.RawTag))
                    throw new ArgumentException("Projection must retain the exact row's raw tag.");
            }
        }
        ValidateReferences(resolution.Provenance);
        if ((resolution.Route is Route.Selected or Route.ClosedThirdPersonMismatchPreview) &&
            !Valid(resolution.PredicateWordIndex))
            throw new ArgumentException("Selected predicate is outside this sentence.");
        if (!complete)
            return new(Status.IncompleteInventory, words, [], resolution);
        if (resolution.Route is Route.ProductiveGraph or Route.ProductiveGraphPreview or Route.ProductiveGraphMismatch)
        {
            if (words.Length > 2) return ProductiveNominal();
            if (words.Length != 2 || !words.Any(word => word.UsesGraph) || resolution.PredicateWordIndex != 1)
                throw new ArgumentException("Unsupported graph subject/predicate topology.");
            Support[] graphPredicate = resolution.Provenance.Where(s => s.TokenIndex == words[1].TokenIndex).ToArray();
            Support[] graphSubject = resolution.Provenance.Where(s => s.TokenIndex == words[0].TokenIndex).ToArray();
            if (graphPredicate.Length != 1 || graphSubject.Length != 1 || graphPredicate[0].ReadingIndices.Length == 0 || graphSubject[0].ReadingIndices.Length == 0)
                throw new ArgumentException("Graph topology needs both bound morphology supports.");
            return new(Status.Calculated, words,
            [
                new(Role.Predicate, Rule.ProductiveGraphPredicate, words[1].Start, words[1].Length, [graphPredicate[0]], [graphSubject[0]]),
                new(Role.Subject, Rule.ProductiveGraphSubject, words[0].Start, words[0].Length, [graphSubject[0]], [graphPredicate[0]])
            ], resolution);
        }
        if (words.Any(word => word.UsesGraph)) throw new ArgumentException("Graph morphology requires its bounded explicit route.");
        var output = ImmutableArray.CreateBuilder<Candidate>();
        bool opt = includeContextualHints;

        // Source order: possessive/title evidence does not require a predicate.
        if (opt)
        {
            for (int i = 1; i < words.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Possessive(C(i)) && Nominal(i - 1) && Bridge(i - 1, i))
                    Emit(Role.Possessive, Rule.NominalPossessive, i, i,
                        [Surface(i)], [NominalRef(i - 1)]);
            }
            for (int i = 0; i + 2 < words.Length; i++)
            {
                if (C(i) == "на" && Has(i, SourcePos.Preposition) && C(i + 1) == "чорбаджи" &&
                    C(i + 2) == "сейрекоолу" && Unknown(i + 2) && StrongTitle(i + 2) && Chain(i, i + 2))
                {
                    Emit(Role.Attribute, Rule.RegisterTitle, i + 1, i + 1,
                        [Surface(i + 1)], [Ref(i, SourcePos.Preposition), UnknownRef(i + 2)]);
                    Emit(Role.PrepositionalObject, Rule.RegisterTitle, i, i + 2,
                        [Ref(i, SourcePos.Preposition), Surface(i + 1), UnknownRef(i + 2)], []);
                }
            }
        }

        int predicate = -1;
        if (resolution.Route is Route.Selected or Route.ClosedThirdPersonMismatchPreview)
        {
            predicate = resolution.PredicateWordIndex;
            if (!Predicate(predicate) && !Impersonal(predicate))
                return new(Status.UnsupportedSelectedPredicate, words, output.ToImmutable(), resolution);
        }
        else if (resolution.Route == Route.SourceScan)
        {
            for (int i = 0; i < words.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Source excludes a homographic short clitic before a known predicate.
                if (opt && i > 0 && C(i - 1) == "да" && DaShort(C(i)) &&
                    Predicate(i + 1) && Chain(i - 1, i + 1)) continue;
                if (Predicate(i) || Impersonal(i)) { predicate = i; break; }
            }
        }
        // Supplied abstention never silently falls back to first-VERB scanning.
        if (predicate < 0) return new(Status.Calculated, words, output.ToImmutable(), resolution);
        int p = predicate;
        ImmutableArray<Support> predicateContext = resolution.Provenance;
        if (Impersonal(p)) predicateContext = predicateContext.AddRange(ImpersonalContext(p));
        Emit(Role.Predicate, Impersonal(p) ? Rule.ImpersonalDoshya : Rule.BasicPredicate,
            p, p, [PredicateRef(p)], predicateContext);

        // Closed speech/da subordinate routes: no generic subordinate parser.
        if (opt && Chain(p, p + 4) && C(p) == "казах" && Predicate(p) &&
            C(p + 1) == "ти" && Has(p + 1, SourcePos.Pronoun) &&
            C(p + 2) == "да" && Has(p + 2, SourcePos.Particle) &&
            C(p + 3) == "се" && Has(p + 3, SourcePos.Particle) &&
            C(p + 4) == "погрижиш" && Predicate(p + 4))
            Emit(Role.Predicate, Rule.DativeSpeech, p + 4, p + 4, [PredicateRef(p + 4)],
                [PredicateRef(p), Ref(p + 1, SourcePos.Pronoun), Ref(p + 2, SourcePos.Particle), Ref(p + 3, SourcePos.Particle)]);
        if (opt && Chain(p, p + 3) && C(p + 1) == "да" && Has(p + 1, SourcePos.Particle) &&
            DaShort(C(p + 2)) && Has(p + 2, SourcePos.Pronoun) && Predicate(p) && Predicate(p + 3) &&
            DaTriple(C(p), C(p + 2), C(p + 3)))
        {
            Emit(Role.Object, Rule.DaSubpredicate, p + 2, p + 2, [Ref(p + 2, SourcePos.Pronoun)],
                [PredicateRef(p), Ref(p + 1, SourcePos.Particle), PredicateRef(p + 3)]);
            Emit(Role.Predicate, Rule.DaSubpredicate, p + 3, p + 3, [PredicateRef(p + 3)],
                [PredicateRef(p), Ref(p + 1, SourcePos.Particle), Ref(p + 2, SourcePos.Pronoun)]);
        }
        if (opt)
            for (int i = 0; i < p && i + 1 < words.Length; i++)
                if (Unknown(i) && Has(i + 1, SourcePos.Noun) && AttributeShape(C(i), cancellationToken) && Bridge(i, i + 1))
                    Emit(Role.Attribute, Rule.UnknownAttributeShape, i, i, [UnknownRef(i)],
                        [Ref(i + 1, SourcePos.Noun), PredicateRef(p)]);

        // Source subject boundary helpers for the named slice, not adjacent archive lanes.
        int end = p - 1;
        if (FutureCluster(p) || DaCluster(p) || Impersonal(p) || NeuterFrame(p)) end = p - 3;
        else if (Reflexive(p) || Immediate(p) || (opt && C(p - 1) == "не" && Bridge(p - 1, p))) end = p - 2;
        else if ((C(p - 1) == "ще" && Has(p - 1, SourcePos.Particle)) || Has(p - 1, SourcePos.Adverb)) end = p - 2;
        while (end >= 0 && Preposition(C(end))) end--;
        bool subjectBefore = false;
        if (end >= 0 && Nominal(end))
        {
            int first = end;
            while (first > 0 && Nominal(first - 1)) first--;
            ImmutableArray<Support> nominal = NominalRefs(first, end);
            var modifiers = ShortMasculineRefs(first, end);
            bool divert = modifiers.Length > 0 && FirstOrSecondPredicate(p) && !FirstOrSecondPronoun(first, end);
            Emit(divert ? Role.Object : Role.Subject, divert ? Rule.ShortMasculineObject : Rule.BasicSubject,
                first, end, nominal, BoundaryContext(end, p).AddRange(modifiers));
            subjectBefore = !divert;
        }

        // The closed neuter frame suppresses se; the closed impersonal emits BOTH objects.
        if (opt && !NeuterFrame(p))
        {
            if (Impersonal(p))
            {
                Emit(Role.Object, Rule.ImpersonalDoshya, p - 2, p - 2, [Ref(p - 2, SourcePos.Pronoun)], ImpersonalContext(p));
                Emit(Role.Object, Rule.ImpersonalDoshya, p - 1, p - 1, [Ref(p - 1, SourcePos.Particle, SourcePos.Pronoun)], ImpersonalContext(p));
            }
            else if (FutureCluster(p) || DaCluster(p) || Reflexive(p) || Immediate(p))
            {
                Rule rule = FutureCluster(p) ? Rule.FutureClitic : DaCluster(p) ? Rule.DaClitic :
                    Reflexive(p) ? Rule.ReflexiveClitic : Rule.ImmediateClitic;
                var context = ImmutableArray.CreateBuilder<Support>();
                context.Add(PredicateRef(p));
                if (FutureCluster(p) || DaCluster(p)) context.Add(Surface(p - 2));
                else if (C(p - 1) == "те") context.Add(C(p - 2) == "да" ?
                    Ref(p - 2, SourcePos.Particle) : Ref(p - 2, SourcePos.Noun, SourcePos.Pronoun));
                Emit(Role.Object, rule, p - 1, p - 1, [Surface(p - 1)], context.ToImmutable());
            }
        }
        bool postSubject = false;
        if (opt && !subjectBefore && p == 0 && C(p) == "чака" && C(p + 1) == "ни" &&
            Valid(p + 2) && !Preposition(C(p + 2)) && Nominal(p + 2) && Chain(p, p + 2))
        {
            int last = ExtendNominal(p + 2, whitespaceOnly: true);
            Emit(Role.Object, Rule.PostverbalChaka, p + 1, p + 1, [Surface(p + 1)],
                ImmutableArray.Create(PredicateRef(p)).AddRange(NominalRefs(p + 2, last)));
            Emit(Role.Subject, Rule.PostverbalChaka, p + 2, last, NominalRefs(p + 2, last),
                [PredicateRef(p), Surface(p + 1)]);
            postSubject = true;
        }
        if (!postSubject && Valid(p + 1) && !Preposition(C(p + 1)) && Nominal(p + 1))
        {
            int last = ExtendNominal(p + 1, whitespaceOnly: false);
            bool copula = Copula(C(p));
            Emit(copula ? Role.Complement : Role.Object, copula ? Rule.BasicComplement : Rule.BasicObject,
                p + 1, last, NominalRefs(p + 1, last), [PredicateRef(p)]);
        }
        if (opt) AddAdverbials(p);
        for (int i = p + 1; i + 1 < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Preposition(C(i)) || !Nominal(i + 1)) continue;
            int last = ExtendNominal(i + 1, whitespaceOnly: false);
            Emit(Role.PrepositionalObject, Rule.BasicPrepositionalObject, i, last,
                ImmutableArray.Create(Ref(i, SourcePos.Preposition)).AddRange(NominalRefs(i + 1, last)), [PredicateRef(p)]);
            break;
        }
        return new(Status.Calculated, words, output.ToImmutable(), resolution);

        bool Valid(int i) => i >= 0 && i < words.Length;
        string C(int i) => Valid(i) ? words[i].Canonical : "";
        bool Unknown(int i) => Valid(i) && words[i].Readings.Length == 0;
        bool Has(int i, params SourcePos[] kinds)
        {
            if (!Valid(i)) return false;
            foreach (Projection projection in words[i].Projection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Array.IndexOf(kinds, projection.Pos) >= 0) return true;
            }
            return false;
        }
        ImmutableArray<int> Indices(int i, Func<Projection, bool> accepts)
        {
            var indices = ImmutableArray.CreateBuilder<int>();
            if (Valid(i)) for (int j = 0; j < words[i].Projection.Length; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (accepts(words[i].Projection[j])) indices.Add(j);
            }
            return indices.ToImmutable();
        }
        Support Ref(int i, params SourcePos[] kinds)
        {
            var indices = Indices(i, x => Array.IndexOf(kinds, x.Pos) >= 0);
            return new(words[i].TokenIndex, indices, indices.Length > 0 ? Basis.ExactCategory : Basis.SurfaceRule);
        }
        Support Surface(int i) => new(words[i].TokenIndex, [], Basis.SurfaceRule);
        Support UnknownRef(int i) => new(words[i].TokenIndex, [], Basis.CompleteUnknown);
        bool Predicate(int i) => Valid(i) && (Has(i, SourcePos.Verb) || Copula(C(i)));
        Support PredicateRef(int i) => Ref(i, SourcePos.Verb);
        bool Nominal(int i)
        {
            if (!Valid(i)) return false;
            if (Indices(i, x => IsNominal(x.Pos)).Length > 0) return true;
            if (!WeakTitle(i)) return false;
            foreach (WordReading reading in words[i].Readings)
                if (!string.IsNullOrWhiteSpace(reading.PartOfSpeech)) return false;
            return true;
        }
        Support NominalRef(int i)
        {
            var indices = Indices(i, x => IsNominal(x.Pos));
            return new(words[i].TokenIndex, indices, indices.Length > 0 ? Basis.ExactCategory : Basis.WeakTitleCase);
        }
        ImmutableArray<Support> NominalRefs(int first, int last)
        {
            var refs = ImmutableArray.CreateBuilder<Support>();
            for (int i = first; i <= last; i++) refs.Add(NominalRef(i));
            return refs.ToImmutable();
        }
        int ExtendNominal(int first, bool whitespaceOnly)
        {
            int last = first;
            while (Valid(last + 1) && !Preposition(C(last + 1)) && Nominal(last + 1) &&
                (!whitespaceOnly || Bridge(last, last + 1))) last++;
            return last;
        }
        // Explicit handoff2 delta only for contiguous contextual patterns, not all basic chunks.
        bool Bridge(int a, int b)
        {
            if (!Valid(a) || b != a + 1 || !Valid(b)) return false;
            int start = words[a].Start + words[a].Length, stop = words[b].Start;
            if (start >= stop) return false;
            for (int k = start; k < stop; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!char.IsWhiteSpace(text[k])) return false;
            }
            return true;
        }
        bool Chain(int first, int last)
        {
            if (!Valid(first) || !Valid(last) || last < first) return false;
            for (int i = first; i < last; i++) if (!Bridge(i, i + 1)) return false;
            return true;
        }
        bool WeakTitle(int i)
        {
            Word w = words[i];
            if (!char.IsUpper(text[w.Start])) return false;
            for (int k = w.Start; k < w.Start + w.Length; k++) if (Cyrillic(text[k])) return true;
            return false;
        }
        bool StrongTitle(int i)
        {
            Word w = words[i];
            if (!Cyrillic(text[w.Start]) || !char.IsUpper(text[w.Start])) return false;
            for (int k = w.Start + 1; k < w.Start + w.Length; k++)
                if (!Cyrillic(text[k]) || !char.IsLower(text[k])) return false;
            return true;
        }
        bool FutureCluster(int i) => opt && Chain(i - 2, i) && C(i - 2) == "ще" && FutureClitic(C(i - 1));
        bool DaCluster(int i) => opt && Chain(i - 2, i) && C(i - 2) == "да" && DaShort(C(i - 1));
        bool Reflexive(int i) => opt && C(i - 1) == "се" && Bridge(i - 1, i);
        bool Immediate(int i) => opt && Bridge(i - 1, i) &&
            (C(i - 1) is "го" or "я" or "ги" or "ме" || C(i - 1) == "те" && Bridge(i - 2, i - 1) &&
             (C(i - 2) == "да" ? Has(i - 2, SourcePos.Particle) : Has(i - 2, SourcePos.Noun, SourcePos.Pronoun)));
        bool Impersonal(int i) => opt && Chain(i - 2, i + 1) && C(i) == "дощя" &&
            C(i - 2) == "ми" && C(i - 1) == "се" && C(i + 1) == "да" &&
            Has(i - 2, SourcePos.Pronoun) && Has(i - 1, SourcePos.Particle, SourcePos.Pronoun) && Has(i + 1, SourcePos.Particle);
        ImmutableArray<Support> ImpersonalContext(int i) => [Ref(i - 2, SourcePos.Pronoun),
            Ref(i - 1, SourcePos.Particle, SourcePos.Pronoun), Surface(i), Ref(i + 1, SourcePos.Particle)];
        bool NeuterFrame(int i) => opt && Chain(i - 4, i) && C(i - 4) == "и" && Has(i - 4, SourcePos.Conjunction) &&
            C(i - 3) == "то" && Has(i - 3, SourcePos.Pronoun) && C(i - 2) == "ще" && Has(i - 2, SourcePos.Particle) &&
            C(i - 1) == "се" && Has(i - 1, SourcePos.Particle) && C(i) == "превърне" && Predicate(i);
        ImmutableArray<Support> BoundaryContext(int endIndex, int i)
        {
            var refs = ImmutableArray.CreateBuilder<Support>();
            refs.Add(PredicateRef(i));
            for (int k = endIndex + 1; k < i; k++) refs.Add(Surface(k));
            if (Impersonal(i)) refs.AddRange(ImpersonalContext(i));
            if (NeuterFrame(i))
            {
                refs.Add(Ref(i - 4, SourcePos.Conjunction)); refs.Add(Ref(i - 3, SourcePos.Pronoun));
                refs.Add(Ref(i - 2, SourcePos.Particle)); refs.Add(Ref(i - 1, SourcePos.Particle));
            }
            return refs.ToImmutable();
        }
        bool FirstOrSecondPredicate(int i)
        {
            string c = C(i);
            if (!(c.EndsWith("х", StringComparison.Ordinal) || c.EndsWith("хме", StringComparison.Ordinal) ||
                  c.EndsWith("хте", StringComparison.Ordinal) || c.EndsWith("ш", StringComparison.Ordinal))) return false;
            var indices = Indices(i, x => x.Pos == SourcePos.Verb);
            if (indices.Length == 0) return false;
            foreach (int j in indices)
            {
                var f = words[i].Projection[j].Features;
                if (f.HasConflictingFeatures || f.Person is not (MorphologicalPerson.First or MorphologicalPerson.Second) ||
                    f.VerbForm is not (MorphologicalVerbForm.Finite or MorphologicalVerbForm.Imperative)) return false;
            }
            return true;
        }
        bool FirstOrSecondPronoun(int first, int last)
        {
            for (int i = first; i <= last; i++)
                if (Indices(i, x => x.Pos == SourcePos.Pronoun && !x.Features.HasConflictingFeatures &&
                    (x.Features.Person is MorphologicalPerson.First or MorphologicalPerson.Second)).Length > 0) return true;
            return false;
        }
        ImmutableArray<Support> ShortMasculineRefs(int first, int last)
        {
            var refs = ImmutableArray.CreateBuilder<Support>();
            for (int i = first; i <= last; i++)
            {
                int wordIndex = i;
                var indices = Indices(i, x => !x.Features.HasConflictingFeatures &&
                    x.Features.Article == MorphologicalArticle.Short && x.Features.Gender == MorphologicalGender.Masculine &&
                    x.Features.Number == MorphologicalNumber.Singular &&
                    (x.Pos == SourcePos.Adjective && C(wordIndex).EndsWith("ия", StringComparison.Ordinal) ||
                     x.Pos == SourcePos.Noun && x.Features.RawTag?.StartsWith("Съществително:", StringComparison.OrdinalIgnoreCase) == true &&
                     x.CanonicalLemma.Length > 0 && C(wordIndex) == x.CanonicalLemma + "а"));
                if (indices.Length > 0) refs.Add(new(words[i].TokenIndex, indices, Basis.TypedFeatures));
            }
            return refs.ToImmutable();
        }
        void AddAdverbials(int i)
        {
            for (int a = 0; a < words.Length; a++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Rule? rule = null;
                var context = ImmutableArray.CreateBuilder<Support>();
                context.Add(PredicateRef(i));
                if (a > i && C(a) == "заднешком" && Preposition(C(a + 1)) && Bridge(a, a + 1))
                { rule = Rule.Zadneshkom; context.Add(Ref(a + 1, SourcePos.Preposition)); }
                else if (a + 2 == i && C(a) == "криво-ляво" && C(a + 1) == "ще" && Predicate(i) && Chain(a, i))
                { rule = Rule.KrivoLyavo; context.Add(Surface(a + 1)); }
                else if (a == i + 1 && C(i) == "беше" && C(a) == "къде-къде" && Has(a + 1, SourcePos.Adverb) &&
                    Preposition(C(a + 2)) && Chain(i, a + 2))
                { rule = Rule.KadeKade; context.Add(Ref(a + 1, SourcePos.Adverb)); context.Add(Ref(a + 2, SourcePos.Preposition)); }
                else if (a == i + 1 && (C(a) is "напред-назад" or "насам-натам") && Predicate(i))
                {
                    if (Preposition(C(a + 1)) && Chain(i, a + 1))
                    { rule = Rule.Directional; context.Add(Ref(a + 1, SourcePos.Preposition)); }
                    else if (C(a + 1) == "и" && Has(a + 1, SourcePos.Conjunction) && Predicate(a + 2) && Chain(i, a + 2))
                    { rule = Rule.Directional; context.Add(Ref(a + 1, SourcePos.Conjunction)); context.Add(PredicateRef(a + 2)); }
                }
                else if (a + 2 == i && C(a) == "замалко" && C(a + 1) == "не" && Has(a + 1, SourcePos.Particle) &&
                    C(i) == "стъпи" && Predicate(i) && Chain(a, i))
                { rule = Rule.Zamalko; context.Add(Ref(a + 1, SourcePos.Particle)); }
                if (rule.HasValue) Emit(Role.Adverbial, rule.Value, a, a, [Surface(a)], context.ToImmutable());
            }
        }
        void Emit(Role role, Rule rule, int first, int last, ImmutableArray<Support> targets, ImmutableArray<Support> context)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Deterministic dispatch order. Different derivations are not silently deduplicated.
            output.Add(new(role, rule, words[first].Start,
                words[last].Start + words[last].Length - words[first].Start, targets, context));
        }
        Result ProductiveNominal()
        {
            int head = resolution.SubjectWordIndex, predicate = resolution.PredicateWordIndex;
            if (head < 1 || predicate != words.Length - 1 || head != predicate - 1 || !words.Any(w => w.UsesGraph) ||
                resolution.Provenance.Length != words.Length)
                throw new ArgumentException("Unsupported bound nominal phrase topology.");
            var bound = new Dictionary<int, Support>();
            foreach (Support support in resolution.Provenance)
            { cancellationToken.ThrowIfCancellationRequested(); if (!bound.TryAdd(support.TokenIndex, support)) throw new ArgumentException("Duplicate nominal support."); }
            Support Bound(int index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!bound.TryGetValue(words[index].TokenIndex, out Support? support) || support.ReadingIndices.Length == 0)
                    throw new ArgumentException("Nominal phrase needs every original bound support.");
                return support;
            }
            Support headSupport = Bound(head), predicateSupport = Bound(predicate);
            var attributes = ImmutableArray.CreateBuilder<Support>();
            var roles = ImmutableArray.CreateBuilder<Candidate>();
            for (int a = 0; a < head; a++)
            {
                Support attribute = Bound(a);
                attributes.Add(attribute);
                roles.Add(new(Role.Attribute, Rule.ProductiveNominalPhraseAttribute, words[a].Start, words[a].Length,
                    [attribute], [headSupport]));
            }
            roles.Insert(0, new(Role.Predicate, Rule.ProductiveNominalPhrasePredicate, words[predicate].Start,
                words[predicate].Length, [predicateSupport], [headSupport]));
            // Subject span covers the contiguous nominal phrase; target remains its one noun head.
            roles.Insert(1, new(Role.Subject, Rule.ProductiveNominalPhraseSubject, words[0].Start,
                words[head].Start + words[head].Length - words[0].Start, [headSupport],
                attributes.ToImmutable().Add(predicateSupport)));
            return new(Status.Calculated, words, roles.ToImmutable(), resolution);
        }

        void ValidateReferences(ImmutableArray<Support> refs)
        {
            if (refs.IsDefault) throw new ArgumentException("Unbound resolution evidence.");
            foreach (Support support in refs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int found = -1;
                for (int i = 0; i < words.Length; i++) if (words[i].TokenIndex == support.TokenIndex) { found = i; break; }
                if (found < 0 || support.ReadingIndices.IsDefault) throw new ArgumentException("Foreign support reference.");
                int previous = -1;
                foreach (int index in support.ReadingIndices)
                {
                    if (index <= previous || index >= words[found].Projection.Length)
                        throw new ArgumentException("Invalid original reading ordinal.");
                    previous = index;
                }
            }
        }
    }

    private static bool IsNominal(SourcePos pos) => pos is SourcePos.Adjective or SourcePos.Noun or SourcePos.Numeral or SourcePos.ProperNoun or SourcePos.Pronoun;
    private static bool Cyrillic(char c) => c is >= 'Ѐ' and <= 'ӿ' or >= 'Ԁ' and <= 'ԯ';
    private static bool Copula(string c) => c is "съм" or "си" or "е" or "сме" or "сте" or "са" or "бях" or "беше" or "бяха" or "бъда" or "бъде";
    private static bool Preposition(string c) => c is "в" or "във" or "до" or "за" or "из" or "към" or "на" or "над" or "от" or "по" or "под" or "пред" or "през" or "при" or "с" or "със" or "след";
    private static bool FutureClitic(string c) => c is "го" or "я" or "ги" or "ме" or "те" or "ни";
    private static bool DaShort(string c) => c is "ти" or "си" or "ми" or "му" or "ви" or "им" or "ни";
    private static bool Possessive(string c) => c is "си" or "ми" or "му" or "ви" or "им" or "ни";
    private static bool DaTriple(string main, string clitic, string sub) => (main, clitic, sub) is
        ("искаха", "си", "върнат") or ("искаха", "ми", "кажат") or ("можеха", "си", "помогнат") or
        ("можеха", "му", "помогнат") or ("трябва", "им", "върнат");
    private static bool AttributeShape(string c, CancellationToken cancellationToken)
    {
        if (c.Length < 12) return false;
        foreach (char ch in c)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Cyrillic(ch)) return false;
        }
        if (c.StartsWith("непреот", StringComparison.Ordinal) && c.Contains("стъп", StringComparison.Ordinal) &&
            c.EndsWith("еният", StringComparison.Ordinal)) return true;
        if (!c.EndsWith("ен", StringComparison.Ordinal)) return false;
        string[] markers = ["тъмно", "светло", "ярко", "бледо", "лилав", "червен", "син", "зелен", "жълт", "север", "юж", "източ", "запад"];
        int matches = 0;
        foreach (string marker in markers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (c.Contains(marker, StringComparison.Ordinal)) matches++;
        }
        return matches >= 2;
    }
}
