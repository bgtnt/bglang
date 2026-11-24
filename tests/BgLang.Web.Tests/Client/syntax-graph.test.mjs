import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import {pathToFileURL} from 'node:url';

const source = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT, 'apps/BgLang.Web/wwwroot/demo-tree/graph.js'))
  : new URL('../../../apps/BgLang.Web/wwwroot/demo-tree/graph.js', import.meta.url);
const {renderGraph, roleName, edgeName, statusName} = await import(source.href);

function fixture() {
  class Element {
    constructor(tag, namespace = null) {
      this.tag = tag; this.namespace = namespace; this.children = []; this.attributes = {};
      this.events = {}; this.textContent = ''; this.scrollLeft = 0; this.scrollTop = 0;
      this.ownerDocument = doc; this.replacements = 0;
    }
    append(...nodes) { this.children.push(...nodes); }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    addEventListener(name, callback) { this.events[name] = callback; }
    replaceChildren(...nodes) { this.children = nodes; this.replacements++; }
    focus() { doc.activeElement = this; }
    scrollIntoView(options) { this.scrollOptions = options; }
    set innerHTML(_) { throw new Error('Untrusted graph data must use textContent.'); }
  }
  const doc = {createElement: tag => new Element(tag),
    createElementNS: (namespace, tag) => new Element(tag, namespace)};
  return {doc, container: doc.createElement('div')};
}

const all = node => [node, ...node.children.flatMap(all)];
const find = (container, predicate) => all(container).filter(predicate);
const words = container => find(container, node => node.attributes.class === 'syntax-token');
const arcs = container => find(container, node => node.attributes['data-edge-from'] !== undefined);
const text = container => all(container).map(node => node.textContent).join('\n');
const token = (index, word = 'дума', extra = {}) =>
  ({index, text: word, kind: 'Word', status: 'unlinked', roles: [], ...extra});
const edge = (from, to, extra = {}) =>
  ({from, to, label: 'nominal_modifier', status: 'candidate', alternative: 'nominal:0:0', ...extra});
const click = (container, label) => {
  const button = find(container, node => node.tag === 'button' && node.textContent === label)[0];
  assert.ok(button, 'Button exists: ' + label); assert.equal(button.disabled, false);
  button.events.click();
};
const openDetails = container => {
  const details = find(container, node => node.tag === 'details')[0];
  assert.ok(details); details.open = true; details.events.toggle();
};

test('Bulgarian labels distinguish local compatibility, alternatives and grammatical constraints', () => {
  assert.equal(roleName('Subject'), 'Подлог');
  assert.equal(roleName('Complement'), 'Именна част на сказуемото');
  assert.equal(roleName('ReflexiveClitic'), 'Възвратна глаголна частица');
  assert.equal(roleName('other'), 'Неглаголен прочит');
  assert.equal(edgeName('grammar_constraint'), 'Граматично условие');
  assert.equal(edgeName('future_marker_marker'), 'Част от глаголната група с „ще“');
  assert.equal(edgeName('reflexive_clitic'), 'Глагол и кратка форма „се“');
  assert.equal(edgeName('coordinated_predicate'), 'Съчинено сказуемо');
  assert.equal(edgeName('coordinating_conjunction'), 'Съчинителна връзка');
  assert.equal(edgeName('object_clitic'), 'Кратко допълнение към глагола');
  assert.equal(edgeName('bound_reflexive_clitic'), 'Възвратна частица към глагола');
  assert.equal(edgeName('conditional_auxiliary_participle'), 'Условно наклонение със спомагателен глагол и причастие');
  assert.equal(edgeName('conditional_auxiliary_participle_marker'), 'Част от глаголната група в условно наклонение');
  assert.equal(statusName('compatible'), 'Локално съвместимо');
  assert.equal(statusName('pending'), 'Очаква анализ');
  assert.equal(statusName('new_status'), 'new_status');
});

test('tokens use safe text, preserve occurrence indexes and support click, Enter and Space', () => {
  const {container} = fixture(), selected = [], unsafe = '<img src=x onerror=alert(1)>😀';
  renderGraph(container, [token(7, unsafe), token(8, ' ', {kind: 'Whitespace'}),
    token(9, 'дума'), token(11, 'дума')], [], index => selected.push(index));
  assert.equal(words(container).length, 3);
  assert.ok(text(container).includes(unsafe), 'The complete original surface remains in SVG title text.');
  assert.equal(find(container, node => node.tag === 'img').length, 0);
  const [first, second, third] = words(container);
  first.events.click();
  let prevented = 0;
  second.events.keydown({key: 'Enter', preventDefault() { prevented++; }});
  third.events.keydown({key: ' ', preventDefault() { prevented++; }});
  third.events.keydown({key: 'Escape', preventDefault() { throw new Error('Unrelated key'); }});
  assert.deepEqual(selected, [7, 9, 11]); assert.equal(prevented, 2);
});

test('arrows retain supplied direction and status, with no invented links for role-only tokens', () => {
  const {container} = fixture();
  renderGraph(container, [token(2, 'Момчето', {roles: [{role: 'Subject'}]}), token(5, 'чете')],
    [edge(5, 2, {status: 'compatible', label: 'grammar_constraint'})]);
  const [arc] = arcs(container);
  assert.equal(arc.attributes['data-edge-from'], '5');
  assert.equal(arc.attributes['data-edge-to'], '2');
  const path = arc.children.find(node => node.tag === 'path');
  assert.match(path.attributes['marker-end'], /-compatible\)$/);
  assert.equal(path.attributes['stroke-dasharray'], 'none');
  assert.ok(text(arc).includes('Граматично условие'));
  renderGraph(container, [token(2, 'Момчето', {roles: [{role: 'Subject'}]}), token(5, 'чете')], []);
  assert.equal(arcs(container).length, 0);
  assert.ok(text(container).includes('Няма локални връзки'));
});

test('unchanged array identities reuse the chart and refresh its selection callback', () => {
  const {container} = fixture(), tokens = [token(0)], edges = [], selected = [];
  const api = renderGraph(container, tokens, edges, () => selected.push('old'));
  const root = container.children[0], replacements = container.replacements;
  const second = renderGraph(container, tokens, edges, () => selected.push('new'));
  assert.equal(api, second); assert.equal(container.children[0], root);
  assert.equal(container.replacements, replacements);
  words(container)[0].events.click(); assert.deepEqual(selected, ['new']);
});

test('large sentences bound DOM work and disclose cross-window edges with navigable endpoints', () => {
  const {container, doc} = fixture(), selected = [];
  const tokens = Array.from({length: 10000}, (_, i) => token(i * 2, 'дума' + i));
  const api = renderGraph(container, tokens, [edge(0, 19998), edge(6, 12)], index => selected.push(index));
  assert.equal(words(container).length, 32);
  assert.ok(text(container).includes('от 10000'));
  assert.ok(text(container).includes('Връзки към други части на изречението: 1'));
  assert.equal(arcs(container).length, 1, 'Cross-window edges are disclosed rather than given a false endpoint.');
  assert.equal(api.focusToken(19998), true);
  assert.equal(words(container).length, 16);
  assert.equal(doc.activeElement.attributes['data-token-index'], '19998');
  assert.deepEqual(selected, [19998]);
  assert.equal(api.focusToken(19999), false);
  assert.ok(text(container).includes('Връзки към други части на изречението: 1'));
});

test('dense alternative edges remain enumerated while SVG and detail list pages stay bounded', () => {
  const {container} = fixture();
  const edges = Array.from({length: 60}, (_, i) => edge(0, 1, {alternative: 'nominal:0:' + i}));
  renderGraph(container, [token(0), token(1)], edges);
  assert.equal(arcs(container).length, 1, 'Identical lines share one arc while retaining separate options.');
  assert.ok(text(container).includes('×60'));
  assert.ok(text(container).includes('Еднаквите линии са обединени'));
  assert.equal(find(container, node => node.tag === 'li').length, 0, 'Closed details allocate no edge rows.');
  openDetails(container);
  assert.equal(find(container, node => node.tag === 'li').length, 24);
  click(container, 'Следващи връзки →');
  assert.equal(find(container, node => node.tag === 'li').length, 24);
  assert.ok(text(container).includes('вариант nominal:0:47'));
  click(container, 'Следващи връзки →');
  assert.equal(find(container, node => node.tag === 'li').length, 12);
  assert.ok(text(container).includes('вариант nominal:0:59'));
});

test('distinct dense edges disclose the SVG display cap without dropping detail options', () => {
  const {container} = fixture(), tokens = Array.from({length: 12}, (_, i) => token(i));
  const edges = Array.from({length: 60}, (_, i) => {
    const from = Math.floor(i / 11), to = (i % 11 + from + 1) % 12;
    return edge(from, to);
  });
  renderGraph(container, tokens, edges);
  assert.equal(arcs(container).length, 48);
  assert.ok(text(container).includes('Схемата показва първите 48; всички са в списъка.'));
  assert.ok(text(container).includes('Връзки и варианти (60)'));
});

test('pending edits retain the previous vertical baseline and never grab editor focus', () => {
  const {container, doc} = fixture(), editor = {name: 'editor'};
  doc.activeElement = editor;
  renderGraph(container, [token(0), token(1), token(2)],
    [edge(0, 1), edge(1, 2), edge(2, 0)]);
  const before = words(container)[0].children.find(node => node.tag === 'rect').attributes.y;
  renderGraph(container, [token(-1, 'думата', {status: 'pending'}),
    token(-2, 'друга', {status: 'pending'})], []);
  const after = words(container)[0].children.find(node => node.tag === 'rect').attributes.y;
  assert.equal(after, before);
  assert.equal(doc.activeElement, editor);
  const tokenTexts = words(container).flatMap(node => node.children.filter(child => child.tag === 'text'));
  assert.ok(tokenTexts.every(node => node.textContent !== '-1' && node.textContent !== '0'));
});

test('disjoint short links share a sparse arc level', () => {
  const {container} = fixture();
  renderGraph(container, Array.from({length: 6}, (_, i) => token(i)), [edge(0, 1), edge(4, 5)]);
  const heights = arcs(container).map(node =>
    node.children.find(child => child.tag === 'path').attributes.d.match(/ V ([\d.]+) Q/)[1]);
  assert.equal(heights[0], heights[1]);
});

test('editing the end of a long sentence shows that token and keeps focus in the editor', () => {
  const { container, doc } = fixture();
  const editor = doc.createElement('textarea'); editor.focus();
  const tokens = Array.from({length:80}, (_, i) => token(i, 'дума' + i));
  renderGraph(container,tokens,[],undefined,79);
  assert.ok(words(container).some(node => node.attributes['data-token-index'] === '79'));
  assert.equal(doc.activeElement,editor);
});

test('editor-driven selection reveals another graph page without focus or callback loops', () => {
  const {container, doc} = fixture(), selected = [], editor = {name: 'editor'};
  doc.activeElement = editor;
  const tokens = Array.from({length: 80}, (_, i) => token(i, 'дума' + i));
  const api = renderGraph(container, tokens, [], index => selected.push(index));
  assert.equal(api.selectToken(79, {focus: false}), true);
  assert.equal(doc.activeElement, editor);
  assert.deepEqual(selected, []);
  const chosen = words(container).find(node => node.attributes['data-token-index'] === '79');
  assert.equal(chosen.attributes['aria-pressed'], 'true');
  assert.equal(chosen.attributes['data-selected'], 'true');
  api.selectToken(78);
  assert.equal(chosen.attributes['aria-pressed'], 'false');
  assert.equal(words(container).find(node => node.attributes['data-token-index'] === '78').attributes['aria-pressed'], 'true');
  api.selectToken(null);
  assert.ok(words(container).every(node => node.attributes['aria-pressed'] === 'false'));
});

test('graph interaction marks only the clicked occurrence before notifying the editor', () => {
  const {container} = fixture(), seen = [];
  renderGraph(container, [token(3), token(5)], [], index => {
    const active = words(container).filter(node => node.attributes['aria-pressed'] === 'true');
    seen.push([index, active.map(node => node.attributes['data-token-index'])]);
  });
  const [first, second] = words(container);
  first.events.click(); second.events.click();
  assert.deepEqual(seen, [[3, ['3']], [5, ['5']]]);
  assert.equal(first.attributes['data-selected'], 'false');
});

test('redraw retains internal scroll and visibly reports unavailable edge endpoints', () => {
  const {container} = fixture(), tokens = [token(3), token(5)];
  renderGraph(container, tokens, [edge(3, 5)]);
  const scroll = find(container, node => node.className === 'syntax-graph-scroll')[0];
  scroll.scrollLeft = 127; scroll.scrollTop = 29;
  renderGraph(container, tokens, [edge(3, 99)]);
  const next = find(container, node => node.className === 'syntax-graph-scroll')[0];
  assert.equal(next.scrollLeft, 127); assert.equal(next.scrollTop, 29);
  assert.equal(arcs(container).length, 0);
  assert.ok(text(container).includes('Връзки с недостъпна опора: 1.'));
});
