// BgLang Live Lab: an experimental UI over the unchanged shared transport in ../live-client.js.
// The textarea is the only editable text. The coloured backdrop behind it is decorative and aria-hidden;
// the checks list and the word card carry the same information as text.
import {createPostLane, createLiveController, completeReadings, caretWord, bindContinuation, applyContinuation} from '../live-client.js';

export const MAX_LENGTH = 4096;

// Presentation only: the warning set matches the current /demo allowlist. Unknown codes stay information.
const WARNING_CODES = new Set([
  'missing_terminal_punctuation',
  'sentence_initial_lowercase',
  'mixed_script_ocr_known_candidate',
  'digit_shape_ocr_known_candidate',
  'accent_sensitive_i_grave',
  'spelling_unknown_word_unique_distance_one_candidate',
  'joined_verb_negation_should_be_separate',
  'pronoun_verb_agreement_mismatch',
  'preposition_missing_complement',
  'full_article_expected_for_subject_candidate',
  'short_article_expected_after_preposition_candidate',
  'productive_morphology_agreement_mismatch',
  'productive_attribute_agreement_mismatch'
]);
const UNSUPPORTED_CODES = new Set(['full_short_article_check_not_ready']);
const AGREEMENT_CODES = new Set([
  'pronoun_verb_agreement_mismatch',
  'productive_morphology_agreement_mismatch',
  'productive_attribute_agreement_mismatch'
]);
// Codes whose projectedText is offered as an explicit, user-confirmed replacement of the original span.
const REPLACEABLE_CODES = new Set([
  'spelling_unknown_word_unique_distance_one_candidate',
  'joined_verb_negation_should_be_separate',
  'accent_sensitive_i_grave',
  'mixed_script_ocr_known_candidate',
  'digit_shape_ocr_known_candidate'
]);

export const MESSAGES = {
  productive_morphology_agreement_mismatch: 'Несъгласуване по лице/число в поддържаната конструкция.',
  pronoun_verb_agreement_mismatch: 'Несъгласуване по лице/число между местоимение и глагол в поддържаната конструкция.',
  productive_attribute_agreement_mismatch: 'Несъгласуване по род/число между определение и съществително в поддържаната конструкция.',
  full_article_expected_for_subject_candidate: 'Ако думата е подлог, се очаква пълен член.',
  short_article_expected_after_preposition_candidate: 'В проверената предложна група се очаква кратък член.',
  preposition_missing_complement: 'След предлога не е намерена дума, към която да се отнася.',
  spelling_unknown_word_unique_distance_one_candidate: 'Няма точен речников прочит; има една близка речникова форма за преглед.',
  joined_verb_negation_should_be_separate: 'Ако е отрицателна глаголна форма, „не“ се пише отделно.',
  accent_sensitive_i_grave: 'Ако е местоимение, се пише „ѝ“.',
  mixed_script_ocr_known_candidate: 'Думата смесва кирилица и латиница; възможна грешка при въвеждане или разпознаване.',
  digit_shape_ocr_known_candidate: 'Цифра на мястото на буква; възможна грешка при разпознаване.',
  missing_terminal_punctuation: 'Изречението няма завършващ препинателен знак.',
  sentence_initial_lowercase: 'Изречението започва с малка буква.',
  full_short_article_check_not_ready: 'Проверката за членуване е частична; тук няма установено решение.',
  clause_missing_predicate: 'Не е намерено сказуемо в проверения фрагмент; възможно заглавие.',
  missing_comma_before_subordinate_conjunction: 'Възможно е да липсва запетая пред подчинено изречение.',
  missing_space_after_punctuation: 'Липсва интервал след препинателния знак.',
  space_before_punctuation: 'Има интервал пред препинателния знак.',
  style_repetition_evidence: 'В текста има повторена речникова форма.',
  reviewed_word_formation_evidence: 'Сложна или производна дума.',
  hyphenated_degree_morphology_evidence: 'Степенувана форма с „по-“ или „най-“.',
  contextual_morphology_reading_evidence: 'Контекстно сведение за прочита на думата.'
};
export const GENERIC_MESSAGE = 'Друго наблюдение от анализа.';

const SEVERITY_ORDER = {warning: 0, unsupported: 1, information: 2};

const POS_NAMES = {
  Noun: 'съществително', Adjective: 'прилагателно', Verb: 'глагол', PersonalPronoun: 'лично местоимение',
  Pronoun: 'местоимение', Numeral: 'числително', Adverb: 'наречие', Particle: 'частица', Preposition: 'предлог',
  Conjunction: 'съюз', Interjection: 'междуметие', ProperNoun: 'собствено име', Unknown: 'неустановена част на речта'
};
const POS_CLASSES = {
  Noun: 'c-noun', Verb: 'c-verb', Adjective: 'c-adj', Adverb: 'c-adv', ProperNoun: 'c-name',
  PersonalPronoun: 'c-pron', Pronoun: 'c-pron', Numeral: 'c-num',
  Preposition: 'c-func', Conjunction: 'c-func', Particle: 'c-func', Interjection: 'c-func'
};
const FEATURE_WORDS = {
  person: {First: '1 л.', Second: '2 л.', Third: '3 л.'},
  number: {Singular: 'ед.ч.', Plural: 'мн.ч.', Count: 'бройна форма'},
  gender: {Masculine: 'м.р.', Feminine: 'ж.р.', Neuter: 'ср.р.'},
  tense: {Present: 'сег. време', Past: 'мин. време', Aorist: 'мин. свършено', Imperfect: 'мин. несвършено', Future: 'бъд. време'},
  verbForm: {Finite: null, Imperative: 'повелително', Participle: 'причастие', Converb: 'деепричастие'},
  article: {Indefinite: 'нечленувано', Definite: 'членувано', Full: 'пълен член', Short: 'кратък член'},
  case: {Accusative: 'вин. падеж', Dative: 'дат. падеж', PrepositionalDative: 'предложен дат.'},
  participleKind: {PastAoristActive: 'мин. св. деятелно', PastImperfectActive: 'мин. несв. деятелно', PastPassive: 'мин. страдателно'},
  nameKind: {GivenName: 'лично име', FamilyName: 'фамилно име', PlaceName: 'място'}
};
const FEATURE_ORDER = ['person', 'number', 'gender', 'tense', 'verbForm', 'participleKind', 'article', 'case', 'nameKind'];

export const LAYERS = {
  pos: [
    ['c-noun', 'съществително'], ['c-verb', 'глагол'], ['c-adj', 'прилагателно'], ['c-adv', 'наречие'],
    ['c-pron', 'местоимение'], ['c-num', 'числително'], ['c-name', 'име'], ['c-multi', 'няколко части на речта'],
    ['c-graph', 'графова хипотеза'], ['c-unknown', 'няма точен прочит']
  ],
  dictionary: [
    ['c-known', 'в речника'], ['c-ambiguous', 'няколко леми или части на речта'],
    ['c-graph', 'графова хипотеза'], ['c-unknown', 'няма точен прочит']
  ],
  none: []
};

// Word card sections. The first two read the accepted analysis; the others ask the API only on an explicit request.
export const TABS = [['readings', 'Прочити'], ['meanings', 'Значения'], ['forms', 'Форми'], ['stress', 'Ударение'], ['formation', 'Словообразуване']];
const STRESS_STATUS = {
  Unknown: 'неизвестно', Supported: 'потвърдено', Inferred: 'изведено', Ambiguous: 'двусмислено', Incomplete: 'частично',
  Conflict: 'противоречиво', Unclassified: 'некласифицирано', InvalidInput: 'невалиден вход'
};
const STRESS_ORIGIN = {
  InputObservation: 'написано в текста', SingleVowelInference: 'една гласна', Editorial: 'редакторско', ReviewedExact: 'прегледано',
  LegacyStored: 'Legacy речник', LegacyCarried: 'Legacy, пренесено', LegacyDisplayInferred: 'Legacy, по изписването',
  DerivationalRulePrediction: 'словообразувателно правило (прогноза)'
};
const MORPH_KIND = {
  Prefix: 'представка', Root: 'корен', Stem: 'основа', DerivationalSuffix: 'наставка', Interfix: 'съединителна гласна',
  InflectionalEnding: 'окончание'
};
// Possible syntactic roles. Object does not claim a direct object and Adverbial does not claim place; Complement is
// the nominal part after a copula, Possessive a short possessive pronoun (си, ми, му…) after a nominal.
export const ROLE_NAMES = {
  Predicate: 'Сказуемо', Subject: 'Подлог', Object: 'Допълнение', Complement: 'Именна част на сказуемото',
  PrepositionalObject: 'Предложно допълнение', Attribute: 'Определение', Adverbial: 'Обстоятелствено пояснение',
  Possessive: 'Кратко притежателно местоимение'
};
// Only routes that change how the roles must be read get a note; the rest stay in the technical details.
const ROUTE_NOTES = {
  Abstained: 'Не е избран основен синтактичен анализ.',
  ClosedThirdPersonMismatchPreview: 'Предварителен преглед при несъгласуване по лице/число.',
  ProductiveGraph: 'Ролите опират на графова морфология (хипотеза).',
  ProductiveGraphPreview: 'Предварителен преглед по графова морфология (хипотеза).',
  ProductiveGraphMismatch: 'Графова хипотеза при несъгласуване.'
};
const SENTENCE_STATUS = {
  IncompleteInventory: 'Прочитите в изречението са непълни, затова ролите не са изчислени.',
  UnsupportedSelectedPredicate: 'Избраното сказуемо е извън поддържаните правила; ролите са частични.'
};

export function tokenAnalysisOf(response, token) {
  if (!token || token.kind !== 'Word') return null;
  if (response?.evidenceFormat !== 'shared-analyses-v1') return token.analysis ?? null;
  const reference = token.analysisRef, list = response.wordAnalyses;
  return Number.isInteger(reference) && reference >= 0 && Array.isArray(list) && reference < list.length
    ? list[reference] ?? null : null;
}

export function featureChips(features) {
  const chips = [];
  for (const key of FEATURE_ORDER) {
    const value = features?.[key];
    if (value === null || value === undefined) continue;
    const word = FEATURE_WORDS[key]?.[value];
    if (word === null) continue;
    chips.push(word ?? String(value));
  }
  if (features?.isShortForm === true) chips.push('кратка форма');
  return chips;
}

// Groups complete readings by lemma and part of speech; identical feature lines are counted, not hidden.
export function readingGroups(rows) {
  const groups = [], byKey = new Map();
  for (const [index, row] of (Array.isArray(rows) ? rows : []).entries()) {
    const lemma = typeof row?.raw?.lemma === 'string' ? row.raw.lemma : '';
    const pos = row?.features?.partOfSpeech ?? 'Unknown';
    const key = lemma + '\u0001' + pos;
    let group = byKey.get(key);
    if (!group) {
      group = {lemma, pos, posName: POS_NAMES[pos] ?? String(pos), lines: [], readingIndices: []};
      byKey.set(key, group);
      groups.push(group);
    }
    group.readingIndices.push(index);
    const line = featureChips(row?.features).join(' · ');
    const existing = group.lines.find(item => item.text === line);
    if (existing) existing.count++;
    else group.lines.push({text: line, count: 1, tag: row?.raw?.partOfSpeech ?? null});
  }
  return groups;
}

function supportTokens(records) {
  const indices = new Set();
  for (const {record} of records) {
    for (const list of [record.readings, record.graphReadings, record.supports]) {
      for (const ref of Array.isArray(list) ? list : []) if (Number.isInteger(ref?.tokenIndex)) indices.add(ref.tokenIndex);
    }
  }
  return [...indices].sort((a, b) => a - b);
}

// Returns returned diagnostic records only (evidence codes never become messages), grouped by code and span.
export function collectChecks(data) {
  const text = typeof data?.text === 'string' ? data.text : '';
  const v = data?.validation;
  const sources = [
    ['diagnostics', data?.diagnostics], ['validation', v?.diagnostics], ['spacing', v?.spacingDiagnostics],
    ['comma', v?.subordinateCommaDiagnostics], ['fragment', v?.fragmentDiagnostics]
  ];
  const groups = new Map();
  for (const [source, list] of sources) {
    for (const record of Array.isArray(list) ? list : []) {
      if (!record || typeof record.code !== 'string') continue;
      const key = record.code + '\u0001' + record.start + '\u0001' + record.length;
      let group = groups.get(key);
      if (!group) groups.set(key, group = {code: record.code, start: record.start, length: record.length, records: []});
      group.records.push({source, record});
    }
  }
  const checks = [];
  for (const group of groups.values()) {
    const {code, start, length, records} = group;
    const validSpan = Number.isInteger(start) && Number.isInteger(length) && start >= 0 && length >= 0 && start + length <= text.length;
    const original = validSpan ? text.slice(start, start + length) : '';
    const severity = WARNING_CODES.has(code) ? 'warning' : UNSUPPORTED_CODES.has(code) ? 'unsupported' : 'information';
    const projected = records.map(({record}) => record.projectedText).find(value => typeof value === 'string' && value.length) ?? null;
    const words = AGREEMENT_CODES.has(code)
      ? supportTokens(records).map(index => data.tokens?.[index]?.text).filter(word => typeof word === 'string' && word.length)
      : [];
    checks.push({
      code, start, length, severity, validSpan, records, projected, words,
      known: Object.hasOwn(MESSAGES, code),
      message: MESSAGES[code] ?? GENERIC_MESSAGE,
      graph: records.some(({record}) => Array.isArray(record.graphReadings) && record.graphReadings.length > 0),
      replaceable: REPLACEABLE_CODES.has(code) && projected !== null && validSpan && replaceableSpan(text, start, length),
      replacement: projected === null ? null : matchCase(original, projected),
      original
    });
  }
  return checks.sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]
    || (Number.isInteger(a.start) ? a.start : Infinity) - (Number.isInteger(b.start) ? b.start : Infinity)
    || a.code.localeCompare(b.code));
}

// Only warnings underline words. Zero-length checks stay in the list without marking a neighbour.
export function warningTokens(data, checks) {
  const marked = new Set();
  for (const check of checks) {
    if (check.severity !== 'warning' || !check.validSpan || check.length === 0) continue;
    (data.tokens || []).forEach((token, index) => {
      if (token.kind === 'Word' && check.start < token.start + token.length && check.start + check.length > token.start) marked.add(index);
    });
  }
  return marked;
}

export function possibleName(data, tokenIndex) {
  const token = data?.tokens?.[tokenIndex];
  const evidence = (data?.properNames?.candidates || []).some(candidate => candidate?.tokenIndex === tokenIndex);
  let signal = false;
  if (token?.kind === 'Word' && /^\p{Lu}/u.test(token.text ?? '')) {
    const sentence = (data.sentences || []).find(s => s.start <= token.start && token.start < s.start + s.length);
    const first = sentence && data.tokens.find(t => t.kind === 'Word' && t.start >= sentence.start && t.start < sentence.start + sentence.length);
    signal = !!first && first !== token;
  }
  return {evidence, signal, possible: evidence || signal};
}

// Raw alternatives keep distance first; the optional contextual order only refines ties, as in /demo.
export function spellingAlternatives(word) {
  const alternatives = Array.isArray(word?.alternatives) ? word.alternatives : [];
  const contextual = Array.isArray(word?.contextualCandidates) ? word.contextualCandidates : [];
  const position = new Map();
  contextual.forEach((candidate, index) => {
    if (Number.isInteger(candidate?.alternativeIndex) && !position.has(candidate.alternativeIndex)) position.set(candidate.alternativeIndex, index);
  });
  return alternatives.map((alternative, index) => ({alternative, index}))
    .filter(({alternative}) => typeof alternative?.form === 'string' && alternative.form.length)
    .sort((a, b) => a.alternative.distance - b.alternative.distance
      || (position.get(a.index) ?? contextual.length + a.index) - (position.get(b.index) ?? contextual.length + b.index) || a.index - b.index)
    .map(({alternative, index}) => {
      const candidate = contextual.find(row => row?.alternativeIndex === index);
      const assessment = typeof candidate?.assessment === 'string' ? candidate.assessment.toLowerCase() : 'unassessed';
      return {form: alternative.form, distance: alternative.distance, index, assessment};
    });
}

// The case policy of /demo (b1011704b): a lowercase Cyrillic candidate copies a simple title-case or
// all-caps original; mixed case and capitals from the dictionary keep the raw form. Unlike /demo, the
// original may contain cased letters of any script, so the OCR projection of „Kнига“ (Latin K) is „Книга“.
export function matchCase(original, form) {
  const letters = (text, cyrillic) => {
    const result = [];
    for (const char of text) {
      if (/^\p{M}$/u.test(char) && result.length) continue;
      if (!/^[\p{Ll}\p{Lu}]$/u.test(char) || (cyrillic && !/^\p{Script=Cyrillic}$/u.test(char))) return null;
      result.push(char);
    }
    return result;
  };
  const source = letters(original, false), candidate = letters(form, true);
  if (!source || source.length < 2 || !candidate?.length || !candidate.every(char => /^\p{Ll}$/u.test(char))) return form;
  const caps = source.every(char => /^\p{Lu}$/u.test(char));
  const title = /^\p{Lu}$/u.test(source[0]) && source.slice(1).every(char => /^\p{Ll}$/u.test(char));
  if (!caps && !title) return form;
  let index = 0, result = '';
  for (const char of form) {
    if (/^\p{M}$/u.test(char)) { result += char; continue; }
    const proposed = caps || index++ === 0 ? char.toUpperCase() : char;
    if (!/^\p{Script=Cyrillic}$/u.test(proposed) || !/^[\p{Ll}\p{Lu}]$/u.test(proposed)) return form;
    result += proposed;
  }
  return result;
}

// As in /demo: no replacement whose span would split a surrogate pair or detach combining marks.
export function replaceableSpan(text, start, length) {
  const end = start + length;
  if (!Number.isInteger(start) || !Number.isInteger(length) || start < 0 || length <= 0 || end > text.length) return false;
  const splits = offset => offset > 0 && offset < text.length && /[\uD800-\uDBFF]/.test(text[offset - 1]) && /[\uDC00-\uDFFF]/.test(text[offset]);
  return !splits(start) && !splits(end) && !/^\p{M}/u.test(text.slice(start, start + 2)) && !/^\p{M}/u.test(text.slice(end, end + 2));
}

const listOrAbsent = value => value === undefined || value === null || Array.isArray(value);
// The identity of the package behind an API envelope (its manifest hash, else its id); null when not reported.
export const packageOf = envelope => envelope?.package?.manifestSha256 ?? envelope?.package?.packageId ?? null;

// The collections the views iterate, including the nested ones a word card reads later, after the response
// is accepted. A present but malformed collection rejects the whole response.
export function responseShapeValid(response) {
  const data = response?.data, v = data?.validation;
  if (!data || typeof data.text !== 'string' || !Array.isArray(data.tokens)) return false;
  if (response.evidenceFormat === 'shared-analyses-v1' && !Array.isArray(response.wordAnalyses)) return false;
  return [data.sentences, data.diagnostics, v?.diagnostics, v?.spacingDiagnostics, v?.subordinateCommaDiagnostics,
    v?.fragmentDiagnostics, v?.unavailableChecks, data.spelling?.words, data.properNames?.candidates, data.lexical?.tokens,
    data.readingInventory?.occurrences, data.readingInventory?.sets].every(listOrAbsent)
    && (response.wordAnalyses || []).every(analysis => listOrAbsent(analysis?.graphCandidates))
    && (data.readingInventory?.sets || []).every(set => listOrAbsent(set?.canonicalReadings) && listOrAbsent(set?.supportReadings))
    && (data.lexical?.tokens || []).every(token => listOrAbsent(token?.owners?.entries)
      && (token?.owners?.entries || []).every(entry => listOrAbsent(entry?.meaning)))
    && listOrAbsent(data.syntax?.sentences) && (data.syntax?.sentences || []).every(sentence => listOrAbsent(sentence?.candidates)
      && (sentence?.candidates || []).every(candidate => listOrAbsent(candidate?.targets)));
}

// Shapes of the on-demand answers the card reads. Anything else is reported as an invalid answer, never rendered.
const isObject = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const ANSWER_SHAPES = {
  'generate-forms': data => Array.isArray(data) && data.every(row => isObject(row) && typeof row.form === 'string'),
  'stress': data => isObject(data) && Array.isArray(data.tokens) && data.tokens.every(token => isObject(token)
    && typeof token.text === 'string' && Number.isInteger(token.start) && listOrAbsent(token.observations) && listOrAbsent(token.inferences)
    && Array.isArray(token.readings) && token.readings.every(reading => isObject(reading) && Array.isArray(reading.owners)
      && listOrAbsent(reading.reviewedEvidence) && reading.owners.every(owner => isObject(owner) && listOrAbsent(owner.realizations)))),
  'decompose-word': data => isObject(data) && Array.isArray(data.segments) && data.segments.every(isObject),
  'derive-words': data => Array.isArray(data) && data.every(row => isObject(row) && typeof row.word === 'string')
};
export function answerShapeValid(operation, data) {
  return ANSWER_SHAPES[operation]?.(data) === true;
}

// The word with exactly the marks of one stress realization. Written stress (ѐ, ѝ or a combining grave/acute,
// as the runtime reads it) is removed first, so a realization never merges with another mark. Positions are
// absolute in the query text and point at the vowel; the mark follows it, as in the runtime's own rendering.
// Null when a mark is outside the word or not on a vowel. The breve of й is not a stress mark and stays.
const WRITTEN_STRESS = {'ѐ': 'е', 'ѝ': 'и', 'Ѐ': 'Е', 'Ѝ': 'И'};
const VOWEL = /^[аеиоуъюяѐѝАЕИОУЪЮЯЀЍ]$/;
export function stressedForm(text, start, realization) {
  const marks = Array.isArray(realization?.marks) ? realization.marks : [];
  const accents = new Map();
  for (const mark of marks) {
    const offset = mark?.utf16Position - start;
    if (!Number.isInteger(offset) || offset < 0 || offset >= text.length || !VOWEL.test(text[offset])) return null;
    accents.set(offset, mark.kind === 'Acute' ? '́' : '̀');
  }
  if (!accents.size) return null;
  let result = '';
  for (let index = 0; index < text.length; index++) {
    const char = text[index];
    if (char === '̀' || char === '́') continue;
    result += WRITTEN_STRESS[char] ?? char;
    if (accents.has(index)) result += accents.get(index);
  }
  return result;
}

export function tokenClass(response, index, layer, warnings) {
  const data = response.data, token = data.tokens[index], classes = [];
  if (layer === 'pos' || layer === 'dictionary') {
    const rows = completeReadings(data, index);
    const known = Array.isArray(rows) && rows.length > 0;
    if (!known) classes.push(tokenAnalysisOf(response, token)?.graphCandidates?.length ? 'c-graph' : 'c-unknown');
    else if (layer === 'pos') {
      const types = new Set(rows.map(row => row?.features?.partOfSpeech));
      classes.push(types.size > 1 ? 'c-multi' : POS_CLASSES[[...types][0]] ?? 'c-other');
    } else {
      classes.push(new Set(rows.map(row => row?.raw?.lemma + '\u0001' + row?.features?.partOfSpeech)).size > 1 ? 'c-ambiguous' : 'c-known');
    }
  }
  if (warnings.has(index)) classes.push('w-warning');
  return classes.join(' ');
}

// Read-only renderer for the decorative backdrop. Every span is validated before the DOM changes.
export function renderBackdrop(target, text, tokens, classify) {
  if (typeof text !== 'string' || !Array.isArray(tokens)) throw new Error('Невалиден текстов отговор.');
  let cursor = 0;
  for (const token of tokens) {
    const start = token?.start, length = token?.length;
    if (!Number.isInteger(start) || !Number.isInteger(length) || start < cursor || length < 0 || start + length > text.length
      || (typeof token.text === 'string' && text.slice(start, start + length) !== token.text))
      throw new Error('Невалидни позиции в текстовия отговор.');
    cursor = start + length;
  }
  const plan = [];
  cursor = 0;
  tokens.forEach((token, index) => {
    if (token.kind !== 'Word' || token.length === 0) return;
    plan.push([text.slice(cursor, token.start), '']);
    plan.push([text.slice(token.start, token.start + token.length), classify(index)]);
    cursor = token.start + token.length;
  });
  // The trailing space gives a final empty line the same height as in the textarea.
  plan.push([text.slice(cursor) + ' ', '']);
  const doc = target.ownerDocument, fragment = doc.createDocumentFragment();
  for (const [chunk, css] of plan) {
    if (!chunk) continue;
    if (css) {
      const span = doc.createElement('span');
      span.className = css;
      span.textContent = chunk;
      fragment.append(span);
    } else fragment.append(doc.createTextNode(chunk));
  }
  target.replaceChildren(fragment);
}

export function renderPlain(target, text) {
  const only = target.childNodes.length === 1 ? target.firstChild : null;
  if (only && only.nodeType === 3) {
    if (only.nodeValue !== text + ' ') only.nodeValue = text + ' ';
  } else target.replaceChildren(target.ownerDocument.createTextNode(text + ' '));
}

const STATE_TEXT = {
  empty: 'Напиши текст или избери пример.',
  waiting: 'Изчаква да спреш да пишеш…',
  working: 'Анализира…',
  ready: 'Анализът е актуален.',
  paused: 'Живият анализ е изключен. Натисни ↻ за анализ.',
  composing: 'Въвеждане…',
  oversize: 'Текстът е над 4096 знака и не се анализира.',
  error: 'Анализаторът не отговори.'
};

export function initLab(doc, {
  fetchImpl = globalThis.fetch?.bind(globalThis),
  now = () => performance.now(),
  wallNow = () => Date.now(),
  setTimer = setTimeout,
  clearTimer = clearTimeout,
  debounceMs = 150,
  maxWaitMs = 600,
  storage = null
} = {}) {
  const $ = id => doc.getElementById(id);
  const editor = $('lab-editor'), backdrop = $('lab-backdrop'), legend = $('lab-legend');
  const stateNode = $('lab-state'), countsNode = $('lab-counts'), lengthNode = $('lab-length');
  const wordHost = $('lab-word'), wordTitle = $('lab-word-title'), checksHost = $('lab-checks');
  const devHost = $('lab-dev-metrics'), rawNode = $('lab-raw'), liveToggle = $('lab-live'), heuristicsToggle = $('lab-heuristics');
  const menuHost = $('lab-menu'), targetSelect = $('lab-target'), sentencesHost = $('lab-sentences'), readingHost = $('lab-reading');
  const layerButtons = [...doc.querySelectorAll('[data-layer]')];
  const continuationTarget = () => targetSelect?.value ?? 'Auto';

  const metrics = {requests: 0, lastHttpMs: null, lastRenderMs: null, lastFreshAfterEditMs: null, lastEditAt: null};
  // Counts POSTs that reach fetch; jobs the shared lane drops before sending are not requests.
  // The visible counter changes at the same moment, whatever the outcome of the request.
  const countingFetch = (url, init) => {
    if (init?.method === 'POST') {
      metrics.requests++;
      renderDev();
    }
    return fetchImpl(url, init);
  };
  const transport = createPostLane({fetchImpl: countingFetch, now, wallNow, setTimer, clearTimer});

  let layer = 'pos', display = null, composing = false, backdropKey = null, cardKey = null, stale = false;
  // Open sections that a rebuild of the word card or the checks keeps open (see remember).
  let keep = new Set();
  // Word card sections. `answers` holds on-demand answers by operation and input (a lemma or a word); an answer
  // depends only on that input and the host's package, so any card with the same input may show it, and a card
  // never reads an entry for another input. `demand` is the newest explicit request, bound to the analysis,
  // occurrence, tab and input it was made for; `failure` is its error, shown only for that same binding.
  let tab = 'readings', demand = null, failure = null;
  const answers = new Map();
  // Continuations are bound to the caret of the last edit (or of an explicit request), not to every caret move:
  // the analysis that an edit sends anyway carries them, and moving the caret sends nothing.
  let editCaret = null, menu = {active: -1, forced: false, dismissed: null, options: []};
  // The read-only stress reading of the whole text, bound to that exact text (and an optional owner selection).
  let reading = null;

  const isFresh = () => !!display && !composing && display.revision === controller.revision && display.text === editor.value
    && display.heuristics === heuristicsToggle.checked;

  function el(tag, text, css) {
    const node = doc.createElement(tag);
    if (text !== undefined && text !== null) node.textContent = text;
    if (css) node.className = css;
    return node;
  }
  // Every action is bound to the analysis it was rendered from and runs only while that analysis is current.
  function button(text, css, action, owner = display?.response) {
    const node = el('button', text, css);
    node.type = 'button';
    node.disabled = stale;
    node.addEventListener('click', () => {
      if (!isFresh() || !owner || display.response !== owner) return;
      action();
    });
    return node;
  }
  // Lazily built section. A key lets a rebuild reopen it (and build it at once) when it was open before.
  function details(summary, build, key) {
    const node = el('details', undefined, 'tech');
    node.dataset.key = key;
    node.append(el('summary', summary));
    let built = false;
    const ensure = () => {
      if (node.open && !built) {
        built = true;
        build(node);
      }
    };
    node.addEventListener('toggle', ensure);
    if (keep.has(key)) {
      node.open = true;
      ensure();
    }
    return node;
  }
  function keyed(node, key) {
    node.dataset.key = key;
    if (node.tagName === 'DETAILS') node.open = keep.has(key);
    return node;
  }

  // A rebuilt panel keeps what the reader opened and where the keyboard was: sections by key, and the focused
  // control by its own key (an action that can repeat, such as one chip per occurrence) or else by its keyed
  // section plus tag and text. Nothing is kept when the section or the action no longer exists.
  const inside = (host, node) => {
    for (let current = node; current; current = current.parentNode) if (current === host) return true;
    return false;
  };
  function focusKeyOf(node) {
    if (node?.dataset?.key) return node.dataset.key + '\u0001' + node.tagName;
    const owner = node?.closest?.('[data-key]');
    // A summary is identified by its section alone: its text may carry a count that changes.
    return owner ? owner.dataset.key + '\u0001' + node.tagName + '\u0001' + (node.tagName === 'SUMMARY' ? '' : node.textContent) : null;
  }
  function remember(...hosts) {
    keep = new Set();
    for (const host of hosts) for (const node of host.querySelectorAll('details')) if (node.open && node.dataset.key) keep.add(node.dataset.key);
    const active = doc.activeElement;
    return hosts.some(host => inside(host, active)) ? focusKeyOf(active) : null;
  }
  function restoreFocus(host, key) {
    if (key === null) return false;
    const target = [...host.querySelectorAll('[data-key]'), ...host.querySelectorAll('summary'), ...host.querySelectorAll('button')]
      .find(node => !node.disabled && focusKeyOf(node) === key);
    target?.focus({preventScroll: true});
    return !!target;
  }
  function technicalList(parent, rows) {
    const list = el('dl');
    for (const [key, value] of rows) list.append(el('dt', key), el('dd', value));
    parent.append(list);
  }

  function setStale(value) {
    stale = value;
    for (const host of [wordHost, checksHost, sentencesHost]) {
      host.dataset.fresh = String(!value);
      for (const control of host.querySelectorAll('button')) control.disabled = value;
    }
    $('lab-stale-word').hidden = !value || !(cardKey?.index >= 0);
    $('lab-stale-checks').hidden = !value || !display;
    $('lab-stale-sentences').hidden = !value || !display;
  }

  function paintBackdrop() {
    if (!isFresh()) {
      const key = 'plain\u0001' + editor.value;
      if (backdropKey !== key) {
        renderPlain(backdrop, editor.value);
        backdropKey = key;
      }
      backdrop.dataset.fresh = 'false';
      syncScroll();
      return;
    }
    const key = display;
    if (backdropKey === key && backdrop.dataset.paintedLayer === layer) return;
    renderBackdrop(backdrop, display.response.data.text, display.response.data.tokens,
      index => tokenClass(display.response, index, layer, display.warnings));
    backdropKey = key;
    backdrop.dataset.paintedLayer = layer;
    backdrop.dataset.fresh = 'true';
    syncScroll();
  }

  function syncScroll() {
    backdrop.scrollTop = editor.scrollTop;
    backdrop.scrollLeft = editor.scrollLeft;
    positionMenu();
  }

  function renderLegend() {
    legend.replaceChildren();
    for (const [css, label] of LAYERS[layer]) {
      const item = el('span', undefined, 'legend-item');
      item.append(el('i', undefined, 'swatch ' + css), doc.createTextNode(label));
      legend.append(item);
    }
    const warning = el('span', undefined, 'legend-item');
    warning.append(el('i', undefined, 'swatch w-warning'), doc.createTextNode('предупреждение (подробности под редактора)'));
    legend.append(warning);
  }

  function setLayer(name, focus = false) {
    if (!Object.hasOwn(LAYERS, name)) return;
    layer = name;
    for (const control of layerButtons) {
      const active = control.dataset.layer === name;
      control.setAttribute('aria-checked', String(active));
      control.tabIndex = active ? 0 : -1;
      if (active && focus) control.focus();
    }
    renderLegend();
    paintBackdrop();
  }

  function setState(state, extra = {}) {
    stateNode.dataset.state = state;
    let text = STATE_TEXT[state] ?? state;
    if (state === 'error' && extra.retryAfterMs) text = 'Хостът поиска пауза от ' + Math.ceil(extra.retryAfterMs / 1000) + ' s.';
    if (state === 'error' && extra.invalid) text = 'Отговорът на анализатора е невалиден и не е приложен.';
    if (state === 'error' && extra.kept) text += ' Показан е предишният анализ на същия текст.';
    stateNode.textContent = text;
  }

  function jump(start, length, original) {
    if (editor.value.slice(start, start + length) !== original) return;
    editor.focus();
    editor.setSelectionRange(start, start + length);
    onCaret();
  }

  // Explicit replacement of one original span. execCommand keeps the native undo history when available.
  function replace(start, length, original, form) {
    if (!isFresh() || editor.value.slice(start, start + length) !== original) return false;
    editor.focus();
    editor.setSelectionRange(start, start + length);
    let done = false;
    try {
      done = typeof doc.execCommand === 'function' && doc.execCommand('insertText', false, form) === true;
    } catch {
      done = false;
    }
    if (!done || editor.value.slice(start, start + form.length) !== form) {
      if (editor.value.slice(start, start + length) !== original) return false;
      editor.setRangeText(form, start, start + length, 'end');
      onInput();
    }
    return true;
  }

  // The views below build detached nodes from `display`; commit applies them only when all of them succeed.
  function countsView() {
    const data = display.response.data;
    let words = 0, unknown = 0;
    data.tokens.forEach((token, index) => {
      if (token.kind !== 'Word') return;
      words++;
      const rows = completeReadings(data, index);
      if (!(Array.isArray(rows) && rows.length)) unknown++;
    });
    return 'Думи: ' + words + ' · Изречения: ' + (data.sentences?.length ?? 0)
      + ' · Без точен прочит: ' + unknown + ' · Предупреждения: ' + display.checks.filter(c => c.severity === 'warning').length;
  }

  function checkRow(check) {
    const owner = display.response, data = owner.data;
    const key = 'checks:' + check.code + ':' + check.start + ':' + check.length;
    const row = keyed(el('li', undefined, 'check check-' + check.severity), key);
    const head = el('div', undefined, 'check-head');
    if (check.validSpan && check.length > 0) {
      const {start, length, original} = check;
      head.append(button('„' + original + '“', 'check-word', () => jump(start, length, original)));
    } else if (check.validSpan) {
      const before = data.text.slice(Math.max(0, check.start - 12), check.start).replace(/\s+/g, ' ');
      head.append(button('…' + before + '▸', 'check-word', () => jump(check.start, 0, '')));
    }
    let message = check.message;
    if (check.words.length > 1) message += ' Думи: ' + check.words.map(word => '„' + word + '“').join(' · ') + '.';
    if (check.projected && check.code === 'spelling_unknown_word_unique_distance_one_candidate') message += ' Предложение: „' + check.projected + '“.';
    if (check.projected && check.code === 'reviewed_word_formation_evidence') message += ' Основа: „' + check.projected + '“.';
    if (check.projected && check.code === 'hyphenated_degree_morphology_evidence') message += ' Основна форма: „' + check.projected + '“.';
    head.append(el('span', message, 'check-message'));
    if (check.graph) head.append(el('span', 'графова хипотеза', 'badge badge-graph'));
    row.append(head);
    const tokenIndex = data.tokens.findIndex(token => token.kind === 'Word' && token.start === check.start && token.length === check.length);
    const name = tokenIndex >= 0 ? possibleName(data, tokenIndex) : {possible: false};
    if (check.replaceable && !name.possible) {
      const {start, length, original, replacement} = check;
      row.append(button('Замени с „' + replacement + '“', 'action', () => replace(start, length, original, replacement)));
    } else if (check.replaceable) row.append(el('span', 'Възможно име: замяната е само в техническите детайли.', 'note'));
    row.append(details('Технически детайли', node => {
      technicalList(node, [['Код', check.code], ['Позиция', check.start + '+' + check.length + ' (UTF-16)'],
        ['Източници', check.records.map(item => item.source + (item.record.ruleId ? ' · ' + item.record.ruleId : '')
          + (item.record.basis ? ' · ' + item.record.basis : '')).join('; ')]]);
      if (check.replaceable && name.possible) node.append(button('Замени с „' + check.replacement + '“', 'action',
        () => replace(check.start, check.length, check.original, check.replacement), owner));
      node.append(el('pre', JSON.stringify(check.records.map(item => item.record), null, 2)));
    }, key + ':tech'));
    return row;
  }

  function spellingRow(word, covered) {
    const owner = display.response, data = owner.data, token = data.tokens[word?.tokenIndex];
    if (token?.kind !== 'Word' || covered.has(word.tokenIndex)) return null;
    // The row must name exactly its token: index, text, start, length and the source slice all agree.
    const original = data.text.slice(token.start, token.start + token.length);
    if (word.text !== token.text || word.start !== token.start || word.length !== token.length || original !== token.text) return null;
    const alternatives = spellingAlternatives(word);
    if (!alternatives.length) return null;
    const key = 'checks:spelling:' + token.start + ':' + original;
    const name = possibleName(data, word.tokenIndex), row = keyed(el('li', undefined, 'check check-spelling'), key);
    const head = el('div', undefined, 'check-head');
    head.append(button('„' + original + '“', 'check-word', () => jump(token.start, token.length, original)));
    head.append(el('span', name.possible
      ? 'Възможно име. Няма точен речников прочит; това не доказва правописна грешка.'
      : 'Няма точен речников прочит. Близки речникови форми за преглед:', 'check-message'));
    if (name.possible) head.append(el('span', 'възможно име', 'badge'));
    row.append(head);
    // One captured replacement is both the label and the inserted text.
    const canReplace = replaceableSpan(data.text, token.start, token.length);
    const replaceButton = alternative => {
      const replacement = matchCase(original, alternative.form);
      return keyed(button(replacement, 'action', () => replace(token.start, token.length, original, replacement), owner),
        key + ':alternative:' + alternative.index);
    };
    if (!canReplace) row.append(el('p', 'Границата на думата разделя знак (емоджи или ударение); замяна не се предлага.', 'note'));
    else if (!name.possible) {
      const actions = el('div', undefined, 'actions');
      for (const alternative of alternatives.slice(0, 4)) actions.append(replaceButton(alternative));
      row.append(actions);
    }
    row.append(details('Технически детайли', node => {
      technicalList(node, [['Търсене', String(word.search)], ['Позиция', token.start + '+' + token.length + ' (UTF-16)'],
        ['Възможно име', name.evidence ? 'по контекстно evidence' : name.signal ? 'главна буква извън началото' : 'не']]);
      const list = el('ul');
      for (const alternative of alternatives) {
        const item = el('li', alternative.form + ' · разстояние ' + alternative.distance + ' · контекст: '
          + (alternative.assessment === 'supported' ? 'подкрепено' : alternative.assessment === 'nocompatiblepair' ? 'без съвместима двойка' : 'неоценено'));
        if (name.possible && canReplace) item.append(doc.createTextNode(' '), replaceButton(alternative));
        list.append(item);
      }
      node.append(list);
    }, key + ':tech'));
    return row;
  }

  function checksView() {
    const data = display.response.data, checks = display.checks, v = data.validation;
    const warnings = checks.filter(check => check.severity === 'warning');
    const unsupported = checks.filter(check => check.severity === 'unsupported');
    const information = checks.filter(check => check.severity === 'information');
    const parts = [];
    const summary = el('p', warnings.length
      ? 'Предупреждения: ' + warnings.length + '. Те са кандидати за преглед, не автоматични поправки.'
      : 'Няма предупреждения в проверения обхват. Това не доказва, че текстът е правилен.', 'checks-summary');
    parts.push(summary);
    if (v && v.available !== true) parts.push(el('p', 'Част от проверките са недостъпни за този текст.', 'note'));
    if (warnings.length) {
      const list = el('ul', undefined, 'check-list');
      for (const check of warnings) list.append(checkRow(check));
      parts.push(list);
    }
    const covered = new Set(warnings.filter(check => check.code === 'spelling_unknown_word_unique_distance_one_candidate')
      .map(check => data.tokens.findIndex(token => token.kind === 'Word' && token.start === check.start && token.length === check.length)));
    const spelling = (data.spelling?.words || []).map(word => spellingRow(word, covered)).filter(Boolean);
    if (spelling.length) {
      parts.push(el('h3', 'Непознати думи'));
      const list = el('ul', undefined, 'check-list');
      list.append(...spelling);
      parts.push(list);
    }
    const byMessage = new Map();
    for (const check of unsupported) {
      if (!byMessage.has(check.message)) byMessage.set(check.message, []);
      byMessage.get(check.message).push(check);
    }
    for (const [message, group] of byMessage) {
      const box = keyed(el('div', undefined, 'check-group check-unsupported'), 'checks:unsupported:' + message);
      box.append(el('p', message));
      const words = el('div', undefined, 'actions');
      for (const check of group) if (check.validSpan && check.length > 0)
        words.append(keyed(button(check.original, 'chip', () => jump(check.start, check.length, check.original)),
          'checks:unsupported:' + message + ':' + check.start + ':' + check.length));
      box.append(words);
      parts.push(box);
    }
    if (information.length) {
      const more = keyed(el('details', undefined, 'information'), 'checks:information');
      more.append(el('summary', 'Други наблюдения (' + information.length + ')'));
      const list = el('ul', undefined, 'check-list');
      for (const check of information) list.append(checkRow(check));
      more.append(list);
      parts.push(more);
    }
    parts.push(details('Технически детайли за проверките', node => {
      technicalList(node, [['Версия', v?.version ?? 'няма'], ['Достъпни', v ? String(v.available) : 'няма отговор'],
        ['Недостъпни проверки', (v?.unavailableChecks || []).join('; ') || 'няма']]);
    }, 'checks:tech'));
    return parts;
  }

  // Possible syntactic roles per sentence, as returned. Every candidate is its own row: competing candidates are never
  // merged into one parse. A role's words are a button that selects them in the editor.
  function sentencesView() {
    const data = display.response.data, syntax = data.syntax;
    if (!syntax) return [el('p', 'Отговорът не съдържа синтактичен анализ.', 'note')];
    if (syntax.available !== true) return [el('p', 'Синтактичните роли не са налични за този текст.', 'note'),
      details('Технически детайли', node => technicalList(node, [['Версия', String(syntax.version)],
        ['Причина', String(syntax.unavailableReason ?? 'няма')]]), 'sentences:tech')];
    const sentences = syntax.sentences || [];
    if (!sentences.length) return [el('p', 'Няма изречения за синтактичен анализ.', 'note')];
    const inText = (start, length) => Number.isInteger(start) && Number.isInteger(length) && start >= 0 && length > 0
      && start + length <= data.text.length;
    const list = el('ol', undefined, 'sentence-list');
    for (const sentence of sentences) {
      const key = 'sentences:' + sentence.start + ':' + sentence.length;
      const item = keyed(el('li', undefined, 'sentence'), key);
      const text = inText(sentence.start, sentence.length) ? data.text.slice(sentence.start, sentence.start + sentence.length).trim() : '';
      item.append(el('p', '„' + (text.length > 140 ? text.slice(0, 139) + '…' : text) + '“', 'sentence-text'));
      for (const note of [ROUTE_NOTES[sentence.route], SENTENCE_STATUS[sentence.status]]) if (note) item.append(el('p', note, 'note'));
      const candidates = sentence.candidates || [];
      if (!candidates.length) item.append(el('p', 'Няма върнати роли за това изречение.', 'note'));
      else {
        const roles = el('ul', undefined, 'roles'), perRole = new Map();
        for (const candidate of candidates) {
          perRole.set(candidate.role, (perRole.get(candidate.role) ?? 0) + 1);
          const row = el('li');
          row.append(doc.createTextNode((ROLE_NAMES[candidate.role] ?? String(candidate.role)) + ': '));
          if (inText(candidate.start, candidate.length)) {
            const words = data.text.slice(candidate.start, candidate.start + candidate.length);
            row.append(keyed(button('„' + words + '“', 'check-word', () => jump(candidate.start, candidate.length, words)),
              key + ':' + candidate.role + ':' + candidate.start + ':' + candidate.length + ':' + candidate.ruleId));
          } else row.append(doc.createTextNode('без позиция в текста'));
          if ((candidate.targets || []).some(target => target?.basis === 'LicensedGraphMorphology' || target?.graphReadings?.length))
            row.append(doc.createTextNode(' '), el('span', 'графова хипотеза', 'badge badge-graph'));
          roles.append(row);
        }
        item.append(roles);
        // The contract marks no competition: a repeated role may be a main and a subordinate predicate, or several
        // attributes, at the same time. The note stays neutral.
        if ([...perRole.values()].some(count => count > 1))
          item.append(el('p', 'Някои роли се срещат повече от веднъж, например главно и подчинено сказуемо или няколко определения. '
            + 'Всеки ред е отделен кандидат, не окончателен разбор.', 'note'));
      }
      item.append(details('Технически детайли', node => {
        technicalList(node, [['Маршрут', String(sentence.route)], ['Състояние', String(sentence.status)],
          ['Позиция', sentence.start + '+' + sentence.length + ' (UTF-16)'], ['Правила', candidates.map(c => c.ruleId).join('; ') || 'няма']]);
        node.append(el('pre', JSON.stringify(sentence, null, 2)));
      }, key + ':tech'));
      list.append(item);
    }
    return [el('p', 'Възможни синтактични роли по изречения: частичен анализ, не пълен разбор.', 'note'), list];
  }

  // Graph hypotheses and graph completeness are separate properties; the flags are shown, not inferred.
  function graphNotes(analysis, hasCandidates) {
    const notes = [];
    if (analysis?.graphSearchIncomplete === true) notes.push(hasCandidates
      ? 'Търсенето в графа не е завършило: хипотезите са частични и може да липсва по-добра.'
      : 'Търсенето в графа не е завършило: липсата на хипотези не доказва, че няма такива.');
    if (analysis?.graphCandidatesTruncated === true) notes.push('Наборът хипотези е съкратен от API-то; не всички проверени хипотези са показани.');
    if (hasCandidates && analysis?.graphSearchIncomplete !== true && analysis?.graphExactRootCandidatesComplete !== true)
      notes.push('Пълнотата на хипотезите с речникова основа не е потвърдена.');
    return notes;
  }

  function cardKeyFor(index) {
    const token = display?.response.data.tokens[index];
    return {response: display?.response ?? null, index: token?.kind === 'Word' ? index : -1};
  }

  // Caret moves rebuild the card only for another word or another analysis.
  function renderWord(index) {
    const next = cardKeyFor(index);
    if (cardKey && cardKey.response === next.response && cardKey.index === next.index) return;
    const focus = remember(wordHost);
    applyWord(safeWordView(next));
    restoreFocus(wordHost, focus);
  }

  // cardKey changes only here, after a view was built.
  function applyWord(view) {
    cardKey = view.key;
    wordTitle.textContent = view.title;
    wordHost.replaceChildren(...view.parts);
  }

  // A card that cannot be built from the accepted response says so instead of leaving the previous card.
  function safeWordView(next) {
    try {
      return wordView(next);
    } catch {
      const text = display?.response.data.tokens[next.index]?.text;
      return {key: next, title: typeof text === 'string' ? text : 'Дума',
        parts: [el('p', 'Картата за тази дума не може да се покаже: отговорът съдържа невалидни данни за нея.', 'note')]};
    }
  }

  // Rebuilds the card on screen after a tab change or an answer for it, keeping open sections and focus.
  // When the focused control is gone (a request button became "Зарежда…"), focus moves to the tab panel.
  function refreshCard() {
    if (!cardKey || cardKey.response !== (display?.response ?? null)) return;
    const focus = remember(wordHost);
    applyWord(safeWordView(cardKey));
    if (focus !== null && !restoreFocus(wordHost, focus)) wordHost.querySelector('#lab-tabpanel')?.focus({preventScroll: true});
  }

  const answerKey = (operation, input) => operation + '\u0001' + input;
  // Answers belong to the package that produced them: a card shows only answers from the package of its analysis.
  const cached = key => {
    const answer = answers.get(key);
    return answer && answer.package === (display?.package ?? null) ? answer : null;
  };
  function keepAnswer(key, answer) {
    if (answer.package !== (display?.package ?? null)) return;
    answers.delete(key);
    answers.set(key, answer);
    if (answers.size > 40) answers.delete(answers.keys().next().value);
  }
  function lemmasOf(data, index) {
    const rows = completeReadings(data, index);
    return [...new Set((Array.isArray(rows) ? rows : []).map(row => row?.raw?.lemma).filter(lemma => typeof lemma === 'string' && lemma.length))];
  }
  // The inputs one tab of one card may ask for: the lemmas of its exact readings or the word itself.
  function tabInputs(name, data, index) {
    const word = data.tokens[index]?.text, lemmas = lemmasOf(data, index);
    if (name === 'forms') return lemmas.map(lemma => answerKey('generate-forms', lemma));
    if (name === 'stress') return [answerKey('stress', word)];
    if (name === 'formation') return [answerKey('decompose-word', word), ...lemmas.map(lemma => answerKey('derive-words', lemma))];
    return [];
  }
  const boundTo = (item, key, next) => !!item && item.key === key && item.response === next.response && item.index === next.index;

  function readAnswer(operation, result) {
    if (result.error) return {status: 'error', message: 'Няма връзка с анализатора.'};
    if (!result.http.ok) return {status: 'error', message: result.retryAfterMs
      ? 'Хостът поиска пауза от ' + Math.ceil(result.retryAfterMs / 1000) + ' s. Опитай отново след нея.'
      : 'Заявката не успя (HTTP ' + result.http.status + ')' + (typeof result.response?.message === 'string' ? ': ' + result.response.message : '.')};
    const data = result.response?.data;
    if (!answerShapeValid(operation, data)) return {status: 'error', message: 'Отговорът е невалиден и не е показан.'};
    return {status: 'ok', operation, data, coverage: result.response.coverage ?? null, limits: result.response.limits ?? null,
      package: packageOf(result.response)};
  }

  // One explicit request for the card, through the shared lane (key `manual`: a newer one replaces a waiting one).
  // `current` is the local generation guard: the newest demand only, while the card still shows the same occurrence
  // of the same analysis of the same text, on the same tab, with this input. The lane drops a waiting job that is no
  // longer current, and an answer that arrives later never reaches the card; a valid one is still kept by input.
  function ask(operation, input) {
    const key = answerKey(operation, input);
    const binding = {key, response: display.response, index: cardKey.index, tab};
    demand = binding;
    failure = null;
    const current = () => demand === binding && isFresh() && display.response === binding.response
      && cardKey?.response === binding.response && cardKey.index === binding.index && tab === binding.tab
      && tabInputs(tab, display.response.data, cardKey.index).includes(key);
    refreshCard();
    transport.post(operation, {text: input}, {key: 'manual', current}).then(result => {
      const live = current() && !result.stale;
      if (demand === binding) demand = null;
      const answer = result.http || result.error ? readAnswer(operation, result) : null;
      if (answer?.status === 'ok') keepAnswer(key, answer);
      if (!live) return;
      if (answer?.status === 'error') failure = {...binding, message: answer.message};
      refreshCard();
    });
  }

  // Activating a tab sends a request only when the tab has one obvious input and no answer for it yet;
  // otherwise its panel offers explicit buttons. Moving between words never sends anything.
  function selectTab(name) {
    tab = name;
    const data = display.response.data, index = cardKey.index, word = data.tokens[index].text, lemmas = lemmasOf(data, index);
    const first = name === 'stress' ? ['stress', word] : name === 'formation' ? ['decompose-word', word]
      : name === 'forms' && lemmas.length === 1 ? ['generate-forms', lemmas[0]] : null;
    const key = first && answerKey(...first);
    if (first && !cached(key) && !boundTo(demand, key, cardKey)) ask(...first);
    else refreshCard();
  }

  // Manual activation: arrows, Home and End move the focus between tabs; Enter or Space selects.
  function moveTabFocus(event, controls, position) {
    const step = {ArrowRight: 1, ArrowLeft: -1}[event.key];
    const target = event.key === 'Home' ? 0 : event.key === 'End' ? controls.length - 1
      : step === undefined ? null : (position + step + controls.length) % controls.length;
    if (target === null) return;
    event.preventDefault();
    controls[target].focus();
  }

  // An answer with its own technical details, the request in progress, its error, or the button that asks for it.
  function answerView(next, operation, input, label, render) {
    const key = answerKey(operation, input), answer = cached(key);
    if (answer) return [...render(answer), details('Технически детайли за заявката', node => {
      technicalList(node, [['Операция', operation], ['Вход', input],
        ['Лимит', answer.limits?.effective === null || answer.limits?.effective === undefined ? 'няма' : String(answer.limits.effective)],
        ['Възможно съкращаване', answer.coverage?.possibleTruncation === true ? 'да' : 'не']]);
      node.append(el('pre', JSON.stringify(answer.data, null, 2)));
    }, 'word:answer:' + key + ':tech')];
    if (boundTo(demand, key, next)) return [el('p', 'Зарежда…', 'loading')];
    const parts = [];
    const failed = boundTo(failure, key, next);
    if (failed) parts.push(el('p', failure.message, 'note answer-error'));
    parts.push(keyed(button(failed ? 'Опитай пак' : label, 'action', () => ask(operation, input)), 'word:ask:' + key));
    return parts;
  }
  const truncationNote = (answer, unit) => answer.coverage?.possibleTruncation === true ? [el('p', 'Списъкът стига лимита'
    + (Number.isInteger(answer.limits?.effective) ? ' от ' + answer.limits.effective + ' ' + unit : '') + '; може да има още.', 'note')] : [];

  function formsView(answer, word) {
    if (!answer.data.length) return [el('p', 'API-то не върна форми за тази лема.', 'note')];
    const table = el('table', undefined, 'forms'), head = el('tr'), body = el('tbody'), lower = word.toLocaleLowerCase('bg');
    head.append(el('th', 'Форма'), el('th', 'Таг от API-то'));
    for (const row of answer.data) {
      const same = row.form.toLocaleLowerCase('bg') === lower, line = el('tr', undefined, same ? 'current-form' : undefined);
      line.append(el('td', row.form + (same ? ' (тази)' : '')), el('td', typeof row.tag === 'string' ? row.tag : 'без таг'));
      body.append(line);
    }
    const thead = el('thead');
    thead.append(head);
    table.append(thead, body);
    return [table, ...truncationNote(answer, 'форми')];
  }

  const stressStatus = value => STRESS_STATUS[value] ?? String(value);
  // Every stress source of one word, separately: written marks, single-vowel inference and, per dictionary reading,
  // each owner's realizations and the reading's reviewed evidence (which belongs to no lexical owner). `chooser`, in
  // the whole-text reading, makes one button per owner that asks for the text with that owner's reading.
  function stressSources(item, chooser = null) {
    const word = item.text;
    const line = realization => el('li', (stressedForm(word, item.start, realization) ?? 'без точна позиция') + ' — '
      + (STRESS_ORIGIN[realization?.origin] ?? String(realization?.origin)) + ' · ' + stressStatus(realization?.status));
    const parts = [];
    for (const [list, title] of [[item.observations, 'Написано в текста'], [item.inferences, 'Изведено']]) {
      if (!list?.length) continue;
      const ul = el('ul');
      for (const realization of list) ul.append(line(realization));
      parts.push(el('h3', title), ul);
    }
    if (!item.readings.length) return parts;
    const ul = el('ul');
    for (const reading of item.readings) {
      const li = el('li', (reading.reading?.lemma ?? '?') + ' · ' + (reading.reading?.partOfSpeech ?? 'без таг')), sources = el('ul');
      for (const owner of reading.owners) {
        const label = reading.owners.length > 1 ? (owner.lexemeKey ?? owner.entryId) + ': ' : '';
        for (const realization of owner.realizations ?? []) {
          const row = line(realization);
          row.textContent = label + row.textContent;
          sources.append(row);
        }
        if (chooser && typeof owner.entryId === 'string' && typeof reading.reading?.form === 'string') {
          const selection = {tokenIndex: item.tokenIndex, entryId: owner.entryId, form: reading.reading.form, fullTag: reading.reading.partOfSpeech ?? null};
          const row = el('li');
          row.append(chooser(selection, 'Покажи текста с този прочит' + (label ? ' (' + label.slice(0, -2) + ')' : '')));
          sources.append(row);
        }
      }
      for (const realization of reading.reviewedEvidence ?? []) sources.append(line(realization));
      if (!sources.childNodes.length) sources.append(el('li', 'няма сведение за ударението на този прочит', 'note'));
      li.append(sources);
      ul.append(li);
    }
    parts.push(el('h3', 'По речникови прочити'), ul);
    return parts;
  }
  const STRESS_NOTE = 'Ударението е лексикално сведение за думата, не прогноза за изговора в изречението. Състоянието и източникът '
    + 'са отделни неща; няколко източника не правят един прочит единствено правилен.';

  function stressView(answer, word) {
    const item = answer.data.tokens.find(token => token.text === word);
    if (!item) return [el('p', 'Отговорът не съдържа тази дума.', 'note')];
    return [el('p', typeof item.renderedText === 'string' ? item.renderedText : word, 'stress-word'),
      el('p', 'Състояние: ' + stressStatus(item.status) + '.'), ...stressSources(item), el('p', STRESS_NOTE, 'note')];
  }

  function decompositionView(answer) {
    const data = answer.data;
    if (!data.segments.length) return [el('p', 'Няма прегледано разлагане за тази дума. Това не доказва, че думата не е производна.', 'note')];
    const parts = [el('p', data.segments.map(segment => (segment.surface ?? '?') + ' (' + (MORPH_KIND[segment.kind] ?? String(segment.kind)) + ')')
      .join(' + '), 'segments')];
    if (typeof data.base === 'string' && data.base.length) parts.push(el('p', 'Основа: „' + data.base + '“.'));
    parts.push(el('p', 'Разлагането е сведение от графа на словообразуването, не речников прочит.', 'note'));
    return parts;
  }

  function derivationsView(answer) {
    if (!answer.data.length) return [el('p', 'Не са върнати производни.', 'note')];
    const list = el('ul');
    for (const row of answer.data) list.append(el('li', (row.derivedLemma ?? row.word) + ' ← „' + (row.base ?? '?') + '“'
      + (Array.isArray(row.fullTags) && row.fullTags.length ? ' · ' + row.fullTags.slice(0, 2).join('; ') : '')
      + (row.isConditional ? ' · условна основа' : '')));
    return [list, el('p', 'Това са пътища в графа на словообразуването. Сами по себе си те не доказват речниково наличие или правилност.', 'note'),
      ...truncationNote(answer, 'пътя')];
  }

  function wordView(next) {
    const data = display?.response.data, index = next.index, token = data?.tokens[index];
    if (next.index < 0) return {key: next, title: 'Дума', parts: [el('p', display
      ? 'Постави курсора в дума, за да видиш прочитите и значенията ѝ.' : 'Прочитите и значенията ще се появят тук.', 'empty')]};
    const key = 'word:' + token.text;
    const analysis = tokenAnalysisOf(display.response, token), rows = completeReadings(data, index);
    const known = Array.isArray(rows) && rows.length > 0, groups = readingGroups(rows), name = possibleName(data, index);
    const graphCandidates = Array.isArray(analysis?.graphCandidates) ? analysis.graphCandidates : [];
    const notes = known ? [] : graphNotes(analysis, graphCandidates.length > 0);
    const badges = el('div', undefined, 'badges');
    badges.append(el('span', known ? 'в речника' : 'няма точен речников прочит', 'badge' + (known ? '' : ' badge-muted')));
    if (!known && graphCandidates.length) badges.append(el('span', 'графова хипотеза', 'badge badge-graph'));
    // Search completeness and output truncation are separate facts in the contract.
    if (!known && analysis?.graphSearchIncomplete === true) badges.append(el('span', 'непълно търсене в графа', 'badge badge-muted'));
    if (!known && analysis?.graphCandidatesTruncated === true) badges.append(el('span', 'съкратен списък хипотези', 'badge badge-muted'));
    if (groups.length > 1) badges.append(el('span', groups.length + ' възможни прочита', 'badge'));
    if (name.possible) badges.append(el('span', 'възможно име', 'badge'));
    const parts = [badges];
    const own = display.checks.filter(check => check.validSpan && check.length > 0 && check.start < token.start + token.length
      && check.start + check.length > token.start);
    if (own.length) {
      const block = el('section', undefined, 'word-checks');
      block.append(el('h3', 'Проверки за думата'));
      const list = el('ul');
      for (const check of own) list.append(el('li', check.message, 'check-' + check.severity));
      block.append(list);
      parts.push(block);
    }
    const lexical = data.lexical?.tokens?.find(item => item.start === token.start && item.length === token.length);
    const entries = (lexical?.owners?.entries || []).filter(entry => Array.isArray(entry.meaning) && entry.meaning.length);
    const meaningCount = entries.reduce((sum, entry) => sum + entry.meaning.length, 0);

    const panels = {
      readings() {
        const out = [];
        for (const group of groups) {
          const block = el('section', undefined, 'reading');
          block.append(el('h3', group.lemma + ' · ' + group.posName));
          const lines = el('ul');
          for (const line of group.lines) lines.append(el('li', (line.text || 'без допълнителни признаци') + (line.count > 1 ? ' (×' + line.count + ')' : '')));
          block.append(lines);
          out.push(block);
        }
        if (!known && graphCandidates.length) {
          const block = el('section', undefined, 'reading reading-graph');
          block.append(el('h3', 'Хипотези от графа на словообразуването'));
          const lines = el('ul');
          for (const candidate of graphCandidates.slice(0, 5)) {
            const tags = Array.isArray(candidate.fullTags) ? candidate.fullTags.slice(0, 3).join('; ') : 'без пълен таг';
            lines.append(el('li', (candidate.derivedLemma ?? candidate.word ?? '?') + ' ← „' + (candidate.base ?? '?') + '“ · ' + tags
              + (candidate.isConditional ? ' · условна основа' : '')));
          }
          block.append(lines);
          if (graphCandidates.length > 5) block.append(el('p', 'и още ' + (graphCandidates.length - 5) + ' хипотези в техническите детайли', 'note'));
          for (const note of notes) block.append(el('p', note, 'note'));
          out.push(block);
        }
        if (!known && !graphCandidates.length) {
          out.push(el('p', 'Няма точен речников прочит. Това не означава непременно грешна дума.', 'note'));
          for (const note of notes) out.push(el('p', note, 'note'));
        }
        return out;
      },
      meanings() {
        if (!entries.length) return [el('p', data.lexical?.metadataAvailable === false
          ? 'Речниковите значения не са налични в този пакет.' : 'Няма значения в отговора за тази дума.', 'note')];
        const out = [];
        for (const entry of entries) {
          if (entries.length > 1) out.push(el('p', entry.lexemeKey ?? entry.entryId, 'note'));
          const list = el('ol');
          for (const meaning of entry.meaning.slice(0, 6)) list.append(el('li', meaning));
          out.push(list);
          if (entry.meaning.length > 6) {
            const rest = keyed(el('details'), key + ':more:' + (entry.lexemeKey ?? entry.entryId));
            rest.append(el('summary', 'Още ' + (entry.meaning.length - 6) + ' значения'));
            const tail = el('ol');
            tail.start = 7;
            for (const meaning of entry.meaning.slice(6)) tail.append(el('li', meaning));
            rest.append(tail);
            out.push(rest);
          }
        }
        if (lexical.owners.truncated) out.push(el('p', 'Списъкът със значения е съкратен от API-то.', 'note'));
        return out;
      },
      forms() {
        const lemmas = lemmasOf(data, index);
        if (!lemmas.length) return [el('p', 'Формите се генерират по лема от речника, а тази дума няма точен речников прочит.', 'note')];
        const out = [];
        for (const lemma of lemmas) {
          if (lemmas.length > 1) out.push(el('h3', 'Форми на „' + lemma + '“'));
          out.push(...answerView(next, 'generate-forms', lemma, 'Покажи формите на „' + lemma + '“', answer => formsView(answer, token.text)));
        }
        out.push(el('p', 'Таговете са показани, както ги връща API-то; една лема може да има няколко парадигми.', 'note'));
        return out;
      },
      stress: () => answerView(next, 'stress', token.text, 'Покажи ударението', answer => stressView(answer, token.text)),
      formation() {
        const out = [el('h3', 'Разлагане'), ...answerView(next, 'decompose-word', token.text, 'Разложи думата', decompositionView)];
        const lemmas = lemmasOf(data, index);
        out.push(el('h3', 'Производни думи'));
        if (!lemmas.length) out.push(el('p', 'Производните се търсят от лема от речника, а тази дума няма точен речников прочит.', 'note'));
        for (const lemma of lemmas) out.push(...answerView(next, 'derive-words', lemma, 'Покажи производни от „' + lemma + '“',
          answer => [el('p', 'От „' + lemma + '“:'), ...derivationsView(answer)]));
        return out;
      }
    };
    const tablist = el('div', undefined, 'tabs');
    tablist.setAttribute('role', 'tablist');
    tablist.setAttribute('aria-label', 'Раздели за думата');
    const controls = TABS.map(([name, label], position) => {
      const control = keyed(button(name === 'meanings' && meaningCount ? label + ' (' + meaningCount + ')' : label, 'tab',
        () => selectTab(name)), 'word:tab:' + name);
      control.id = 'lab-tab-' + name;
      control.setAttribute('role', 'tab');
      control.setAttribute('aria-selected', String(name === tab));
      control.setAttribute('aria-controls', 'lab-tabpanel');
      control.tabIndex = name === tab ? 0 : -1;
      control.addEventListener('keydown', event => moveTabFocus(event, controls, position));
      return control;
    });
    tablist.append(...controls);
    const panel = keyed(el('div', undefined, 'tabpanel'), 'word:tabpanel');
    panel.id = 'lab-tabpanel';
    panel.setAttribute('role', 'tabpanel');
    panel.setAttribute('aria-labelledby', 'lab-tab-' + tab);
    panel.tabIndex = 0;
    if (demand && demand.response === next.response && demand.index === next.index && demand.tab === tab) panel.setAttribute('aria-busy', 'true');
    panel.append(...panels[tab]());
    parts.push(tablist, panel);
    parts.push(details('Технически детайли', node => {
      technicalList(node, [['Позиция', token.start + '+' + token.length + ' (UTF-16)'],
        ['Пълни прочити', rows === null ? 'недостъпни' : String(rows.length)],
        ['Сурови тагове', (rows || []).map(row => row.raw?.partOfSpeech ?? '—').join('; ') || 'няма']]);
      node.append(el('pre', JSON.stringify({readings: rows, analysis}, null, 2)));
    }, key + ':tech'));
    return {key: next, title: token.text, parts};
  }

  // ---------- continuation menu ----------
  // Dictionary forms compatible with the left context at the bound caret, in dictionary_score order (not a
  // probability). It opens by itself only for a fresh analysis with available, non-empty suggestions and a typed
  // prefix, while the caret is still where they were asked for; "Покажи съвместими форми" (or Ctrl+Space) opens it
  // without a prefix. Arrow Down activates an option; Enter or Tab accept only an activated option, otherwise they
  // keep their usual meaning; Esc closes; keys of an IME composition are never captured.
  // The continuation still belongs to this moment: the same text, collapsed caret, controller revision and target,
  // and no IME. Equal text and caret give the same span and prefix, so this is exactly when the shared
  // applyContinuation accepts a returned form; after Live is paused or the target changes it no longer holds.
  const continuationCurrent = snapshot => !!snapshot && !composing && editor.selectionStart === editor.selectionEnd
    && snapshot.text === editor.value && snapshot.caret === editor.selectionStart && snapshot.revision === controller.revision
    && (snapshot.requestTarget ?? null) === continuationTarget();

  function menuModel() {
    const continuation = display?.continuation;
    if (!continuation || !isFresh() || doc.activeElement !== editor || menu.dismissed === continuation
      || !continuationCurrent(continuation.snapshot)) return null;
    const {binding, evidence, snapshot} = continuation;
    if (!binding) return menu.forced ? {options: [], notice: evidence?.available === false
      ? 'Няма поддържан контекст за продължение на това място.' : 'Няма съвместими форми за това място.'} : null;
    const current = snapshot.text.slice(binding.start, binding.end);
    const options = evidence.suggestions.filter(row => row.form !== current);
    if (!menu.forced && (!binding.prefix.length || !options.length)) return null;
    return {options, noun: evidence.target === 'Noun', truncated: evidence.truncated === true,
      notice: options.length ? null : 'Няма съвместими форми за това място.'};
  }

  function renderMenu() {
    const model = menuModel();
    menu.options = model?.options ?? [];
    if (menu.active >= menu.options.length) menu.active = -1;
    if (!model) {
      if (!menuHost.hidden) {
        menuHost.hidden = true;
        menuHost.replaceChildren();
      }
      editor.removeAttribute('aria-activedescendant');
      return;
    }
    const parts = [el('p', model.noun ? 'Съществителни, съвместими с определенията пред курсора'
      : 'Глаголни форми, съвместими с контекста пред курсора', 'menu-title')];
    if (model.notice) parts.push(el('p', model.notice, 'note'));
    if (menu.options.length) {
      const list = el('div', undefined, 'menu-list');
      list.id = 'lab-menu-list';
      list.setAttribute('role', 'listbox');
      list.setAttribute('aria-label', 'Съвместими речникови форми');
      menu.options.forEach((row, index) => {
        const option = el('div', row.form, 'option');
        option.id = 'lab-option-' + index;
        option.setAttribute('role', 'option');
        option.setAttribute('aria-selected', String(index === menu.active));
        // Keeps focus and caret in the editor; the click itself accepts.
        option.addEventListener('pointerdown', event => event.preventDefault());
        option.addEventListener('click', () => acceptContinuation(row.form));
        list.append(option);
      });
      parts.push(list, el('p', 'Ред по речников score, не вероятност; текстът след курсора не е проверен.'
        + (model.truncated ? ' Има още форми извън показаните.' : '') + ' ↓ избира, Enter или Tab приема, Esc затваря.', 'note'));
    }
    menuHost.replaceChildren(...parts);
    menuHost.hidden = false;
    editor.setAttribute('aria-controls', 'lab-menu');
    if (menu.active >= 0) editor.setAttribute('aria-activedescendant', 'lab-option-' + menu.active);
    else editor.removeAttribute('aria-activedescendant');
    positionMenu();
  }

  // Places the menu under the caret line: the backdrop has the textarea's geometry and text, so a collapsed range at
  // the caret offset gives the caret's box. Without layout (or a range) the menu keeps its stylesheet position.
  function positionMenu() {
    const shell = editor.parentNode;
    if (menuHost.hidden || typeof doc.createRange !== 'function' || typeof shell?.getBoundingClientRect !== 'function') return;
    let remaining = editor.selectionStart, target = null;
    const walk = node => {
      for (const child of node.childNodes) {
        if (target) return;
        if (child.nodeType === 3) {
          if (remaining <= child.nodeValue.length) target = [child, remaining];
          else remaining -= child.nodeValue.length;
        } else walk(child);
      }
    };
    walk(backdrop);
    if (!target) return;
    const range = doc.createRange();
    range.setStart(target[0], target[1]);
    range.collapse(true);
    const caret = range.getClientRects()[0] ?? range.getBoundingClientRect(), box = shell.getBoundingClientRect();
    const left = Math.max(4, Math.min(caret.left - box.left, box.width - menuHost.offsetWidth - 4));
    const top = Math.max(4, Math.min(caret.bottom - box.top + 4, box.height - 4));
    menuHost.style.left = Math.round(left) + 'px';
    menuHost.style.top = Math.round(top) + 'px';
  }

  // Accepting runs the shared applyContinuation check (same text, revision, caret, span, prefix and target) and then
  // replaces exactly the original UTF-16 span through the undoable editor path.
  function acceptContinuation(form) {
    const continuation = display?.continuation;
    if (!continuation?.binding || !isFresh() || composing) return false;
    const applied = applyContinuation(editor.value, editor.selectionStart, editor.selectionEnd, controller.revision,
      continuation.binding, form, composing, continuationTarget());
    if (!applied) return false;
    const {start, end} = continuation.binding;
    menu = {active: -1, forced: false, dismissed: continuation, options: []};
    return replace(start, end - start, editor.value.slice(start, end), form);
  }

  // The explicit request: forms for the caret even without a prefix. Without a current continuation (another caret,
  // a paused Live, another target, a stale text or a failed request) it sends one analysis now through the shared
  // controller; flush also works while Live is paused, and the colours of the same text stay meanwhile.
  function openMenu() {
    if (!editor.value.trim() || composing || editor.selectionStart !== editor.selectionEnd) return;
    menu = {active: -1, forced: true, dismissed: null, options: []};
    editor.focus();
    if (!isFresh() || !continuationCurrent(display?.continuation?.snapshot)) {
      editCaret = editor.selectionStart;
      controller.flush();
    }
    renderMenu();
  }

  function menuKeys(event) {
    if (event.isComposing || event.keyCode === 229) return;
    if (event.ctrlKey && !event.altKey && !event.metaKey && (event.key === ' ' || event.code === 'Space')) {
      event.preventDefault();
      openMenu();
      return;
    }
    if (menuHost.hidden) return;
    const count = menu.options.length, plain = !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey;
    if (event.key === 'Escape') {
      event.preventDefault();
      menu = {active: -1, forced: false, dismissed: display?.continuation ?? null, options: []};
      renderMenu();
    } else if (event.key === 'ArrowDown' && count && plain) {
      event.preventDefault();
      menu.active = (menu.active + 1) % count;
      renderMenu();
    } else if (event.key === 'ArrowUp' && menu.active >= 0 && plain) {
      event.preventDefault();
      menu.active -= 1;
      renderMenu();
    } else if ((event.key === 'Enter' || event.key === 'Tab') && menu.active >= 0 && plain) {
      // The key is taken only when the form was really inserted; otherwise it keeps its usual meaning.
      if (acceptContinuation(menu.options[menu.active].form)) event.preventDefault();
      else renderMenu();
    }
  }

  // ---------- read-only stress reading of the whole text ----------
  // A separate text for reading only; its offsets never touch the editor. It is bound to the exact text it was asked
  // for, so an edit makes it stale and its choices inactive. One owner choice applies to one word only.
  // Why a shown reading no longer describes what is on screen: another text, or another package on the host.
  function readingStaleness(shown) {
    if (editor.value !== shown.binding.text) return 'text';
    if (display && shown.package !== (display.package ?? null)) return 'package';
    return null;
  }

  function readingButton(text, binding, action) {
    const node = el('button', text, 'action');
    node.type = 'button';
    node.disabled = !reading || readingStaleness(reading) !== null;
    node.addEventListener('click', () => {
      if (reading?.binding === binding && readingStaleness(reading) === null && !composing) action();
    });
    return node;
  }

  // An answer is shown only for its own reading, text and package. A reading that was abandoned while waiting (an
  // edit, an IME composition, a job the lane dropped) ends its loading state and never applies late data.
  function askReading(selection = null) {
    const text = editor.value;
    if (!text.trim() || text.length > MAX_LENGTH || composing) return;
    const binding = {text, selection};
    reading = {binding, status: 'loading'};
    const current = () => reading?.binding === binding && editor.value === binding.text && !composing;
    renderReading();
    transport.post('stress', {text, ...(selection ? {stressSelection: selection} : {})}, {key: 'reading', current}).then(result => {
      if (reading?.binding !== binding) return;
      const answer = result.http || result.error ? readAnswer('stress', result) : null;
      if (!current() || result.stale || !answer) reading = {binding, status: 'error', message: 'Прочитът беше прекъснат; прочети отново.'};
      else if (answer.status !== 'ok') reading = {binding, status: 'error', message: answer.message};
      else if (answer.data.text !== text) reading = {binding, status: 'error', message: 'Отговорът е за друг текст и не е показан.'};
      else if (display && answer.package !== (display.package ?? null))
        reading = {binding, status: 'error', message: 'Отговорът е от друг пакет на хоста и не е показан; прочети отново.'};
      else reading = {binding, status: 'ready', data: answer.data, package: answer.package};
      renderReading();
    });
  }

  // What the answer to an owner choice really shows for that word. Matched only says that the choice met a reading;
  // the word's own status says whether its stress could be shown (a written mark may conflict with it).
  function selectionNote(selectionStatus, token) {
    const word = '„' + (token?.text ?? '?') + '“', status = stressStatus(token?.status ?? 'Unknown');
    if (selectionStatus === 'Matched' && (token?.status === 'Supported' || token?.status === 'Inferred'))
      return 'Показан е избраният прочит за ' + word + ': ' + token.renderedText + '. Изборът е за тази дума и не доказва единствен нормативен прочит.';
    if (selectionStatus === 'Matched' && token?.status === 'Conflict')
      return 'Изборът съвпадна с прочит на ' + word + ', но има конфликт в сведенията за ударението; думата е показана, както е написана.';
    if (selectionStatus === 'Matched')
      return 'Изборът съвпадна с прочит на ' + word + ', но за него няма еднозначно ударение (' + status + '); не е показано избрано ударение.';
    return 'Изборът не съвпадна с прочит на ' + word + '; не е показано избрано ударение (' + status + ').';
  }

  const STATUS_ORDER = ['Supported', 'Inferred', 'Ambiguous', 'Incomplete', 'Conflict', 'Unknown', 'Unclassified', 'InvalidInput'];
  function renderReading() {
    const focus = remember(readingHost), parts = [], read = $('lab-read');
    if (read) read.disabled = !editor.value.trim() || reading?.status === 'loading';
    if (!reading) parts.push(el('p', 'Прочитът показва целия текст с ударенията от речника и източника на всяко ударение.', 'empty'));
    else if (reading.status === 'loading') parts.push(el('p', 'Зарежда…', 'loading'));
    else if (reading.status === 'error') parts.push(el('p', reading.message, 'note answer-error'));
    else {
      const {binding, data} = reading, staleness = readingStaleness(reading);
      reading.shownStale = staleness;
      if (staleness === 'text') parts.push(el('p', 'Текстът е променен; прочитът е за предишния текст и изборите са неактивни.', 'stale'));
      if (staleness === 'package') parts.push(el('p', 'Хостът вече използва друг пакет; прочитът е от предишния и изборите са неактивни. Прочети отново.', 'stale'));
      parts.push(el('p', data.renderedText, 'stress-text'));
      if (binding.selection) parts.push(el('p', selectionNote(data.selectionStatus,
        data.tokens.find(token => token.tokenIndex === binding.selection.tokenIndex)), 'note'));
      const counts = new Map();
      for (const token of data.tokens) counts.set(token.status, (counts.get(token.status) ?? 0) + 1);
      const statuses = [...counts.keys()].sort((a, b) => (STATUS_ORDER.indexOf(a) + 1 || 99) - (STATUS_ORDER.indexOf(b) + 1 || 99));
      parts.push(el('p', 'Думи по състояние: ' + statuses.map(status => stressStatus(status) + ' ' + counts.get(status)).join(' · ') + '.', 'note'));
      const review = data.tokens.filter(token => token.status !== 'Supported' && token.status !== 'Inferred');
      if (review.length) {
        const box = keyed(el('details', undefined, 'review'), 'reading:review');
        box.append(el('summary', 'Думи за преглед (' + review.length + ')'));
        const list = el('ul');
        for (const token of review) {
          const item = el('li');
          item.append(el('p', '„' + token.text + '“ — ' + stressStatus(token.status)), ...stressSources(token, (selection, label) =>
            keyed(readingButton(label, binding, () => askReading(selection)),
              'reading:choose:' + selection.tokenIndex + ':' + selection.entryId + ':' + selection.form + ':' + selection.fullTag)));
          list.append(item);
        }
        box.append(list);
        parts.push(box);
      }
      parts.push(el('p', STRESS_NOTE, 'note'),
        details('Технически детайли', node => node.append(el('pre', JSON.stringify(data, null, 2))), 'reading:tech'));
    }
    readingHost.replaceChildren(...parts);
    restoreFocus(readingHost, focus);
  }

  function onCaret() {
    renderMenu();
    if (!display || stale) return;
    renderWord(caretWord(display.response.data.tokens, editor.selectionStart));
  }

  function renderDev() {
    devHost.textContent = 'Изпратени заявки към API: ' + metrics.requests
      + (metrics.lastHttpMs === null ? '' : ' · последен анализ: ' + Math.round(metrics.lastHttpMs) + ' ms HTTP, '
        + Math.round(metrics.lastRenderMs) + ' ms изобразяване')
      + (metrics.lastFreshAfterEditMs === null ? '' : ' · от последната промяна до актуални цветове: '
        + Math.round(metrics.lastFreshAfterEditMs) + ' ms');
  }

  function invalidate(text, revision, change = {}) {
    lengthNode.textContent = text.length + ' / ' + MAX_LENGTH;
    if (display && ['selection', 'enabled', 'target'].includes(change.reason) && display.text === text
      && display.heuristics === change.heuristics && display.revision === revision - 1 && !composing) {
      display.revision = revision;
    }
    if (!text.trim()) {
      display = null;
      cardKey = null;
      demand = null;
      failure = null;
      checksHost.replaceChildren(el('p', 'Проверките ще се появят тук.', 'empty'));
      sentencesHost.replaceChildren(el('p', 'Възможните синтактични роли ще се появят тук.', 'empty'));
      countsNode.textContent = '';
      renderWord(-1);
    }
    paintBackdrop();
    setStale(!!display && display.revision !== revision);
    renderMenu();
    // An edit abandons the card's request: its answer can no longer reach the (now stale) card, so the card
    // shows the request button again (inactive) instead of a "Зарежда…" that would never finish.
    if (demand && !isFresh()) {
      demand = null;
      refreshCard();
    }
    // The stress reading follows its own text: a waiting reading of another text is abandoned, a shown one is
    // re-rendered only when it turns stale or current again (e.g. after undo).
    const read = $('lab-read');
    if (read) read.disabled = !text.trim() || reading?.status === 'loading';
    if (reading?.status === 'loading' && (reading.binding.text !== text || composing)) {
      reading = null;
      renderReading();
    } else if (reading?.status === 'ready' && reading.shownStale !== readingStaleness(reading)) renderReading();
  }

  const controller = createLiveController({
    lane: transport, invalidate, state: setState, setTimer, clearTimer, debounceMs, maxWaitMs,
    readSelection: () => editCaret === null ? null : {start: editCaret, end: editCaret},
    readContinuationTarget: continuationTarget,
    continuationLimit: 12,
    commit(result, revision, snapshot) {
      // A failed request for the text already on screen (a refresh) keeps that analysis current: it still
      // describes exactly this text. After an edit the previous analysis stays stale.
      if (result.error || !result.http?.ok || result.response?.data?.text !== editor.value || revision !== controller.revision) {
        setState('error', {retryAfterMs: result.retryAfterMs, kept: isFresh()});
        paintBackdrop();
        return;
      }
      const started = now(), response = result.response, previous = display;
      const focus = remember(wordHost, checksHost, sentencesHost);
      let view;
      // All views are built detached first; the backdrop validates every span before its single mutation.
      // Any failure leaves the previous panels, shows the current text plain and applies nothing.
      try {
        if (!responseShapeValid(response)) throw new Error('Невалидна колекция в отговора.');
        const checks = collectChecks(response.data), evidence = response.data.continuation ?? null;
        display = {response, revision, text: editor.value, heuristics: heuristicsToggle.checked, checks,
          warnings: warningTokens(response.data, checks), package: packageOf(response),
          continuation: {snapshot: snapshot ?? null, evidence, binding: bindContinuation(snapshot, evidence)}};
        // Answers of another package are never shown; a new package starts an empty cache.
        if (previous && previous.package !== display.package) answers.clear();
        view = {checks: checksView(), counts: countsView(), sentences: sentencesView(),
          word: safeWordView(cardKeyFor(caretWord(response.data.tokens, editor.selectionStart)))};
        paintBackdrop();
      } catch {
        // Nothing was mutated, so the backdrop keeps or repaints only what the previous display allows.
        display = previous;
        paintBackdrop();
        setStale(!!display && !isFresh());
        setState('error', {invalid: true, kept: isFresh()});
        return;
      }
      checksHost.replaceChildren(...view.checks);
      sentencesHost.replaceChildren(...view.sentences);
      countsNode.textContent = view.counts;
      applyWord(view.word);
      setStale(false);
      for (const host of [wordHost, checksHost, sentencesHost]) restoreFocus(host, focus);
      menu.active = -1;
      renderMenu();
      // A reading of another package becomes stale here, without waiting for an edit.
      if (reading?.status === 'ready' && reading.shownStale !== readingStaleness(reading)) renderReading();
      setState('ready');
      metrics.lastHttpMs = result.elapsedMs;
      metrics.lastRenderMs = now() - started;
      metrics.lastFreshAfterEditMs = metrics.lastEditAt === null ? null : now() - metrics.lastEditAt;
      renderDev();
      if (rawNode.closest('details')?.open) rawNode.textContent = JSON.stringify(response, null, 2);
    }
  });

  function onInput() {
    metrics.lastEditAt = now();
    // The continuation of the next analysis is asked for the caret of this edit.
    editCaret = editor.selectionStart === editor.selectionEnd ? editor.selectionStart : null;
    menu = {active: -1, forced: false, dismissed: null, options: []};
    controller.edit(editor.value, heuristicsToggle.checked);
    renderDev();
  }

  editor.addEventListener('input', onInput);
  editor.addEventListener('scroll', syncScroll);
  editor.addEventListener('keydown', menuKeys);
  editor.addEventListener('blur', renderMenu);
  editor.addEventListener('compositionstart', () => {
    composing = true;
    controller.compositionStart();
    paintBackdrop();
  });
  editor.addEventListener('compositionend', () => {
    composing = false;
    editCaret = editor.selectionStart === editor.selectionEnd ? editor.selectionStart : null;
    controller.compositionEnd(editor.value);
  });
  for (const type of ['click', 'select', 'focus']) editor.addEventListener(type, onCaret);
  editor.addEventListener('keyup', event => {
    if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown'].includes(event.key)) onCaret();
  });
  doc.addEventListener('selectionchange', () => {
    if (doc.activeElement === editor) onCaret();
  });
  const ResizeObserverType = doc.defaultView?.ResizeObserver;
  if (typeof ResizeObserverType === 'function') new ResizeObserverType(syncScroll).observe(editor);

  layerButtons.forEach((control, position) => {
    control.addEventListener('click', () => setLayer(control.dataset.layer));
    control.addEventListener('keydown', event => {
      const step = {ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1}[event.key];
      const target = event.key === 'Home' ? 0 : event.key === 'End' ? layerButtons.length - 1
        : step === undefined ? null : (position + step + layerButtons.length) % layerButtons.length;
      if (target === null) return;
      event.preventDefault();
      setLayer(layerButtons[target].dataset.layer, true);
    });
  });
  liveToggle.addEventListener('change', () => controller.setEnabled(liveToggle.checked));
  heuristicsToggle.addEventListener('change', onInput);
  $('lab-refresh').addEventListener('click', () => controller.flush());
  targetSelect?.addEventListener('change', () => {
    menu = {active: -1, forced: false, dismissed: null, options: []};
    controller.refreshTarget();
  });
  $('lab-continue')?.addEventListener('click', openMenu);
  $('lab-read')?.addEventListener('click', () => askReading());
  for (const example of doc.querySelectorAll('[data-example]')) example.addEventListener('click', () => {
    editor.focus();
    editor.setSelectionRange(0, editor.value.length);
    let done = false;
    try {
      done = typeof doc.execCommand === 'function' && doc.execCommand('insertText', false, example.dataset.example) === true;
    } catch {
      done = false;
    }
    if (!done || editor.value !== example.dataset.example) {
      editor.value = example.dataset.example;
      onInput();
    }
    editor.setSelectionRange(editor.value.length, editor.value.length);
  });
  rawNode.closest('details')?.addEventListener('toggle', event => {
    if (event.target.open) rawNode.textContent = display ? JSON.stringify(display.response, null, 2) : 'Все още няма отговор.';
  });

  const theme = $('lab-theme');
  try {
    const saved = storage?.getItem('bglang-lab-theme');
    if (saved === 'light' || saved === 'dark') doc.documentElement.dataset.theme = saved;
  } catch {}
  theme?.addEventListener('click', () => {
    const dark = doc.documentElement.dataset.theme === 'dark'
      || (!doc.documentElement.dataset.theme && doc.defaultView?.matchMedia?.('(prefers-color-scheme: dark)').matches);
    doc.documentElement.dataset.theme = dark ? 'light' : 'dark';
    try {
      storage?.setItem('bglang-lab-theme', doc.documentElement.dataset.theme);
    } catch {}
  });

  setLayer('pos');
  renderWord(-1);
  renderDev();
  renderReading();
  checksHost.replaceChildren(el('p', 'Проверките ще се появят тук.', 'empty'));
  sentencesHost.replaceChildren(el('p', 'Възможните синтактични роли ще се появят тук.', 'empty'));
  controller.edit(editor.value, heuristicsToggle.checked);

  if (typeof fetchImpl === 'function') {
    Promise.resolve().then(() => fetchImpl('/api/v2/')).then(response => response.ok ? response.json() : null).then(metadata => {
      if (!metadata) return;
      transport.configureBudget(metadata.limits?.requestsPerMinutePerIp);
      $('lab-identity').textContent = 'API ' + metadata.apiVersion + ' · host ' + metadata.versions?.host + ' · пакет '
        + metadata.package?.packageId + ' · бюджет ' + metadata.limits?.requestsPerMinutePerIp + ' заявки/мин';
    }).catch(() => {});
  }

  return {controller, metrics, get display() { return display; }, get layer() { return layer; }, setLayer, replace};
}

if (typeof document !== 'undefined' && document.getElementById('lab-editor')) {
  let storage = null;
  try {
    storage = globalThis.localStorage;
  } catch {}
  initLab(document, {storage});
}
