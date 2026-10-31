import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {readFile} from 'node:fs/promises';
import {pathToFileURL, fileURLToPath} from 'node:url';

// BgLang Live Lab client harness. No browser engine is simulated: a tiny DOM, a controllable host and a
// manual clock exercise the risks named in the lab assignment. Real-browser layout/IME evidence stays separate.
const root = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT) + path.sep)
  : new URL('../../../', import.meta.url);
const labUrl = new URL('apps/BgLang.Web/wwwroot/demo-lab/', root);
const lab = await import(new URL('demo.js', labUrl).href);
const {collectChecks, warningTokens, possibleName, renderBackdrop, renderPlain, initLab, MESSAGES, GENERIC_MESSAGE,
  featureChips, readingGroups, spellingAlternatives, tokenClass, matchCase, replaceableSpan, responseShapeValid,
  stressedForm, answerShapeValid, packageOf, constructionAdvice, advicePreview} = lab;

const drain = () => new Promise(resolve => setImmediate(resolve));
// Node identity checks with a cheap message: assert.equal on fake DOM nodes would describe the whole object graph.
const sameNode = (actual, expected, message = 'not the expected node') => assert.ok(actual === expected, message);
const otherNode = (actual, unexpected, message = 'unexpectedly the same node') => assert.ok(actual !== unexpected, message);
function deferred() {
  let resolve;
  const promise = new Promise(yes => { resolve = yes; });
  return {promise, resolve};
}

// ---------- tiny DOM ----------
class FakeNode {
  constructor(doc, nodeType, tagName = '', value = '') {
    this.ownerDocument = doc; this.nodeType = nodeType; this.tagName = tagName.toUpperCase();
    this.childNodes = []; this.parentNode = null; this.nodeValue = nodeType === 3 ? value : null;
    this.listeners = new Map(); this.dataset = {}; this.attributes = new Map();
    this.className = ''; this.id = ''; this.hidden = false; this.disabled = false; this.open = false; this.checked = false;
    this.type = ''; this.tabIndex = 0; this.scrollTop = 0; this.scrollLeft = 0;
    this.value = ''; this.selectionStart = 0; this.selectionEnd = 0; this.history = [];
  }
  get firstChild() { return this.childNodes[0] ?? null; }
  get textContent() { return this.nodeType === 3 ? this.nodeValue : this.childNodes.map(child => child.textContent).join(''); }
  set textContent(value) {
    if (this.nodeType === 3) { this.nodeValue = String(value); return; }
    this.ownerDocument.mutation(this);
    this.detachAll();
    if (value !== '') this.adopt(this.ownerDocument.createTextNode(String(value)));
  }
  detachAll() { for (const child of this.childNodes) child.parentNode = null; this.childNodes = []; }
  adopt(node) {
    if (node.nodeType === 11) { for (const child of [...node.childNodes]) this.adopt(child); node.childNodes = []; return; }
    node.parentNode?.childNodes.splice(node.parentNode.childNodes.indexOf(node), 1);
    node.parentNode = this;
    this.childNodes.push(node);
  }
  append(...nodes) { if (this.nodeType !== 11) this.ownerDocument.mutation(this); for (const node of nodes) this.adopt(node); }
  replaceChildren(...nodes) { this.ownerDocument.mutation(this); this.detachAll(); for (const node of nodes) this.adopt(node); }
  setAttribute(name, value) { this.attributes.set(name, String(value)); if (name === 'id') this.id = String(value); }
  getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
  removeAttribute(name) { this.attributes.delete(name); }
  addEventListener(type, listener) { if (!this.listeners.has(type)) this.listeners.set(type, []); this.listeners.get(type).push(listener); }
  dispatch(type, init = {}) {
    const event = {type, target: this, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...init};
    for (const listener of this.listeners.get(type) || []) listener(event);
    return event;
  }
  click() { if (!this.disabled) this.dispatch('click'); }
  focus() { this.ownerDocument.activeElement = this; }
  closest(selector) { for (let node = this; node; node = node.parentNode) if (node.nodeType === 1 && matches(node, selector)) return node; return null; }
  *descendants() { for (const child of this.childNodes) { yield child; if (child.nodeType === 1) yield* child.descendants(); } }
  querySelectorAll(selector) { return [...this.descendants()].filter(node => node.nodeType === 1 && matches(node, selector)); }
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
  setSelectionRange(start, end) { this.selectionStart = start; this.selectionEnd = end; }
  setRangeText(text, start, end, mode) {
    this.value = this.value.slice(0, start) + text + this.value.slice(end);
    const caret = mode === 'end' ? start + text.length : start;
    this.selectionStart = this.selectionEnd = caret;
  }
}
function matches(node, selector) {
  if (selector.startsWith('#')) return node.id === selector.slice(1);
  if (selector.startsWith('.')) return node.className.split(/\s+/).includes(selector.slice(1));
  const data = /^\[data-([a-z-]+)\]$/.exec(selector);
  if (data) return Object.hasOwn(node.dataset, data[1].replace(/-([a-z])/g, (_, c) => c.toUpperCase()));
  return node.tagName === selector.toUpperCase();
}
class FakeDocument {
  constructor() {
    this.mutations = new Map(); this.listeners = new Map(); this.defaultView = null;
    this.documentElement = new FakeNode(this, 1, 'html');
    this.body = new FakeNode(this, 1, 'body');
    this.documentElement.adopt(this.body);
    this.activeElement = this.body;
  }
  mutation(node) { this.mutations.set(node, (this.mutations.get(node) ?? 0) + 1); }
  createElement(tag) { return new FakeNode(this, 1, tag); }
  createTextNode(text) { return new FakeNode(this, 3, '', text); }
  createDocumentFragment() { return new FakeNode(this, 11, '#fragment'); }
  getElementById(id) { return this.documentElement.querySelectorAll('#' + id)[0] ?? null; }
  querySelectorAll(selector) { return this.documentElement.querySelectorAll(selector); }
  addEventListener(type, listener) { if (!this.listeners.has(type)) this.listeners.set(type, []); this.listeners.get(type).push(listener); }
  execCommand(command, ui, value) {
    const target = this.activeElement;
    if (command !== 'insertText' || target?.tagName !== 'TEXTAREA') return false;
    target.history.push(target.value);
    target.value = target.value.slice(0, target.selectionStart) + value + target.value.slice(target.selectionEnd);
    target.selectionStart = target.selectionEnd = target.selectionStart + value.length;
    target.dispatch('input', {inputType: 'insertText'});
    return true;
  }
}

const LAB_IDS = ['lab-backdrop', 'lab-legend', 'lab-state', 'lab-counts', 'lab-length', 'lab-word', 'lab-word-title', 'lab-checks',
  'lab-dev-metrics', 'lab-identity', 'lab-refresh', 'lab-theme', 'lab-stale-word', 'lab-stale-checks',
  'lab-menu', 'lab-continue', 'lab-sentences', 'lab-stale-sentences', 'lab-reading', 'lab-read'];
const BUTTON_IDS = new Set(['lab-refresh', 'lab-theme', 'lab-continue', 'lab-read']);
function labDom(text) {
  const doc = new FakeDocument();
  for (const id of LAB_IDS) { const node = doc.createElement(BUTTON_IDS.has(id) ? 'button' : 'div'); node.id = id; doc.body.adopt(node); }
  const target = doc.createElement('select'); target.id = 'lab-target'; target.value = 'Auto'; doc.body.adopt(target);
  const editor = doc.createElement('textarea'); editor.id = 'lab-editor'; editor.value = text; doc.body.adopt(editor);
  for (const id of ['lab-live', 'lab-heuristics']) { const box = doc.createElement('input'); box.id = id; box.checked = true; doc.body.adopt(box); }
  const raw = doc.createElement('details'); const pre = doc.createElement('pre'); pre.id = 'lab-raw'; raw.adopt(pre); doc.body.adopt(raw);
  for (const layer of ['pos', 'dictionary', 'none']) { const b = doc.createElement('button'); b.dataset.layer = layer; doc.body.adopt(b); }
  const example = doc.createElement('button'); example.dataset.example = 'Пример.'; doc.body.adopt(example);
  return {doc, editor, $: id => doc.getElementById(id)};
}

// ---------- clock and host ----------
function clockFixture() {
  let time = 0, next = 0;
  const timers = new Map();
  return {
    now: () => time,
    wallNow: () => Date.UTC(2026, 9, 26) + time,
    setTimer(fn, delay = 0) { const id = ++next; timers.set(id, {at: time + Math.max(0, delay), fn}); return id; },
    clearTimer(id) { timers.delete(id); },
    async advance(ms) {
      const end = time + ms;
      for (;;) {
        const due = [...timers.entries()].filter(([, t]) => t.at <= end).sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
        if (!due) break;
        timers.delete(due[0]); time = due[1].at; due[1].fn(); await drain();
      }
      time = end; await drain();
    }
  };
}
function hostFixture() {
  const calls = [];
  let active = 0;
  const host = {calls, maxActive: 0,
    fetchImpl(url, init) {
      if (!init) return Promise.resolve({ok: true, status: 200, headers: {get: () => null},
        json: async () => ({apiVersion: '2', versions: {host: 'test'}, package: {packageId: 'test'}, limits: {requestsPerMinutePerIp: 600}})});
      const wait = deferred();
      active++; host.maxActive = Math.max(host.maxActive, active);
      calls.push({url, payload: JSON.parse(init.body), respond(status, body, headers = {}) {
        active--;
        wait.resolve({ok: status >= 200 && status < 300, status, headers: {get: key => headers[key] ?? null}, json: async () => body});
      }});
      return wait.promise;
    }};
  return host;
}

// ---------- response builder ----------
const WORD = /[\p{L}\p{M}]+(?:-[\p{L}\p{M}]+)*/gu;
const NOUN = lemma => ({raw: {form: lemma, lemma, partOfSpeech: 'Съществително:41,Женски'}, features: {partOfSpeech: 'Noun', number: 'Singular', gender: 'Feminine', article: 'Indefinite'}});
const VERB = (form, lemma, person, number) => ({raw: {form, lemma, partOfSpeech: 'Глагол:161'}, features: {partOfSpeech: 'Verb', person, number, tense: 'Present', verbForm: 'Finite'}});
function analysis(text, {lexicon = {}, graph = {}, diagnostics = [], validation = undefined, spelling = [], names = [], lexical = [],
  continuation = undefined, syntax = undefined, pkg = undefined, nominal = undefined} = {}) {
  const tokens = [], wordAnalyses = [], occurrences = [], sets = [];
  for (const match of text.matchAll(WORD)) {
    const index = tokens.length, readings = lexicon[match[0]] ?? [];
    wordAnalyses.push({word: match[0], normalizedWord: match[0].toLowerCase(), readings: readings.map(r => r.raw), isKnown: readings.length > 0, graphCandidates: graph[match[0]] ?? []});
    tokens.push({text: match[0], kind: 'Word', start: match.index, length: match[0].length, analysisRef: index});
    occurrences.push({tokenIndex: index, text: match[0], start: match.index, length: match[0].length, readingSetIndex: sets.length});
    sets.push({lookupText: match[0], canonicalKey: match[0].toLowerCase(), canonicalReadings: readings, supportKey: null, supportReadings: null});
  }
  const terminators = [...text.matchAll(/[.!?]+/g)];
  const sentences = [];
  let start = 0;
  for (const t of terminators) { sentences.push({start, length: t.index + t[0].length - start}); start = t.index + t[0].length; }
  if (start < text.length && text.slice(start).trim()) sentences.push({start, length: text.length - start});
  return {evidenceFormat: 'shared-analyses-v1', wordAnalyses, ...(pkg ? {package: {packageId: 'test', manifestSha256: pkg}} : {}), data: {
    text, tokens, sentences, diagnostics, ...(continuation ? {continuation} : {}), ...(syntax ? {syntax} : {}),
    ...(nominal ? {nominalConstructions: nominal} : {}),
    readingInventory: {occurrences, sets},
    validation: validation ?? {version: 'bounded-validation.v2', available: true, diagnostics: [], spacingDiagnostics: [], subordinateCommaDiagnostics: [], fragmentDiagnostics: [], unavailableChecks: []},
    spelling: {configuredMaxDistance: 2, configuredLimit: 8, words: spelling},
    properNames: {candidates: names.map(tokenIndex => ({tokenIndex, lane: 'Surname', kinds: 'Person'})), groups: [], deferredTokenIndices: []},
    lexical: {metadataAvailable: true, tokens: lexical, phrases: []}
  }};
}
async function boot(text, options = {}) {
  const dom = labDom(text), clock = clockFixture(), host = hostFixture();
  dom.editor.selectionStart = dom.editor.selectionEnd = text.length;
  const labHandle = initLab(dom.doc, {fetchImpl: host.fetchImpl, now: clock.now, wallNow: clock.wallNow,
    setTimer: clock.setTimer, clearTimer: clock.clearTimer, ...options});
  await drain();
  await clock.advance(150);
  return {...dom, clock, host, lab: labHandle};
}
async function respond(env, call, status, body, headers) { call.respond(status, body, headers); await drain(); await env.clock.advance(0); }
async function type(env, value) { env.editor.value = value; env.editor.selectionStart = env.editor.selectionEnd = value.length; env.editor.dispatch('input', {inputType: 'insertText'}); await drain(); }
const spans = env => env.$('lab-backdrop').querySelectorAll('span');
const enabledButtons = host => host.querySelectorAll('button').filter(button => !button.disabled);

// ---------- pure presentation ----------
test('backdrop keeps literal HTML, emoji, NFD accents, repeated and long words and final empty lines as text', () => {
  const doc = new FakeDocument(), target = doc.createElement('div');
  const text = '<b>x</b> 😀 по-лѐсен ѝ ѝ дума дума ' + 'а'.repeat(300) + '\n\n';
  const tokens = [...text.matchAll(WORD)].map(m => ({text: m[0], kind: 'Word', start: m.index, length: m[0].length}));
  renderBackdrop(target, text, tokens, index => index === 0 ? 'c-noun w-warning' : 'c-verb');
  assert.equal(target.textContent, text + ' ');
  const rendered = target.querySelectorAll('span');
  assert.deepEqual(rendered.map(span => span.textContent), tokens.map(token => token.text));
  assert.equal(rendered[0].className, 'c-noun w-warning');
  assert.ok(target.querySelectorAll('b').length === 0, 'markup stays literal text');
  assert.ok(rendered.some(span => span.textContent === 'дума') && rendered.filter(span => span.textContent === 'дума').length === 2);
});

test('backdrop rejects every invalid span before touching the existing DOM', () => {
  const doc = new FakeDocument(), target = doc.createElement('div'), text = 'едно две';
  renderPlain(target, 'предишен');
  const before = doc.mutations.get(target) ?? 0, previous = target.textContent;
  const bad = [
    [{text: 'едно', kind: 'Word', start: 0, length: 4}, {text: 'дно', kind: 'Word', start: 1, length: 3}],
    [{text: 'две', kind: 'Word', start: 5, length: 9}],
    [{text: 'едно', kind: 'Word', start: 0.5, length: 4}],
    [{text: 'едно', kind: 'Word', start: 0, length: -1}],
    [{text: 'друго', kind: 'Word', start: 0, length: 4}]
  ];
  for (const tokens of bad) assert.throws(() => renderBackdrop(target, text, tokens, () => 'c-noun'), /Невалидни позиции/);
  assert.equal(doc.mutations.get(target) ?? 0, before);
  assert.equal(target.textContent, previous);
});

test('returned records become checks; evidence codes and unknown codes never become warnings', () => {
  const text = 'Децата играе в парка';
  const response = analysis(text, {diagnostics: [
    {code: 'productive_morphology_agreement_mismatch', start: 7, length: 5, ruleId: 'x', readings: [{tokenIndex: 0, completeReadingIndex: 0}, {tokenIndex: 1, completeReadingIndex: 0}]},
    {code: 'full_short_article_check_not_ready', start: 15, length: 5, ruleId: 'grammar.full_short_article_morphology_coverage.v1'},
    {code: 'brand_new_future_code', start: 0, length: 6, ruleId: 'future'},
    {code: 'missing_terminal_punctuation', start: 20, length: 0, ruleId: 'terminal_punctuation'}
  ], validation: {available: true, diagnostics: [{code: 'full_short_article_check_not_ready', start: 15, length: 5, basis: 'existing_diagnostic', supports: []}],
    spacingDiagnostics: null, subordinateCommaDiagnostics: null, fragmentDiagnostics: null, unavailableChecks: []}});
  response.data.context = {sentences: [{evidenceCodes: ['subject_predicate_agreement_mismatch_retained_for_diagnostic']}]};
  const checks = collectChecks(response.data);
  const byCode = Object.fromEntries(checks.map(check => [check.code, check]));
  assert.equal(checks.length, 4, 'identical code/span records from two arrays are one check');
  assert.equal(byCode.full_short_article_check_not_ready.records.length, 2, 'both original records are kept');
  assert.equal(byCode.productive_morphology_agreement_mismatch.severity, 'warning');
  assert.deepEqual(byCode.productive_morphology_agreement_mismatch.words, ['Децата', 'играе']);
  assert.equal(byCode.productive_morphology_agreement_mismatch.graph, false, 'productive_* name alone is not graph evidence');
  assert.equal(byCode.full_short_article_check_not_ready.severity, 'unsupported');
  assert.equal(byCode.full_short_article_check_not_ready.message, MESSAGES.full_short_article_check_not_ready);
  assert.equal(byCode.brand_new_future_code.severity, 'information');
  assert.equal(byCode.brand_new_future_code.message, GENERIC_MESSAGE);
  assert.ok(!checks.some(check => check.code.includes('retained_for_diagnostic')));
  const marked = warningTokens(response.data, checks);
  assert.deepEqual([...marked], [1], 'only the warning span is underlined; the zero-length check marks no neighbour');
});

test('graph supports are labelled as a graph hypothesis', () => {
  const response = analysis('Суперработниците работи', {diagnostics: [{code: 'productive_morphology_agreement_mismatch', start: 0, length: 16,
    readings: [{tokenIndex: 1, completeReadingIndex: 0}], graphReadings: [{tokenIndex: 0, graphCandidateIndex: 0, fullTagIndex: 0}]}]});
  const [check] = collectChecks(response.data);
  assert.equal(check.graph, true);
  assert.deepEqual(check.words, ['Суперработниците', 'работи']);
});

test('possible names use proper-name evidence first and a mid-sentence capital only as a signal', () => {
  const response = analysis('Процесът срещу Чорбанова приключи. Чорбанова дойде.', {names: [2]});
  const data = response.data;
  assert.deepEqual(possibleName(data, 2), {evidence: true, signal: true, possible: true});
  assert.equal(possibleName(data, 0).possible, false, 'sentence-initial capital is not a name signal');
  assert.equal(possibleName(data, 4).possible, false, 'sentence-initial repeated surname without evidence');
  assert.equal(possibleName(data, 1).possible, false);
});

test('prepositional article candidate is a warning with one mirrored record and no automatic rewrite', () => {
  const diagnostic = {code:'short_article_expected_after_preposition_candidate',start:9,length:8,
    ruleId:'grammar.short_article.exact_prepositional_group.v1',projectedText:'учителя',
    readings:[{tokenIndex:0,completeReadingIndex:0},{tokenIndex:1,completeReadingIndex:0},{tokenIndex:2,completeReadingIndex:0}]};
  const data = {text:'Говоря с учителят.',diagnostics:[diagnostic],validation:{available:true,diagnostics:[
    {code:diagnostic.code,start:9,length:8,basis:'existing_diagnostic='+diagnostic.ruleId+';bounded_article_advice',supports:diagnostic.readings}]}};
  const check = collectChecks(data).find(row => row.code === diagnostic.code);
  assert.ok(check); assert.equal(check.severity,'warning'); assert.equal(check.message,'В проверената предложна група се очаква кратък член.');
  assert.equal(check.projected,'учителя'); assert.equal(check.records.length,2); assert.equal(check.replaceable,false);
});

test('feature chips are short Bulgarian labels and identical lines are counted', () => {
  assert.deepEqual(featureChips({person: 'Third', number: 'Plural', tense: 'Aorist', verbForm: 'Finite'}), ['3 л.', 'мн.ч.', 'мин. свършено']);
  assert.deepEqual(featureChips({number: 'Singular', article: 'Full', isShortForm: true, futureAxis: 'x'}), ['ед.ч.', 'пълен член', 'кратка форма']);
  assert.deepEqual(featureChips({tense: 'Remote'}), ['Remote'], 'unknown enum values stay visible');
  const groups = readingGroups([VERB('играе', 'играя', 'Third', 'Singular'), VERB('играе', 'играя', 'Third', 'Singular'), NOUN('пила')]);
  assert.equal(groups.length, 2);
  assert.deepEqual(groups[0].lines.map(line => [line.text, line.count]), [['3 л. · ед.ч. · сег. време', 2]]);
});

test('spelling keeps distance first and uses contextual order only for ties', () => {
  const ordered = spellingAlternatives({alternatives: [{form: 'друма', distance: 2}, {form: 'друга', distance: 1}, {form: 'дружа', distance: 1}],
    contextualCandidates: [{alternativeIndex: 2, assessment: 'Supported', matches: []}, {alternativeIndex: 0, assessment: 'NoCompatiblePair', matches: []}]});
  assert.deepEqual(ordered.map(row => row.form), ['дружа', 'друга', 'друма']);
  assert.equal(ordered[0].assessment, 'supported');
});

test('replacements copy a simple title-case or all-caps original and keep every other form raw', () => {
  assert.equal(matchCase('друфа', 'друга'), 'друга');
  assert.equal(matchCase('Друфа', 'друга'), 'Друга');
  assert.equal(matchCase('ДРУФА', 'друга'), 'ДРУГА');
  assert.equal(matchCase('дРуфа', 'друга'), 'друга', 'mixed case stays raw');
  assert.equal(matchCase('софиа', 'София'), 'София', 'a capital from the dictionary is kept');
  assert.equal(matchCase('СОФИА', 'София'), 'София');
  assert.equal(matchCase('Й', 'ѝ'), 'ѝ', 'one letter carries no case pattern');
  assert.equal(matchCase('Дру̀фа', 'друга'), 'Друга', 'a combining accent is not a letter');
  assert.equal(matchCase('ДРУФА', 'дру̀га'), 'ДРУ̀ГА', 'combining marks of the candidate are kept');
  assert.equal(matchCase('Kнига', 'книга'), 'Книга', 'a mixed-script OCR original still gives its case');
  assert.equal(matchCase('KНИГА', 'книга'), 'КНИГА');
  assert.equal(matchCase('Друфа', 'kniga'), 'kniga', 'only a Cyrillic candidate changes case');
  assert.equal(matchCase('4ЕТЕ', 'чете'), 'чете', 'a digit is not a letter');
});

test('a replacement span never splits an emoji or detaches a combining mark', () => {
  const text = 'а😀б дума̀ x';
  assert.equal(replaceableSpan(text, 0, 2), false, 'ends inside the emoji');
  assert.equal(replaceableSpan(text, 2, 2), false, 'starts inside the emoji');
  assert.equal(replaceableSpan(text, 0, 4), true);
  assert.equal(replaceableSpan(text, 5, 4), false, 'a mark follows the span');
  assert.equal(replaceableSpan(text, 5, 5), true);
  assert.equal(replaceableSpan(text, 9, 1), false, 'the span starts with a mark');
  assert.equal(replaceableSpan(text, 0, 0), false);
  assert.equal(replaceableSpan(text, 10, 3), false, 'past the end');
});

test('layer classes come from complete readings, graph candidates and warnings', () => {
  const response = analysis('Тя пила суперпила', {lexicon: {'Тя': [{raw: {lemma: 'тя'}, features: {partOfSpeech: 'PersonalPronoun'}}],
    'пила': [NOUN('пила'), VERB('пила', 'пия', null, 'Singular')]}, graph: {'суперпила': [{word: 'суперпила', base: 'пила', derivedLemma: 'суперпила', fullTags: ['Съществително:41,Женски']}]}});
  const warnings = new Set([1]);
  assert.equal(tokenClass(response, 0, 'pos', warnings), 'c-pron');
  assert.equal(tokenClass(response, 1, 'pos', warnings), 'c-multi w-warning');
  assert.equal(tokenClass(response, 2, 'pos', warnings), 'c-graph');
  assert.equal(tokenClass(response, 1, 'dictionary', warnings), 'c-ambiguous w-warning');
  assert.equal(tokenClass(response, 1, 'none', warnings), 'w-warning');
});

// ---------- lifecycle through the shared lane ----------
test('one response colours the backdrop, then an edit shows the current text plain and stales every action', async () => {
  const text = 'Децата играе.';
  const env = await boot(text);
  assert.equal(env.host.calls.length, 1);
  assert.equal(env.host.calls[0].payload.text, text);
  assert.equal(env.host.calls[0].payload.evidenceFormat, 'shared-analyses-v1');
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon: {'Децата': [NOUN('дете')], 'играе': [VERB('играе', 'играя', 'Third', 'Singular')]},
    diagnostics: [{code: 'productive_morphology_agreement_mismatch', start: 7, length: 5, readings: [{tokenIndex: 0, completeReadingIndex: 0}, {tokenIndex: 1, completeReadingIndex: 0}]}]}));
  assert.equal(spans(env).length, 2);
  assert.match(spans(env)[1].className, /w-warning/);
  assert.equal(env.$('lab-state').dataset.state, 'ready');
  assert.ok(enabledButtons(env.$('lab-checks')).length > 0);
  await type(env, 'Децата играят.');
  assert.equal(spans(env).length, 0, 'no old colours over new text');
  assert.equal(env.$('lab-backdrop').textContent, 'Децата играят. ');
  assert.equal(enabledButtons(env.$('lab-checks')).length, 0);
  assert.equal(env.$('lab-stale-checks').hidden, false);
  assert.equal(env.$('lab-checks').dataset.fresh, 'false');
});

test('layer switches never request and never revive colours of an earlier revision; late and failed responses stay out', async () => {
  const text = 'Котката спи.';
  const lexicon = {'Котката': [NOUN('котка')], 'спи': [VERB('спи', 'спя', 'Third', 'Singular')]};
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon}));
  const before = env.host.calls.length;
  env.lab.setLayer('dictionary');
  env.lab.setLayer('pos');
  assert.equal(env.host.calls.length, before, 'visual layer changes are local');
  await type(env, 'Котката спят.');
  env.lab.setLayer('dictionary');
  assert.equal(spans(env).length, 0);
  await env.clock.advance(150);
  const pending = env.host.calls.at(-1);
  await type(env, 'Котката спи.');
  await respond(env, pending, 200, analysis('Котката спят.', {lexicon}));
  assert.equal(spans(env).length, 0, 'a late success for an earlier revision is dropped');
  await env.clock.advance(600);
  const current = env.host.calls.at(-1);
  assert.equal(current.payload.text, 'Котката спи.');
  await respond(env, current, 429, {error: 'rate'}, {'Retry-After': '2'});
  assert.equal(env.$('lab-state').dataset.state, 'error');
  assert.equal(spans(env).length, 0, 'same text as an old frame still shows no colours after a failure');
  env.$('lab-refresh').click();
  await env.clock.advance(2500);
  const retry = env.host.calls.at(-1);
  await respond(env, retry, 503, {error: 'busy'});
  assert.equal(spans(env).length, 0);
  env.$('lab-refresh').click();
  await env.clock.advance(2500);
  await respond(env, env.host.calls.at(-1), 200, analysis('Котката спи.', {lexicon}));
  assert.equal(spans(env).length, 2);
  assert.match(spans(env)[0].className, /c-known/, 'the layer chosen while stale applies to the fresh frame');
  env.lab.setLayer('pos');
  assert.match(spans(env)[0].className, /c-noun/);
  assert.ok(env.host.maxActive <= 1, 'one active request at a time');
});

test('a transport failure or server error keeps the editor usable and shows no colours', async () => {
  const dom = labDom('Текст.'), clock = clockFixture();
  initLab(dom.doc, {fetchImpl: (url, init) => init ? Promise.reject(new Error('offline')) : Promise.resolve({ok: false}),
    now: clock.now, wallNow: clock.wallNow, setTimer: clock.setTimer, clearTimer: clock.clearTimer});
  await drain();
  await clock.advance(150);
  await drain();
  assert.equal(dom.$('lab-state').dataset.state, 'error');
  assert.equal(dom.$('lab-backdrop').querySelectorAll('span').length, 0);
  assert.equal(dom.editor.value, 'Текст.');
  const env = await boot('Текст.');
  await respond(env, env.host.calls[0], 500, {error: 'boom'});
  assert.equal(env.$('lab-state').dataset.state, 'error');
  assert.equal(spans(env).length, 0);
});

test('caret movement between words updates the word card without a request', async () => {
  const text = 'Тя пила вода.';
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon: {'Тя': [{raw: {lemma: 'тя'}, features: {partOfSpeech: 'PersonalPronoun'}}],
    'пила': [NOUN('пила'), VERB('пила', 'пия', null, 'Singular')], 'вода': [NOUN('вода')]}}));
  const count = env.host.calls.length;
  env.editor.setSelectionRange(4, 4); env.editor.dispatch('click');
  assert.equal(env.$('lab-word-title').textContent, 'пила');
  assert.match(env.$('lab-word').textContent, /2 възможни прочита/);
  assert.match(env.$('lab-word').textContent, /пия · глагол/);
  env.editor.setSelectionRange(9, 9); env.editor.dispatch('keyup', {key: 'ArrowRight'});
  assert.equal(env.$('lab-word-title').textContent, 'вода');
  const mutations = env.doc.mutations.get(env.$('lab-word'));
  env.editor.setSelectionRange(10, 10); env.editor.dispatch('keyup', {key: 'ArrowRight'});
  assert.equal(env.doc.mutations.get(env.$('lab-word')), mutations, 'same occurrence is not rebuilt');
  assert.equal(env.host.calls.length, count);
});

test('IME composition sends nothing until the final text and shows the current text plain', async () => {
  const text = 'Дом.';
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon: {'Дом': [NOUN('дом')]}}));
  const count = env.host.calls.length;
  env.editor.dispatch('compositionstart');
  await type(env, 'Дом к');
  await type(env, 'Дом ка');
  await env.clock.advance(1000);
  assert.equal(env.host.calls.length, count, 'no request during composition');
  assert.equal(spans(env).length, 0);
  assert.equal(env.$('lab-backdrop').textContent, 'Дом ка ');
  env.editor.value = 'Дом кафе.';
  env.editor.dispatch('compositionend');
  await env.clock.advance(1000);
  assert.equal(env.host.calls.length, count + 1);
  assert.equal(env.host.calls.at(-1).payload.text, 'Дом кафе.');
});

test('"друфа" is replaced only explicitly, only on its own span, and undo cannot revive the old action', async () => {
  const text = 'Това е друфа дума. Друфа.';
  const env = await boot(text);
  const response = analysis(text, {lexicon: {'Това': [NOUN('това')], 'е': [VERB('е', 'съм', 'Third', 'Singular')], 'дума': [NOUN('дума')]},
    spelling: [{tokenIndex: 2, text: 'друфа', start: 7, length: 5, search: 'Searched', alternatives: [{form: 'друга', distance: 1}, {form: 'дърва', distance: 2}]}]});
  await respond(env, env.host.calls[0], 200, response);
  const replaceButton = enabledButtons(env.$('lab-checks')).find(button => button.textContent === 'друга');
  assert.ok(replaceButton, 'a replacement is offered for the unknown word');
  assert.equal(env.editor.value, text, 'nothing changes without a click');
  replaceButton.click();
  assert.equal(env.editor.value, 'Това е друга дума. Друфа.', 'only the captured span changed');
  assert.equal(env.editor.history.length, 1, 'native undo history is used');
  replaceButton.click();
  assert.equal(env.editor.value, 'Това е друга дума. Друфа.', 'the old action is inert after the edit');
  env.editor.value = env.editor.history.pop();
  env.editor.dispatch('input', {inputType: 'historyUndo'});
  await drain();
  assert.equal(env.editor.value, text);
  replaceButton.click();
  assert.equal(env.editor.value, text, 'same text after undo is still a new revision');
  assert.equal(spans(env).length, 0);
});

test('spelling and OCR replacements keep the case of the original, and the label is the inserted text', async () => {
  const text = 'Друфа дума. ДРУФА. Kнига.';
  const env = await boot(text);
  const alternatives = [{form: 'друга', distance: 1}];
  await respond(env, env.host.calls[0], 200, analysis(text, {
    spelling: [{tokenIndex: 0, text: 'Друфа', start: 0, length: 5, search: 'Searched', alternatives},
      {tokenIndex: 2, text: 'ДРУФА', start: 12, length: 5, search: 'Searched', alternatives}],
    diagnostics: [{code: 'mixed_script_ocr_known_candidate', start: 19, length: 5, ruleId: 'ocr.mixed_script_exact_projection.v1', projectedText: 'книга'}]}));
  const labels = enabledButtons(env.$('lab-checks')).map(button => button.textContent);
  assert.ok(labels.includes('Друга') && labels.includes('ДРУГА') && labels.includes('Замени с „Книга“'), labels.join(' | '));
  assert.ok(!labels.includes('друга'), 'the raw lowercase form is not offered');
  enabledButtons(env.$('lab-checks')).find(button => button.textContent === 'ДРУГА').click();
  assert.equal(env.editor.value, 'Друфа дума. ДРУГА. Kнига.');
});

test('the OCR projection is inserted with the original case', async () => {
  const text = 'Kнига е тук.';
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {
    diagnostics: [{code: 'mixed_script_ocr_known_candidate', start: 0, length: 5, ruleId: 'ocr.mixed_script_exact_projection.v1', projectedText: 'книга'}]}));
  enabledButtons(env.$('lab-checks')).find(button => button.textContent === 'Замени с „Книга“').click();
  assert.equal(env.editor.value, 'Книга е тук.');
});

test('a word span that would detach a combining mark offers no replacement', async () => {
  const text = 'Това е друфа̀.';
  const env = await boot(text);
  const response = analysis(text, {spelling: [{tokenIndex: 2, text: 'друфа', start: 7, length: 5, search: 'Searched', alternatives: [{form: 'друга', distance: 1}]}]});
  response.data.tokens[2] = {...response.data.tokens[2], text: 'друфа', length: 5};
  await respond(env, env.host.calls[0], 200, response);
  const checks = env.$('lab-checks');
  assert.equal(checks.querySelectorAll('button').filter(button => button.textContent === 'друга').length, 0);
  assert.match(checks.textContent, /замяна не се предлага/);
});

test('a replacement refuses a changed span even when the analysis is otherwise current', async () => {
  const text = 'Това е друфа дума.';
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {spelling: [{tokenIndex: 2, text: 'друфа', start: 7, length: 5, search: 'Searched', alternatives: [{form: 'друга', distance: 1}]}]}));
  env.editor.value = 'Това е друфф дума.';
  assert.equal(env.lab.replace(7, 5, 'друфа', 'друга'), false);
  assert.equal(env.editor.value, 'Това е друфф дума.');
});

test('дума → дом does not keep the old morphological support in the card', async () => {
  const env = await boot('дума');
  await respond(env, env.host.calls[0], 200, analysis('дума', {lexicon: {'дума': [NOUN('дума')]}}));
  assert.match(env.$('lab-word').textContent, /дума · съществително/);
  await type(env, 'дом');
  assert.equal(env.$('lab-stale-word').hidden, false, 'the old card is marked as the previous analysis');
  await env.clock.advance(1000);
  await respond(env, env.host.calls.at(-1), 200, analysis('дом', {lexicon: {'дом': [{raw: {lemma: 'дом'}, features: {partOfSpeech: 'Noun', number: 'Singular', gender: 'Masculine'}}]}}));
  assert.equal(env.$('lab-word-title').textContent, 'дом');
  assert.doesNotMatch(env.$('lab-word').textContent, /дума/);
  assert.equal(env.$('lab-stale-word').hidden, true);
});

test('a possible name gets no main replacement button; alternatives stay in technical details', async () => {
  const text = 'Процесът срещу Чорбанова приключи.';
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {names: [2],
    spelling: [{tokenIndex: 2, text: 'Чорбанова', start: 15, length: 9, search: 'Searched', alternatives: [{form: 'Чобанова', distance: 1}]}]}));
  const checks = env.$('lab-checks');
  assert.match(checks.textContent, /Възможно име/);
  assert.equal(checks.querySelectorAll('button').filter(button => button.textContent === 'Чобанова').length, 0);
  const details = checks.querySelectorAll('details').find(node => node.textContent.includes('Технически детайли'));
  details.open = true; details.dispatch('toggle');
  assert.equal(checks.querySelectorAll('button').filter(button => button.textContent === 'Чобанова').length, 1);
});

test('graph hypotheses and unknown codes are shown as such, without underline', async () => {
  const text = 'Суперработникът работи.';
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon: {'работи': [VERB('работи', 'работя', 'Third', 'Singular')]},
    graph: {'Суперработникът': [{word: 'суперработникът', base: 'работник', derivedLemma: 'суперработник', fullTags: ['Съществително:14a,Мъжки,Пълен']}]},
    diagnostics: [{code: 'future_observation', start: 0, length: 15}]}));
  env.editor.setSelectionRange(3, 3); env.editor.dispatch('click');
  assert.match(env.$('lab-word').textContent, /графова хипотеза/);
  assert.match(env.$('lab-word').textContent, /суперработник ← „работник“/);
  assert.match(spans(env)[0].className, /c-graph/);
  assert.doesNotMatch(spans(env)[0].className, /w-warning/);
  assert.match(env.$('lab-checks').textContent, /Други наблюдения \(1\)/);
});

// ---------- review root-review-1 (CL-01 … CL-06) ----------
const GRAPH = {'Суперработник': [{word: 'суперработник', base: 'работник', derivedLemma: 'суперработник', fullTags: ['Съществително:14a,Мъжки']}]};

test('CL-01: a new analysis of the same text keeps open sections and the keyboard focus', async () => {
  const text = 'Суперработник.';
  const env = await boot(text);
  env.editor.setSelectionRange(3, 3);
  const body = () => analysis(text, {graph: GRAPH, diagnostics: [{code: 'future_observation', start: 0, length: 13}]});
  await respond(env, env.host.calls[0], 200, body());
  const tech = env.$('lab-word').querySelectorAll('details').find(node => node.dataset.key === 'word:Суперработник:tech');
  tech.open = true; tech.dispatch('toggle');
  const information = env.$('lab-checks').querySelectorAll('details').find(node => node.dataset.key === 'checks:information');
  information.open = true;
  const summary = tech.querySelector('summary');
  summary.focus();
  env.$('lab-refresh').click();
  await env.clock.advance(200);
  assert.equal(env.host.calls.length, 2, 'refresh sends the same text again');
  await respond(env, env.host.calls[1], 200, body());
  const rebuilt = env.$('lab-word').querySelectorAll('details').find(node => node.dataset.key === 'word:Суперработник:tech');
  otherNode(rebuilt, tech, 'the card was rebuilt from the new response');
  assert.equal(rebuilt.open, true);
  assert.ok(rebuilt.querySelector('pre'), 'a reopened section is built at once');
  sameNode(env.doc.activeElement, rebuilt.querySelector('summary'), 'focus returns to the same section');
  assert.equal(env.$('lab-checks').querySelectorAll('details').find(node => node.dataset.key === 'checks:information').open, true);
});

test('CL-01: a focused replacement button keeps the focus when the same result arrives again', async () => {
  const text = 'Това е друфа дума.';
  const env = await boot(text);
  const body = () => analysis(text, {spelling: [{tokenIndex: 2, text: 'друфа', start: 7, length: 5, search: 'Completed', alternatives: [{form: 'друга', distance: 1}]}]});
  await respond(env, env.host.calls[0], 200, body());
  enabledButtons(env.$('lab-checks')).find(button => button.textContent === 'друга').focus();
  env.$('lab-refresh').click();
  await env.clock.advance(200);
  await respond(env, env.host.calls[1], 200, body());
  const focused = env.doc.activeElement;
  assert.equal(focused.textContent, 'друга');
  assert.ok(env.$('lab-checks').querySelectorAll('button').includes(focused), 'the focused button is the new one');
  assert.equal(focused.disabled, false);
});

test('CL-02: an incomplete or truncated graph search is stated next to the hypotheses', async () => {
  const text = 'Суперработник глуфа.';
  const env = await boot(text);
  env.editor.setSelectionRange(3, 3);
  const response = analysis(text, {graph: GRAPH});
  Object.assign(response.wordAnalyses[0], {graphSearchIncomplete: true, graphCandidatesTruncated: true});
  Object.assign(response.wordAnalyses[1], {graphSearchIncomplete: true});
  await respond(env, env.host.calls[0], 200, response);
  const card = () => env.$('lab-word').textContent;
  assert.match(card(), /графова хипотеза/);
  assert.match(card(), /непълно търсене в графа/);
  assert.match(card(), /не е завършило: хипотезите са частични/);
  assert.match(card(), /съкратен от API-то/);
  env.editor.setSelectionRange(16, 16); env.editor.dispatch('click');
  assert.match(card(), /липсата на хипотези не доказва/, 'an empty incomplete search proves no absence');
});

test('CL-02: a complete graph search adds no completeness warning', async () => {
  const text = 'Суперработник.';
  const env = await boot(text);
  env.editor.setSelectionRange(3, 3);
  const response = analysis(text, {graph: GRAPH});
  response.wordAnalyses[0].graphExactRootCandidatesComplete = true;
  await respond(env, env.host.calls[0], 200, response);
  assert.match(env.$('lab-word').textContent, /графова хипотеза/);
  assert.doesNotMatch(env.$('lab-word').textContent, /непълно|не е завършило|съкратен|не е потвърдена/);
});

test('CL-03: a spelling row that does not name its own token gets no action', async () => {
  const text = 'друфа дума.';
  const env = await boot(text);
  const row = {tokenIndex: 1, text: 'друфа', start: 0, length: 5, search: 'Completed', alternatives: [{form: 'друга', distance: 1}]};
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon: {'дума': [NOUN('дума')]}, spelling: [row]}));
  const checks = env.$('lab-checks');
  assert.equal(checks.querySelectorAll('button').filter(button => button.textContent === 'друга').length, 0);
  assert.doesNotMatch(checks.textContent, /Непознати думи/);
  assert.equal(env.editor.value, text);
});

test('CL-04: a malformed nested collection applies nothing and is not reported as current', async () => {
  const first = 'друфа друга.';
  const env = await boot(first);
  await respond(env, env.host.calls[0], 200, analysis(first, {lexicon: {'друга': [NOUN('друга')]},
    spelling: [{tokenIndex: 0, text: 'друфа', start: 0, length: 5, search: 'Completed', alternatives: [{form: 'друга', distance: 1}]}]}));
  const checksBefore = env.$('lab-checks').textContent, wordBefore = env.$('lab-word').textContent;
  await type(env, 'Нов текст.');
  await env.clock.advance(1000);
  const broken = analysis('Нов текст.', {lexicon: {'Нов': [NOUN('нов')], 'текст': [NOUN('текст')]}});
  broken.data.spelling.words = {};
  await respond(env, env.host.calls.at(-1), 200, broken);
  assert.equal(env.$('lab-state').dataset.state, 'error');
  assert.match(env.$('lab-state').textContent, /невалиден/);
  assert.equal(env.lab.display.text, first, 'the previous analysis stays the displayed one');
  assert.equal(env.$('lab-backdrop').textContent, 'Нов текст. ');
  assert.equal(spans(env).length, 0, 'the new text is not coloured');
  assert.equal(env.$('lab-checks').textContent, checksBefore);
  assert.equal(env.$('lab-word').textContent, wordBefore);
  assert.equal(env.$('lab-stale-checks').hidden, false);
  assert.equal(enabledButtons(env.$('lab-checks')).length, 0);
});

test('CL-04: response shape check accepts absent collections and rejects malformed ones', () => {
  const valid = () => analysis('Дом.');
  assert.equal(responseShapeValid(valid()), true);
  const withoutValidation = valid(); delete withoutValidation.data.validation;
  assert.equal(responseShapeValid(withoutValidation), true);
  for (const breakIt of [r => { r.data.spelling.words = {}; }, r => { r.data.tokens = null; }, r => { r.wordAnalyses = {}; },
    r => { r.data.validation.diagnostics = 'x'; }, r => { r.data.readingInventory.sets = {}; }, r => { r.data.text = 1; }]) {
    const response = valid();
    breakIt(response);
    assert.equal(responseShapeValid(response), false, breakIt.toString());
  }
});

test('CL-05: the counter counts sent POSTs, not jobs the lane dropped before sending', async () => {
  const env = await boot('А.');
  await type(env, 'Аб.');
  await env.clock.advance(150);
  await type(env, 'Абв.');
  await env.clock.advance(150);
  await respond(env, env.host.calls[0], 200, analysis('А.'));
  await env.clock.advance(200);
  assert.equal(env.host.calls.length, 2, 'the queued job for „Аб.“ was dropped before fetch');
  assert.equal(env.host.calls[1].payload.text, 'Абв.');
  assert.equal(env.lab.metrics.requests, env.host.calls.length);
});

test('CL-06: the repetition message claims no proximity', () => {
  assert.equal(MESSAGES.style_repetition_evidence, 'В текста има повторена речникова форма.');
});

// ---------- review root-review-2 (CL-01a, CL-02a, CL-05a, CL-04a, CL-04b) ----------
const refresh = async env => { env.$('lab-refresh').click(); await env.clock.advance(200); return env.host.calls.at(-1); };
const devText = env => env.$('lab-dev-metrics').textContent;

test('CL-01a: focus stays on the same occurrence when two actions have the same text', async () => {
  const text = 'големия дом и големия двор.';
  const env = await boot(text);
  const article = start => ({code: 'full_short_article_check_not_ready', start, length: 7, ruleId: 'article.v1'});
  const body = () => analysis(text, {diagnostics: [article(0), article(14)]});
  await respond(env, env.host.calls[0], 200, body());
  const chips = () => env.$('lab-checks').querySelectorAll('button').filter(button => button.textContent === 'големия');
  assert.equal(chips().length, 2);
  chips()[1].focus();
  await respond(env, await refresh(env), 200, body());
  sameNode(env.doc.activeElement, chips()[1], 'the second occurrence keeps the focus');
  otherNode(env.doc.activeElement, chips()[0]);
});

test('CL-02a: a truncated list from a complete search is not called an incomplete search', async () => {
  const text = 'Суперработник.';
  const env = await boot(text);
  env.editor.setSelectionRange(3, 3);
  const response = analysis(text, {graph: GRAPH});
  Object.assign(response.wordAnalyses[0], {graphSearchIncomplete: false, graphCandidatesTruncated: true, graphExactRootCandidatesComplete: true});
  await respond(env, env.host.calls[0], 200, response);
  const card = env.$('lab-word').textContent;
  assert.match(card, /съкратен списък хипотези/);
  assert.match(card, /съкратен от API-то/);
  assert.doesNotMatch(card, /непълно търсене|не е завършило|не е потвърдена/);
});

test('CL-05a: the visible counter follows every sent POST, also when the request fails', async () => {
  const text = 'Дом.';
  const env = await boot(text);
  assert.match(devText(env), /Изпратени заявки към API: 1(?!\d)/, 'counted when sent, before any answer');
  await respond(env, env.host.calls[0], 200, analysis(text));
  const failing = await refresh(env);
  assert.match(devText(env), /Изпратени заявки към API: 2(?!\d)/);
  await respond(env, failing, 500, {code: 'internal', message: 'x'});
  assert.equal(env.$('lab-state').dataset.state, 'error');
  assert.match(devText(env), /Изпратени заявки към API: 2(?!\d)/);
  assert.equal(env.host.calls.length, 2);
});

test('CL-04a: a malformed nested collection of a later card is rejected at commit, even with the caret outside words', async () => {
  const text = 'дом вода.';
  const env = await boot(text);
  const response = analysis(text, {lexical: [{start: 0, length: 3, owners: {entries: {}}}]});
  await respond(env, env.host.calls[0], 200, response);
  assert.equal(env.$('lab-state').dataset.state, 'error');
  assert.match(env.$('lab-state').textContent, /невалиден/);
  assert.equal(env.lab.display, null);
  env.editor.setSelectionRange(0, 0);
  assert.doesNotThrow(() => env.editor.dispatch('click'));
  assert.equal(env.$('lab-word-title').textContent, 'Дума');
});

test('CL-04a: a card that cannot be built from an accepted response says so and other cards still work', async () => {
  const text = 'Суперработник дом.';
  const env = await boot(text);
  const response = analysis(text, {lexicon: {'дом': [NOUN('дом')]}});
  response.wordAnalyses[0].graphCandidates = [null];
  await respond(env, env.host.calls[0], 200, response);
  assert.equal(env.$('lab-state').dataset.state, 'ready', 'collections are well formed; one element is not');
  env.editor.setSelectionRange(3, 3);
  assert.doesNotThrow(() => env.editor.dispatch('click'));
  assert.equal(env.$('lab-word-title').textContent, 'Суперработник');
  assert.match(env.$('lab-word').textContent, /не може да се покаже/);
  env.editor.setSelectionRange(15, 15); env.editor.dispatch('click');
  assert.equal(env.$('lab-word-title').textContent, 'дом');
  assert.match(env.$('lab-word').textContent, /дом · съществително/);
});

test('CL-04b: a failed refresh of the same text keeps that analysis current everywhere', async () => {
  const text = 'Това е друфа дума.';
  const env = await boot(text);
  const body = () => analysis(text, {lexicon: {'дума': [NOUN('дума')]},
    spelling: [{tokenIndex: 2, text: 'друфа', start: 7, length: 5, search: 'Completed', alternatives: [{form: 'друга', distance: 1}]}]});
  await respond(env, env.host.calls[0], 200, body());
  const coloured = spans(env).length;
  assert.ok(coloured > 0);
  for (const failure of [async call => respond(env, call, 500, {code: 'internal', message: 'x'}),
    async call => { const broken = body(); broken.data.tokens[0].length = 99; await respond(env, call, 200, broken); }]) {
    await failure(await refresh(env));
    assert.equal(env.$('lab-state').dataset.state, 'error');
    assert.match(env.$('lab-state').textContent, /предишният анализ на същия текст/);
    assert.equal(env.$('lab-backdrop').dataset.fresh, 'true');
    assert.equal(spans(env).length, coloured, 'the colours of the same text stay');
    assert.equal(env.$('lab-stale-checks').hidden, true);
    assert.ok(enabledButtons(env.$('lab-checks')).some(button => button.textContent === 'друга'), 'its actions stay usable');
  }
});

test('invalid response spans leave the previous DOM and report an error', async () => {
  const env = await boot('Дом.');
  const broken = analysis('Дом.', {lexicon: {'Дом': [NOUN('дом')]}});
  broken.data.tokens[0].length = 99;
  const before = env.doc.mutations.get(env.$('lab-backdrop'));
  await respond(env, env.host.calls[0], 200, broken);
  assert.equal(env.$('lab-state').dataset.state, 'error');
  assert.equal(env.$('lab-backdrop').textContent, 'Дом. ');
  assert.equal(spans(env).length, 0);
  assert.equal(env.doc.mutations.get(env.$('lab-backdrop')), before);
});

// ---------- stage 2: word card sections with on-demand requests ----------
const DOM_TEXT = 'Дом и вода.';
const DOM_LEXICON = {'Дом': [NOUN('дом')], 'вода': [NOUN('вода')]};
const PILA_TEXT = 'Тя пила вода.';
const PILA_LEXICON = {'пила': [NOUN('пила'), VERB('пила', 'пия', null, 'Singular')], 'вода': [NOUN('вода')]};
const envelope = (data, coverage = {}) => ({apiVersion: '1',
  limits: {requested: null, effective: 128, hostMaximum: 256, hostLimitApplied: false, scope: 'per-result-collection'},
  coverage: {hostTruncated: false, possibleTruncation: false, complete: null, ...coverage}, data});
const formsOf = lemma => envelope([{form: lemma, lemma, tag: 'Съществително:41,ед.ч.', source: 'test'},
  {form: lemma + 'ът', lemma, tag: 'Съществително:41,ед.ч.,пълен член', source: 'test'}]);
const stressOf = (word, vowel) => envelope({text: word, renderedText: word, ownersAvailable: true, selectionStatus: 'NotRequested',
  tokens: [{tokenIndex: 0, text: word, start: 0, length: word.length, status: 'Supported', observations: [], inferences: [],
    renderedText: word.slice(0, vowel + 1) + '̀' + word.slice(vowel + 1),
    readings: [{completeReadingIndex: 0, reading: {form: word, lemma: word, partOfSpeech: 'Съществително:41'}, reviewedEvidence: [],
      owners: [{entryId: 'e-' + word, lexemeKey: word, realizations: [{origin: 'LegacyStored', status: 'Supported', sourceId: null,
        marks: [{vowelOrdinal: 1, utf16Position: vowel, kind: 'Grave'}]}]}]}]}]});
async function cardOn(text, lexicon, caret, options = {}) {
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon, ...options}));
  env.editor.setSelectionRange(caret, caret); env.editor.dispatch('click');
  return env;
}
const tabOf = (env, name) => env.$('lab-tab-' + name);
const panelText = env => env.$('lab-tabpanel').textContent;
const panelButton = (env, label) => env.$('lab-tabpanel').querySelectorAll('button').find(button => button.textContent === label);
const callsTo = (env, operation) => env.host.calls.filter(call => call.url === '/api/v2/' + operation);
async function choose(env, name) { tabOf(env, name).click(); await env.clock.advance(200); }
async function moveTo(env, caret) { env.editor.setSelectionRange(caret, caret); env.editor.dispatch('click'); await drain(); }

test('stage 2: answers are checked before anything is rendered', () => {
  assert.equal(answerShapeValid('generate-forms', [{form: 'дом', tag: null}]), true);
  assert.equal(answerShapeValid('generate-forms', {forms: []}), false);
  assert.equal(answerShapeValid('generate-forms', [{form: 1}]), false);
  assert.equal(answerShapeValid('stress', {tokens: [{text: 'а', start: 0, readings: [{owners: [{realizations: []}]}]}]}), true);
  assert.equal(answerShapeValid('stress', {tokens: [{text: 'а', start: 0, readings: [{owners: {}}]}]}), false);
  assert.equal(answerShapeValid('decompose-word', {segments: [{surface: 'а'}]}), true);
  assert.equal(answerShapeValid('decompose-word', {segments: null}), false);
  assert.equal(answerShapeValid('derive-words', [{word: 'а'}]), true);
  assert.equal(answerShapeValid('analyze-text', {}), false, 'only the operations the card asks for');
});

test('stage 2: a stress mark goes after its vowel, as in the runtime, and never outside the word', () => {
  assert.equal(stressedForm('вода', 0, {marks: [{utf16Position: 3, kind: 'Grave'}]}), 'вода̀');
  assert.equal(stressedForm('вода', 10, {marks: [{utf16Position: 11, kind: 'Acute'}]}), 'во́да', 'positions are absolute');
  assert.equal(stressedForm('во̀да', 0, {marks: [{utf16Position: 1, kind: 'Grave'}]}), 'во̀да', 'a written mark is not doubled');
  assert.equal(stressedForm('вода', 0, {marks: [{utf16Position: 4}]}), null);
  assert.equal(stressedForm('вода', 0, {marks: []}), null);
});

test('stage 2: local tabs switch without requests and arrow keys only move the focus', async () => {
  const env = await cardOn(DOM_TEXT, DOM_LEXICON, 1, {lexical: [{start: 0, length: 3,
    owners: {entries: [{entryId: 'e1', lexemeKey: 'дом', meaning: ['Сграда за живеене.', 'Семейство.']}], truncated: false}}]});
  const sent = env.host.calls.length;
  assert.equal(tabOf(env, 'readings').getAttribute('aria-selected'), 'true');
  assert.equal(tabOf(env, 'readings').tabIndex, 0);
  assert.equal(tabOf(env, 'forms').tabIndex, -1);
  assert.match(panelText(env), /дом · съществително/);
  assert.equal(tabOf(env, 'meanings').textContent, 'Значения (2)', 'the count is of meanings, not of dictionary entries');
  tabOf(env, 'readings').focus();
  tabOf(env, 'readings').dispatch('keydown', {key: 'ArrowRight'});
  sameNode(env.doc.activeElement, tabOf(env, 'meanings'));
  assert.equal(tabOf(env, 'meanings').getAttribute('aria-selected'), 'false', 'focus alone selects nothing');
  env.doc.activeElement.dispatch('keydown', {key: 'End'});
  sameNode(env.doc.activeElement, tabOf(env, 'formation'));
  env.doc.activeElement.dispatch('keydown', {key: 'ArrowRight'});
  sameNode(env.doc.activeElement, tabOf(env, 'readings'), 'the arrows wrap around');
  tabOf(env, 'meanings').click();
  assert.equal(tabOf(env, 'meanings').getAttribute('aria-selected'), 'true');
  assert.equal(env.$('lab-tabpanel').getAttribute('aria-labelledby'), 'lab-tab-meanings');
  assert.match(panelText(env), /Сграда за живеене/);
  await env.clock.advance(1000);
  assert.equal(env.host.calls.length, sent, 'neither the keys nor the local tabs ask the API');
});

test('stage 2: Форми asks generate-forms by lemma through the shared lane and reuses the answer', async () => {
  const env = await cardOn(DOM_TEXT, DOM_LEXICON, 1);
  await choose(env, 'forms');
  const [call] = callsTo(env, 'generate-forms');
  assert.ok(call, 'activating the tab is the explicit request');
  assert.deepEqual(call.payload, {text: 'дом'}, 'the lemma only: no option the host would reject');
  assert.match(panelText(env), /Зарежда/);
  assert.equal(env.$('lab-tabpanel').getAttribute('aria-busy'), 'true');
  await respond(env, call, 200, formsOf('дом'));
  assert.deepEqual(env.$('lab-tabpanel').querySelectorAll('tr').map(row => row.textContent),
    ['ФормаТаг от API-то', 'дом (тази)Съществително:41,ед.ч.', 'домътСъществително:41,ед.ч.,пълен член']);
  await moveTo(env, 8);
  assert.equal(env.$('lab-word-title').textContent, 'вода');
  assert.equal(tabOf(env, 'forms').getAttribute('aria-selected'), 'true', 'the chosen tab stays');
  assert.ok(panelButton(env, 'Покажи формите на „вода“'), 'another word waits for its own explicit request');
  await moveTo(env, 1);
  assert.match(panelText(env), /домът/);
  await env.clock.advance(1000);
  assert.equal(callsTo(env, 'generate-forms').length, 1, 'moving between words sends nothing; the answer is reused');
  assert.ok(env.host.maxActive <= 1);
  assert.match(devText(env), new RegExp('Изпратени заявки към API: ' + env.host.calls.length + '(?!\\d)'));
});

test('stage 2: an answer for another word, tab or text never reaches the card', async () => {
  const env = await cardOn(DOM_TEXT, DOM_LEXICON, 1);
  await choose(env, 'forms');
  await moveTo(env, 8);
  await respond(env, callsTo(env, 'generate-forms')[0], 200, formsOf('дом'));
  assert.equal(env.$('lab-word-title').textContent, 'вода');
  assert.doesNotMatch(panelText(env), /домът|Зарежда/, 'another word');

  await choose(env, 'stress');
  const stressCall = callsTo(env, 'stress')[0];
  assert.deepEqual(stressCall.payload, {text: 'вода'});
  tabOf(env, 'readings').click();
  await respond(env, stressCall, 200, stressOf('вода', 3));
  assert.equal(tabOf(env, 'readings').getAttribute('aria-selected'), 'true');
  assert.doesNotMatch(panelText(env), /вода̀/, 'another tab');
  tabOf(env, 'stress').click();
  assert.match(panelText(env), /вода̀/, 'a valid late answer is kept for its own input');
  assert.equal(callsTo(env, 'stress').length, 1);

  await choose(env, 'forms');
  const waterCall = callsTo(env, 'generate-forms').at(-1);
  assert.deepEqual(waterCall.payload, {text: 'вода'});
  await type(env, DOM_TEXT + ' ');
  assert.equal(env.$('lab-stale-word').hidden, false);
  assert.doesNotMatch(panelText(env), /Зарежда/, 'an abandoned request is not shown as running');
  assert.equal(tabOf(env, 'forms').disabled, true);
  await respond(env, waterCall, 200, formsOf('вода'));
  assert.doesNotMatch(panelText(env), /водаът/, 'another text revision');
  assert.ok(env.host.maxActive <= 1);
});

test('stage 2: a waiting request that stops being current is dropped by the lane before it is sent', async () => {
  const env = await cardOn(DOM_TEXT, DOM_LEXICON, 1);
  env.$('lab-refresh').click();
  await env.clock.advance(200);
  const live = env.host.calls.at(-1);
  assert.equal(live.url, '/api/v2/analyze-text');
  tabOf(env, 'forms').click();
  await env.clock.advance(200);
  assert.equal(callsTo(env, 'generate-forms').length, 0, 'it waits behind the active request');
  await moveTo(env, 8);
  await respond(env, live, 200, analysis(DOM_TEXT, {lexicon: DOM_LEXICON}));
  await env.clock.advance(1000);
  assert.equal(callsTo(env, 'generate-forms').length, 0, 'dropped, never sent');
  assert.ok(env.host.maxActive <= 1);
});

test('stage 2: a word with several lemmas gets one button per lemma and no automatic request', async () => {
  const env = await cardOn(PILA_TEXT, PILA_LEXICON, 4);
  tabOf(env, 'forms').click();
  await env.clock.advance(1000);
  assert.equal(callsTo(env, 'generate-forms').length, 0);
  assert.deepEqual(env.$('lab-tabpanel').querySelectorAll('button').map(button => button.textContent),
    ['Покажи формите на „пила“', 'Покажи формите на „пия“']);
  const verb = panelButton(env, 'Покажи формите на „пия“');
  verb.focus();
  verb.click();
  sameNode(env.doc.activeElement, env.$('lab-tabpanel'), 'the clicked button became "Зарежда…"; focus moves to the panel');
  await env.clock.advance(200);
  assert.deepEqual(callsTo(env, 'generate-forms')[0].payload, {text: 'пия'});
  await respond(env, callsTo(env, 'generate-forms')[0], 200, formsOf('пия'));
  sameNode(env.doc.activeElement, env.$('lab-tabpanel'));
  assert.match(panelText(env), /пияът/);
  assert.ok(panelButton(env, 'Покажи формите на „пила“'), 'the other lemma still waits for its own request');
});

test('stage 2: failed and invalid answers show an error with a retry and are not reused', async () => {
  const env = await cardOn(DOM_TEXT, DOM_LEXICON, 1);
  await choose(env, 'forms');
  await respond(env, callsTo(env, 'generate-forms')[0], 500, {code: 'internal', message: 'Вътрешна грешка.'});
  assert.match(panelText(env), /Заявката не успя \(HTTP 500\): Вътрешна грешка\./);
  panelButton(env, 'Опитай пак').click();
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'generate-forms')[1], 200, envelope({forms: []}));
  assert.match(panelText(env), /Отговорът е невалиден и не е показан/);
  panelButton(env, 'Опитай пак').click();
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'generate-forms')[2], 429, {code: 'rate', message: 'x'}, {'Retry-After': '7'});
  assert.match(panelText(env), /пауза от 7 s/);
  panelButton(env, 'Опитай пак').click();
  await env.clock.advance(8000);
  await respond(env, callsTo(env, 'generate-forms')[3], 200, formsOf('дом'));
  assert.match(panelText(env), /домът/);
  assert.equal(callsTo(env, 'generate-forms').length, 4);
  assert.ok(env.host.maxActive <= 1);
});

test('stage 2: Ударение shows the word, its status and each source separately, for every reading', async () => {
  const env = await cardOn(PILA_TEXT, PILA_LEXICON, 4);
  await choose(env, 'stress');
  const call = callsTo(env, 'stress')[0];
  assert.deepEqual(call.payload, {text: 'пила'});
  const reading = (lemma, tag, vowel) => ({completeReadingIndex: 0, reading: {form: 'пила', lemma, partOfSpeech: tag}, reviewedEvidence: [],
    owners: [{entryId: 'e-' + lemma, lexemeKey: lemma, realizations: [{origin: 'LegacyStored', status: 'Supported', sourceId: null,
      marks: [{vowelOrdinal: 0, utf16Position: vowel, kind: 'Grave'}]}]}]});
  await respond(env, call, 200, envelope({text: 'пила', renderedText: 'пила', ownersAvailable: true, selectionStatus: 'NotRequested',
    tokens: [{tokenIndex: 0, text: 'пила', start: 0, length: 4, status: 'Ambiguous', observations: [],
      inferences: [{origin: 'SingleVowelInference', status: 'Inferred', sourceId: null, marks: []}], renderedText: 'пила',
      readings: [reading('пила', 'Съществително:41', 1), reading('пия', 'Глагол:161', 3)]}]}));
  const panel = panelText(env);
  assert.match(panel, /Състояние: двусмислено\./);
  assert.match(panel, /Изведенобез точна позиция — една гласна · изведено/);
  assert.match(panel, /пила · Съществително:41пѝла — Legacy речник · потвърдено/);
  assert.match(panel, /пия · Глагол:161пила̀ — Legacy речник · потвърдено/);
  assert.match(panel, /не прогноза за изговора/);
});

test('stage 2: Словообразуване decomposes on activation and derives only on request', async () => {
  const env = await cardOn(DOM_TEXT, DOM_LEXICON, 1);
  await choose(env, 'formation');
  const call = callsTo(env, 'decompose-word')[0];
  assert.deepEqual(call.payload, {text: 'Дом'});
  assert.equal(callsTo(env, 'derive-words').length, 0);
  await respond(env, call, 200, envelope({word: 'Дом', normalizedWord: 'дом', base: 'дом', applicationId: null, ruleId: null,
    confidence: 1, evidenceCodes: [], source: 'test', hasEvidence: true,
    segments: [{surface: 'Дом', canonical: 'дом', kind: 'Root', start: 0, length: 3, evidenceId: null}]}));
  assert.match(panelText(env), /Дом \(корен\)/);
  panelButton(env, 'Покажи производни от „дом“').click();
  await env.clock.advance(200);
  const derive = callsTo(env, 'derive-words')[0];
  assert.deepEqual(derive.payload, {text: 'дом'});
  await respond(env, derive, 200, envelope([{word: 'домашен', base: 'дом', fromState: null, toState: null, segments: [], edgeIds: [],
    ruleIds: [], applicationIds: [], confidence: 1, evidenceCodes: [], source: 'test', derivedLemma: 'домашен', fullTags: ['Прилагателно:76']}],
  {possibleTruncation: true}));
  const panel = panelText(env);
  assert.match(panel, /домашен ← „дом“ · Прилагателно:76/);
  assert.match(panel, /не доказват речниково наличие или правилност/, 'a graph path is no dictionary verdict either way');
  assert.match(panel, /стига лимита от 128 пътя/);
});

test('stage 2: the activated tab keeps the focus while its answer arrives', async () => {
  const env = await cardOn(DOM_TEXT, DOM_LEXICON, 1);
  tabOf(env, 'forms').focus();
  tabOf(env, 'forms').click();
  sameNode(env.doc.activeElement, tabOf(env, 'forms'));
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'generate-forms')[0], 200, formsOf('дом'));
  sameNode(env.doc.activeElement, tabOf(env, 'forms'));
  assert.equal(tabOf(env, 'forms').tabIndex, 0);
  assert.equal(tabOf(env, 'readings').tabIndex, -1);
});

// ---------- review root-review-3 (LAB-STRESS-01, LAB-STRESS-02, LAB-WORDING-01, package identity) ----------
const realization = (origin, status, position, kind = 'Grave') => ({origin, status, sourceId: null, marks: [{vowelOrdinal: 0, utf16Position: position, kind}]});

test('LAB-STRESS-02: a realization shows exactly its own marks, never on top of written stress', () => {
  assert.equal(stressedForm('ѝ', 0, realization('InputObservation', 'Unclassified', 0)), 'ѝ', 'NFC ѝ gets one mark');
  assert.equal(stressedForm('ѝ', 0, realization('InputObservation', 'Unclassified', 0)), 'ѝ', 'the same in NFD');
  assert.equal(stressedForm('ѐ', 0, realization('SingleVowelInference', 'Inferred', 0, 'Unspecified')), 'ѐ');
  assert.equal(stressedForm('Ѝ', 0, realization('InputObservation', 'Unclassified', 0)), 'Ѝ');
  assert.equal(stressedForm('полетѝ', 0, realization('LegacyStored', 'Supported', 1)), 'по̀лети', 'a written mark elsewhere is not merged');
  assert.equal(stressedForm('полетѝ', 0, realization('LegacyStored', 'Supported', 1)), 'по̀лети', 'nor in NFD');
  assert.equal(stressedForm('край', 0, realization('LegacyStored', 'Supported', 2)), 'кра̀й', 'the breve of й stays');
  assert.equal(stressedForm('край', 0, realization('LegacyStored', 'Supported', 2)), 'кра̀й', 'and so does the NFD breve');
  assert.equal(stressedForm('край', 0, realization('LegacyStored', 'Supported', 3)), null, 'й is not a vowel');
  assert.equal(stressedForm('дом', 0, realization('LegacyStored', 'Supported', 0)), null, 'a mark on a consonant has no exact position');
});

test('LAB-STRESS-01: reviewed evidence is its own source; "no evidence" only when there is none', async () => {
  const env = await cardOn('книги', {'книги': [NOUN('книга')]}, 2);
  await choose(env, 'stress');
  const reading = (owners, reviewed) => ({completeReadingIndex: 0, reading: {form: 'книги', lemma: 'книга', partOfSpeech: 'Съществително:41,мн.ч.'},
    owners, reviewedEvidence: reviewed});
  await respond(env, callsTo(env, 'stress')[0], 200, envelope({text: 'книги', renderedText: 'кнѝги', ownersAvailable: false,
    selectionStatus: 'NotRequested', tokens: [{tokenIndex: 0, text: 'книги', start: 0, length: 5, status: 'Supported', observations: [],
      inferences: [], renderedText: 'кнѝги', readings: [
        reading([], [realization('ReviewedExact', 'Supported', 2, 'Primary')]),
        reading([{entryId: 'e1', lexemeKey: 'книга', realizations: [realization('LegacyStored', 'Ambiguous', 4)]}],
          [realization('ReviewedExact', 'Supported', 2, 'Primary')])]}]}));
  const panel = panelText(env);
  assert.doesNotMatch(panel, /няма сведение/);
  assert.match(panel, /книга · Съществително:41,мн\.ч\.кнѝги — прегледано · потвърдено/, 'reviewed-only reading');
  assert.match(panel, /книгѝ — Legacy речник · двусмислено/, 'the owner keeps its own origin and status');
  assert.equal((panel.match(/кнѝги — прегледано · потвърдено/g) || []).length, 2, 'reviewed evidence is listed for each reading that has it');
  assert.doesNotMatch(panel, /книга: кнѝги/, 'reviewed evidence is not attributed to an owner');
});

test('LAB-STRESS-02: a written ѝ is shown once in the card, also as an inference', async () => {
  const env = await cardOn('ѝ', {}, 1);
  await choose(env, 'stress');
  await respond(env, callsTo(env, 'stress')[0], 200, envelope({text: 'ѝ', renderedText: 'ѝ', ownersAvailable: false, selectionStatus: 'NotRequested',
    tokens: [{tokenIndex: 0, text: 'ѝ', start: 0, length: 1, status: 'Inferred', readings: [], renderedText: 'ѝ',
      observations: [realization('InputObservation', 'Unclassified', 0)], inferences: [realization('SingleVowelInference', 'Inferred', 0, 'Unspecified')]}]}));
  const panel = panelText(env);
  assert.match(panel, /Написано в текстаѝ — написано в текста · некласифицирано/);
  assert.match(panel, /Изведеноѝ — една гласна · изведено/);
  assert.doesNotMatch(panel, /ѝ̀|ѝ̀/);
});

test('answers belong to their package: another package clears them and a late answer of the old one is dropped', async () => {
  const env = await boot(DOM_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(DOM_TEXT, {lexicon: DOM_LEXICON, pkg: 'A'}));
  env.editor.setSelectionRange(1, 1); env.editor.dispatch('click');
  await choose(env, 'forms');
  await respond(env, callsTo(env, 'generate-forms')[0], 200, {...formsOf('дом'), package: {packageId: 'test', manifestSha256: 'A'}});
  assert.match(panelText(env), /домът/);
  await respond(env, await refresh(env), 200, analysis(DOM_TEXT, {lexicon: DOM_LEXICON, pkg: 'B'}));
  assert.doesNotMatch(panelText(env), /домът/, 'answers of package A are not shown for package B');
  assert.ok(panelButton(env, 'Покажи формите на „дом“'));
  panelButton(env, 'Покажи формите на „дом“').click();
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'generate-forms')[1], 200, {...formsOf('дом'), package: {packageId: 'test', manifestSha256: 'A'}});
  assert.doesNotMatch(panelText(env), /домът/, 'an answer of the old package is not kept');
  assert.equal(packageOf({package: {packageId: 'x', manifestSha256: 'h'}}), 'h');
  assert.equal(packageOf({}), null);
});

// ---------- stage 3: continuation menu ----------
const wordPart = /[\p{L}\p{M}-]/u;
function continuationFor(text, caret, forms, {available = true, target = 'Finite', truncated = false} = {}) {
  let start = caret, end = caret;
  while (start > 0 && wordPart.test(text[start - 1])) start--;
  while (end < text.length && wordPart.test(text[end])) end++;
  const noun = target === 'Noun';
  return {version: noun ? 'nominal_continuation.v1' : 'finite_continuation.v1', available,
    unavailableReason: available ? null : 'finite_subject_topology_unsupported',
    candidateSource: noun ? 'exact_package_nominal_vocabulary' : 'exact_package_finite_vocabulary', ranking: 'dictionary_score',
    caret, start, length: end - start, prefix: text.slice(start, caret),
    expectations: noun ? [] : [{person: 'Third', number: 'Plural', supports: []}],
    ...(noun ? {target: 'Noun', nominalExpectations: [{number: 'Singular', gender: 'Feminine', firstAttributeArticle: 'Definite',
      nounArticle: 'Indefinite', attributes: []}]} : {}),
    suggestions: forms.map(form => ({form, score: 1, readings: [{form, lemma: form, partOfSpeech: 'Глагол:1'}],
      matches: [{readingIndex: 0, expectationIndices: [0]}]})),
    truncated, searchComplete: true, examinedReadings: 10};
}
const menuOptions = env => env.$('lab-menu').querySelectorAll('div').filter(node => node.getAttribute('role') === 'option');
const key = (env, init) => env.editor.dispatch('keydown', {shiftKey: false, ctrlKey: false, altKey: false, metaKey: false, ...init});
async function menuOn(text, forms = ['играе', 'играят', 'играем'], options = {}) {
  const env = await boot('Децата');
  await respond(env, env.host.calls[0], 200, analysis('Децата'));
  env.editor.focus();
  await type(env, text);
  await env.clock.advance(1000);
  const call = env.host.calls.at(-1);
  await respond(env, call, 200, analysis(text, {continuation: continuationFor(text, text.length, forms, options)}));
  return {env, call};
}

test('stage 3: an edit asks for continuations at its caret, and moving the caret sends nothing', async () => {
  const {env, call} = await menuOn('Децата игр');
  assert.deepEqual(call.payload.context.continuation, {caret: 10, limit: 12, target: 'Auto'});
  assert.equal(env.$('lab-menu').hidden, false, 'fresh, available, non-empty and a typed prefix');
  assert.deepEqual(menuOptions(env).map(node => node.textContent), ['играе', 'играят', 'играем'], 'dictionary_score order, not re-sorted');
  assert.ok(menuOptions(env).every(node => node.getAttribute('aria-selected') === 'false'), 'nothing is activated by itself');
  assert.equal(env.editor.getAttribute('aria-activedescendant'), null);
  const sent = env.host.calls.length;
  await moveTo(env, 3);
  assert.equal(env.$('lab-menu').hidden, true, 'the forms are for another caret');
  await moveTo(env, 10);
  assert.equal(env.$('lab-menu').hidden, false);
  await env.clock.advance(1000);
  assert.equal(env.host.calls.length, sent, 'caret moves never ask the API');
});

test('stage 3: without a typed prefix the menu stays closed until it is asked for', async () => {
  const {env} = await menuOn('Децата ', ['играят']);
  assert.equal(env.$('lab-menu').hidden, true);
  env.$('lab-continue').click();
  assert.equal(env.$('lab-menu').hidden, false, 'the explicit request shows forms for the same caret without a new request');
  assert.deepEqual(menuOptions(env).map(node => node.textContent), ['играят']);
  const escape = key(env, {key: 'Escape'});
  assert.ok(escape.defaultPrevented);
  assert.equal(env.$('lab-menu').hidden, true);
  const sent = env.host.calls.length;
  await moveTo(env, 3);
  const ctrlSpace = key(env, {key: ' ', code: 'Space', ctrlKey: true});
  assert.ok(ctrlSpace.defaultPrevented);
  await env.clock.advance(1000);
  assert.equal(env.host.calls.length, sent + 1, 'another caret needs one new analysis');
  assert.deepEqual(env.host.calls.at(-1).payload.context.continuation, {caret: 3, limit: 12, target: 'Auto'});
  assert.ok(spans(env).length > 0 && env.$('lab-backdrop').dataset.fresh === 'true', 'the colours of the same text stay meanwhile');
  await respond(env, env.host.calls.at(-1), 200, analysis('Децата ', {continuation: continuationFor('Децата ', 3, ['Децата'])}));
  assert.equal(env.$('lab-menu').hidden, false);
  assert.match(env.$('lab-menu').textContent, /Няма съвместими форми/, 'the only form equals the word: nothing to offer');
});

test('stage 3: Enter and Tab accept only an option activated with the arrows; otherwise they keep their meaning', async () => {
  const {env} = await menuOn('Децата игр');
  for (const name of ['Enter', 'Tab', 'ArrowUp']) assert.equal(key(env, {key: name}).defaultPrevented, false, name + ' without an active option');
  assert.equal(env.editor.value, 'Децата игр');
  assert.ok(key(env, {key: 'ArrowDown'}).defaultPrevented);
  assert.equal(menuOptions(env)[0].getAttribute('aria-selected'), 'true');
  assert.equal(env.editor.getAttribute('aria-activedescendant'), 'lab-option-0');
  key(env, {key: 'ArrowDown'}); key(env, {key: 'ArrowDown'}); key(env, {key: 'ArrowDown'});
  assert.equal(menuOptions(env)[0].getAttribute('aria-selected'), 'true', 'Arrow Down wraps');
  key(env, {key: 'ArrowUp'});
  assert.equal(env.editor.getAttribute('aria-activedescendant'), null, 'Arrow Up from the first option leaves the menu');
  assert.equal(key(env, {key: 'Enter'}).defaultPrevented, false);
  key(env, {key: 'ArrowDown'}); key(env, {key: 'ArrowDown'});
  assert.equal(key(env, {key: 'Enter', shiftKey: true}).defaultPrevented, false, 'Shift+Enter stays a line break');
  const enter = key(env, {key: 'Enter'});
  assert.ok(enter.defaultPrevented);
  assert.equal(env.editor.value, 'Децата играят', 'only the original span „игр“ is replaced');
  assert.equal(env.editor.selectionStart, 13);
  assert.equal(env.editor.history.length, 1, 'through the undoable editor path (the fixture typed by value)');
  assert.equal(env.$('lab-menu').hidden, true);
});

test('stage 3: Tab accepts an activated option and a click accepts without leaving the editor', async () => {
  const first = await menuOn('Децата игр');
  key(first.env, {key: 'ArrowDown'});
  assert.ok(key(first.env, {key: 'Tab'}).defaultPrevented);
  assert.equal(first.env.editor.value, 'Децата играе');
  const second = await menuOn('Децата игр');
  const option = menuOptions(second.env)[2];
  assert.ok(option.dispatch('pointerdown').defaultPrevented, 'pointer down keeps focus and caret in the editor');
  option.dispatch('click');
  assert.equal(second.env.editor.value, 'Децата играем');
});

test('stage 3: keys of an IME composition are never captured, and composition closes the menu', async () => {
  const {env} = await menuOn('Децата игр');
  key(env, {key: 'ArrowDown'});
  assert.equal(key(env, {key: 'Enter', isComposing: true}).defaultPrevented, false);
  assert.equal(key(env, {key: 'Enter', keyCode: 229}).defaultPrevented, false);
  assert.equal(key(env, {key: 'ArrowDown', isComposing: true}).defaultPrevented, false);
  assert.equal(env.editor.value, 'Децата игр');
  env.editor.dispatch('compositionstart');
  assert.equal(env.$('lab-menu').hidden, true);
});

test('stage 3: a stale analysis or a mismatched continuation never offers or applies a form', async () => {
  const {env} = await menuOn('Децата игр');
  const option = menuOptions(env)[1];
  await moveTo(env, 3);
  option.dispatch('click');
  assert.equal(env.editor.value, 'Децата игр', 'the shared apply check refuses a caret that moved, even for a current analysis');
  await moveTo(env, 10);
  await type(env, 'Децата игра');
  assert.equal(env.$('lab-menu').hidden, true, 'an edit closes the menu at once');
  option.dispatch('click');
  assert.equal(env.editor.value, 'Децата игра', 'an option of the previous analysis does nothing');
  await env.clock.advance(1000);
  const wrong = continuationFor('Децата игра', 11, ['играят']);
  wrong.caret = 4;
  await respond(env, env.host.calls.at(-1), 200, analysis('Децата игра', {continuation: wrong}));
  assert.equal(env.$('lab-menu').hidden, true, 'the shared binding rejects a continuation for another caret');
  env.$('lab-continue').click();
  assert.match(env.$('lab-menu').textContent, /Няма съвместими форми/);
  assert.equal(menuOptions(env).length, 0);
});

test('stage 3: an unavailable continuation is silent unless asked for, and the target choice asks again', async () => {
  const {env} = await menuOn('Децата игр', [], {available: false});
  assert.equal(env.$('lab-menu').hidden, true);
  env.$('lab-continue').click();
  assert.match(env.$('lab-menu').textContent, /Няма поддържан контекст за продължение на това място/);
  key(env, {key: 'Escape'});
  env.$('lab-target').value = 'Noun';
  env.$('lab-target').dispatch('change');
  await env.clock.advance(1000);
  assert.deepEqual(env.host.calls.at(-1).payload.context.continuation, {caret: 10, limit: 12, target: 'Noun'});
  await respond(env, env.host.calls.at(-1), 200, analysis('Децата игр', {continuation: continuationFor('Децата игр', 10, ['играчка'], {target: 'Noun'})}));
  assert.equal(env.$('lab-menu').hidden, false);
  assert.match(env.$('lab-menu').textContent, /Съществителни, съвместими с определенията пред курсора/);
  key(env, {key: 'ArrowDown'});
  key(env, {key: 'Enter'});
  assert.equal(env.editor.value, 'Децата играчка');
});

test('LAB-MENU-REVISION-01: pausing Live or changing the target closes the menu; Enter is never swallowed', async () => {
  const {env} = await menuOn('Децата игр');
  assert.equal(env.$('lab-menu').hidden, false);
  env.$('lab-live').checked = false;
  env.$('lab-live').dispatch('change');
  assert.equal(env.$('lab-menu').hidden, true, 'the binding belongs to the previous revision');
  for (const name of ['ArrowDown', 'Enter']) assert.equal(key(env, {key: name}).defaultPrevented, false, name);
  assert.equal(env.editor.value, 'Децата игр');
  env.$('lab-live').checked = true;
  env.$('lab-live').dispatch('change');
  await env.clock.advance(1000);
  await respond(env, env.host.calls.at(-1), 200, analysis('Децата игр', {continuation: continuationFor('Децата игр', 10, ['играят'])}));
  assert.equal(env.$('lab-menu').hidden, false, 'a current binding opens again');
  env.$('lab-target').value = 'Finite';
  env.$('lab-target').dispatch('change');
  assert.equal(env.$('lab-menu').hidden, true, 'another target');
  await env.clock.advance(1000);
  await respond(env, env.host.calls.at(-1), 200, analysis('Децата игр', {continuation: continuationFor('Децата игр', 10, ['играят'])}));
  key(env, {key: 'ArrowDown'});
  assert.ok(key(env, {key: 'Enter'}).defaultPrevented, 'the accepted control takes Enter');
  assert.equal(env.editor.value, 'Децата играят');
});

test('LAB-MENU-REVISION-01: a form that cannot be inserted lets Enter keep its meaning', async () => {
  const {env} = await menuOn('Децата игр');
  key(env, {key: 'ArrowDown'});
  env.lab.controller.edit(env.editor.value, true, 'selection');
  assert.equal(env.$('lab-menu').hidden, true, 'a new revision of the same text closes the menu at once');
  assert.equal(key(env, {key: 'Enter'}).defaultPrevented, false);
  assert.equal(env.editor.value, 'Децата игр');
  const second = await menuOn('Децата игр');
  key(second.env, {key: 'ArrowDown'});
  second.env.editor.value = 'Децата игр.';
  assert.equal(second.env.$('lab-menu').hidden, false, 'a change without an input event leaves the menu on screen');
  assert.equal(key(second.env, {key: 'Enter'}).defaultPrevented, false, 'the refused insertion does not take the key');
  assert.equal(second.env.editor.value, 'Децата игр.');
  assert.equal(second.env.$('lab-menu').hidden, true);
});

test('LAB-MENU-RETRY-01: the explicit request sends a new analysis after a failure and while Live is paused', async () => {
  const {env} = await menuOn('Децата игр');
  await type(env, 'Децата игра');
  await env.clock.advance(1000);
  await respond(env, env.host.calls.at(-1), 500, {code: 'internal', message: 'x'});
  const sent = env.host.calls.length;
  env.$('lab-continue').click();
  await env.clock.advance(1000);
  assert.equal(env.host.calls.length, sent + 1, 'the failed analysis is asked again');
  assert.deepEqual(env.host.calls.at(-1).payload.context.continuation, {caret: 11, limit: 12, target: 'Auto'});
  await respond(env, env.host.calls.at(-1), 200, analysis('Децата игра', {continuation: continuationFor('Децата игра', 11, ['играят'])}));
  assert.equal(env.$('lab-menu').hidden, false);
  env.$('lab-live').checked = false;
  env.$('lab-live').dispatch('change');
  const paused = env.host.calls.length;
  env.$('lab-continue').click();
  await env.clock.advance(1000);
  assert.equal(env.host.calls.length, paused + 1, 'an explicit request is sent while Live is paused');
  await respond(env, env.host.calls.at(-1), 200, analysis('Децата игра', {continuation: continuationFor('Децата игра', 11, ['играят'])}));
  assert.equal(env.$('lab-menu').hidden, false);
  key(env, {key: 'ArrowDown'});
  key(env, {key: 'Enter'});
  assert.equal(env.editor.value, 'Децата играят');
});

// ---------- stage 3: possible syntactic roles ----------
test('LAB-SYNTAX-WORDING-01: repeated roles get a neutral note; nothing is called competing', async () => {
  const text = 'Казах ти да се погрижиш за новата малка къща.';
  const syntax = {version: 'bounded-syntax-roles.v2', available: true, additionalGroups: null, sentences: [
    {start: 0, length: text.length, status: 'Calculated', route: 'Selected', predicateTokenIndex: 0, candidates: [
      role('Predicate', 0, 5), role('Predicate', 15, 8), role('Attribute', 27, 5), role('Attribute', 33, 5)]}]};
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {syntax}));
  const box = env.$('lab-sentences').textContent;
  assert.equal((box.match(/Сказуемо: /g) || []).length, 2, 'main and subordinate predicate both stay');
  assert.equal((box.match(/Определение: /g) || []).length, 2);
  assert.match(box, /Някои роли се срещат повече от веднъж, например главно и подчинено сказуемо или няколко определения\./);
  assert.doesNotMatch(box, /конкурент/);
});

const role = (name, start, length, targets = [{tokenIndex: 0, basis: 'TypedFeatures', readings: []}]) =>
  ({role: name, ruleId: 'Rule' + name + start, start, length, targets, context: []});

test('stage 3: possible roles keep competing candidates apart and use careful names', async () => {
  const text = 'Децата са учители в парка. Тук.';
  const syntax = {version: 'bounded-syntax-roles.v2', available: true, additionalGroups: null, sentences: [
    {start: 0, length: 26, status: 'Calculated', route: 'Selected', predicateTokenIndex: 1, candidates: [
      role('Subject', 0, 6), role('Subject', 10, 7),
      role('Predicate', 7, 2, [{tokenIndex: 1, basis: 'LicensedGraphMorphology', readings: [], graphReadings: [{tokenIndex: 1}]}]),
      role('Complement', 10, 7), role('Object', 10, 7), role('Adverbial', 18, 7)]},
    {start: 27, length: 4, status: 'Calculated', route: 'Abstained', predicateTokenIndex: null, candidates: []}]};
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {syntax}));
  const box = env.$('lab-sentences').textContent;
  assert.match(box, /частичен анализ, не пълен разбор/);
  assert.equal((box.match(/Подлог: /g) || []).length, 2, 'both subject candidates stay');
  assert.match(box, /Някои роли се срещат повече от веднъж.*Всеки ред е отделен кандидат, не окончателен разбор\./);
  assert.doesNotMatch(box, /конкурент/, 'the contract marks no competition');
  assert.match(box, /Сказуемо: „са“ графова хипотеза/);
  assert.match(box, /Именна част на сказуемото: „учители“/);
  assert.match(box, /Допълнение: „учители“/);
  assert.match(box, /Обстоятелствено пояснение: „в парка“/);
  assert.doesNotMatch(box, /[Пп]ряко допълнение|място/);
  assert.match(box, /„Тук\.“Не е избран основен синтактичен анализ\.Няма върнати роли/);
  const jump = env.$('lab-sentences').querySelectorAll('button').find(node => node.textContent === '„в парка“');
  jump.click();
  assert.deepEqual([env.editor.selectionStart, env.editor.selectionEnd], [18, 25], 'a role selects its words in the editor');
  await type(env, text + ' ');
  assert.equal(env.$('lab-stale-sentences').hidden, false);
  assert.ok(env.$('lab-sentences').querySelectorAll('button').every(node => node.disabled), 'stale roles are inactive');
});

test('stage 3: unavailable or missing syntax says so, and malformed candidates reject the response', async () => {
  const env = await boot('Дом.');
  await respond(env, env.host.calls[0], 200, analysis('Дом.', {syntax: {version: 'v2', available: false, unavailableReason: 'budget', sentences: []}}));
  assert.match(env.$('lab-sentences').textContent, /Синтактичните роли не са налични/);
  const broken = analysis('Дом.', {syntax: {version: 'v2', available: true, sentences: [{start: 0, length: 4, candidates: {}}]}});
  assert.equal(responseShapeValid(broken), false);
  assert.equal(responseShapeValid(analysis('Дом.')), true, 'no syntax at all is allowed');
});

// ---------- stage 3: read-only stress reading of the whole text ----------
const READ_TEXT = 'Тя пила вода.';
function readingOf(text, {selectionStatus = 'NotRequested', rendered = 'Тя пила вода̀.', pila = {}} = {}) {
  const owner = (entryId, lemma, vowel) => ({entryId, lexemeKey: lemma, realizations: [realization('LegacyStored', 'Supported', vowel)]});
  return envelope({text, renderedText: rendered, ownersAvailable: true, selectionStatus, tokens: [
    {tokenIndex: 0, text: 'Тя', start: 0, length: 2, status: 'Inferred', observations: [], inferences: [], readings: [], renderedText: 'Тя'},
    {tokenIndex: 1, text: 'пила', start: 3, length: 4, status: 'Ambiguous', observations: [], inferences: [], renderedText: 'пила', ...pila,
      readings: [{completeReadingIndex: 0, reading: {form: 'пила', lemma: 'пила', partOfSpeech: 'Съществително:41'}, owners: [owner('n1', 'пила', 4)], reviewedEvidence: []},
        {completeReadingIndex: 1, reading: {form: 'пила', lemma: 'пия', partOfSpeech: 'Глагол:161'}, owners: [owner('v1', 'пия', 6)], reviewedEvidence: []}]},
    {tokenIndex: 2, text: 'вода', start: 8, length: 4, status: 'Supported', observations: [], inferences: [], readings: [], renderedText: 'вода̀'}]});
}
const chooseButtons = env => env.$('lab-reading').querySelectorAll('button').filter(node => node.textContent.startsWith('Покажи текста с този прочит'));

test('stage 3: the stress reading is a separate read-only text, asked for explicitly through the shared lane', async () => {
  const env = await boot(READ_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(READ_TEXT));
  assert.equal(callsTo(env, 'stress').length, 0, 'nothing is read without a request');
  env.$('lab-read').click();
  await env.clock.advance(200);
  const [call] = callsTo(env, 'stress');
  assert.deepEqual(call.payload, {text: READ_TEXT});
  assert.match(env.$('lab-reading').textContent, /Зарежда/);
  await respond(env, call, 200, readingOf(READ_TEXT));
  const box = env.$('lab-reading');
  assert.equal(box.querySelectorAll('p').find(node => node.className === 'stress-text').textContent, 'Тя пила вода̀.');
  assert.match(box.textContent, /Думи по състояние: потвърдено 1 · изведено 1 · двусмислено 1\./);
  assert.match(box.textContent, /Думи за преглед \(1\)„пила“ — двусмислено/);
  assert.match(box.textContent, /пѝла — Legacy речник · потвърдено/);
  assert.equal(env.editor.value, READ_TEXT, 'the editor is never changed');
  assert.equal(env.$('lab-backdrop').textContent, READ_TEXT + ' ', 'and the reading never reaches the backdrop');
  assert.equal(chooseButtons(env).length, 2);
});

test('stage 3: an owner choice asks for that reading of the same text and says what it shows', async () => {
  const env = await boot(READ_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(READ_TEXT));
  env.$('lab-read').click();
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'stress')[0], 200, readingOf(READ_TEXT));
  chooseButtons(env)[1].click();
  await env.clock.advance(200);
  const call = callsTo(env, 'stress')[1];
  assert.deepEqual(call.payload, {text: READ_TEXT, stressSelection: {tokenIndex: 1, entryId: 'v1', form: 'пила', fullTag: 'Глагол:161'}});
  await respond(env, call, 200, readingOf(READ_TEXT, {selectionStatus: 'Matched', rendered: 'Тя пила̀ вода̀.',
    pila: {status: 'Supported', renderedText: 'пила̀'}}));
  assert.match(env.$('lab-reading').textContent, /Тя пила̀ вода̀\./);
  assert.match(env.$('lab-reading').textContent,
    /Показан е избраният прочит за „пила“: пила̀\. Изборът е за тази дума и не доказва единствен нормативен прочит\./);
});

test('LAB-READING-SELECTION-01: a matched choice is reported by what the word really shows', async () => {
  const env = await boot(READ_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(READ_TEXT));
  env.$('lab-read').click();
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'stress')[0], 200, readingOf(READ_TEXT));
  const answerChoice = async (index, options) => {
    chooseButtons(env)[index].click();
    await env.clock.advance(200);
    await respond(env, callsTo(env, 'stress').at(-1), 200, readingOf(READ_TEXT, options));
    return env.$('lab-reading').textContent;
  };
  const conflict = await answerChoice(1, {selectionStatus: 'Matched', pila: {status: 'Conflict', renderedText: 'пила'}});
  assert.match(conflict, /Изборът съвпадна с прочит на „пила“, но има конфликт в сведенията за ударението; думата е показана, както е написана\./);
  assert.doesNotMatch(conflict, /Показан е избраният прочит/, 'Matched alone is no shown stress');
  const unknown = await answerChoice(0, {selectionStatus: 'NotConnected', pila: {status: 'Unknown', renderedText: 'пила'}});
  assert.match(unknown, /Изборът не съвпадна с прочит на „пила“; не е показано избрано ударение \(неизвестно\)\./);
  assert.doesNotMatch(unknown, /общият прочит/, 'no general fallback is promised');
  const supported = await answerChoice(1, {selectionStatus: 'Matched', rendered: 'Тя пила̀ вода̀.', pila: {status: 'Supported', renderedText: 'пила̀'}});
  assert.match(supported, /Показан е избраният прочит за „пила“: пила̀\./);
});

test('LAB-READING-CONFLICT-WORDING-02: source disagreement and a written accent conflict preserve the original text', async () => {
  for (const written of [false, true]) {
    const text = written ? 'Тя пѝла вода.' : READ_TEXT;
    const word = written ? 'пѝла' : 'пила';
    const answer = readingOf(text, {selectionStatus: 'Matched', rendered: text,
      pila: {text: word, length: written ? 5 : 4, status: 'Conflict', renderedText: word}});
    const token = answer.data.tokens[1];
    const mark = (origin, ordinal, position) => ({origin, status: origin === 'InputObservation' ? 'Unclassified' : 'Supported',
      sourceId: null, marks: [{vowelOrdinal: ordinal, utf16Position: position, kind: 'Grave'}]});
    token.observations = written ? [mark('InputObservation', 0, 4)] : [];
    token.readings[1].owners[0].realizations = written
      ? [mark('LegacyStored', 1, 7)]
      : [mark('LegacyStored', 0, 4), mark('Editorial', 1, 6)];
    answer.data.tokens[2].start = written ? 9 : 8;
    answer.data.tokens[2].renderedText = 'вода';
    assert.equal(text.substring(token.start, token.start + token.length), word);
    assert.equal(text.substring(answer.data.tokens[2].start, answer.data.tokens[2].start + 4), 'вода');
    assert.equal(token.observations.length, written ? 1 : 0);
    const env = await boot(text);
    await respond(env, env.host.calls[0], 200, analysis(text));
    env.$('lab-read').click();
    await env.clock.advance(200);
    const initial = structuredClone(answer);
    initial.data.selectionStatus = 'NotRequested';
    await respond(env, callsTo(env, 'stress')[0], 200, initial);
    chooseButtons(env)[1].click();
    await env.clock.advance(200);
    await respond(env, callsTo(env, 'stress').at(-1), 200, answer);
    const panel = env.$('lab-reading');
    assert.ok(panel.textContent.includes('Изборът съвпадна с прочит на „' + word +
      '“, но има конфликт в сведенията за ударението; думата е показана, както е написана.'));
    assert.doesNotMatch(panel.textContent, /Показан е избраният прочит|противоречи на написаното/);
    assert.equal(panel.querySelectorAll('p').find(node => node.className === 'stress-text').textContent, text);
    assert.equal(env.editor.value, text);
    assert.equal(env.$('lab-backdrop').textContent, text + ' ');
  }
});

test('LAB-READING-PACKAGE-01: a reading belongs to its package; another package makes it stale and blocks its choices', async () => {
  const env = await boot(READ_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(READ_TEXT, {pkg: 'A'}));
  const ofPackage = (body, name) => ({...body, package: {packageId: 'test', manifestSha256: name}});
  env.$('lab-read').click();
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'stress')[0], 200, ofPackage(readingOf(READ_TEXT), 'A'));
  assert.ok(chooseButtons(env).every(node => !node.disabled));
  await respond(env, await refresh(env), 200, analysis(READ_TEXT, {pkg: 'B'}));
  assert.match(env.$('lab-reading').textContent, /Хостът вече използва друг пакет; прочитът е от предишния/);
  assert.ok(chooseButtons(env).every(node => node.disabled), 'the old choices are blocked');
  const analysisC = await refresh(env);
  env.$('lab-read').click();
  await env.clock.advance(200);
  assert.equal(callsTo(env, 'stress').length, 1, 'the new reading waits behind the analysis in the one shared lane');
  await respond(env, analysisC, 200, analysis(READ_TEXT, {pkg: 'C'}));
  await env.clock.advance(200);
  const late = callsTo(env, 'stress').at(-1);
  await respond(env, late, 200, ofPackage(readingOf(READ_TEXT), 'B'));
  assert.match(env.$('lab-reading').textContent, /Отговорът е от друг пакет на хоста и не е показан/, 'a late answer of the old package');
  assert.equal(env.$('lab-reading').querySelectorAll('p').filter(node => node.className === 'stress-text').length, 0);
});

test('LAB-READING-IME-01: a reading abandoned by an IME composition ends its loading and allows a new try', async () => {
  const env = await boot(READ_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(READ_TEXT));
  env.$('lab-read').click();
  await env.clock.advance(200);
  const call = callsTo(env, 'stress')[0];
  env.editor.dispatch('compositionstart');
  await respond(env, call, 200, readingOf(READ_TEXT));
  env.editor.dispatch('compositionend');
  await drain();
  assert.equal(env.editor.value, READ_TEXT, 'the composition was cancelled without a change');
  assert.doesNotMatch(env.$('lab-reading').textContent, /Зарежда/);
  assert.equal(env.$('lab-reading').querySelectorAll('p').filter(node => node.className === 'stress-text').length, 0, 'late data is never applied');
  assert.equal(env.$('lab-read').disabled, false, 'a new reading can be asked for');
  env.$('lab-read').click();
  await env.clock.advance(1000);
  await respond(env, callsTo(env, 'stress').at(-1), 200, readingOf(READ_TEXT));
  assert.equal(env.$('lab-reading').querySelectorAll('p').find(node => node.className === 'stress-text').textContent, 'Тя пила вода̀.');
});

test('stage 3: an edit makes the reading stale and its choices inactive; the same text again makes it current', async () => {
  const env = await boot(READ_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(READ_TEXT));
  env.$('lab-read').click();
  await env.clock.advance(200);
  await respond(env, callsTo(env, 'stress')[0], 200, readingOf(READ_TEXT));
  await type(env, READ_TEXT + ' Още.');
  assert.match(env.$('lab-reading').textContent, /Текстът е променен; прочитът е за предишния текст/);
  assert.ok(chooseButtons(env).every(node => node.disabled));
  await type(env, READ_TEXT);
  assert.doesNotMatch(env.$('lab-reading').textContent, /Текстът е променен/);
  assert.ok(chooseButtons(env).every(node => !node.disabled));
});

test('stage 3: a waiting reading of a text that changed is abandoned and its late answer ignored', async () => {
  const env = await boot(READ_TEXT);
  await respond(env, env.host.calls[0], 200, analysis(READ_TEXT));
  env.$('lab-read').click();
  await env.clock.advance(200);
  const call = callsTo(env, 'stress')[0];
  await type(env, 'Друг текст.');
  assert.doesNotMatch(env.$('lab-reading').textContent, /Зарежда/);
  await respond(env, call, 200, readingOf(READ_TEXT));
  assert.equal(env.$('lab-reading').querySelectorAll('p').filter(node => node.className === 'stress-text').length, 0);
  assert.ok(env.host.maxActive <= 1);
});

// ---------- construction advice (nominalConstructions.reviews) ----------
// The real review shape captured from the owner's host (1.0.0+37e2ec982, package bglang-neutral:36b0b69685d6d3e9) for
// „Рисувам сипя река.“: the ten distance-zero/one forms, only „синя“ with a Compatible phrase whose head support has a
// Compatible member for the reviewed word with replacementReadings [0]; parameterized by positions for other texts.
const SIPYA_FORMS = ['сипя', 'кипя', 'сивя', 'силя', 'синя', 'сип', 'сипа', 'сипе', 'сипея', 'сипи'];
function sipyaReview(tokenIndex, start, head, {pattern = 'finite+NP', context = tokenIndex - 1, headReading = 2,
  order = [0, 4, 1, 2, 3, 5, 6, 7, 8, 9], phraseLength = 9, member = {}, phrase = {}, review = {}} = {}) {
  return {tokenIndex, start, length: 4, pattern, noChangeRetained: true, maxDistance: 1, limit: 10, allPackageAlternativesExamined: false,
    contextualOrder: order, contextReadings: [{tokenIndex: context, completeReadingIndex: 0}], ...review,
    alternatives: SIPYA_FORMS.map((form, index) => ({
      alternative: {form, distance: index === 0 ? 0 : 1, readings: form === 'синя'
        ? [{form: 'синя', lemma: 'син', partOfSpeech: 'Прилагателно:86,Женски'}, {form: 'синя', lemma: 'синея', partOfSpeech: 'Глагол:160ii,2л.,Минало_свършено'},
          {form: 'синя', lemma: 'синея', partOfSpeech: 'Глагол:160ii,3л.,Минало_свършено'}, {form: 'Синя', lemma: 'Синя', partOfSpeech: 'Място:195'}]
        : [{form, lemma: form, partOfSpeech: 'Глагол:1'}]},
      phrase: form !== 'синя' ? null : {constructionKey: 'NP', start, length: phraseLength, headTokenIndex: head, modifierTokenIndices: [tokenIndex],
        connectorTokenIndices: [], assessment: 'Compatible', ...phrase, headSupports: [{reading: {tokenIndex: head, completeReadingIndex: headReading},
          graphReading: null, members: [{tokenIndex, assessment: 'Compatible', readings: [], graphReadings: [], replacementReadings: [0], ...member}]}]}}))};
}
const nominalOf = (...reviews) => ({version: 'nominal_constructions.v1', available: true, unavailableReason: null, phrases: [], reviews});
const PLACE = lemma => ({raw: {form: lemma.toLowerCase(), lemma, partOfSpeech: 'Място:195'}, features: {partOfSpeech: 'ProperNoun', nameKind: 'PlaceName'}});
const RIVER = {'Рисувам': [VERB('Рисувам', 'рисувам', 'First', 'Singular')], 'сипя': [VERB('сипя', 'сипя', 'First', 'Singular')],
  'Сипя': [VERB('Сипя', 'сипя', 'First', 'Singular')], 'река': [VERB('река', 'река', 'First', 'Singular'), PLACE('Река'), NOUN('река')]};
const adviceRows = env => env.$('lab-checks').querySelectorAll('li').filter(node => node.className.includes('check-advice'));
const adviceButton = (env, label) => env.$('lab-checks').querySelectorAll('button').find(node => node.textContent === label);
async function adviceOn(text, reviews, lexicon = RIVER) {
  const env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {lexicon, nominal: nominalOf(...reviews)}));
  return env;
}
// Graph refs use the actual response-local graphCandidateIndex/fullTagIndex schema and an exact-empty head.
function graphAdviceResponse() {
  const review = sipyaReview(1, 8, 2, {phraseLength: 14}), support = review.alternatives[4].phrase.headSupports[0];
  support.reading = null;
  support.graphReading = {tokenIndex: 2, graphCandidateIndex: 0, fullTagIndex: 0};
  const response = analysis('Рисувам сипя суперрека.', {lexicon: RIVER, nominal: nominalOf(review), graph: {'суперрека': [
    {word: 'суперрека', base: 'река', derivedLemma: 'суперрека', fullTags: ['Съществително:41,Женски']}
  ]}});
  Object.assign(response.wordAnalyses[2], {normalizedWord: 'суперрека', graphSearchIncomplete: false, graphExactRootCandidatesComplete: true});
  return response;
}
function graphWireResponse(format) {
  const response = graphAdviceResponse();
  if (format === 'shared-analyses-v1') return response;
  for (const token of response.data.tokens) {
    token.analysis = response.wordAnalyses[token.analysisRef];
    delete token.analysisRef;
  }
  delete response.wordAnalyses;
  if (format === null) delete response.evidenceFormat; else response.evidenceFormat = format;
  return response;
}

test('construction advice: a known word whose phrase agrees with a near form is advice, with what changes and why', async () => {
  const env = await adviceOn('Рисувам сипя река.', [sipyaReview(1, 8, 2)]);
  const box = env.$('lab-checks').textContent;
  assert.match(box, /Няма предупреждения в проверения обхват/, 'advice is not a warning');
  assert.match(box, /Съвети по конструкцията/);
  assert.equal(adviceRows(env).length, 1);
  const row = adviceRows(env)[0].textContent;
  assert.match(row, /„сипя“Позната дума\. След глагола „Рисувам“ би имало съгласувана именна група с близка дума:/);
  assert.match(row, /При „синя“: Рисувам синя река\./, 'the sentence as it would read');
  assert.match(row, /Подкрепа: една буква разлика · „синя“ пояснява „река“ · съгласувани по род и число \(ж\.р\., ед\.ч\.\)\./);
  assert.match(row, /„сипя“ също остава възможна: съветът не е грешка и не доказва какво е искал авторът\./);
  assert.ok(adviceButton(env, 'Замени с „синя“'));
  assert.ok(!adviceButton(env, 'Замени с „кипя“') && !adviceButton(env, 'Замени с „сипя“'), 'unsupported forms and the original are not offered');
  const preview = env.$('lab-checks').querySelectorAll('strong').find(node => node.className === 'preview-change');
  assert.equal(preview.textContent, 'синя');
  assert.match(env.$('lab-counts').textContent, /Съвети: 1/);
  const advised = spans(env).find(span => span.textContent === 'сипя');
  assert.match(advised.className, /w-advice/);
  assert.doesNotMatch(advised.className, /w-warning/);
  env.editor.setSelectionRange(9, 9); env.editor.dispatch('click');
  assert.match(env.$('lab-word').textContent, /Съвет: близка дума допълва конструкцията \(„синя“\); вижте „Проверки“\./);
});

test('construction advice: choosing replaces only the original span through the undoable editor path', async () => {
  const env = await adviceOn('Рисувам сипя река.', [sipyaReview(1, 8, 2)]);
  env.editor.focus();
  adviceButton(env, 'Замени с „синя“').click();
  assert.equal(env.editor.value, 'Рисувам синя река.');
  assert.equal(env.editor.history.length, 1, 'execCommand insertText keeps the native undo history');
  assert.equal(env.editor.selectionStart, 12);
  assert.equal(adviceRows(env).length, 1, 'the previous advice stays on screen as the previous analysis');
  assert.ok(env.$('lab-checks').querySelectorAll('button').every(node => node.disabled), 'and its actions are inactive');
});

test('construction advice: a repeated word keeps each occurrence and an emoji before it keeps the UTF-16 offsets', async () => {
  const text = '🙂 Рисувам сипя река. Рисувам сипя река.';
  const env = await adviceOn(text, [sipyaReview(1, 11, 2), sipyaReview(4, 30, 5)]);
  assert.equal(adviceRows(env).length, 2);
  const buttons = env.$('lab-checks').querySelectorAll('button').filter(node => node.textContent === 'Замени с „синя“');
  assert.equal(buttons.length, 2);
  buttons[1].click();
  assert.equal(env.editor.value, '🙂 Рисувам сипя река. Рисувам синя река.', 'only the second occurrence changed');
});

test('construction advice: the preposition pattern names the preposition and keeps the head’s own reading', async () => {
  const text = 'Спирам пред сипя къща.';
  const lexicon = {'Спирам': [VERB('Спирам', 'спирам', 'First', 'Singular')], 'сипя': [VERB('сипя', 'сипя', 'First', 'Singular')],
    'пред': [{raw: {form: 'пред', lemma: 'пред', partOfSpeech: 'Предлог'}, features: {partOfSpeech: 'Preposition'}}], 'къща': [NOUN('къща')]};
  const env = await adviceOn(text, [sipyaReview(2, 12, 3, {pattern: 'preposition+NP', context: 1, headReading: 0})], lexicon);
  const row = adviceRows(env)[0].textContent;
  assert.match(row, /След предлога „пред“ би имало съгласувана именна група/);
  assert.match(row, /При „синя“: Спирам пред синя къща\./);
  assert.match(row, /„синя“ пояснява „къща“ · съгласувани по род и число \(ж\.р\., ед\.ч\.\)/);
});

test('construction advice: a plural head names agreement in number only, as the kernel compares it', async () => {
  const review = sipyaReview(1, 8, 2, {headReading: 0});
  review.alternatives[4].alternative.form = 'сини';
  review.alternatives[4].alternative.readings = [{form: 'сини', lemma: 'син', partOfSpeech: 'Прилагателно:86,мн.ч.'}];
  const lexicon = {'Рисувам': RIVER['Рисувам'], 'сипи': [VERB('сипи', 'сипя', 'Third', 'Singular')],
    'реки': [{raw: {form: 'реки', lemma: 'река', partOfSpeech: 'Съществително:41,Женски,Множествено'},
      features: {partOfSpeech: 'Noun', number: 'Plural', gender: 'Feminine', article: 'Indefinite'}}]};
  const env = await adviceOn('Рисувам сипи реки.', [review], lexicon);
  const row = adviceRows(env)[0].textContent;
  assert.match(row, /При „сини“: Рисувам сини реки\./);
  assert.match(row, /„сини“ пояснява „реки“ · съгласувани по число \(мн\.ч\.\)\./);
  assert.ok(adviceButton(env, 'Замени с „сини“'));
});

test('construction advice: a graph head says the agreement rests on a graph hypothesis; a support of another word is not one', async () => {
  const response = graphAdviceResponse(), env = await boot(response.data.text);
  await respond(env, env.host.calls[0], 200, response);
  assert.match(adviceRows(env)[0].textContent, /„синя“ пояснява „суперрека“ · съгласувани по графова хипотеза за „суперрека“\./);
  assert.ok(adviceButton(env, 'Замени с „синя“'));
  response.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].graphReading.tokenIndex = 1;
  assert.equal(constructionAdvice(response.data, response).length, 0);
});

test('construction advice: the exact head ordinal must resolve to that original occurrence and noun reading', () => {
  const fresh = () => analysis('Рисувам сипя река.', {lexicon: structuredClone(RIVER), nominal: nominalOf(sipyaReview(1, 8, 2))});
  const valid = fresh();
  assert.equal(constructionAdvice(valid.data, valid).length, 1);
  const cases = [
    ['missing head reading', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].reading.completeReadingIndex = 999; }],
    ['negative head reading', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].reading.completeReadingIndex = -1; }],
    ['fractional head reading', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].reading.completeReadingIndex = 2.5; }],
    ['verb homograph ordinal', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].reading.completeReadingIndex = 0; }],
    ['place-name homograph ordinal', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].reading.completeReadingIndex = 1; }],
    ['foreign token ordinal', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].reading.tokenIndex = 1; }],
    ['foreign occurrence span', r => { r.data.readingInventory.occurrences[2].start = 8; }],
    ['foreign occurrence text', r => { r.data.readingInventory.occurrences[2].text = 'къща'; }],
    ['foreign reading set', r => { r.data.readingInventory.occurrences[2].readingSetIndex = 1; }],
    ['foreign exact form', r => { r.data.readingInventory.sets[2].canonicalReadings[2] = NOUN('къща'); }],
    ['ambiguous witness origin', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].graphReading =
      {tokenIndex: 2, graphCandidateIndex: 0, fullTagIndex: 0}; }]
  ];
  for (const [name, change] of cases) {
    const response = fresh(); change(response);
    assert.deepEqual(constructionAdvice(response.data, response), [], name);
  }
});

test('construction advice: the complete original head must be inside the phrase, including its final character', async () => {
  for (const [name, phraseLength] of [['head excluded', 4], ['head cut short', 8]]) {
    const env = await adviceOn('Рисувам сипя река.', [sipyaReview(1, 8, 2, {phraseLength})]);
    assert.equal(adviceRows(env).length, 0, name);
    assert.equal(adviceButton(env, 'Замени с „синя“'), undefined, name);
    assert.doesNotMatch(env.$('lab-checks').textContent, /съгласувани по род и число/, name);
  }
});

test('construction advice: missing or unmeasured exact axes cannot default to singular agreement wording', async () => {
  const cases = [{}, {partOfSpeech: 'Noun'}, {partOfSpeech: 'Noun', number: 'Singular'},
    {partOfSpeech: 'Noun', number: 'Count', gender: 'Feminine'},
    {partOfSpeech: 'Noun', number: 'Singular', gender: 'toString'},
    {...NOUN('река').features, hasUnrecognizedFeatures: true}, {...NOUN('река').features, hasConflictingFeatures: true}];
  for (const features of cases) {
    const lexicon = {...RIVER, 'река': [{raw: NOUN('река').raw, features}]};
    const env = await adviceOn('Рисувам сипя река.', [sipyaReview(1, 8, 2, {headReading: 0})], lexicon);
    assert.equal(adviceRows(env).length, 0, JSON.stringify(features));
    assert.equal(adviceButton(env, 'Замени с „синя“'), undefined);
    assert.doesNotMatch(env.$('lab-checks').textContent, /съгласувани по/);
  }
});

test('construction advice: plural exact support needs no gender and never claims gender agreement', async () => {
  const review = sipyaReview(1, 8, 2, {headReading: 0});
  review.alternatives[4].alternative.form = 'сини';
  review.alternatives[4].alternative.readings = [{form: 'сини', lemma: 'син', partOfSpeech: 'Прилагателно:86,мн.ч.'}];
  const lexicon = {...RIVER, 'реки': [{raw: {form: 'реки', lemma: 'река', partOfSpeech: 'Съществително:41,Женски,мн.ч.'},
    features: {partOfSpeech: 'Noun', number: 'Plural'}}]};
  const env = await adviceOn('Рисувам сипя реки.', [review], lexicon);
  assert.equal(adviceRows(env).length, 1);
  assert.match(adviceRows(env)[0].textContent, /съгласувани по число \(мн\.ч\.\)\./);
  assert.doesNotMatch(adviceRows(env)[0].textContent, /съгласувани по род|ед\.ч\./);
});

test('construction advice: graph ordinals and candidate identity must belong to the exact-empty head analysis', () => {
  const valid = graphAdviceResponse();
  assert.equal(constructionAdvice(valid.data, valid).length, 1);
  const cases = [
    ['missing candidate', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].graphReading.graphCandidateIndex = 999; }],
    ['missing full tag', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].graphReading.fullTagIndex = 999; }],
    ['negative candidate', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].graphReading.graphCandidateIndex = -1; }],
    ['fractional full tag', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].graphReading.fullTagIndex = 0.5; }],
    ['foreign shared analysis', r => { r.data.tokens[2].analysisRef = 1; }],
    ['foreign graph owner', r => { r.wordAnalyses[2].word = 'суперкъща'; }],
    ['foreign normalized owner', r => { r.wordAnalyses[2].normalizedWord = 'суперкъща'; }],
    ['foreign candidate word', r => { r.wordAnalyses[2].graphCandidates[0].word = 'суперкъща'; }],
    ['empty full tag', r => { r.wordAnalyses[2].graphCandidates[0].fullTags[0] = ''; }],
    ['incomplete graph search', r => { r.wordAnalyses[2].graphSearchIncomplete = true; }],
    ['incomplete exact roots', r => { r.wordAnalyses[2].graphExactRootCandidatesComplete = false; }],
    ['conditional graph', r => { r.wordAnalyses[2].graphCandidates[0].isConditional = true; }],
    ['unmet graph requirement', r => { r.wordAnalyses[2].graphCandidates[0].unmetExactSelfReadingRequirements = [{}]; }],
    ['wrong candidateIndex schema', r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].graphReading =
      {tokenIndex: 2, candidateIndex: 0, fullTagIndex: 0}; }],
    ['graph over an exact head', r => { r.data.readingInventory.sets[2].canonicalReadings = [NOUN('суперрека')]; }]
  ];
  for (const [name, change] of cases) {
    const response = graphAdviceResponse(); change(response);
    assert.deepEqual(constructionAdvice(response.data, response), [], name);
  }
});

test('construction advice: a malformed support cannot hide a later valid exact or graph support', async () => {
  for (const graph of [false, true]) {
    const response = graph ? graphAdviceResponse()
      : analysis('Рисувам сипя река.', {lexicon: RIVER, nominal: nominalOf(sipyaReview(1, 8, 2))});
    const phrase = response.data.nominalConstructions.reviews[0].alternatives[4].phrase;
    const valid = phrase.headSupports[0], invalid = structuredClone(valid);
    if (graph) invalid.graphReading.fullTagIndex = 999; else invalid.reading.completeReadingIndex = 999;
    phrase.headSupports = [null, invalid, valid];
    const advice = constructionAdvice(response.data, response);
    assert.equal(advice.length, 1, graph ? 'graph' : 'exact');
    assert.strictEqual(advice[0].supported[0].phrase.support, valid);
    const env = await boot(response.data.text);
    await respond(env, env.host.calls[0], 200, response);
    assert.equal(adviceRows(env).length, 1);
    assert.ok(adviceButton(env, 'Замени с „синя“'));
    assert.match(adviceRows(env)[0].textContent, graph ? /съгласувани по графова хипотеза за „суперрека“\./
      : /съгласувани по род и число \(ж\.р\., ед\.ч\.\)\./);
  }
});

test('construction advice: embedded graph analyses preserve the same reference checks as shared analyses', () => {
  const response = graphAdviceResponse();
  response.data.tokens[2].analysis = response.wordAnalyses[2];
  delete response.evidenceFormat; delete response.wordAnalyses;
  assert.equal(constructionAdvice(response.data, response).length, 1);
  response.data.tokens[2].analysis.graphCandidates[0].word = 'суперкъща';
  assert.deepEqual(constructionAdvice(response.data, response), []);
});

test('construction advice: an exact raw accented form keeps its unaccented lookup key and original head span', async () => {
  const response = structuredClone(analysis('Рисувам сипя река.', {lexicon: RIVER, nominal: nominalOf(sipyaReview(1, 8, 2))}));
  response.data.readingInventory.sets[2].canonicalReadings[2].raw.form = 'ре\u0301ка';
  response.wordAnalyses[2].readings[2].form = 'ре\u0301ка';
  const advice = constructionAdvice(response.data, response);
  assert.equal(advice.length, 1, 'DAWG preserves the raw acute form indexed under река');
  assert.equal(response.data.readingInventory.sets[2].canonicalKey, 'река');
  assert.equal(response.data.tokens[2].text, 'река');
  const env = await boot(response.data.text);
  await respond(env, env.host.calls[0], 200, response);
  assert.equal(adviceRows(env).length, 1);
  assert.ok(adviceButton(env, 'Замени с „синя“'));
  assert.match(adviceRows(env)[0].textContent, /„синя“ пояснява „река“ · съгласувани по род и число \(ж\.р\., ед\.ч\.\)\./);
});

test('construction advice: an abbreviation lookup alias retains the entire original occurrence span', () => {
  const response = analysis('Рисувам сипя река.', {lexicon: RIVER, nominal: nominalOf(sipyaReview(1, 8, 2, {headReading: 0, phraseLength: 11}))});
  response.data.text = 'Рисувам сипя кв. м.';
  response.data.sentences = [{start: 0, length: response.data.text.length}];
  Object.assign(response.data.tokens[2], {text: 'кв. м.', length: 6});
  Object.assign(response.data.readingInventory.occurrences[2], {text: 'кв. м.', length: 6});
  response.data.readingInventory.sets[2] = {lookupText: 'кв.м.', canonicalKey: 'кв.м.', canonicalReadings: [NOUN('кв.м.')],
    supportKey: null, supportReadings: null, displayedReadingCount: 1};
  Object.assign(response.wordAnalyses[2], {word: 'кв.м.', normalizedWord: 'кв.м.', readings: [NOUN('кв.м.').raw]});
  const advice = constructionAdvice(response.data, response);
  assert.equal(advice.length, 1, 'lookupText owns the acquired analysis while occurrence.text owns the original text');
  assert.equal(advice[0].supported[0].phrase.length, 11);
  assert.deepEqual(advicePreview(response.data, advice[0], 'синя', advice[0].supported[0].phrase),
    {before: 'Рисувам ', phraseBefore: '', replacement: 'синя', phraseAfter: ' кв. м.', after: ''});
  response.data.readingInventory.occurrences[2].length = 5;
  assert.deepEqual(constructionAdvice(response.data, response), [], 'the permitted lookup alias never relaxes the raw occurrence span');
});

test('construction advice: a nonempty stress-support route owns the normalized word instead of the canonical key', () => {
  const response = analysis('Рисувам сипя рѐка.', {lexicon: {...RIVER, 'рѐка': RIVER['река']}, nominal: nominalOf(sipyaReview(1, 8, 2))});
  const set = response.data.readingInventory.sets[2];
  set.supportReadings = set.canonicalReadings; set.canonicalReadings = []; set.supportKey = 'река';
  response.wordAnalyses[2].normalizedWord = 'река';
  assert.equal(set.canonicalKey, 'рѐка');
  assert.equal(constructionAdvice(response.data, response).length, 1);
  response.wordAnalyses[2].normalizedWord = 'рѐка';
  assert.deepEqual(constructionAdvice(response.data, response), [], 'nonempty support rows select supportKey');
});

test('construction advice: a complete head ordinal beyond the displayed prefix remains authoritative', () => {
  const response = structuredClone(analysis('Рисувам сипя река.', {lexicon: RIVER, nominal: nominalOf(sipyaReview(1, 8, 2))}));
  response.data.readingInventory.sets[2].displayedReadingCount = 1;
  response.wordAnalyses[2].readings = response.wordAnalyses[2].readings.slice(0, 1);
  const advice = constructionAdvice(response.data, response);
  assert.equal(advice.length, 1, 'the noun at complete ordinal 2 is outside the displayed verb-only prefix');
  assert.equal(advice[0].supported[0].phrase.support.reading.completeReadingIndex, 2);
  assert.equal(response.data.readingInventory.sets[2].canonicalReadings.length, 3);
});

test('construction advice: nonnull direct or shared conditional donors exclude a graph support in every wire format', () => {
  for (const format of [null, 'shared-donors-v1', 'shared-analyses-v1']) {
    const valid = graphWireResponse(format);
    assert.equal(constructionAdvice(valid.data, valid).length, 1, String(format));
    for (const donors of [[], ['река']]) {
      for (const shared of [false, true]) {
        const response = graphWireResponse(format), token = response.data.tokens[2];
        const owner = format === 'shared-analyses-v1' ? response.wordAnalyses[token.analysisRef] : token.analysis;
        if (shared) { owner.graphCandidates[0].conditionalSupportingLemmasRef = 0; response.donorSets = [donors]; }
        else owner.graphCandidates[0].conditionalSupportingLemmas = donors;
        assert.deepEqual(constructionAdvice(response.data, response), [], String(format) + ' shared=' + shared + ' donors=' + donors.length);
      }
    }
  }
});

test('construction advice: graph evidence requires both the owning display and canonical inventory to be exact-empty', () => {
  for (const format of [null, 'shared-donors-v1', 'shared-analyses-v1']) {
    const response = graphWireResponse(format), token = response.data.tokens[2];
    const owner = format === 'shared-analyses-v1' ? response.wordAnalyses[token.analysisRef] : token.analysis;
    owner.readings = [NOUN('суперрека').raw];
    assert.deepEqual(constructionAdvice(response.data, response), [], String(format) + ' nonempty exact display');
    owner.readings = [];
    Object.assign(response.data.readingInventory.sets[2], {canonicalReadings: [NOUN('суперрека')], supportKey: 'суперрека', supportReadings: []});
    assert.deepEqual(constructionAdvice(response.data, response), [], String(format) + ' a searched-empty support cannot hide canonical exact rows');
  }
});

test('construction advice: a foreign displayed raw witness or conditional graph support does not hide a later valid support', () => {
  const exact = structuredClone(analysis('Рисувам сипя река.', {lexicon: RIVER, nominal: nominalOf(sipyaReview(1, 8, 2))}));
  exact.data.readingInventory.sets[2].canonicalReadings.push(structuredClone(NOUN('река')));
  exact.wordAnalyses[2].readings.push(structuredClone(NOUN('река').raw));
  exact.data.readingInventory.sets[2].canonicalReadings[2].raw = NOUN('къща').raw;
  const phrase = exact.data.nominalConstructions.reviews[0].alternatives[4].phrase, validExact = structuredClone(phrase.headSupports[0]);
  validExact.reading.completeReadingIndex = 3; phrase.headSupports.push(validExact);
  assert.strictEqual(constructionAdvice(exact.data, exact)[0].supported[0].phrase.support, validExact);
  for (const format of [null, 'shared-donors-v1', 'shared-analyses-v1']) {
    const response = graphWireResponse(format), token = response.data.tokens[2];
    const owner = format === 'shared-analyses-v1' ? response.wordAnalyses[token.analysisRef] : token.analysis;
    owner.graphCandidates.push(structuredClone(owner.graphCandidates[0]));
    if (format === null) owner.graphCandidates[0].conditionalSupportingLemmas = [];
    else { owner.graphCandidates[0].conditionalSupportingLemmasRef = 0; response.donorSets = [[]]; }
    const graphPhrase = response.data.nominalConstructions.reviews[0].alternatives[4].phrase, validGraph = structuredClone(graphPhrase.headSupports[0]);
    validGraph.graphReading.graphCandidateIndex = 1; graphPhrase.headSupports.push(validGraph);
    const advice = constructionAdvice(response.data, response);
    assert.equal(advice.length, 1, String(format));
    assert.strictEqual(advice[0].supported[0].phrase.support, validGraph);
  }
});

test('construction advice: the replacement keeps the case of the original word', async () => {
  const env = await adviceOn('Рисувам Сипя река.', [sipyaReview(1, 8, 2)]);
  assert.ok(adviceButton(env, 'Замени с „Синя“'));
  assert.match(adviceRows(env)[0].textContent, /При „Синя“: Рисувам Синя река\./);
});

test('construction advice: a review that does not name its word exactly or lacks a measured support gives no advice', async () => {
  const cases = [
    ['another start', sipyaReview(1, 9, 2)],
    ['noChangeRetained false', sipyaReview(1, 8, 2, {review: {noChangeRetained: false}})],
    ['a mismatched phrase', sipyaReview(1, 8, 2, {phrase: {assessment: 'Mismatch'}})],
    ['replacement readings outside the alternative', sipyaReview(1, 8, 2, {member: {replacementReadings: [9]}})],
    ['a member for another word', sipyaReview(1, 8, 2, {member: {tokenIndex: 2}})],
    ['an incompatible member', sipyaReview(1, 8, 2, {member: {assessment: 'Unassessed'}})],
    ['a phrase that does not contain the word', sipyaReview(1, 8, 2, {phrase: {start: 13, length: 4}})]
  ];
  for (const [name, review] of cases) {
    const env = await adviceOn('Рисувам сипя река.', [review]);
    assert.equal(adviceRows(env).length, 0, name);
    assert.doesNotMatch(env.$('lab-checks').textContent, /Съвети по конструкцията/, name);
  }
  const marked = 'Рисувам сипя̀ река.';
  const env = await boot(marked);
  const response = analysis(marked, {lexicon: RIVER, nominal: nominalOf(sipyaReview(1, 8, 2, {phraseLength: 10}))});
  response.data.tokens[1] = {...response.data.tokens[1], text: 'сипя', length: 4};
  await respond(env, env.host.calls[0], 200, response);
  assert.equal(adviceRows(env).length, 0, 'a replacement would detach the combining mark');
});

test('construction advice: the server order is used only as a complete permutation that keeps letter distance first', () => {
  const response = analysis('Рисувам сипя река.', {lexicon: RIVER}), data = response.data;
  const advice = order => constructionAdvice({...data, nominalConstructions: nominalOf(sipyaReview(1, 8, 2, {order}))}, response)[0];
  assert.equal(advice([0, 4, 1, 2, 3, 5, 6, 7, 8, 9]).ordered, true);
  assert.deepEqual(advice([0, 4, 1, 2, 3, 5, 6, 7, 8, 9]).others.map(other => other.form).slice(0, 3), ['кипя', 'сивя', 'силя']);
  assert.equal(advice([4, 0, 1, 2, 3, 5, 6, 7, 8, 9]).ordered, false, 'distance one before distance zero');
  assert.equal(advice([0, 4, 1]).ordered, false, 'incomplete');
  assert.equal(advice([0, 4, 4, 2, 3, 5, 6, 7, 8, 9]).ordered, false, 'repeated');
  assert.deepEqual(advice([0, 4, 1]).supported.map(choice => choice.form), ['синя'], 'the raw order still finds the supported form');
  assert.equal(advice([0, 4, 1]).others.length, 8, 'an incomplete order falls back to every raw alternative');
  assert.equal(advice([0, 4, 4, 2, 3, 5, 6, 7, 8, 9]).supported.length, 1, 'a repeated index never repeats a choice');
  const preview = advicePreview(data, advice(null), 'синя', advice(null).supported[0].phrase);
  assert.deepEqual(preview, {before: 'Рисувам ', phraseBefore: '', replacement: 'синя', phraseAfter: ' река', after: '.'});
});

test('construction advice: an edit makes the advice stale, a new analysis without reviews removes it', async () => {
  const env = await adviceOn('Рисувам сипя река.', [sipyaReview(1, 8, 2)]);
  await type(env, 'Рисувам синя река.');
  assert.equal(env.$('lab-stale-checks').hidden, false);
  assert.equal(adviceButton(env, 'Замени с „синя“').disabled, true);
  assert.equal(spans(env).length, 0, 'no advice underline on a stale text');
  await env.clock.advance(1000);
  await respond(env, env.host.calls.at(-1), 200, analysis('Рисувам синя река.', {lexicon: RIVER, nominal: nominalOf()}));
  assert.equal(adviceRows(env).length, 0);
  assert.doesNotMatch(env.$('lab-counts').textContent, /Съвети/);
});

test('construction advice: malformed nested collections reject the response; an unavailable lane says so quietly', async () => {
  const base = () => analysis('Рисувам сипя река.', {lexicon: RIVER, nominal: nominalOf(sipyaReview(1, 8, 2))});
  for (const breakIt of [r => { r.data.nominalConstructions.reviews = {}; }, r => { r.data.nominalConstructions.reviews[0].alternatives = {}; },
    r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports = {}; },
    r => { r.data.nominalConstructions.reviews[0].alternatives[4].phrase.headSupports[0].members[0].replacementReadings = 0; }]) {
    const response = base();
    breakIt(response);
    assert.equal(responseShapeValid(response), false, breakIt.toString());
  }
  assert.equal(responseShapeValid(base()), true);
  const env = await boot('Рисувам сипя река.');
  await respond(env, env.host.calls[0], 200, analysis('Рисувам сипя река.', {lexicon: RIVER,
    nominal: {version: 'nominal_constructions.v1', available: false, unavailableReason: 'morphology_unavailable', phrases: [], reviews: []}}));
  assert.match(env.$('lab-checks').textContent, /Съветите по конструкцията не са налични за този текст\./);
});

// ---------- static page ----------
test('the static page has no inline code, no external resources and every element the module reads', async () => {
  const html = await readFile(fileURLToPath(new URL('index.html', labUrl)), 'utf8');
  const js = await readFile(fileURLToPath(new URL('demo.js', labUrl)), 'utf8');
  assert.match(html, /<script type="module" src="demo\.js"><\/script>/);
  assert.equal([...html.matchAll(/<script\b/g)].length, 1, 'one external module script');
  assert.doesNotMatch(html, /<style\b|\sstyle=|\son[a-z]+=/i);
  assert.doesNotMatch(html, /(src|href)="(https?:)?\/\//i);
  assert.match(js, /from '\.\.\/live-client\.js'/);
  for (const id of new Set([...js.matchAll(/\$\('([a-z-]+)'\)/g)].map(m => m[1]))) assert.match(html, new RegExp('id="' + id + '"'), id);
  for (const id of LAB_IDS) assert.match(html, new RegExp('id="' + id + '"'), id);
});


test('reviewed negation phrase diagnostics keep backend case and remain explicit source-span replacements', () => {
  for (const [source, projected] of [['незнам','не знам'],['Незнам','Не знам'],['НЕМОЖЕ','НЕ МОЖЕ']]) {
    const text = '😀 „' + source + '“.', diagnostic = Object.freeze({
      code: 'joined_verb_negation_should_be_separate', start: 4, length: 6,
      ruleId: 'spelling.joined_verb_negation.reviewed_forms.v1', projectedText: projected
    });
    const check = collectChecks(analysis(text, {diagnostics: [diagnostic]}).data).find(item => item.code === diagnostic.code);
    assert.equal(check.severity, 'warning'); assert.equal(check.replaceable, true);
    assert.equal(check.original, source); assert.equal(check.replacement, projected);
    assert.strictEqual(check.records[0].record, diagnostic); assert.equal(check.graph, false);
    assert.equal(MESSAGES[diagnostic.code], 'Ако е отрицателна глаголна форма, „не“ се пише отделно.');
  }
});

test('a reviewed phrase is applied only by the user and preserves surrounding emoji quotes and literal HTML', async () => {
  const text = '😀 „Незнам“. <b>буквално</b>', env = await boot(text);
  await respond(env, env.host.calls[0], 200, analysis(text, {diagnostics: [{
    code: 'joined_verb_negation_should_be_separate', start: 4, length: 6,
    ruleId: 'spelling.joined_verb_negation.reviewed_forms.v1', projectedText: 'Не знам'
  }]}));
  assert.equal(env.editor.value, text, 'a diagnostic never applies its own correction');
  const apply = enabledButtons(env.$('lab-checks')).find(button => button.textContent === 'Замени с „Не знам“');
  assert.ok(apply); apply.click();
  assert.equal(env.editor.value, '😀 „Не знам“. <b>буквално</b>');
});

test('new phrase replacement codes cannot detach combining marks or split surrogate pairs', () => {
  for (const [text, start] of [['незнам\u0300.', 0], ['😀неможе.', 1]]) {
    const check = collectChecks(analysis(text, {diagnostics: [{
      code: 'joined_verb_negation_should_be_separate', start, length: 6,
      ruleId: 'spelling.joined_verb_negation.reviewed_forms.v1', projectedText: 'не може'
    }]}).data).find(item => item.code === 'joined_verb_negation_should_be_separate');
    assert.equal(check.replaceable, false);
  }
});

// Portable projection of recorded FP01 diagnostics; runtime/package correctness is checked separately.
test('finite predicate diagnostic is a known Lab warning without a replacement', () => {
  const diagnostic = {
    code: 'unseparated_finite_predications', start: 7, length: 7,
    ruleId: 'grammar.closed_pronoun_two_present_predications.v1', projectedText: null,
    readings: [{tokenIndex: 0, completeReadingIndex: 0}, {tokenIndex: 1, completeReadingIndex: 0},
      {tokenIndex: 1, completeReadingIndex: 1}, {tokenIndex: 2, completeReadingIndex: 0}]
  };
  const data = {text: 'Аз пея пътувам.', diagnostics: [diagnostic], validation: {available: true, diagnostics: []}};
  const check = collectChecks(data).find(row => row.code === diagnostic.code);
  assert.equal(check.severity, 'warning');
  assert.equal(check.known, true);
  assert.equal(check.replaceable, false);
  assert.equal(check.projected, null);
  assert.equal(check.replacement, null);
  assert.equal(check.original, 'пътувам');
  assert.equal(check.message, 'Две лични глаголни форми в проверената конструкция изискват свързване или пунктуационно разделяне.');
  assert.strictEqual(check.records[0].record, diagnostic);
});

test('unknown diagnostic remains Lab information after finite predicate catalog support', () => {
  const data = {text: 'а', diagnostics: [{code: 'test_unknown_code', start: 0, length: 1}], validation: {available: true}};
  assert.equal(collectChecks(data)[0].severity, 'information');
});
