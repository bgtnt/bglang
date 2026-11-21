import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const moduleUrl = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT, 'apps/BgLang.Web/wwwroot/demo-tree/live.js'))
  : new URL('../../../apps/BgLang.Web/wwwroot/demo-tree/live.js', import.meta.url);
const { createAnalysisLane, draftSentence, groupTree } = await import(moduleUrl.href);

const tick = () => new Promise(resolve => setImmediate(resolve));
function harness() {
  const calls = [], shown = [], failures = [];
  const lane = createAnalysisLane((revision, text) => new Promise((resolve, reject) =>
    calls.push({ text, finish: () => resolve({ revision }), reject })), job => shown.push(job.text),
    (_job, error) => failures.push(error.message));
  return { lane, calls, shown, failures };
}
test('a burst replaces the queued text and an obsolete response never reaches the view', async () => {
  const { lane, calls, shown } = harness();
  lane.connect(true);
  for (const text of ['Ч', 'Че', 'Чет', 'Чета.']) lane.edit(text);
  assert.deepEqual(calls.map(c => c.text), ['Ч']);
  calls[0].finish(); await tick();
  assert.deepEqual(shown, []);
  assert.equal(calls[1].text, 'Чета.');
  calls[1].finish(); await tick();
  assert.deepEqual(shown, ['Чета.']);
});
test('input survives reconnect and superseded errors and composition never replace it', async () => {
  const { lane, calls, shown, failures } = harness();
  lane.edit('Ще'); lane.connect(true); lane.connect(false);
  lane.edit('Ще се'); lane.connect(true);
  calls[0].reject(new Error('old')); await tick();
  lane.edit('Ще се в', true); calls[1].finish(); await tick();
  assert.equal(calls.length, 2);
  lane.edit('Ще се върна.'); calls[2].finish(); await tick();
  lane.edit(''); assert.equal(calls.length, 3);
  assert.deepEqual(shown, ['Ще се върна.']); assert.deepEqual(failures, []);
});
test('clearing the editor discards an active analysis', async () => {
  const { lane, calls, shown } = harness();
  lane.connect(true); lane.edit('аз'); lane.edit(''); calls[0].finish();
  await tick(); assert.deepEqual(shown, []); assert.equal(calls.length, 1);
});
test('draws single-target role support separately from syntax, excluding self, missing and group targets', () => {
  const role = (alternative, contextTokenIndices) => ({source:'syntax', role:'Object', alternative, contextTokenIndices});
  const tokens = ['Тя','се','смее'].map((text,index) => ({ index, text, sentence:0, kind:'Word', roles:[] }));
  tokens[1].roles.push(role('reflexive',[1,2,9]),role('group',[0]));
  tokens[2].roles.push(role('group',[0]));
  const group = groupTree({tokens,edges:[],sentences:[{index:0}]}).get(0);
  assert.equal(group.linkCount,0);
  assert.deepEqual(group.edges.map(e=>[e.from,e.to,e.status,e.label]),[[2,1,'evidence','role_support_Object']]);
});
test('UTF-16 aligned preview retains untouched endpoints and drops an edited repeated word', () => {
  const text = 'Тя 📚 чете чете.';
  const tokens = Array.from(text.matchAll(/[^\s.]+|\./gu), (m,i) =>
    ({ index:i, text:m[0], start:m.index, length:m[0].length, sentence:0, kind:'Word', roles:[] }));
  const edges = [{ from:0, to:2, label:'a' }, { from:0, to:3, label:'b' }];
  const tree = { tokens, edges, sentences:[{index:0, start:0, length:text.length}] };
  const next = 'Тя 📚 чете четеш.';
  const draft = draftSentence({ text, tree, groups:groupTree(tree) }, next, next.length);
  assert.deepEqual(draft.tokens.map(t => next.slice(t.start,t.start+t.length)), draft.tokens.map(t => t.text));
  assert.deepEqual(draft.edges.map(e => [e.label,e.status]), [['a','unassessed']]);
  assert.ok(draft.tokens.find(t => t.text === 'четеш').index < 0);
});
