import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

const source = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT, 'apps/BgLang.Web/wwwroot/demo-tree/editor-view.js'))
  : new URL('../../../apps/BgLang.Web/wwwroot/demo-tree/editor-view.js', import.meta.url);
const {renderEditorBackdrop, setBackdropSelection, syncEditorScroll, posClass} = await import(source.href);

function fixture() {
  class Element {
    constructor(tag) {
      this.tag = tag; this.children = []; this.attributes = {}; this.value = '';
      this.ownerDocument = doc; this.replacements = 0;
    }
    set textContent(value) { this.children = []; this.value = value; }
    get textContent() { return this.value + this.children.map(node => node.textContent).join(''); }
    append(...nodes) {
      for (const node of nodes) {
        if (node.tag === '#fragment') this.children.push(...node.children); else this.children.push(node);
      }
    }
    setAttribute(key, value) { this.attributes[key] = String(value); }
    replaceChildren(...nodes) { this.value = ''; this.children = []; this.append(...nodes); this.replacements++; }
    set innerHTML(_) { throw new Error('Use textContent for original source.'); }
  }
  const doc = {createdTextNodes: 0, createElement: tag => new Element(tag),
    createDocumentFragment: () => new Element('#fragment'),
    createTextNode(text) {
      this.createdTextNodes++;
      return {nodeType: 3, nodeValue: text, get textContent() { return this.nodeValue; }};
    }};
  return {doc, container: doc.createElement('div')};
}

const span = (index, text, start, pos, extra = {}) =>
  ({index, text, start, length: text.length, pos, kind: 'Word', known: true, ...extra});
const spans = container => container.children.filter(node => node.tag === 'span');

test('POS backgrounds use Lab categories and disclose multiple complete reading categories', () => {
  const cases = {Noun: 'c-noun', Verb: 'c-verb', Adjective: 'c-adj', Adverb: 'c-adv',
    ProperNoun: 'c-name', PersonalPronoun: 'c-pron', Pronoun: 'c-pron', Numeral: 'c-num',
    Preposition: 'c-func', Conjunction: 'c-func', Particle: 'c-func', Interjection: 'c-func'};
  for (const [pos, expected] of Object.entries(cases)) assert.equal(posClass(span(0, 'а', 0, [pos])), expected);
  assert.equal(posClass(span(0, 'а', 0, ['Noun', 'Verb'])), 'c-multi');
  assert.equal(posClass(span(0, 'а', 0, ['Noun', 'Noun'])), 'c-noun');
  assert.equal(posClass(span(0, 'а', 0, [], {known: false})), 'c-unknown');
  assert.equal(posClass(span(0, 'а', 0, [])), 'c-other');
  assert.equal(posClass(span(0, '.', 0, [], {kind: 'Punctuation'})), '');
});

test('backdrop preserves exact UTF-16 text, CRLF, combining marks, repeated words and unsafe markup', () => {
  const {container} = fixture(), text = '😀 книга\tкнига\r\nле\u0300сен <img src=x>\n';
  const first = text.indexOf('книга'), second = text.indexOf('книга', first + 1), last = text.indexOf('ле');
  const tokens = [span(2, 'книга', first, ['Noun']), span(4, 'книга', second, ['Noun']),
    span(6, 'ле\u0300сен', last, ['Adjective'])];
  renderEditorBackdrop(container, text, tokens, {selectedIndex: 4, warningIndices: new Set([2]), suggestedIndices: new Set([6])});
  assert.equal(container.textContent, text);
  assert.equal(container.attributes['aria-hidden'], 'true');
  assert.equal(spans(container).length, 3);
  assert.equal(container.children.some(node => node.tag === 'img'), false);
  assert.match(spans(container)[0].className, /c-noun w-warning/);
  assert.match(spans(container)[1].className, /is-selected/);
  assert.match(spans(container)[2].className, /c-adj w-advice/);
});

test('caret selection only changes two span classes while retaining warnings and source DOM', () => {
  const {container} = fixture(), text = 'книга книга';
  const tokens = [span(1, 'книга', 0, ['Noun']), span(3, 'книга', 6, ['Noun'])];
  renderEditorBackdrop(container, text, tokens, {selectedIndex: 1, warningIndices: new Set([1])});
  const [first, second] = spans(container), replacements = container.replacements;
  assert.equal(setBackdropSelection(container, 3), true);
  assert.equal(container.replacements, replacements);
  assert.equal(spans(container)[0], first); assert.equal(spans(container)[1], second);
  assert.equal(first.className, 'editor-token c-noun w-warning');
  assert.equal(second.className, 'editor-token c-noun is-selected');
  setBackdropSelection(container, null);
  assert.equal(second.className, 'editor-token c-noun');
  assert.equal(container.textContent, text);
});

test('pending edits reuse one plain node without scanning old tokens or keeping stale highlights', () => {
  const {container, doc} = fixture();
  renderEditorBackdrop(container, 'книга', [span(1, 'книга', 0, ['Noun'])], {selectedIndex: 1});
  const unscannable = new Proxy([], {get(target, property) {
    if (property === Symbol.iterator) throw new Error('Pending frames must not scan prior token inventories.');
    return Reflect.get(target, property);
  }});
  const long = '😀 книга\n'.repeat(40000);
  renderEditorBackdrop(container, long, unscannable, {pending: true});
  const [plain] = container.children, replacements = container.replacements, created = doc.createdTextNodes;
  renderEditorBackdrop(container, long + 'а', unscannable, {pending: true});
  assert.equal(container.textContent, long + 'а'); assert.equal(container.children.length, 1);
  assert.equal(container.children[0], plain); assert.equal(container.replacements, replacements);
  assert.equal(doc.createdTextNodes, created); assert.equal(setBackdropSelection(container, 1), false);
});

test('a local pending edit retains exact unchanged POS nodes and expires every actionable style', () => {
  const {container} = fixture(), original = 'синя книга стара книга';
  const tokens = [span(1, 'синя', 0, ['Adjective']), span(3, 'книга', 5, ['Noun']),
    span(5, 'стара', 11, ['Adjective']), span(7, 'книга', 17, ['Verb'])];
  renderEditorBackdrop(container, original, tokens, {selectedIndex: 7,
    warningIndices: new Set([1, 3]), suggestedIndices: new Set([5, 7])});
  const [first, touched, third, last] = spans(container);
  const edited = 'синя книXга стара книга';
  renderEditorBackdrop(container, edited, [], {pending: true, selectedIndex: 7,
    warningIndices: new Set([1]), suggestedIndices: new Set([5])});
  assert.equal(container.textContent, edited);
  assert.deepEqual(spans(container), [first, third, last]);
  assert.equal(spans(container).includes(touched), false);
  assert.deepEqual(spans(container).map(node => node.className),
    ['editor-token c-adj', 'editor-token c-adj', 'editor-token c-verb']);
  assert.equal(setBackdropSelection(container, 7), false);
  // Repeated words retain the original occurrence's POS; no surface-based rebinding occurs.
  assert.equal(spans(container).at(-1).attributes['data-token-index'], '7');
  renderEditorBackdrop(container, 'синя книXYга стара книга', [], {pending: true});
  assert.equal(container.textContent, 'синя книXYга стара книга');
  assert.deepEqual(spans(container), [first, third, last]);
});

test('word merges and immediate edit boundaries are neutral instead of retaining partial tokens', () => {
  const {container} = fixture(), original = 'книга книга книга';
  const tokens = [span(1, 'книга', 0, ['Noun']), span(3, 'книга', 6, ['Verb']), span(5, 'книга', 12, ['Adjective'])];
  renderEditorBackdrop(container, original, tokens);
  const last = spans(container)[2];
  renderEditorBackdrop(container, 'книгакнига книга', [], {pending: true});
  assert.equal(container.textContent, 'книгакнига книга');
  assert.deepEqual(spans(container), [last]);
  assert.equal(last.className, 'editor-token c-adj');
  // A later split cannot mint new POS for the words whose source identity was discarded.
  renderEditorBackdrop(container, original, [], {pending: true});
  assert.deepEqual(spans(container), [last]);
  assert.equal(container.textContent, original);
});

test('emoji edits and a large inserted paste preserve shifted suffix identity without tokenizing the paste', () => {
  const {container} = fixture(), original = '😀 книга книга';
  renderEditorBackdrop(container, original, [span(1, 'книга', 3, ['Noun']), span(3, 'книга', 9, ['Verb'])],
    {warningIndices: new Set([3])});
  const [first, last] = spans(container);
  renderEditorBackdrop(container, '😁 книга книга', [], {pending: true});
  assert.deepEqual(spans(container), [first, last]);
  const paste = '🙂 нов текст\r\n'.repeat(30000), edited = paste + '😁 книга книга';
  renderEditorBackdrop(container, edited, [], {pending: true});
  assert.equal(container.textContent, edited);
  assert.deepEqual(spans(container), [first, last]);
  assert.equal(spans(container).length, 2, 'Inserted words get no inferred POS or per-word DOM.');
  assert.equal(last.className, 'editor-token c-verb');
});

test('pending without a text change removes old warning, advice and selected styles but retains POS', () => {
  const {container} = fixture(), text = 'книга';
  renderEditorBackdrop(container, text, [span(1, text, 0, ['Noun'])],
    {selectedIndex: 1, warningIndices: new Set([1]), suggestedIndices: new Set([1])});
  const [node] = spans(container);
  renderEditorBackdrop(container, text, [], {pending: true});
  assert.equal(spans(container)[0], node);
  assert.equal(node.className, 'editor-token c-noun');
  assert.equal(container.textContent, text);
});

test('invalid or stale analyzed spans are rejected before changing the current source backdrop', () => {
  const {container} = fixture(), text = '😀 книга';
  renderEditorBackdrop(container, text, [span(1, 'книга', 3, ['Noun'])]);
  const original = container.children.slice(), replacements = container.replacements;
  for (const tokens of [
    [span(1, 'друга', 3, ['Noun'])],
    [span(1, 'книга', 3, ['Noun']), span(1, 'книга', 3, ['Noun'])],
    [span(1, text[0], 0, [], {kind: 'Symbol'})]
  ]) assert.throws(() => renderEditorBackdrop(container, text, tokens), /Невалидни позиции/);
  assert.deepEqual(container.children, original); assert.equal(container.replacements, replacements);
  assert.equal(container.textContent, text);
});

test('matching data reuses the source DOM and native scroll synchronization touches no selection', () => {
  const {container} = fixture(), tokens = [span(1, 'книга', 0, ['Noun'])];
  renderEditorBackdrop(container, 'книга', tokens);
  const replacements = container.replacements;
  renderEditorBackdrop(container, 'книга', tokens, {selectedIndex: 1});
  assert.equal(container.replacements, replacements);
  const textarea = {scrollTop: 128, scrollLeft: 14, selectionStart: 4, selectionEnd: 4,
    focus() { throw new Error('Scroll synchronization must never move focus.'); }};
  syncEditorScroll(textarea, container);
  assert.equal(container.scrollTop, 128); assert.equal(container.scrollLeft, 14);
  assert.equal(textarea.selectionStart, 4); assert.equal(textarea.selectionEnd, 4);
});
