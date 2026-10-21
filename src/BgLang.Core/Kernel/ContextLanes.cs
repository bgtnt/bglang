// Seven bounded contextual lanes over the complete response reading inventory.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BgLang.Contracts;
using Guard = BgLang.Core.ContextEvidenceGuards;

namespace BgLang.Core;

internal static class ContextLanes
{
    // Exact source Category, NOT a cast from current MorphologicalPartOfSpeech.
    internal enum Category { Unknown, Other, Noun, Pronoun, Verb, Adjective, Number, Adposition, Particle }
    internal sealed record ReadingView(WordReading Reading, MorphologicalFeatures Features,
        Category Category, bool SourceAdverb, bool SourceTransparentAdverb, bool SourceConjunction, bool SourcePersonalPronoun,
        bool SourceGenericUnknown);
    internal sealed record TokenView(int OriginalIndex, TokenAnalysis Token, string CanonicalText,
        IReadOnlyList<ReadingView> Readings, bool Complete);
    private sealed record Group(int Token, Category Category, MorphologicalPerson? Person,
        MorphologicalNumber? Number, int[] Members);
    private sealed record ClosedClause(bool Adverb, int? Marker, bool Clitic);

    private const string FirstSecond = "typed_first_second_pronoun_ambiguous_predicate.v1";
    private const string Third = "closed_third_person_pronoun_nominal_verb_predicate.v1";
    private const string AmbiguousSubject = "closed_ambiguous_personal_pronoun_decisive_predicate.v1";
    private const string Negative = "closed_negative_particle_postverbal_full_article_subject.v1";
    private const string Particle = "closed_particle_compatible_postverbal_full_article_subject.v1";
    private const string Postverbal = "closed_postverbal_full_article_subject.v1";
    private const string PrepEvidence = "closed_adposition_phrase_typed_adjective_noun_agreement";
    // Historical source compatibility choice, NOT a new owner/resource requirement.
    private const int SourceBackwardSubjectDistance = 4;

    // Caller supplies the already validated identity ordering seam; no invented stable IDs.
    // Invoke the unchanged ContextJointSelection.Select on the returned input.
    internal static ContextOverlayInput Calculate(string text, SentenceSpan sentence,
        IReadOnlyList<TokenView> words,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> sourceCanonicalKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(text, sentence, words, cancellationToken);
        var state = new Calculation(text, sentence, words, sourceCanonicalKey, cancellationToken);
        return state.Run();
    }

    private sealed class Calculation(string text, SentenceSpan sentence, IReadOnlyList<TokenView> words,
        Func<IReadOnlyList<ContextOverlayRoleSelection>, string> sourceKey, CancellationToken cancel)
    {
        private readonly List<ContextOverlayRoleSelection> local = [];
        private readonly List<ContextOverlayCandidate> candidates = [];
        private void Check() => cancel.ThrowIfCancellationRequested();
        private TokenView T(int i) => words[i];
        private bool Ready(int i) => T(i).Readings.Count > 0 &&
            T(i).Readings.All(r => !r.Features.HasConflictingFeatures);
        private bool All(int i, Category category) => Ready(i) && T(i).Readings.All(r => r.Category == category);
        private bool Exact(int i, Category a, Category b) => Ready(i) &&
            T(i).Readings.Any(r => r.Category == a) && T(i).Readings.Any(r => r.Category == b) &&
            T(i).Readings.All(r => r.Category == a || r.Category == b);
        private bool Unknown(int i) => T(i).Readings.Any(r => r.Category == Category.Unknown);
        private bool NominalVerb(int i) => T(i).Readings.Any(r => r.Category == Category.Verb) &&
            T(i).Readings.Any(r => r.Category is Category.Noun or Category.Pronoun);
        private bool WordBridge(int a, int b) => b == a + 1 &&
            T(b).OriginalIndex == T(a).OriginalIndex + 1 &&
            Space(T(a).Token.Start + T(a).Token.Length, T(b).Token.Start, nonempty: true);
        private bool Space(int start, int end, bool nonempty = false)
        {
            if (start > end || nonempty && start == end) return false;
            for (int i = start; i < end; i++) { Check(); if (!char.IsWhiteSpace(text[i])) return false; }
            return true;
        }
        private bool Closed()
        {
            if (words.Count == 0 || !Space(sentence.Start, T(0).Token.Start)) return false;
            for (int i = 0; i + 1 < words.Count; i++) { Check(); if (!WordBridge(i, i + 1)) return false; }
            int from = T(words.Count - 1).Token.Start + T(words.Count - 1).Token.Length;
            ReadOnlySpan<char> tail = text.AsSpan(from, sentence.Start + sentence.Length - from).Trim();
            bool terminator = false;
            foreach (char c in tail)
            {
                Check();
                if (c is '.' or '!' or '?' or '\u2026') terminator = true;
                else if (c is not ('"' or '\'' or '\u00bb' or '\u201d' or '\u2019' or ')' or ']' or '}')) return false;
            }
            return terminator;
        }

        internal ContextOverlayInput Run()
        {
            Check();
            int[] ambiguous = Enumerable.Range(0, words.Count).Where(RoleAmbiguous)
                .Select(i => T(i).OriginalIndex).ToArray();
            bool target = ambiguous.Length > 0 || MismatchTopology();
            Check();
            if (!target) return new(false, false, null, [], [], []);
            LocalRoles();
            HashSet<int> excluded = local.Select(r => r.TokenIndex).ToHashSet();
            for (int p = 0; p < words.Count; p++)
            {
                Check();
                if (!Ready(p) || Unknown(p) || excluded.Contains(T(p).OriginalIndex) || KeepMarker(p)) continue;
                foreach (Group predicate in Groups(p, predicate: true))
                {
                    Check();
                    int score = NominalVerb(p) ? 20 : 40;
                    List<string> evidence = ["predicate_has_exact_verb_reading"];
                    if (!NominalVerb(p)) evidence.Add("predicate_role_not_pos_ambiguous");
                    Group[] subjects = BackwardSubjects(p);
                    bool marked = false, post = false;
                    if (subjects.Length == 0 && p > 0 && p + 1 < words.Count &&
                        predicate.Person == MorphologicalPerson.Third && IsNumber(predicate.Number) &&
                        ClauseMarker(p - 1) && All(p + 1, Category.Noun) && WordBridge(p - 1, p) && WordBridge(p, p + 1))
                    {
                        subjects = Groups(p + 1, false).Where(g => g.Number == predicate.Number).ToArray();
                        if (subjects.Length != 1) subjects = [];
                        marked = subjects.Length > 0;
                    }
                    if (subjects.Length == 0 && p == 0 && words.Count == 2 && Closed() &&
                        predicate.Person == MorphologicalPerson.Third && FullSubject(1))
                    {
                        subjects = Groups(1, false).Where(g => g.Number == predicate.Number).ToArray();
                        if (subjects.Length != 1) subjects = [];
                        post = subjects.Length > 0;
                    }
                    if (subjects.Length == 0) { Add(predicate, null, score, evidence, null, false); continue; }
                    bool joint = false;
                    foreach (Group subject in subjects)
                    {
                        Check();
                        Guard.Agreement agreement = Agreement(subject, predicate);
                        bool sa = RoleAmbiguous(subject.Token), pa = RoleAmbiguous(p);
                        ClosedClause? closed = ThirdTopology(subject.Token, p);
                        string? lane = Authority(subject, predicate, agreement, sa, pa, marked, post, closed);
                        bool mismatchPreview = lane is null && !sa && !pa &&
                            agreement == Guard.Agreement.Mismatch && closed is not null &&
                            All(p, Category.Verb) && ThirdSubject(subject) && PersonalGroup(predicate);
                        if (sa && pa && (agreement != Guard.Agreement.Compatible ||
                            !(subject.Token == 0 && p == 1 && words.Count == 2 && Closed()))) continue;
                        if ((sa || pa) && agreement == Guard.Agreement.Unmeasured ||
                            subject.Category == Category.Noun && agreement == Guard.Agreement.Mismatch) continue;
                        List<string> codes = [.. evidence, marked
                            ? Exact(p - 1, Category.Particle, Category.Verb)
                                ? "unresolved_particle_verb_marker_finite_verb_postverbal_subject_agreement"
                                : "typed_particle_finite_verb_postverbal_subject_agreement"
                            : post ? "typed_finite_verb_postverbal_full_article_subject_agreement"
                            : "explicit_preverbal_subject_role"];
                        int jointScore = score + 30 + Math.Max(0, 8 - Math.Max(1, Math.Abs(p - subject.Token)));
                        if (mismatchPreview) codes.Add("closed_clause_pronoun_agreement_mismatch_diagnostic_preview");
                        if (lane == Third || mismatchPreview)
                        {
                            if (closed!.Adverb) codes.Add("closed_clause_decisive_adverb_adjunct");
                            if (closed.Marker is not null) codes.Add("closed_clause_supported_particle_marker");
                            if (closed.Clitic) codes.Add("closed_clause_short_oblique_personal_pronoun_clitic");
                        }
                        if (subject.Category == Category.Pronoun) { jointScore += 5; codes.Add("subject_pronoun_role_compatible"); }
                        if (agreement == Guard.Agreement.Compatible) { jointScore += 12; codes.Add("subject_predicate_agreement_compatible"); }
                        else if (agreement == Guard.Agreement.Mismatch) codes.Add("subject_predicate_agreement_mismatch_retained_for_diagnostic");
                        Add(predicate, subject, jointScore, codes, lane, marked || post);
                        joint = true;
                    }
                    if (!joint && !RoleAmbiguous(p)) Add(predicate, null, score, evidence, null, false);
                }
            }
            Check();
            return new(true, false, null, candidates.ToArray(), local.ToArray(), ambiguous);
        }

        // Six joint authority lanes; PREP is calculated by LocalRoles below.
        private string? Authority(Group s, Group p, Guard.Agreement agreement,
            bool sa, bool pa, bool marked, bool post, ClosedClause? closed)
        {
            if (s.Category == Category.Pronoun && All(s.Token, Category.Pronoun) &&
                s.Person is MorphologicalPerson.First or MorphologicalPerson.Second && IsNumber(s.Number) && pa)
                return FirstSecond; // A measured mismatch remains explicitly labelled diagnostic support.
            if (!sa && pa && agreement == Guard.Agreement.Compatible && closed is not null &&
                ThirdSubject(s) && PersonalGroup(p) && Competition(p, s)) return Third;
            if (marked && pa && agreement == Guard.Agreement.Compatible && PersonalGroup(p) &&
                p.Person == MorphologicalPerson.Third && words.Count == 3 && p.Token == 1 && Closed() && FullSubject(s.Token))
            {
                if (T(0).CanonicalText == "не" && All(0, Category.Particle)) return Negative;
                if (Competition(p, s)) return Exact(0, Category.Particle, Category.Verb) ? Postverbal : Particle;
            }
            if (post && pa && agreement == Guard.Agreement.Compatible && PersonalGroup(p) &&
                p.Person == MorphologicalPerson.Third && Competition(p, s)) return Postverbal;
            if (sa && !pa && agreement == Guard.Agreement.Compatible &&
                s.Token == 0 && p.Token == 1 && words.Count == 2 && Closed() &&
                AmbiguousPronounSubject(s) && PersonalGroup(p)) return AmbiguousSubject;
            return null;
        }

        private Group[] Groups(int token, bool predicate)
        {
            Check();
            // Original indices are group members; equal tuples at0/32 remain TWO members.
            return T(token).Readings.Select((r, i) => (r, i))
                .Where(x => predicate ? x.r.Category == Category.Verb : x.r.Category is Category.Noun or Category.Pronoun)
                .GroupBy(x => (x.r.Category,
                    Person: predicate || x.r.Category == Category.Pronoun ? x.r.Features.Person : null,
                    x.r.Features.Number))
                .Select(g => new Group(token, g.Key.Category, g.Key.Person, g.Key.Number, g.Select(x => x.i).ToArray()))
                .ToArray();
        }
        private Guard.Agreement Agreement(Group s, Group p)
        {
            if (!Ready(s.Token) || !Ready(p.Token) || !IsNumber(s.Number) || !IsNumber(p.Number) || p.Person is null)
                return Guard.Agreement.Unmeasured;
            if (s.Category == Category.Pronoun && s.Person is null) return Guard.Agreement.Unmeasured;
            MorphologicalPerson? person = s.Category == Category.Noun ? MorphologicalPerson.Third : s.Person;
            return person == p.Person && s.Number == p.Number ? Guard.Agreement.Compatible : Guard.Agreement.Mismatch;
        }
        private bool PersonalGroup(Group p) => p.Members.Length > 0 && p.Members.All(i =>
            Guard.IsPersonalPredicate(T(p.Token).Readings[i].Features) &&
            T(p.Token).Readings[i].Features.Person == p.Person && T(p.Token).Readings[i].Features.Number == p.Number);
        private bool FullSubject(int i) => All(i, Category.Noun) && T(i).Readings.All(r =>
            !r.Features.HasConflictingFeatures && r.Features.Article == MorphologicalArticle.Full &&
            r.Features.Gender == MorphologicalGender.Masculine && r.Features.Number == MorphologicalNumber.Singular);
        private bool ThirdSubject(Group s) => s.Category == Category.Pronoun &&
            s.Person == MorphologicalPerson.Third && IsNumber(s.Number) &&
            Norm(T(s.Token).CanonicalText) is "той" or "тя" or "то" or "те" &&
            s.Members.Length == T(s.Token).Readings.Count && Ready(s.Token) && T(s.Token).Readings.All(r =>
                r.Category == Category.Pronoun && r.SourcePersonalPronoun &&
                Norm(r.Reading.Lemma) is "той" or "тя" or "то" or "те" &&
                r.Features.Person == s.Person && r.Features.Number == s.Number);
        private bool Competition(Group p, Group s)
        {
            if (!Exact(p.Token, Category.Noun, Category.Verb) || !PersonalGroup(p)) return false;
            var selected = p.Members.ToHashSet(); bool noun = false;
            for (int i = 0; i < T(p.Token).Readings.Count; i++)
            {
                Check(); if (selected.Contains(i)) continue;
                ReadingView r = T(p.Token).Readings[i];
                if (r.Category == Category.Noun)
                {
                    if (!IsNumber(r.Features.Number) || r.Features.Number == s.Number) return false;
                    noun = true;
                }
                else if (!Guard.IsPersonalPredicate(r.Features) ||
                    r.Features.Person == s.Person && r.Features.Number == s.Number) return false;
            }
            return noun;
        }
        private bool AmbiguousPronounSubject(Group s)
        {
            if (!Exact(s.Token, Category.Pronoun, Category.Verb) ||
                s.Person is not (MorphologicalPerson.First or MorphologicalPerson.Second) || !IsNumber(s.Number)) return false;
            var selected = s.Members.ToHashSet(); bool alternative = false;
            for (int i = 0; i < T(s.Token).Readings.Count; i++)
            {
                Check(); ReadingView r = T(s.Token).Readings[i];
                if (selected.Contains(i))
                {
                    if (r.Category != Category.Pronoun || !r.SourcePersonalPronoun ||
                        Norm(r.Reading.Lemma) is not ("аз" or "ти" or "ние" or "вие") ||
                        r.Features.Person != s.Person || r.Features.Number != s.Number) return false;
                }
                else
                {
                    if (r.Category != Category.Verb || !Guard.IsPersonalPredicate(r.Features) ||
                        r.Features.Person == s.Person && r.Features.Number == s.Number) return false;
                    alternative = true;
                }
            }
            return selected.Count > 0 && alternative;
        }

        private Group[] BackwardSubjects(int p)
        {
            for (int i = p - 1; i >= Math.Max(0, p - SourceBackwardSubjectDistance); i--)
            {
                Check();
                if (!WordBridge(i, i + 1) || i > 0 && All(i - 1, Category.Adposition)) break;
                if (Clitic(i)) continue;
                if (!Ready(i) || Unknown(i)) break;
                Group[] groups = Groups(i, false);
                if (groups.Length > 0)
                {
                    if (i == 0 && !Space(sentence.Start, T(i).Token.Start)) break;
                    return groups;
                }
                if (!(T(i).Readings.All(r => r.SourceTransparentAdverb || r.Category == Category.Particle) || PreMarker(i))) break;
            }
            return [];
        }
        private bool Clitic(int i)
        {
            if (!Ready(i)) return false;
            bool typed = false;
            foreach (ReadingView r in T(i).Readings)
            {
                Check();
                if (r.Category == Category.Pronoun && r.SourcePersonalPronoun &&
                    r.Features.PartOfSpeech == MorphologicalPartOfSpeech.PersonalPronoun &&
                    r.Features.IsShortForm == true && r.Features.Case is MorphologicalCase.Accusative or MorphologicalCase.Dative)
                { typed = true; continue; }
                if (r.Category == Category.Pronoun && (r.SourcePersonalPronoun || r.Features.IsShortForm == true) ||
                    r.Category == Category.Particle || r.SourceConjunction ||
                    r.SourceGenericUnknown && Norm(r.Reading.Lemma) == Norm(T(i).CanonicalText)) continue;
                return false;
            }
            return typed;
        }
        private bool CompatibleMarker(int i) => Ready(i) && T(i).Readings.Any(r => r.Category == Category.Particle) &&
            T(i).Readings.All(r => r.Category == Category.Particle || r.SourceConjunction);
        private bool ClauseMarker(int i) => CompatibleMarker(i) || Exact(i, Category.Particle, Category.Verb);
        private bool PreMarker(int i) => CompatibleMarker(i) || Exact(i, Category.Particle, Category.Verb) &&
            T(i).Readings.Where(r => r.Category == Category.Verb).All(r => Guard.IsPresentThirdSingular(r.Features));
        private bool RoleAmbiguous(int i)
        {
            if (Clitic(i)) return false;
            var cats = T(i).Readings.Select(r => r.Category).ToHashSet();
            int roles = (cats.Contains(Category.Verb) ? 1 : 0) +
                (cats.Any(c => c is Category.Noun or Category.Pronoun or Category.Number) ? 1 : 0) +
                (cats.Contains(Category.Adjective) ? 1 : 0) + (cats.Contains(Category.Adposition) ? 1 : 0) +
                (cats.Any(c => c is Category.Particle or Category.Other) ? 1 : 0);
            return roles > 1;
        }
        private ClosedClause? ThirdTopology(int subject, int predicate)
        {
            if (words.Count is < 2 or > 5 || subject != 0 || predicate <= 0 || !Closed()) return null;
            bool adverb = false; int? marker = null, clitic = null;
            for (int i = 1; i < words.Count; i++)
            {
                Check(); if (i == predicate) continue;
                if (Ready(i) && T(i).Readings.All(r => r.SourceAdverb))
                { if (adverb) return null; adverb = true; }
                else if (PreMarker(i)) { if (marker is not null) return null; marker = i; }
                else if (Clitic(i)) { if (clitic is not null) return null; clitic = i; }
                else return null;
            }
            if (clitic is not null && clitic != predicate - 1 || marker is not null && marker != (clitic ?? predicate) - 1) return null;
            return new(adverb, marker, clitic is not null);
        }
        private bool MismatchTopology()
        {
            if (words.Count is < 2 or > 5) return false;
            Group[] subjects = Groups(0, false).Where(ThirdSubject).ToArray();
            if (subjects.Length != 1) return false;
            for (int p = 1; p < words.Count; p++)
            {
                Check(); if (!All(p, Category.Verb)) continue;
                Group[] predicates = Groups(p, true);
                if (predicates.Length == 1 && PersonalGroup(predicates[0]) &&
                    Agreement(subjects[0], predicates[0]) == Guard.Agreement.Mismatch && ThirdTopology(0, p) is not null) return true;
            }
            return false;
        }
        private bool KeepMarker(int marker)
        {
            int p = marker + 1;
            if (p < words.Count && Clitic(p)) p++;
            if (marker <= 0 || p >= words.Count || !Exact(marker, Category.Particle, Category.Verb)) return false;
            if (ThirdTopology(0, p)?.Marker != marker || RoleAmbiguous(0) || Unknown(p)) return false;
            if (MismatchTopology()) return true;
            if (!RoleAmbiguous(p)) return false;
            return Groups(0, false).Any(s => ThirdSubject(s) && Groups(p, true).Any(v =>
                Agreement(s, v) == Guard.Agreement.Compatible && PersonalGroup(v) && Competition(v, s)));
        }

        private void LocalRoles()
        {
            for (int i = 0; i < words.Count; i++)
            {
                Check(); if (!Ready(i) || !RoleAmbiguous(i)) continue;
                // Preview-only source adposition/nominal contexts also exclude predicate generation.
                int[] adpositions = Indices(i, r => r.Category == Category.Adposition);
                if (adpositions.Length > 0 && DecisiveComplement(i))
                { local.Add(Role(ContextOverlayRole.Adposition, i, adpositions, "exact_adposition_with_nominal_complement")); continue; }
                if (i > 0 && i + 1 < words.Count && Exact(i, Category.Adjective, Category.Verb) &&
                    All(i - 1, Category.Adposition) && All(i + 1, Category.Noun) && WordBridge(i - 1, i) && WordBridge(i, i + 1))
                {
                    // Coarse POS adapter changes only the category view, never invents agreement axes.
                    int[] members = Guard.CompatibleAttributeMembers(FeatureView(i), FeatureView(i + 1), cancel);
                    if (members.Length > 0) { local.Add(Role(ContextOverlayRole.Attribute, i, members, PrepEvidence)); continue; }
                }
                if (i == 0 || !NominalVerb(i) || !WordBridge(i - 1, i)) continue;
                int[] nominal = Indices(i, r => r.Category is Category.Noun or Category.Pronoun);
                if (All(i - 1, Category.Adposition))
                { local.Add(Role(ContextOverlayRole.NominalContext, i, nominal, "closed_preposition_nominal_licensor")); continue; }
                if (i + 1 < words.Count && All(i + 1, Category.Adposition) && WordBridge(i, i + 1) &&
                    DecisiveAdjective(i - 1, out var number, out var gender))
                {
                    int[] members = Indices(i, r => r.Category == Category.Noun && r.Features.Number == number &&
                        (number == MorphologicalNumber.Plural || r.Features.Gender == gender));
                    if (members.Length > 0) local.Add(Role(ContextOverlayRole.NominalContext, i, members,
                        "typed_adjective_nominal_agreement_before_decisive_adposition"));
                }
            }
        }
        private bool DecisiveComplement(int i)
        {
            // Source1012-1024: immediate all-NOUN/PRON/NUM complement, no adjective-chain rule.
            int next = i + 1;
            return next < words.Count && WordBridge(i, next) && Ready(next) &&
                T(next).Readings.All(r => r.Category is Category.Noun or Category.Pronoun or Category.Number);
        }
        private bool DecisiveAdjective(int i, out MorphologicalNumber? number, out MorphologicalGender? gender)
        {
            number = null; gender = null; if (!All(i, Category.Adjective)) return false;
            var tuples = T(i).Readings.Select(r => (r.Features.Number, r.Features.Gender)).Distinct().ToArray();
            if (tuples.Length != 1 || !IsNumber(tuples[0].Number) ||
                tuples[0].Number == MorphologicalNumber.Singular && tuples[0].Gender is null) return false;
            (number, gender) = tuples[0]; return true;
        }
        private MorphologicalFeatures[] FeatureView(int i) => T(i).Readings.Select(r => r.Features with
        {
            PartOfSpeech = r.Category switch
            {
                Category.Noun => MorphologicalPartOfSpeech.Noun,
                Category.Adjective => MorphologicalPartOfSpeech.Adjective,
                _ => r.Features.PartOfSpeech
            }
        }).ToArray();
        private int[] Indices(int i, Func<ReadingView, bool> predicate) =>
            T(i).Readings.Select((r, index) => (r, index)).Where(x => predicate(x.r)).Select(x => x.index).ToArray();
        private ContextOverlayRoleSelection Role(ContextOverlayRole role, int i, int[] members, string evidence) =>
            new(role, T(i).OriginalIndex, T(i).Token.Start, T(i).Token.Length, members, [evidence]);
        private void Add(Group p, Group? s, int score, IReadOnlyList<string> evidence, string? lane, bool post)
        {
            Check();
            List<ContextOverlayRoleSelection> roles = [Role(ContextOverlayRole.Predicate, p.Token, p.Members, "exact_verb_reading")];
            if (s is not null) roles.Add(Role(ContextOverlayRole.Subject, s.Token, s.Members,
                post ? "postverbal_nominal_reading" : "preverbal_nominal_reading"));
            candidates.Add(new(T(p.Token).OriginalIndex, !RoleAmbiguous(p.Token) || s is not null,
                lane, score, sourceKey(roles), roles.ToArray(), evidence.ToArray()));
        }
    }

    private static bool IsNumber(MorphologicalNumber? number) => number is MorphologicalNumber.Singular or MorphologicalNumber.Plural;
    private static string Norm(string value) => value.Trim().ToLowerInvariant();
    private static void Validate(string text, SentenceSpan sentence, IReadOnlyList<TokenView> words, CancellationToken cancel)
    {
        if (sentence.Start < 0 || sentence.Length < 0 || (long)sentence.Start + sentence.Length > text.Length)
            throw new ArgumentException("Sentence is outside original UTF16 source.");
        int end = sentence.Start, priorIndex = -1;
        foreach (TokenView word in words)
        {
            cancel.ThrowIfCancellationRequested();
            TokenAnalysis t = word.Token;
            if (!word.Complete) throw new InvalidOperationException("Context requires complete inventories; unavailable is not empty.");
            if (t.Kind != TokenKind.Word || word.OriginalIndex <= priorIndex || t.Length <= 0 ||
                t.Start < end || (long)t.Start + t.Length > (long)sentence.Start + sentence.Length ||
                !text.AsSpan(t.Start, t.Length).SequenceEqual(t.Text) || string.IsNullOrWhiteSpace(word.CanonicalText))
                throw new ArgumentException("Tokens must be original ordered Word spans in one sentence.");
            end = t.Start + t.Length; priorIndex = word.OriginalIndex;
        }
        cancel.ThrowIfCancellationRequested();
    }
}
