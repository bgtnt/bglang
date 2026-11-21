import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
const source = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT, 'apps/BgLang.Web/wwwroot/demo-tree/completion.js'))
  : new URL('../../../apps/BgLang.Web/wwwroot/demo-tree/completion.js', import.meta.url);
const {installCompletion} = await import(source.href);
const tick = () => new Promise(resolve => setImmediate(resolve));

function fixture(text = 'Ти рабтим.', caret = 6) {
  class Element {
    constructor(tag) { this.tag = tag; this.ownerDocument = doc; this.children = []; this.listeners = {}; this.attributes = {}; this.hidden = false; }
    addEventListener(name, fn) { (this.listeners[name] ??= []).push(fn); }
    dispatch(name, extra = {}) {
      const event = {prevented: false, preventDefault() { this.prevented = true; }, ...extra};
      for (const fn of this.listeners[name] ?? []) fn(event);
      return event;
    }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    removeAttribute(name) { delete this.attributes[name]; }
    replaceChildren(...children) { this.children = children; }
    append(...children) { this.children.push(...children); }
    focus() { doc.activeElement = this; }
    set innerHTML(_) { throw new Error('Original text must stay in textContent.'); }
  }
  const doc = {activeElement: null, listeners: {}, addEventListener: Element.prototype.addEventListener,
    dispatch: Element.prototype.dispatch, createElement: tag => new Element(tag)};
  const editor = new Element('textarea'), button = new Element('button'), target = new Element('select'), host = new Element('div');
  Object.assign(editor, {value: text, selectionStart: caret, selectionEnd: caret, maxLength: 16384});
  target.value = 'Auto'; host.id = 'completion-menu'; doc.activeElement = editor;
  let revision = 1;
  const requests = [], changes = [], cancelled = [];
  const transport = {cancel: key => cancelled.push(key), post: (operation, payload, options) => new Promise(resolve =>
    requests.push({operation, payload, options, resolve}))};
  const menu = installCompletion({editor, button, target, host, transport, getRevision: () => revision,
    replace(change) {
      changes.push(change);
      editor.value = editor.value.slice(0, change.start) + change.replacement + editor.value.slice(change.end);
      editor.selectionStart = editor.selectionEnd = change.caret;
      revision++; editor.dispatch('input');
      return true;
    }});
  const reply = async (index, {forms = ['работиш'], start = 3, length = 6, prefix = 'раб', target: actual = 'Finite', ...extra} = {}) => {
    const request = requests[index], caret = request.payload.context.continuation.caret;
    const noun = actual === 'Noun';
    request.resolve({http: {ok: true}, response: {data: {continuation: {version: noun ? 'nominal_continuation.v1' : 'finite_continuation.v1',
      target: actual, available: true, candidateSource: noun ? 'exact_package_nominal_vocabulary' : 'exact_package_finite_vocabulary',
      ranking: 'dictionary_score', caret, start, length, prefix, expectations: noun ? [] : [{}],
      ...(noun ? {nominalExpectations: [{}]} : {}),
      suggestions: forms.map(form => ({form, readings: [{}], matches: [{readingIndex: 0, expectationIndices: [0]}]})), ...extra}}}});
    await tick();
  };
  const options = () => host.children.flatMap(node => node.children).filter(node => node.attributes.role === 'option');
  return {editor, button, target, host, doc, requests, changes, cancelled, menu, reply, options,
    revise: () => revision++, key: (key, extra = {}) => editor.dispatch('keydown', {key, ...extra})};
}

test('typing and caret movement never request HTTP completion; the explicit shortcut captures mode and caret', () => {
  const f = fixture();
  f.editor.dispatch('input'); f.editor.dispatch('click'); f.editor.dispatch('keyup');
  assert.equal(f.requests.length, 0);
  const key = f.key(' ', {ctrlKey: true});
  assert.equal(key.prevented, true);
  assert.equal(f.requests.length, 1);
  assert.equal(f.requests[0].operation, 'analyze-text');
  assert.deepEqual(f.requests[0].payload.context.continuation, {caret: 6, limit: 12, target: 'Auto'});
  assert.equal(f.requests[0].payload.text, f.editor.value);
});

test('choosing a form replaces the whole word at a UTF-16 caret through the supplied undoable editor path', async () => {
  const f = fixture('😀 Ти рабтим.', 9);
  f.button.dispatch('click');
  await f.reply(0, {start: 6, length: 6, prefix: 'раб'});
  assert.deepEqual(f.options().map(node => node.textContent), ['работиш']);
  assert.equal(f.options()[0].dispatch('pointerdown').prevented, true);
  f.options()[0].dispatch('click');
  assert.equal(f.editor.value, '😀 Ти работиш.');
  assert.deepEqual(f.changes, [{start: 6, end: 12, original: 'рабтим', replacement: 'работиш', caret: 13}]);
  assert.equal(f.host.hidden, true);
});

test('Enter and Tab keep normal behavior until ArrowDown activates a current option', async () => {
  const f = fixture(); f.menu.request(); await f.reply(0);
  assert.equal(f.key('Enter').prevented, false); assert.equal(f.key('Tab').prevented, false);
  assert.equal(f.key('ArrowDown').prevented, true);
  assert.match(f.editor.attributes['aria-activedescendant'], /-option-0$/);
  assert.equal(f.key('Enter').prevented, true);
  assert.equal(f.editor.value, 'Ти работиш.');
  assert.equal(f.editor.attributes['aria-expanded'], 'false');
});

test('an edit makes both the response and previously rendered option callbacks stale', async () => {
  const f = fixture(); f.menu.request(); await f.reply(0);
  const old = f.options()[0];
  f.editor.value = 'Аз рабтим.'; f.revise(); f.editor.dispatch('input');
  old.dispatch('click');
  assert.equal(f.changes.length, 0); assert.equal(f.host.hidden, true);
  f.menu.request(); f.editor.value = 'Ние рабтим.'; f.revise(); f.editor.dispatch('input');
  await f.reply(1);
  assert.equal(f.host.hidden, true); assert.equal(f.changes.length, 0);
});

test('caret selection, revision, target, focus and Escape each invalidate a pending response', async () => {
  for (const invalidate of [
    f => {f.editor.selectionStart = f.editor.selectionEnd = 5; f.doc.dispatch('selectionchange');},
    f => {f.editor.selectionEnd = 8; f.editor.dispatch('select');},
    f => {f.revise(); f.editor.dispatch('keyup');},
    f => {f.target.value = 'Noun'; f.target.dispatch('change');},
    f => {f.doc.activeElement = f.button; f.editor.dispatch('blur');},
    f => f.key('Escape')
  ]) {
    const f = fixture(); f.menu.request(); invalidate(f); await f.reply(0);
    assert.equal(f.host.hidden, true); assert.equal(f.changes.length, 0);
  }
});

test('composition never requests or accepts a completion, including keyCode 229', async () => {
  const f = fixture(); f.editor.dispatch('compositionstart');
  assert.equal(f.menu.request(), false);
  assert.equal(f.key(' ', {ctrlKey: true, isComposing: true}).prevented, false);
  assert.equal(f.requests.length, 0);
  f.editor.dispatch('compositionend');
  assert.equal(f.key(' ', {ctrlKey: true, keyCode: 229}).prevented, false);
  f.menu.request(); await f.reply(0); const old = f.options()[0];
  f.editor.dispatch('compositionstart'); old.dispatch('click');
  assert.equal(f.changes.length, 0); assert.equal(f.host.hidden, true);
});

test('malformed evidence and unavailable contexts expose no actionable choices', async () => {
  for (const extra of [{available: false}, {start: 0}, {prefix: 'друго'}, {candidateSource: 'invented'}, {target: 'Other'}]) {
    const f = fixture(); f.menu.request(); await f.reply(0, extra);
    assert.equal(f.options().length, 0); assert.equal(f.changes.length, 0);
    assert.equal(f.host.hidden, false);
  }
});

test('explicit noun mode is bound to its requested target and supports insertion without a prefix', async () => {
  const f = fixture('малката ', 8); f.target.value = 'Noun'; f.menu.request();
  assert.equal(f.requests[0].payload.context.continuation.target, 'Noun');
  await f.reply(0, {start: 8, length: 0, prefix: '', target: 'Noun', forms: ['книга']});
  f.options()[0].dispatch('click');
  assert.equal(f.editor.value, 'малката книга');
});

test('grapheme boundaries and noncollapsed selections cannot open a request', () => {
  const f = fixture('😀 Ти рабтим.', 1);
  assert.equal(f.menu.request(), false);
  f.editor.selectionStart = 6; f.editor.selectionEnd = 9;
  assert.equal(f.menu.request(), false);
  assert.equal(f.requests.length, 0);
});

test('a superseded explicit request cannot replace the latest choices', async () => {
  const f = fixture(); f.menu.request(); f.menu.request();
  await f.reply(1, {forms: ['работиш']}); await f.reply(0, {forms: ['работи']});
  assert.deepEqual(f.options().map(option => option.textContent), ['работиш']);
  assert.equal(f.requests[0].options.current(), false);
});

test('a detached option from an earlier request cannot apply through a newer binding', async () => {
  const f = fixture(); f.menu.request(); await f.reply(0); const old = f.options()[0];
  f.menu.request(); await f.reply(1);
  old.dispatch('click');
  assert.equal(f.changes.length, 0);
  f.options()[0].dispatch('click');
  assert.equal(f.changes.length, 1);
});

test('returned forms are literal text, duplicate/current forms are omitted and maxlength is enforced', async () => {
  const f = fixture(); f.menu.request(); await f.reply(0, {forms: ['рабтим', '<b>работиш</b>', '<b>работиш</b>']});
  assert.deepEqual(f.options().map(option => option.textContent), ['<b>работиш</b>']);
  f.editor.maxLength = 12; f.options()[0].dispatch('click');
  assert.equal(f.changes.length, 0); assert.equal(f.editor.value, 'Ти рабтим.');
});
