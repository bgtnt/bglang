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
    targetMode = document.querySelector('#live-continuation-target'),
    raw = document.querySelector('#live-json');
  const currentTarget = () => targetMode?.value ?? 'Auto';
  let response = null,
    selected = -1,
    selectionRevision = 0,
    composing = false;
  let actionBusy = false,
    rawValue = null;
  const labels = [
    'Морфология и лексика',
    'Контекст и синтаксис',
    'Правопис и проверки',
    'Имена и конструкции',
    'Фонетика и ударение',
    'Метрики и пълнота'
  ];
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
    preview.setAttribute('aria-busy', String(state === 'working'));
  }

  function invalidate(text) {
    response = null;
    selected = -1;
    selectionRevision++;
    completion.hidden = true;
    actionHost.hidden = true;
    preview.textContent = text;
    preview.dataset.fresh = 'false';
    document.querySelector('#inspect-title').textContent = 'Текущата дума';
    inspector.replaceChildren(el('p', text.trim() ? 'Очаква новия текстов анализ.' : 'Напиши текст или избери пример.', 'empty-copy'));
    document.querySelector('#live-length').textContent = text.length + ' / 4096';
    for (const id of ['#live-word-count', '#live-reading-count', '#live-timing']) document.querySelector(id).textContent = '—';
    rawValue = null;
    raw.textContent = 'Няма актуален отговор за този текст.';
    blankPanels();
  }
  const controller = createLiveController({
    lane,
    invalidate,
    state: setState,
    readSelection: () => ({start: editor.selectionStart, end: editor.selectionEnd}),
    readContinuationTarget: currentTarget,
    continuationLimit: 12,
    commit(result, revision, snapshot) {
      if (result.error) {
        setState('error');
        return;
      }
      if (!result.http.ok) {
        setState('error', {
          retryAfterMs: result.retryAfterMs
        });
        return;
      }
      if (result.response.data?.text !== editor.value) {
        setState('error');
        return;
      }
      response = result.response;
      renderFiniteContinuation(completion, response.data.continuation, snapshot, (form, binding) => {
        const applied = applyContinuation(editor.value, editor.selectionStart, editor.selectionEnd, controller.revision, binding, form, composing, currentTarget());
        if (!applied) return;
        editor.setRangeText(form, binding.start, binding.end, 'end');
        editor.focus();
        onInput();
      });
      rawValue = response;
      const started = performance.now();
      paint();
      renderPanels();
      select(caretWord(response.data.tokens, editor.selectionStart));
      document.querySelector('#live-timing').textContent = Math.round(result.elapsedMs) + ' ms HTTP · ' + Math.round(performance.now() - started)
        + ' ms DOM';
      document.querySelector('#live-word-count').textContent = 'Думи: ' + response.data.tokens.filter(t => t.kind === 'Word').length;
      const full = response.data.profile?.fullReadingOccurrences;
      document.querySelector('#live-reading-count').textContent = 'Пълни прочити: ' + (full === null || full === undefined ? '—' : full);
      preview.dataset.fresh = 'true';
      if (raw.closest('details').open) raw.textContent = JSON.stringify(rawValue, null, 2);
    }
  });
  raw.closest('details').addEventListener('toggle', event => {
    if (event.target.open && rawValue) raw.textContent = JSON.stringify(rawValue, null, 2);
  });

  function diagnostics() {
    const v = response.data.validation;
    return [
      ...(response.data.diagnostics || []),
      ...(v?.diagnostics || []),
      ...(v?.spacingDiagnostics || []),
      ...(v?.subordinateCommaDiagnostics || []),
      ...(v?.fragmentDiagnostics || [])
    ];
  }

  function classify(index) {
    const data = response.data,
      token = data.tokens[index];
    if (layer.value === 'knowledge') {
      const profile = data.profile?.words.find(w => w.tokenIndex === index);
      if (profile?.observedProjectedAmbiguity === true) return 'known-ambiguous';
      return tokenAnalysis(token, response)?.isKnown ? 'known-known' : 'known-unknown';
    }
    if (layer.value === 'checks') {
      if (!data.validation?.available) return 'check-unavailable';
      return diagnostics().some(d => d.start < token.start + token.length && d.start + d.length > token.start) ? 'check-signal' : 'check-clear';
    }
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
    renderTokenText(preview, response.data.text, response.data.tokens, classify, index => {
      const token = response.data.tokens[index];
      editor.setSelectionRange(token.start, token.start + token.length);
      select(index);
    });
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
      ['check-signal', 'Върнато наблюдение'],
      ['check-clear', 'Няма върнато съобщение'],
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
    if (index !== selected) {
      selectionRevision++;
      actionHost.hidden = true;
    }
    selected = index;
    for (const button of preview.querySelectorAll('[data-token-index]')) button.setAttribute('aria-pressed', String(Number(button.dataset.tokenIndex) === index));
    if (!response || index < 0) {
      document.querySelector('#inspect-title').textContent = 'Между думите';
      inspector.replaceChildren(
        el('p', 'Избери дума или премести курсора в нея. Контекстните кандидати са в слоя за синтаксис.', 'empty-copy')
      );
      return;
    }
    const token = response.data.tokens[index],
      word = tokenAnalysis(token, response),
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
    (rows || []).forEach((row, i) => {
      const view = el('details', undefined, 'reading');
      view.append(el('summary', row.raw.lemma + ' · ' + (posNames[row.features.partOfSpeech] || row.features.partOfSpeech)));
      const chips = el('div', undefined, 'feature-tags');
      for (const [key, value] of Object.entries(row.features)) {
        if (key === 'partOfSpeech' || key === 'rawTag' || value === null || value === undefined) continue;
        chips.append(el('span', (featureNames[key] || key) + ': ' + show(value), 'feature-tag'));
      }
      view.append(chips, el('p', 'Пълен прочит #' + i), el('code', row.raw.partOfSpeech ?? 'Без raw tag'));
      const forms = el('button', 'Словоформи за тази лема');
      forms.type = 'button';
      forms.addEventListener('click', () => openExplorer('generate-forms', row.raw.lemma));
      view.append(forms);
      inspector.append(view);
    });

    const lexical = response.data.lexical?.tokens.find(t => t.start === token.start && t.length === token.length && t.surface === token.text);
    if (lexical) {
      if (!lexical.owners.metadataAvailable) inspector.append(el('p', 'Owner metadata е недостъпно.', 'word-meta'));
      for (const owner of lexical.owners.entries) {
        const view = el('details', undefined, 'reading');
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
      b.addEventListener('click', () => openExplorer(op, token.text));
      actions.append(b);
    }
    const evidence = el('button', 'Пълно evidence');
    evidence.type = 'button';
    evidence.addEventListener('click', () => {
      document.querySelector('.secondary-workspace').open = true;
      showTokenDetails(token, response.data.text, response);
    });
    actions.append(evidence);
    inspector.append(actions);
  }

  function openExplorer(op, text) {
    document.querySelector('.secondary-workspace').open = true;
    document.querySelector('#explorer').open = true;
    search(op, text);
    document.querySelector('#explorer').scrollIntoView({
      block: 'start',
      behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth'
    });
  }

  function renderPanels() {
    panels.replaceChildren();
    const d = response.data,
      v = d.validation,
      context = d.context,
      syntax = d.syntax;
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
      'Наблюдения: ' + diagnostics().length,
      v?.available ? 'Кандидатни диагностики; няма автоматична поправка.' : v?.unavailableReason || 'Проверката е недостъпна.',
      2
    );
    detailList(p, 'Диагностики', diagnostics(), r => r.code + ' · ' + r.start + '+' + r.length + graphSupportLabel([r]));
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
  }

  async function stress(selection) {
    if (actionBusy || !editor.value.trim()) return;
    const rev = controller.revision,
      sel = selectionRevision;
    const current = () => rev === controller.revision && sel === selectionRevision;
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
            button.addEventListener('click', () => stress({
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
      trigger.disabled = false;
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
    if (response?.data.text === editor.value) select(caretWord(response.data.tokens, editor.selectionStart));
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
