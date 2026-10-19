namespace BgLang.Contracts;

/// <summary>
/// A tag projection, never an inferred reading or a lexical verdict. Missing or conflicting
/// axes are null; RawTag retains all source semantics, including features outside this projection.
/// </summary>
public readonly record struct MorphologicalFeatures(
    string? RawTag,
    MorphologicalPartOfSpeech PartOfSpeech,
    string? ParadigmId,
    MorphologicalPerson? Person,
    MorphologicalNumber? Number,
    MorphologicalGender? Gender,
    MorphologicalTense? Tense,
    MorphologicalVerbForm? VerbForm,
    MorphologicalArticle? Article,
    bool HasUnrecognizedFeatures,
    bool HasConflictingFeatures)
{
    /// <summary>
    /// Projects Bulgarian compatibility tags without inspecting a word's spelling.
    /// Unmarked Legacy noun/adjective/verb number is singular; unmarked noun/adjective
    /// article is indefinite. Personal-pronoun types 90/91/95/96 license person and number.
    /// Simple imperatives license second person and no finite tense; Legacy First/Present
    /// boilerplate is projected without changing RawTag or hiding contradictory markers.
    /// </summary>
    public static MorphologicalFeatures FromTag(string? fullTag)
    {
        if (string.IsNullOrWhiteSpace(fullTag))
            return new(fullTag, MorphologicalPartOfSpeech.Unknown, null, null, null, null, null, null, null, false, false);
        int end = fullTag.IndexOfAny([',', ';']);
        string head = end < 0 ? fullTag : fullTag[..end];
        int colon = head.IndexOf(':');
        string category = colon < 0 ? head : head[..colon];
        string? paradigm = colon < 0 ? null : head[(colon + 1)..];
        MorphologicalPartOfSpeech pos = Category(category);
        if (pos == MorphologicalPartOfSpeech.Unknown)
            return new(fullTag, pos, paradigm, null, null, null, null, null, null, true, false);
        string[] atoms = end < 0 ? [] : fullTag[(end + 1)..].Split(
            [',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        MorphologicalPerson? person = null;
        MorphologicalNumber? number = null;
        MorphologicalGender? gender = null;
        MorphologicalTense? tense = null;
        MorphologicalVerbForm? form = null;
        MorphologicalArticle? article = null;
        bool unknown = false, conflict = false;
        bool nonFinite = pos == MorphologicalPartOfSpeech.Verb && atoms.Any(IsNonFinite);
        int personAtoms = 0;
        bool numberConflict = false, genderConflict = false, tenseConflict = false, formConflict = false;
        bool nominal = pos is MorphologicalPartOfSpeech.Noun or MorphologicalPartOfSpeech.Adjective;
        bool definite = false, full = false, shortArticle = false, indefinite = false;
        foreach (string atom in atoms)
        {
            if (Starts(atom, "1л.")) { person = MorphologicalPerson.First; personAtoms++; }
            else if (Starts(atom, "2л.")) { person = MorphologicalPerson.Second; personAtoms++; }
            else if (Starts(atom, "3л.")) { person = MorphologicalPerson.Third; personAtoms++; }
            else if (Starts(atom, "мн.ч.")) Set(ref number, MorphologicalNumber.Plural, ref numberConflict);
            else if (Starts(atom, "ед.ч.")) Set(ref number, MorphologicalNumber.Singular, ref numberConflict);
            else if (Equal(atom, "Бройна") && nominal) Set(ref number, MorphologicalNumber.Count, ref numberConflict);
            else if (Equal(atom, "Мъжки")) Set(ref gender, MorphologicalGender.Masculine, ref genderConflict);
            else if (Equal(atom, "Женски")) Set(ref gender, MorphologicalGender.Feminine, ref genderConflict);
            else if (Equal(atom, "Среден")) Set(ref gender, MorphologicalGender.Neuter, ref genderConflict);
            else if (nominal && Equal(atom, "Членуван")) definite = true;
            else if (nominal && Equal(atom, "Пълен")) full = true;
            else if (nominal && (Equal(atom, "Непълен") || Equal(atom, "Кратка"))) shortArticle = true;
            else if (nominal && Equal(atom, "Нечленуван")) indefinite = true;
            else if (pos == MorphologicalPartOfSpeech.Verb && Equal(atom, "Повелително"))
                Set(ref form, MorphologicalVerbForm.Imperative, ref formConflict);
            else if (pos == MorphologicalPartOfSpeech.Verb && IsNonFinite(atom))
            {
                Set(ref form, Starts(atom, "Деепричастие") ? MorphologicalVerbForm.Converb : MorphologicalVerbForm.Participle, ref formConflict);
                if (Starts(atom, "Сегашно")) Set(ref tense, MorphologicalTense.Present, ref tenseConflict);
                else if (Starts(atom, "Минало")) Set(ref tense, MorphologicalTense.Past, ref tenseConflict);
            }
            // Non-finite class markers outrank the finite compatibility boilerplate in Legacy tags.
            else if (pos == MorphologicalPartOfSpeech.Verb && nonFinite &&
                (Equal(atom, "Сегашно") || Starts(atom, "Минало") || Starts(atom, "Бъдеще"))) { }
            else if (pos == MorphologicalPartOfSpeech.Verb && Equal(atom, "Сегашно")) Set(ref tense, MorphologicalTense.Present, ref tenseConflict);
            else if (pos == MorphologicalPartOfSpeech.Verb && Equal(atom, "Минало_свършено")) Set(ref tense, MorphologicalTense.Aorist, ref tenseConflict);
            else if (pos == MorphologicalPartOfSpeech.Verb && Equal(atom, "Минало_несвършено")) Set(ref tense, MorphologicalTense.Imperfect, ref tenseConflict);
            else if (pos == MorphologicalPartOfSpeech.Verb && Starts(atom, "Минало")) Set(ref tense, MorphologicalTense.Past, ref tenseConflict);
            else if (pos == MorphologicalPartOfSpeech.Verb && Starts(atom, "Бъдеще")) Set(ref tense, MorphologicalTense.Future, ref tenseConflict);
            else unknown = true;
        }
        if (pos == MorphologicalPartOfSpeech.PersonalPronoun)
        {
            (MorphologicalPerson? Person, MorphologicalNumber? Number) pronoun = paradigm switch
            {
                "90" => (MorphologicalPerson.First, MorphologicalNumber.Singular),
                "91" => (MorphologicalPerson.Second, MorphologicalNumber.Singular),
                "95" => (MorphologicalPerson.First, MorphologicalNumber.Plural),
                "96" => (MorphologicalPerson.Second, MorphologicalNumber.Plural),
                _ => (null, null)
            };
            if (pronoun.Person is null) { person = null; number = null; unknown = true; }
            else
            {
                if (personAtoms > 0 && person != pronoun.Person) { person = null; conflict = true; }
                else person = pronoun.Person;
                if (numberConflict || number is not null && number != pronoun.Number) { number = null; conflict = true; }
                else number = pronoun.Number;
            }
        }
        if (personAtoms > 1) { person = null; conflict = true; }
        if (pos == MorphologicalPartOfSpeech.Verb)
        {
            if (nonFinite) person = null;
            else if (form == MorphologicalVerbForm.Imperative)
            {
                // Legacy ConvertType inherits First/Present from the lexical base into imperative slots.
                if (personAtoms <= 1 && (person is null or MorphologicalPerson.Second ||
                    person == MorphologicalPerson.First && tense == MorphologicalTense.Present && !tenseConflict))
                    person = MorphologicalPerson.Second;
                else { person = null; conflict = true; }
                if (tense is not null and not MorphologicalTense.Present) conflict = true;
                tense = null;
            }
            else if (form is null && !formConflict && person is not null) form = MorphologicalVerbForm.Finite;
        }
        if (number is null && !numberConflict && (nominal || pos == MorphologicalPartOfSpeech.Verb))
            number = MorphologicalNumber.Singular;
        if (nominal)
        {
            if (full && shortArticle || indefinite && (full || shortArticle || definite)) conflict = true;
            else article = full ? MorphologicalArticle.Full : shortArticle ? MorphologicalArticle.Short :
                definite ? MorphologicalArticle.Definite : MorphologicalArticle.Indefinite;
        }
        conflict |= numberConflict || genderConflict || tenseConflict || formConflict;
        return new(fullTag, pos, paradigm, person, number, gender, tense, form, article, unknown, conflict);
    }

    private static MorphologicalPartOfSpeech Category(string category) => category.ToUpperInvariant() switch
    {
        "СЪЩЕСТВИТЕЛНО" or "СЪЩЕСТВИТЕЛНО ИМЕ" => MorphologicalPartOfSpeech.Noun,
        "ПРИЛАГАТЕЛНО" or "ПРИЛАГАТЕЛНО ИМЕ" => MorphologicalPartOfSpeech.Adjective,
        "ГЛАГОЛ" => MorphologicalPartOfSpeech.Verb,
        "ЛИЧНО_МЕСТОИМЕНИЕ" => MorphologicalPartOfSpeech.PersonalPronoun,
        "МЕСТОИМЕНИЕ" or "ПРИТЕЖАТЕЛНО_МЕСТОИМЕНИЕ" or "ВЪПРОСИТЕЛНО_МЕСТОИМЕНИЕ" or
        "ПОКАЗАТЕЛНО_МЕСТОИМЕНИЕ" or "ОТНОСИТЕЛНО_МЕСТОИМЕНИЕ" or "ОБОБЩИТЕЛНО_МЕСТОИМЕНИЕ" or
        "НЕОПРЕДЕЛИТЕЛНО_МЕСТОИМЕНИЕ" or "ОТРИЦАТЕЛНО_МЕСТОИМЕНИЕ" => MorphologicalPartOfSpeech.Pronoun,
        "ЧИСЛИТЕЛНО" or "БРОЙНО_ЧИСЛИТЕЛНО" or "РЕДНО_ЧИСЛИТЕЛНО" => MorphologicalPartOfSpeech.Numeral,
        "НАРЕЧИЕ" => MorphologicalPartOfSpeech.Adverb,
        "ЧАСТИЦА" => MorphologicalPartOfSpeech.Particle,
        "ПРЕДЛОГ" => MorphologicalPartOfSpeech.Preposition,
        "СЪЮЗ" => MorphologicalPartOfSpeech.Conjunction,
        "МЕЖДУМЕТИЕ" => MorphologicalPartOfSpeech.Interjection,
        "ИМЕ" or "ФАМИЛИЯ" or "МЯСТО" => MorphologicalPartOfSpeech.ProperNoun,
        _ => MorphologicalPartOfSpeech.Unknown
    };

    private static void Set<T>(ref T? axis, T value, ref bool conflict) where T : struct, Enum
    {
        if (conflict) return;
        if (axis is { } previous && !previous.Equals(value)) { axis = null; conflict = true; }
        else axis = value;
    }
    private static bool Equal(string atom, string value) => atom.Equals(value, StringComparison.OrdinalIgnoreCase);
    private static bool Starts(string atom, string value) => atom.StartsWith(value, StringComparison.OrdinalIgnoreCase);
    private static bool IsNonFinite(string atom) => atom.Contains("деятелно", StringComparison.OrdinalIgnoreCase) ||
        atom.Contains("страдателно", StringComparison.OrdinalIgnoreCase) || Starts(atom, "Деепричастие");
}
