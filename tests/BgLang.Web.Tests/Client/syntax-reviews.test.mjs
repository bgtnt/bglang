import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
const root = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT) + path.sep)
  : new URL('../../../', import.meta.url);
const view = new URL('apps/BgLang.Web/wwwroot/demo-tree/', root);
const { replacementRange, tokenAt } = await import(new URL('live.js', view).href);
const { reviewIndex } = await import(new URL('reviews.js', view).href);

function tokens(text) {
  return Array.from(text.matchAll(/[^\s.,]+|[.,]/gu), (m,index) =>
    ({index, start:m.index, length:m[0].length, text:m[0], kind:/^[.,]$/.test(m[0])?'Punctuation':'Word'}));
}
test('selecting the repeated occurrence addresses its original UTF-16 span on either side of an emoji', () => {
  const s='Аз 📚 чета. Аз чета.', ts=tokens(s);
  assert.equal(tokenAt(ts,s.lastIndexOf('чета'),4).index,5);
  assert.equal(tokenAt(ts,0).index,0);
  assert.equal(tokenAt(ts,s.indexOf('📚')+1).text,'📚');
  assert.equal(tokenAt(ts,s.length).text,'.');
});
test('one multi-edit grammar variant preserves intervening source and rejects stale or overlapping spans', () => {
  const s='Тези 📚 дете четат.';
  const edits=[{start:0,length:4,original:'Тези',replacement:'Това'},
    {start:s.indexOf('четат'),length:5,original:'четат',replacement:'чете'}];
  const r=replacementRange(s,edits.toReversed());
  assert.equal(s.slice(0,r.start)+r.replacement+s.slice(r.end),'Това 📚 дете чете.');
  assert.equal(replacementRange(s.replace('Тези','Тази'),edits),null);
  assert.equal(replacementRange(s,[edits[0],edits[0]]),null);
  assert.equal(replacementRange('📚',[{start:1,length:1,original:'\udcda',replacement:'Х'}]),null);
  assert.equal(replacementRange('и\u0301',[{start:0,length:1,original:'и',replacement:'а'}]),null);
});
test('warnings and suggestions link to their own occurrence, while conditional choices stay available', () => {
  const s='друфа 📚 друфа.', ts=tokens(s), start=s.lastIndexOf('друфа');
  const option={source:'spelling',kind:'spelling',conditional:true,tokenIndices:[2],edits:[{start,length:5,original:'друфа',replacement:'друга'}]};
  const r=reviewIndex({tokens:ts,diagnostics:[{start,length:5,code:'productive_attribute_agreement_mismatch'}],suggestions:[option]},s);
  assert.deepEqual([...r.warnings],[2]); assert.deepEqual([...r.advised],[2]);
  assert.equal(r.locations.length,1); assert.equal(r.byToken.get(2)[0],option);
  assert.ok(!r.byToken.has(0));
});

test('a missing punctuation mark at an empty span stays in the list without marking the preceding word', () => {
  const s='Тя чете';
  const r=reviewIndex({ tokens:tokens(s),diagnostics:[{code:'missing_terminal_punctuation',start:s.length,length:0}] },s);
  assert.equal(r.locations.length,1); assert.equal(r.warnings.size,0);
});

test('the subordinate comma candidate marks the observed conjunction for review', () => {
  const text = 'Учителят каза че е умен.', start = text.indexOf('че'), ts = tokens(text);
  const result = reviewIndex({tokens:ts, diagnostics:[{start,length:2,code:'missing_comma_before_subordinate_conjunction'}]},text);
  const index = ts.find(t => t.start === start).index;
  assert.equal(result.punctuation.get(index).start, start);
  assert.ok(result.advised.has(index));
  assert.ok(!result.warnings.has(index));
  assert.equal(reviewIndex({tokens:ts,diagnostics:[]},text).punctuation.size,0);
});

test('a compound conjunction warning points to the offending comma among repeated UTF-16 occurrences without an edit', () => {
  const text = '🙂 Дойдох, за да чета, за, да пиша.', start = text.lastIndexOf(','), ts = tokens(text);
  const result = reviewIndex({tokens:ts,
    diagnostics:[{start,length:1,code:'spurious_comma_inside_compound_conjunction'}]},text);
  const index = ts.find(t => t.start === start).index;
  assert.equal(index,8);
  assert.deepEqual([...result.warnings],[index]);
  assert.deepEqual([...result.punctuation.keys()],[index]);
  assert.deepEqual(result.locations,[{start,length:1,
    message:'Излишна запетая между частите на сложния съюз.',warning:true}]);
  assert.equal(result.advised.size,0);
  assert.equal(result.byToken.size,0);
  assert.equal(text.slice(result.locations[0].start,result.locations[0].start + result.locations[0].length),',');
  assert.equal(ts.map(token => token.text).filter(token => token === ',').length,3);
});
