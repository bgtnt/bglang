import {
  createLiveController,
  completeReadings,
  caretWord,
  bindContinuation,
  applyContinuation,
  renderTokenText
} from './live-client.js';

const posNames = {
  Noun: 'Съществително',
  Adjective: 'Прилагателно',
  Verb: 'Глагол',
  PersonalPronoun: 'Лично местоимение',
  Pronoun: 'Местоимение',
  Numeral: 'Числително',
  Adverb: 'Наречие',
  Particle: 'Частица',
  Preposition: 'Предлог',
  Conjunction: 'Съюз',
  Interjection: 'Междуметие',
  ProperNoun: 'Собствено име',
  Unknown: 'Неустановен тип'
};

const featureNames = {
  person: 'Лице',
  number: 'Число',
  gender: 'Род',
  tense: 'Време',
  verbForm: 'Форма',
  article: 'Членуване',
  case: 'Падеж',
  nameKind: 'Вид име',
  isShortForm: 'Кратка форма',
  participleKind: 'Причастие',
  hasUnrecognizedFeatures: 'Неразпознати признаци',
  hasConflictingFeatures: 'Конфликтни признаци',
  paradigmId: 'Парадигма'
};

const words = {
  Singular: 'Единствено',
  Plural: 'Множествено',
  Masculine: 'Мъжки',
  Feminine: 'Женски',
  Neuter: 'Среден',
  First: 'Първо',
  Second: 'Второ',
  Third: 'Трето',
  Present: 'Сегашно',
  Past: 'Минало',
  Indefinite: 'Нечленувано',
  DefiniteFull: 'Пълен член',
  DefiniteShort: 'Кратък член'
};

const show = value =>
  value === null || value === undefined
    ? 'Недостъпно'
    : typeof value === 'boolean'
      ? (value ? 'Да' : 'Не')
      : (words[value] || String(value));
const count = value => Array.isArray(value) ? String(value.length) : '—';
const sum = (rows, key) => rows.reduce((n, row) => n + (Array.isArray(row[key]) ? row[key].length : 0), 0);

function el(tag, text, css) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (css) node.className = css;
  return node;
}

function detailList(parent, title, rows, describe) {
  const view = el('details'),
    summary = el('summary', title);
  view.append(summary);
  let built = false;
  view.addEventListener('toggle', () => {
    if (view.open && !built) {
      built = true;
      const list = el('ul');
      for (const row of rows) list.append(el('li', describe(row)));
      view.append(list);
    }
  });
  parent.append(view);
}

function facts(parent, object) {
  for (const [key, value] of Object.entries(object || {})) {
    if (value === null || ['string', 'number', 'boolean'].includes(typeof value)) parent.append(el('p', (featureNames[key]
      || key) + ': ' + show(value)));
  }
}

export function graphSupportLabel(supports) {
  return supports.some(support => Array.isArray(support.graphReadings) && support.graphReadings.length > 0)
    ? ' · Графова опора (хипотеза)' : '';
}

// Consumer presentation only: classify actual diagnostics, never context preview codes.
const diagnosticWarningCodes = new Set([
  "missing_terminal_punctuation",
  "sentence_initial_lowercase",
  "mixed_script_ocr_known_candidate",
  "digit_shape_ocr_known_candidate",
  "accent_sensitive_i_grave",
  "spelling_unknown_word_unique_distance_one_candidate",
  "pronoun_verb_agreement_mismatch",
  "preposition_missing_complement",
  "full_article_expected_for_subject_candidate",
  "productive_morphology_agreement_mismatch",
  "productive_attribute_agreement_mismatch"
]);
const diagnosticLabels = {
  "warning": "Предупреждение",
  "unsupported-check": "Неподдържана проверка",
  "information": "Информация"
};

export function diagnosticPresentation(data, token = null) {
  const validation = data?.validation;
  const messages = [
    ...(data?.diagnostics || []),
    ...(validation?.diagnostics || []),
    ...(validation?.spacingDiagnostics || []),
    ...(validation?.subordinateCommaDiagnostics || []),
    ...(validation?.fragmentDiagnostics || [])
  ].map(diagnostic => {
    const classification = diagnostic.code === 'full_short_article_check_not_ready'
      ? 'unsupported-check' : diagnosticWarningCodes.has(diagnostic.code) ? 'warning' : 'information';
    const label = diagnosticLabels[classification];
    return {
      diagnostic, classification, label,
      text: label + ' · ' + diagnostic.code + ' · ' + diagnostic.start + '+' + diagnostic.length + graphSupportLabel([diagnostic])
    };
  });
  const strongest = token && ['warning', 'unsupported-check', 'information'].find(classification =>
    messages.some(row => row.classification === classification
      && row.diagnostic.start < token.start + token.length
      && row.diagnostic.start + row.diagnostic.length > token.start));
  const coverage = validation === null ? 'Проверките не са върнати.'
    : validation === undefined ? 'Проверките липсват в отговора.'
      : validation.available === true
        ? 'Кандидатни проверки в ограничения обхват; липсата на съобщение не удостоверява граматична правилност.'
        : 'Проверките са недостъпни.' + (validation.unavailableReason ? ' ' + validation.unavailableReason : '');
  return {
    messages,
    tokenClass: strongest ? 'check-' + strongest : validation?.available === true ? 'check-empty' : 'check-unavailable',
    coverage
  };
}

export function renderContextSyntaxSummary(parent, context, syntax) {
  const lanes = [
    ['Контекстни роли', 'Контекст', context,
      sum(context?.sentences || [], 'localRoles') + sum(context?.sentences || [], 'selectedRoles')],
    ['Синтактични кандидати', 'Синтаксис', syntax,
      sum(syntax?.sentences || [], 'candidates')]
  ];
  for (const [title, name, evidence, total] of lanes) {
    const value = parent.ownerDocument.createElement('p');
    value.className = 'panel-value';
    value.textContent = title + ': ' + (evidence?.available === true ? total : '—');
    const availability = parent.ownerDocument.createElement('p');
    const state = evidence === null ? 'не е върнат.'
      : evidence === undefined ? 'липсва в отговора.'
        : evidence.available === true ? 'достъпен.'
          : 'недостъпен.' + (evidence.unavailableReason ? ' ' + evidence.unavailableReason : '');
    availability.textContent = name + ': ' + state;
    parent.append(value, availability);
  }
}


export function renderFiniteContinuation(parent, evidence, snapshot, select) {
  const doc = parent.ownerDocument;
  const make = (tag, text) => {const node = doc.createElement(tag); node.textContent = text; return node;};
  parent.replaceChildren();
  parent.hidden = true;
  const noun = evidence?.target === 'Noun';
  if (!snapshot || evidence?.version !== (noun ? 'nominal_continuation.v1' : 'finite_continuation.v1')
    || (!noun && evidence.target != null && evidence.target !== 'Finite')) return;
  parent.append(make('p', noun
    ? 'Съществителни · локално съвместими речникови интерпретации, не вероятност за следваща дума.'
    : 'Финитни речникови продължения · подкрепени интерпретации, не вероятност за следваща дума.'));
  if (evidence.available !== true) {
    parent.append(make('p', 'Контекстът за продължение е недостъпен. ' + (evidence.unavailableReason ?? '')));
    parent.hidden = false;
    return;
  }
  const binding = bindContinuation(snapshot, evidence);
  if (!binding) {
    parent.append(make('p', 'Продължението не съвпада с текущия текстов snapshot.'));
    parent.hidden = false;
    return;
  }
  const expectations = noun ? evidence.nominalExpectations : evidence.expectations;
  for (const [index, expectation] of expectations.entries()) {
    const supports = (noun ? expectation.attributes : expectation.supports) ?? [];
    const exact = supports.flatMap(row => row.readings ?? []).length;
    const graph = supports.flatMap(row => row.graphReadings ?? []).length;
    parent.append(make('p', noun
      ? 'Интерпретация ' + (index + 1) + ': ' + expectation.number + ' · род: ' + (expectation.gender ?? 'не е зададен')
        + '. Първо определение: ' + expectation.firstAttributeArticle + '; съществително: ' + expectation.nounArticle
        + '. Контекстни опори: ' + supports.length + '; exact refs: ' + exact + '; graph refs: ' + graph + '.'
      : 'Интерпретация ' + (index + 1) + ': ' + expectation.person + ' · ' + expectation.number
        + '. Контекстни опори: ' + supports.length + '; exact refs: ' + exact + '; graph refs: ' + graph + '.'));
  }
  parent.append(make('p', 'Източник: точният пакетен речник; ред по речников score, не вероятност. '
    + (evidence.truncated ? 'Има още съвместими форми извън показания лимит. ' : 'Лимитът не е отрязал върнатите форми. ')
    + (evidence.searchComplete ? 'Търсенето в заявения речников обхват е изчерпано.' : 'Изчерпаността на търсенето не е удостоверена.')));
  for (const suggestion of evidence.suggestions) {
    const button = make('button', suggestion.form + ' · score ' + suggestion.score);
    button.type = 'button';
    button.addEventListener('pointerdown', event => event.preventDefault());
    button.addEventListener('click', () => select(suggestion.form, binding));
    parent.append(button);
    const details = doc.createElement('details');
    details.append(make('summary', suggestion.form + ' · пълни речникови прочити и съвместими индекси'));
    details.append(make('pre', JSON.stringify({readings: suggestion.readings, matches: suggestion.matches}, null, 2)));
    parent.append(details);
  }
  if (!evidence.suggestions.length) parent.append(make('p', 'Няма върнати съвместими форми.'));
  if (noun) {
    const details = doc.createElement('details');
    details.append(make('summary', 'Оригинални опори на определенията по интерпретации'));
    details.append(make('pre', JSON.stringify({nominalExpectations: evidence.nominalExpectations}, null, 2)));
    parent.append(details);
  }
  parent.append(make('p', noun
    ? 'Обхват: локална съвместимост със съществителни от точния речник. Граматиката вдясно от курсора не е проверена.'
    : 'Обхват: финитни форми от точния речник. Граматиката вдясно от курсора не е проверена.'));
  parent.hidden = false;
}

export function initLiveDemo({
  lane,
  tokenAnalysis,
  showTokenDetails,
  search
}) {
  const editor = document.querySelector('#live-text'),
    preview = document.querySelector('#live-preview'),
    inspector = document.querySelector('#live-inspector');
  const auto = document.querySelector('#live-auto'),
    heuristics = document.querySelector('#live-heuristics'),
    layer = document.querySelector('#live-layer');
  const stateNode = document.querySelector('#live-state'),
    panels = document.querySelector('#live-panels'),
    actionHost = document.querySelector('#live-action-result');
  const completion = document.querySelector('#live-completion'),
    spellingHost = document.querySelector('#live-spelling'),
    targetMode = document.querySelector('#live-continuation-target'),
    raw = document.querySelector('#live-json');
  const currentTarget = () => targetMode?.value ?? 'Auto';
  let response = null,
    selected = -1,
    selectionRevision = 0,
    composing = false;
  let actionBusy = false,
    rawValue = null,
    displayValid = false,
    displayedSettings = null,
    inspectorData = null,
    frameRevision = 0,
    inspectorFrameRevision = -1,
    inspectorOccurrence = null,
    inspectorBinding = null,
    spellingBinding = null,
    panelsSignature = null,
    paintedLayer = null,
    placeholderShown = false;
  const labels = [
    'Морфология и лексика',
    'Контекст и синтаксис',
    'Правопис и проверки',
    'Имена и конструкции',
    'Фонетика и ударение',
    'Метрики и пълнота'
  ];

  function currentDisplay() {
    return displayValid && response?.data.text === editor.value && !composing
      && displayedSettings?.heuristics === heuristics.checked && displayedSettings?.target === currentTarget();
  }

  function rememberView(parent) {
    const details = [...parent.querySelectorAll('details')];
    const key = node => node.dataset.liveKey || node.querySelector('summary')?.textContent;
    const active = document.activeElement;
    const focused = parent.contains(active) ? active : null;
    const owner = focused?.closest('details');
    const scroll = parent.closest('.inspector') || parent;
    return {open: new Set(details.filter(node => node.open).map(key)), scroll, top: scroll.scrollTop, left: scroll.scrollLeft,
      focusKey: focused?.dataset.liveKey, summaryKey: focused?.nodeName.toLowerCase() === 'summary' ? key(owner) : null};
  }

  function restoreView(parent, saved) {
    if (!saved) return;
    let summary;
    for (const node of parent.querySelectorAll('details')) {
      const key = node.dataset.liveKey || node.querySelector('summary')?.textContent;
      node.open = saved.open.has(key);
      if (key === saved.summaryKey) summary = node.querySelector('summary');
    }
    const focused = saved.focusKey
      ? [...parent.querySelectorAll('[data-live-key]')].find(node => node.dataset.liveKey === saved.focusKey) : summary;
    focused?.focus({preventScroll: true});
    saved.scroll.scrollTop = saved.top;
    saved.scroll.scrollLeft = saved.left;
  }

  function currentInspector(binding) {
    return !!binding && currentDisplay() && binding === inspectorBinding && binding.response === response && binding.index === selected
      && binding.revision === controller.revision && binding.start === editor.selectionStart && binding.end === editor.selectionEnd;
  }

  function currentSpelling(binding) {
    return !!binding && currentDisplay() && binding === spellingBinding && binding.response === response
      && binding.revision === controller.revision && binding.start === editor.selectionStart && binding.end === editor.selectionEnd;
  }

  function rebindSpelling() {
    if (spellingBinding && currentDisplay() && spellingBinding.response === response) {
      spellingBinding.revision = controller.revision;
      spellingBinding.start = editor.selectionStart;
      spellingBinding.end = editor.selectionEnd;
    }
  }

  function actionAvailability() {
    const fresh = currentDisplay();
    preview.dataset.fresh = String(fresh);
    inspector.dataset.fresh = String(fresh);
    panels.dataset.fresh = String(fresh);
    spellingHost.dataset.fresh = String(fresh);
    for (const button of preview.querySelectorAll('[data-token-index]')) button.disabled = !fresh;
    for (const button of inspector.querySelectorAll('button')) button.disabled = !currentInspector(inspectorBinding);
    for (const button of spellingHost.querySelectorAll('button')) button.disabled = !currentSpelling(spellingBinding);
    for (const button of actionHost.querySelectorAll('button')) button.disabled = !fresh;
    document.querySelector('#live-stress').disabled = !fresh || actionBusy;
  }

  function spellingReplacement(original, form) {
    const letters = text => {
      const result = [];
      for (const char of text) {
        if (/^\p{M}$/u.test(char) && result.length) continue;
        if (!/^\p{Script=Cyrillic}$/u.test(char) || !/^[\p{Ll}\p{Lu}]$/u.test(char)) return null;
        result.push(char);
      }
      return result;
    };
    const source = letters(original), candidate = letters(form);
    if (!source || source.length < 2 || !candidate?.length || !candidate.every(char => /^\p{Ll}$/u.test(char))) return form;
    const caps = source.every(char => /^\p{Lu}$/u.test(char));
    const title = /^\p{Lu}$/u.test(source[0]) && source.slice(1).every(char => /^\p{Ll}$/u.test(char));
    if (!caps && !title) return form;
    let index = 0, replacement = '';
    for (const char of form) {
      if (/^\p{M}$/u.test(char)) { replacement += char; continue; }
      const proposed = caps || index === 0 ? char.toUpperCase() : char;
      if (!/^\p{Script=Cyrillic}$/u.test(proposed) || !/^[\p{Ll}\p{Lu}]$/u.test(proposed)) return form;
      replacement += proposed;
      index++;
    }
    return replacement;
  }

  function spellingRange(text, token) {
    const start = token.start, end = start + token.length;
    if (!Number.isInteger(start) || !Number.isInteger(token.length) || start < 0 || end <= start || end > text.length
      || token.text !== text.slice(start, end)) return null;
    const splitsSurrogate = offset => offset > 0 && offset < text.length
      && /[\uD800-\uDBFF]/u.test(text[offset - 1]) && /[\uDC00-\uDFFF]/u.test(text[offset]);
    if (splitsSurrogate(start) || splitsSurrogate(end) || /^\p{M}/u.test(text.slice(start)) || /^\p{M}/u.test(text.slice(end))) return null;
    return {start, end};
  }

  function renderSpellings() {
    const data = response.data;
    const binding = {response, revision: controller.revision, start: editor.selectionStart, end: editor.selectionEnd};
    spellingBinding = binding;
    const rows = [];
    for (const word of data.spelling?.words || []) {
      const token = data.tokens[word.tokenIndex], alternatives = word.alternatives || [];
      if (!token || token.kind !== 'Word' || !alternatives.length) continue;
      const range = spellingRange(data.text, token);
      if (!range) continue;
      const {start, end} = range, original = data.text.slice(start, end);
      const heading = el('p', 'За „' + original + '“ · ' + token.start + '+' + token.length, 'word-meta');
      rows.push(heading);
      const contextual = Array.isArray(word.contextualCandidates) ? word.contextualCandidates : [];
      const positions = new Map();
      for (const [position, candidate] of contextual.entries()) {
        if (Number.isInteger(candidate.alternativeIndex) && candidate.alternativeIndex >= 0
          && candidate.alternativeIndex < alternatives.length && !positions.has(candidate.alternativeIndex))
          positions.set(candidate.alternativeIndex, position);
      }
      const order = alternatives.map((_, index) => index);
      // Distance stays primary; only the server's optional order refines its ties.
      order.sort((a, b) => alternatives[a].distance - alternatives[b].distance
        || (positions.get(a) ?? contextual.length + a) - (positions.get(b) ?? contextual.length + b) || a - b);
      for (const alternativeIndex of order) {
        const alternative = alternatives[alternativeIndex];
        if (typeof alternative.form !== 'string' || !alternative.form.length) continue;
        const replacement = spellingReplacement(original, alternative.form);
        const candidate = contextual.find(row => row.alternativeIndex === alternativeIndex);
        const kind = typeof candidate?.assessment === 'string' ? candidate.assessment.toLowerCase() : '';
        const supported = kind === 'supported' && Array.isArray(candidate.matches) && candidate.matches.length > 0;
        const following = supported ? [...new Set(candidate.matches.map(match => data.tokens[match.followingNounReading?.tokenIndex]?.text)
          .filter(text => typeof text === 'string' && text.length))] : [];
        const assessment = supported && following.length ? 'съвместим прочит с „' + following.join('“, „') + '“'
          : kind === 'nocompatiblepair' ? 'няма съвместима измерена двойка' : 'контекст: неоценен';
        const button = el('button', replacement + ' · разстояние ' + alternative.distance + ' · ' + assessment);
        button.type = 'button';
        button.addEventListener('click', () => {
          if (!currentSpelling(binding) || response.data !== data
            || editor.value.slice(start, end) !== original) return;
          editor.setRangeText(replacement, start, end, 'preserve');
          editor.focus({preventScroll: true});
          onInput();
        });
        rows.push(button);
      }
    }
    if (!rows.length) {
      spellingHost.replaceChildren();
      spellingHost.hidden = true;
      return;
    }
    spellingHost.replaceChildren(el('p', 'Близки речникови форми в целия текст'), ...rows,
      el('p', 'Предложения за преглед, не доказани правописни грешки. Имена, графови прочити и текуща незавършена дума остават несигурни. Замяна има само при изричен избор.'));
    spellingHost.hidden = false;
  }

  const theme = document.querySelector('#theme-toggle');
  try {
    const saved = localStorage.getItem('bglang-theme');
    if (saved === 'light' || saved === 'dark') document.documentElement.dataset.theme = saved;
  } catch {}

  function updateThemeLabel() {
    const dark = document.documentElement.dataset.theme === 'dark' || (!document.documentElement.dataset.theme && matchMedia('(prefers-color-scheme: dark)').matches);
    theme.setAttribute('aria-label', dark ? 'Включи светла тема' : 'Включи тъмна тема');
    theme.setAttribute('aria-pressed', String(dark));
  }
  theme.addEventListener('click', () => {
    const dark = theme.getAttribute('aria-pressed') === 'true';
    document.documentElement.dataset.theme = dark ? 'light' : 'dark';
    try {
      localStorage.setItem('bglang-theme', dark ? 'light' : 'dark');
    } catch {}
    updateThemeLabel();
  });
  updateThemeLabel();

  function panel(title, value, note, index) {
    const node = el('section', undefined, 'analysis-panel'),
      h = el('h3');
    h.append(el('span', String(index + 1).padStart(2, '0'), 'panel-number'), document.createTextNode(title));
    node.append(h);
    if (value !== undefined) node.append(el('p', value, 'panel-value'));
    node.append(el('p', note));
    panels.append(node);
    return node;
  }

  function blankPanels() {
    panels.replaceChildren();
    labels.forEach((title, i) => panel(title, '—', 'Очаква текущия текст', i));
  }

  function setState(state, extra = {}) {
    stateNode.dataset.state = state;
    const messages = {
      empty: 'Започни да пишеш',
      waiting: 'Изчаква актуалния текст…',
      working: 'Анализира текущия текст…',
      ready: 'Анализът е тук',
      paused: 'Автоматичният анализ е на пауза',
      composing: 'Въвеждане с клавиатура…',
      oversize: 'Текстът надхвърля 4096 UTF-16 позиции',
      error: 'HTTP отговорът не е достъпен'
    };
    stateNode.querySelector('span').textContent = extra.retryAfterMs ? 'Host-ът поиска изчакване (' + Math.ceil(extra.retryAfterMs / 1000)
      + ' s).' : messages[state] || state;
    if (response && !currentDisplay()) stateNode.querySelector('span').textContent += ' Показан е предишният анализ.';
    preview.setAttribute('aria-busy', String(state === 'working'));
  }

  function invalidate(text, revision, change = {}) {
    const retainCurrent = displayValid && ['selection', 'enabled'].includes(change.reason)
      && response?.data.text === text && displayedSettings?.heuristics === change.heuristics
      && displayedSettings?.target === change.target && !composing;
    displayValid = retainCurrent;
    selectionRevision++;
    completion.hidden = true;
    actionHost.hidden = true;
    if (!retainCurrent) spellingBinding = null;
    document.querySelector('#live-length').textContent = text.length + ' / 4096';
    if (!text.trim()) {
      response = null;
      displayedSettings = null;
      inspectorData = null;
      inspectorFrameRevision = -1;
      inspectorOccurrence = null;
      inspectorBinding = null;
      selected = -1;
      placeholderShown = false;
    }
    if (!response) {
      if (preview.textContent !== text) preview.textContent = text;
      if (!placeholderShown) {
        document.querySelector('#inspect-title').textContent = 'Текущата дума';
        inspector.replaceChildren(el('p', text.trim() ? 'Очаква първия текстов анализ.' : 'Напиши текст или избери пример.', 'empty-copy'));
        for (const id of ['#live-word-count', '#live-reading-count', '#live-timing']) document.querySelector(id).textContent = '—';
        rawValue = null;
        raw.textContent = 'Няма актуален отговор за този текст.';
        spellingHost.replaceChildren();
        spellingHost.hidden = true;
        panelsSignature = null;
        blankPanels();
        placeholderShown = true;
      }
    } else if (retainCurrent) {
      if (inspectorBinding && change.reason === 'enabled') {
        inspectorBinding.revision = revision;
        inspectorBinding.start = editor.selectionStart;
        inspectorBinding.end = editor.selectionEnd;
      }
      rebindSpelling();
    }
    actionAvailability();
  }
  const controller = createLiveController({
    lane,
    invalidate,
    state: setState,
    readSelection: () => ({start: editor.selectionStart, end: editor.selectionEnd}),
    readContinuationTarget: currentTarget,
    continuationLimit: 12,
    commit(result, revision, snapshot) {
      if (result.error || !result.http?.ok || result.response.data?.text !== editor.value) {
        displayValid = false;
        spellingBinding = null;
        completion.hidden = true;
        actionHost.hidden = true;
        actionAvailability();
        setState('error', {retryAfterMs: result.retryAfterMs});
        return;
      }
      const previous = response;
      response = result.response;
      const started = performance.now();
      try {
        paint();
      } catch {
        // renderTokenText validates all spans before mutation; retain the last good frame.
        response = previous;
        displayValid = false;
        spellingBinding = null;
        completion.hidden = true;
        actionHost.hidden = true;
        actionAvailability();
        setState('error');
        return;
      }
      displayValid = true;
      frameRevision++;
      displayedSettings = {heuristics: heuristics.checked, target: currentTarget()};
      placeholderShown = false;
      const frame = response;
      renderFiniteContinuation(completion, response.data.continuation, snapshot, (form, binding) => {
        if (!currentDisplay() || response !== frame) return;
        const applied = applyContinuation(editor.value, editor.selectionStart, editor.selectionEnd, controller.revision, binding, form, composing, currentTarget());
        if (!applied) return;
        editor.setRangeText(form, binding.start, binding.end, 'end');
        editor.focus();
        onInput();
      });
      rawValue = response;
      renderPanels();
      select(caretWord(response.data.tokens, editor.selectionStart));
      renderSpellings();
      actionAvailability();
      setState('ready');
      document.querySelector('#live-timing').textContent = Math.round(result.elapsedMs) + ' ms HTTP · ' + Math.round(performance.now() - started)
        + ' ms DOM';
      document.querySelector('#live-word-count').textContent = 'Думи: ' + response.data.tokens.filter(t => t.kind === 'Word').length;
      const full = response.data.profile?.fullReadingOccurrences;
      document.querySelector('#live-reading-count').textContent = 'Пълни прочити: ' + (full === null || full === undefined ? '—' : full);
      if (raw.closest('details').open) raw.textContent = JSON.stringify(rawValue, null, 2);
    }
  });
  raw.closest('details').addEventListener('toggle', event => {
    if (event.target.open && rawValue) raw.textContent = JSON.stringify(rawValue, null, 2);
  });

  function classify(index) {
    const data = response.data,
      token = data.tokens[index];
    if (layer.value === 'knowledge') {
      const profile = data.profile?.words.find(w => w.tokenIndex === index);
      if (profile?.observedProjectedAmbiguity === true) return 'known-ambiguous';
      return tokenAnalysis(token, response)?.isKnown ? 'known-known' : 'known-unknown';
    }
    if (layer.value === 'checks') return diagnosticPresentation(data, token).tokenClass;
    const types = [
      ...new Set((completeReadings(data, index) || []).map(r => r.features.partOfSpeech))
    ];
    if (types.length > 1) return 'pos-multiple';
    return {
      Noun: 'pos-noun',
      Verb: 'pos-verb',
      Adjective: 'pos-adjective',
      Adverb: 'pos-adverb',
      ProperNoun: 'pos-name'
    }[types[0]] || 'pos-other';
  }

  function paint() {
    if (!response) return;
    const frame = response;
    renderTokenText(preview, response.data.text, response.data.tokens, classify, index => {
      if (!currentDisplay() || response !== frame) return;
      const token = frame.data.tokens[index];
      editor.setSelectionRange(token.start, token.start + token.length);
      controller.refreshSelection();
      select(index);
      rebindSpelling();
      actionAvailability();
    });
    actionAvailability();
    if (paintedLayer === layer.value) return;
    paintedLayer = layer.value;
    const legend = document.querySelector('#live-legend');
    legend.replaceChildren();
    const rows = layer.value === 'pos' ? [
      ['pos-noun', 'Съществително'],
      ['pos-verb', 'Глагол'],
      ['pos-adjective', 'Прилагателно'],
      ['pos-adverb', 'Наречие'],
      ['pos-name', 'Име'],
      ['pos-multiple', 'Няколко типа'],
      ['pos-other', 'Друг / неустановен']
    ] : layer.value === 'knowledge' ? [
      ['known-known', 'Точен речников прочит'],
      ['known-ambiguous', 'Няколко typed проекции'],
      ['known-unknown', 'Няма точен прочит']
    ] : [
      ['check-warning', 'Предупреждение'],
      ['check-unsupported-check', 'Неподдържана проверка'],
      ['check-information', 'Информация'],
      ['check-empty', 'Няма върнато съобщение'],
      ['check-unavailable', 'Недостъпна проверка']
    ];
    rows.forEach(([css, text]) => {
      const item = el('span', undefined, 'legend-item');
      item.append(el('i', undefined, 'legend-dot ' + css), document.createTextNode(text));
      legend.append(item);
    });
  }

  function rolesFor(index) {
    return (response.data.context?.sentences || []).flatMap(s => [
      ...(s.selectedRoles || []),
      ...(s.localRoles || [])
    ]).filter(r => r.tokenIndex === index);
  }

  function select(index) {
    const data = response?.data, token = data?.tokens[index];
    const occurrence = token && token.kind === 'Word' ? JSON.stringify([index, token.start, token.length, token.text]) : null;
    for (const button of preview.querySelectorAll('[data-token-index]')) button.setAttribute('aria-pressed', String(Number(button.dataset.tokenIndex) === index));
    if (inspectorData === data && inspectorOccurrence === occurrence && inspectorFrameRevision === frameRevision) {
      if (inspectorBinding) {
        inspectorBinding.revision = controller.revision;
        inspectorBinding.start = editor.selectionStart;
        inspectorBinding.end = editor.selectionEnd;
      }
      actionAvailability();
      return;
    }
    const saved = rememberView(inspector);
    selectionRevision++;
    actionHost.hidden = true;
    selected = index;
    inspectorData = data;
    inspectorFrameRevision = frameRevision;
    inspectorOccurrence = occurrence;
    const binding = {response, index, revision: controller.revision, start: editor.selectionStart, end: editor.selectionEnd};
    inspectorBinding = binding;
    if (!response || index < 0 || token?.kind !== 'Word') {
      document.querySelector('#inspect-title').textContent = 'Между думите';
      inspector.replaceChildren(
        el('p', 'Избери дума или премести курсора в нея. Контекстните кандидати са в слоя за синтаксис.', 'empty-copy')
      );
      restoreView(inspector, saved);
      actionAvailability();
      return;
    }
    const word = tokenAnalysis(token, response),
      rows = completeReadings(response.data, index),
      profile = response.data.profile?.words.find(w => w.tokenIndex === index);
    document.querySelector('#inspect-title').textContent = token.text;
    inspector.replaceChildren(el('p', 'Позиция ' + token.start + ' · дължина ' + token.length + ' UTF-16', 'word-meta'));
    inspector.append(
      el(
        'p',
        (word?.isKnown ? 'Точна речникова форма.' : 'Няма точен речников прочит; хипотезите остават отделни.') + ' ' + (profile?.observedProjectedAmbiguity === true
          ? 'Няколко typed интерпретации.' : 'Контекстът не удостоверява един правилен прочит.'),
        'word-summary'
      )
    );
    if (profile?.actualDisplayTruncated) inspector.append(el('p', 'Краткият display е ограничен. Тук следваме пълния inventory.', 'word-meta'));
    const roles = rolesFor(index);
    if (roles.length) inspector.append(el('p', 'Контекстни роли: ' + roles.map(r => r.role + graphSupportLabel([r])).join(' · '), 'word-meta'));
    if (rows === null) inspector.append(el('p', 'Пълният reading inventory е недостъпен.', 'word-meta'));
    const duplicateReadings = new Map();
    (rows || []).forEach((row, i) => {
      const rawKey = JSON.stringify(row.raw), ordinal = duplicateReadings.get(rawKey) || 0;
      duplicateReadings.set(rawKey, ordinal + 1);
      const key = 'reading:' + rawKey + ':' + ordinal;
      const view = el('details', undefined, 'reading'), summary = el('summary', row.raw.lemma + ' · ' + (posNames[row.features.partOfSpeech] || row.features.partOfSpeech));
      view.dataset.liveKey = key;
      summary.dataset.liveKey = 'summary:' + key;
      view.append(summary);
      const chips = el('div', undefined, 'feature-tags');
      for (const [key, value] of Object.entries(row.features)) {
        if (key === 'partOfSpeech' || key === 'rawTag' || value === null || value === undefined) continue;
        chips.append(el('span', (featureNames[key] || key) + ': ' + show(value), 'feature-tag'));
      }
      view.append(chips, el('p', 'Пълен прочит #' + i), el('code', row.raw.partOfSpeech ?? 'Без raw tag'));
      const forms = el('button', 'Словоформи за тази лема');
      forms.type = 'button';
      forms.dataset.liveKey = 'forms:' + key;
      forms.addEventListener('click', () => { if (currentInspector(binding)) openExplorer('generate-forms', row.raw.lemma); });
      view.append(forms);
      inspector.append(view);
    });

    const lexical = response.data.lexical?.tokens.find(t => t.start === token.start && t.length === token.length && t.surface === token.text);
    if (lexical) {
      if (!lexical.owners.metadataAvailable) inspector.append(el('p', 'Owner metadata е недостъпно.', 'word-meta'));
      for (const owner of lexical.owners.entries) {
        const view = el('details', undefined, 'reading');
        view.dataset.liveKey = 'owner:' + owner.entryId;
        view.append(el('summary', 'Лексика · ' + owner.meaning.length + ' значения'), el('p', owner.entryId), el('code', owner.lexemeKey));
        for (const meaning of owner.meaning) view.append(el('p', meaning));
        if (!owner.meaning.length) view.append(el('p', 'Няма върнато значение.'));
        inspector.append(view);
      }
      if (lexical.owners.truncated) inspector.append(el('p', 'Owner списъкът е ограничен.', 'word-meta'));
    }

    const spelling = response.data.spelling?.words.find(t => t.tokenIndex === index);
    if (spelling) {
      inspector.append(el('p', 'Правописно търсене: ' + spelling.search, 'word-meta'));
      if (spelling.alternatives.length) detailList(inspector, 'Върнати правописни алтернативи', spelling.alternatives, a => a.form
        + ' · разстояние ' + a.distance);
    }

    const actions = el('div');
    for (const [label, op] of [
      ['Анализ и граф', 'analyze-word'],
      ['Разлагане', 'decompose-word'],
      ['Словообразуване', 'derive-words']
    ]) {
      const b = el('button', label);
      b.type = 'button';
      b.dataset.liveKey = 'action:' + op;
      b.addEventListener('click', () => { if (currentInspector(binding)) openExplorer(op, token.text); });
      actions.append(b);
    }
    const evidence = el('button', 'Пълно evidence');
    evidence.type = 'button';
    evidence.dataset.liveKey = 'action:evidence';
    evidence.addEventListener('click', () => {
      if (!currentInspector(binding)) return;
      document.querySelector('.secondary-workspace').open = true;
      showTokenDetails(token, response.data.text, response);
    });
    actions.append(evidence);
    inspector.append(actions);
    restoreView(inspector, saved);
    actionAvailability();
  }

  function openExplorer(op, text) {
    if (!currentDisplay()) return;
    document.querySelector('.secondary-workspace').open = true;
    document.querySelector('#explorer').open = true;
    search(op, text);
    document.querySelector('#explorer').scrollIntoView({
      block: 'start',
      behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
    });
  }

  function renderPanels() {
    const shown = response.data;
    const signature = JSON.stringify([shown.readingInventory, shown.lexical, shown.context, shown.syntax, shown.validation,
      shown.diagnostics, shown.properNames, shown.wordConstructions, shown.phonetics, shown.metrics, shown.profile]);
    if (signature === panelsSignature) return;
    const saved = rememberView(panels);
    panels.replaceChildren();
    const d = response.data,
      v = d.validation,
      context = d.context,
      syntax = d.syntax,
      checks = diagnosticPresentation(d);
    let p;
    p = panel(
      labels[0],
      'Думи: ' + count(d.readingInventory?.occurrences),
      'Пълни точни прочити; display и owner completeness са отделни.',
      0
    );
    detailList(
      p,
      'Лексикални owners',
      d.lexical?.tokens || [],
      t => t.surface + ' · ' + t.owners.entries.length + ' owners' + (t.owners.truncated ? ' · ограничени' : '')
    );
    const sentences = context?.sentences || [];
    p = panel(
      labels[1],
      undefined,
      'Отделни контекстни роли и синтактични кандидати; общ разбор не е удостоверен.',
      1
    );
    renderContextSyntaxSummary(p, context, syntax);
    detailList(
      p,
      'Контекстни алтернативи',
      sentences,
      s => s.status + ' · ' + s.candidates.length + ' кандидата · complete: ' + show(s.complete)
    );
    detailList(p, 'Синтактични кандидати', syntax?.sentences.flatMap(s => s.candidates) || [], c => c.role + ' · ' + c.ruleId + graphSupportLabel([...(c.targets || []), ...(c.context || [])]));
    p = panel(
      labels[2],
      'Съобщения: ' + checks.messages.length,
      checks.coverage,
      2
    );
    detailList(p, 'Диагностики', checks.messages, r => r.text);
    for (const [key, title] of [
      ['spacingDiagnostics', 'Разстояния'],
      ['subordinateCommaDiagnostics', 'Запетаи'],
      ['fragmentDiagnostics', 'Фрагменти']
    ]) p.append(
      el('p', title + ': ' + (v?.[key] === null ? 'Изключено' : Array.isArray(v?.[key]) ? v[key].length + ' върнати' : 'Недостъпно'))
    );
    detailList(
      p,
      'Недостъпни проверки',
      v?.unavailableChecks || [],
      r => typeof r === 'string' ? r : Object.entries(r).map(([k, x]) => k + ': ' + show(x)).join(' · ')
    );
    p = panel(
      labels[3],
      'Имена: ' + count(d.properNames?.candidates),
      d.wordConstructions === null ? 'Конструкциите са изключени.' : 'Конструкциите са owner-bound кандидати, не grammar authority.',
      3
    );
    detailList(p, 'Кандидати за имена', d.properNames?.candidates || [], r => 'Токен ' + r.tokenIndex + ' · ' + r.lane
      + ' · ' + r.kinds);
    detailList(
      p,
      'Конструкции в текста',
      d.wordConstructions?.matches || [],
      r => 'Кандидат #' + r.candidateIndex + ' · ' + r.start + '+' + r.length
    );
    if (d.wordConstructions) p.append(el('p', 'Конструкции complete: ' + show(d.wordConstructions.complete)));
    p = panel(
      labels[4],
      'Сричкови кандидати: ' + count(d.phonetics?.syllableCandidates),
      'Писмени знаци, inference и lexical evidence са различни.',
      4
    );
    for (const key of [
      'explicitStressPositions',
      'inferredSingleVowelStressPositions',
      'reviewedExactStressPositions',
      'lexicalStressPositions'
    ]) if (d.phonetics?.[key]) p.append(el('p', ({
      explicitStressPositions: 'Писмени знаци',
      inferredSingleVowelStressPositions: 'Една гласна / inference',
      reviewedExactStressPositions: 'Reviewed exact',
      lexicalStressPositions: 'Лексикално evidence'
    })[key] + ': ' + d.phonetics[key].length));
    p = panel(
      labels[5],
      'Прочити: ' + show(d.profile?.fullReadingOccurrences),
      'Nullable показателите не стават нула или удостоверена пълнота.',
      5
    );
    detailList(p, 'Метрики', Object.entries(d.metrics || {}), r => r[0] + ': ' + show(r[1]));
    detailList(
      p,
      'Профил на прочитите',
      (d.profile?.words || []),
      r => 'Токен ' + r.tokenIndex + ' · ' + r.fullReadingCount + ' прочита · ' + show(r.projectedGroupCount) + ' typed групи · '
        + r.issues
    );
    restoreView(panels, saved);
    panelsSignature = signature;
  }

  async function stress(selection) {
    if (actionBusy || !currentDisplay() || !editor.value.trim()) return;
    const rev = controller.revision,
      sel = selectionRevision,
      start = editor.selectionStart,
      end = editor.selectionEnd,
      frame = response;
    const current = () => currentDisplay() && frame === response && rev === controller.revision && sel === selectionRevision
      && start === editor.selectionStart && end === editor.selectionEnd;
    actionBusy = true;
    const trigger = document.querySelector('#live-stress');
    trigger.disabled = true;
    actionHost.hidden = false;
    actionHost.replaceChildren(el('p', 'Изчаква отделната заявка за ударение…'));
    try {
      const result = await lane.post('stress', {
        text: editor.value,
        ...(selection ? {
          stressSelection: selection
        } : {})
      }, {
        key: 'manual',
        current
      });
      if (!current() || result.stale) return;
      if (result.error || !result.http.ok) {
        actionHost.replaceChildren(el('p', 'Няма успешен stress отговор.'));
        return;
      }
      const data = result.response.data;
      actionHost.replaceChildren(
        el('h3', 'Ударение · върнати алтернативи'),
        el('p', data.renderedText),
        el(
          'p',
          'Owner selection: ' + data.selectionStatus + '. Писмен знак, Legacy capture и qualified inference не са еднаква authority.'
        )
      );
      for (const token of data.tokens) {
        const view = el('details');
        view.append(el('summary', token.text + ' · ' + token.status));
        for (const reading of token.readings) {
          view.append(el('p', 'Пълен прочит #' + reading.completeReadingIndex + ' · ' + reading.reading.lemma));
          for (const owner of reading.owners) {
            for (const realization of owner.realizations) view.append(
              el('p', realization.origin + ' · ' + realization.status + ' · ' + realization.marks.map(m => m.utf16Position).join(', '))
            );
            const button = el('button', 'Избери ' + owner.entryId);
            button.type = 'button';
            button.addEventListener('click', () => current() && stress({
              tokenIndex: token.tokenIndex,
              entryId: owner.entryId,
              form: reading.reading.form,
              fullTag: reading.reading.partOfSpeech ?? null
            }));
            view.append(button);
          }
        }
        actionHost.append(view);
      }
    } finally {
      actionBusy = false;
      actionAvailability();
    }
  }

  function onInput() {
    controller.edit(editor.value, heuristics.checked);
  }
  editor.addEventListener('input', onInput);
  editor.addEventListener('compositionstart', () => {
    composing = true;
    controller.compositionStart();
    completion.hidden = true;
  });
  editor.addEventListener('compositionend', () => {
    composing = false;
    controller.compositionEnd(editor.value);
  });

  function onCaret() {
    controller.refreshSelection();
    if (currentDisplay()) select(caretWord(response.data.tokens, editor.selectionStart));
    rebindSpelling();
    actionAvailability();
  }
  editor.addEventListener('click', onCaret);
  editor.addEventListener('select', onCaret);
  editor.addEventListener('keyup', event => {
    if (['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) onCaret();
  });
  auto.addEventListener('change', () => {
    controller.setEnabled(auto.checked);
    completion.hidden = true;
  });
  targetMode?.addEventListener('change', () => controller.refreshTarget());
  heuristics.addEventListener('change', onInput);
  document.querySelector('#live-refresh').addEventListener('click', () => controller.flush());
  layer.addEventListener('change', () => {
    paint();
    if (selected >= 0) select(selected);
  });
  document.querySelector('#live-stress').addEventListener('click', () => stress());
  document.querySelectorAll('[data-live-example]').forEach(button => button.addEventListener('click', () => {
    editor.value = button.dataset.liveExample;
    editor.focus();
    editor.setSelectionRange(editor.value.length, editor.value.length);
    onInput();
  }));
  controller.edit(editor.value, heuristics.checked);
  return {
    metadata(metadata) {
      lane.configureBudget(metadata.limits.requestsPerMinutePerIp);
      document.querySelector('#live-budget').textContent = 'Текущ host бюджет: ' + metadata.limits.requestsPerMinutePerIp
        + ' POST/min/IP. Една активна заявка; последният текст замества изчакващите версии. HTTP времето включва прочетения/parsed body; DOM времето е отделно.';
    }
  };
}
