import test from "node:test";
import assert from "node:assert/strict";
import path from "node:path";
import { pathToFileURL } from "node:url";

const source = process.env.BGLANG_CLIENT_SOURCE_ROOT
  ? pathToFileURL(path.resolve(process.env.BGLANG_CLIENT_SOURCE_ROOT, "apps/BgLang.Web/wwwroot/demo-tree/word-tools.js"))
  : new URL("../../../apps/BgLang.Web/wwwroot/demo-tree/word-tools.js", import.meta.url);
const { createWordTools, stressAlternatives } = await import(source.href);
const drain = () => new Promise(resolve => setImmediate(resolve));
const envelope = (data, id = "package-a", coverage = {}) => ({ data, package:{ manifestSha256:id }, coverage });
const reply = response => ({ http:{ ok:true, status:200 }, response });
const realization = (offset, origin = "LegacyStored") => ({ origin, status:"Supported", marks:[{ utf16Position:offset, kind:"Grave" }] });
const stressed = (text = "вълна") => ({ text, renderedText:text, selectionStatus:"NotRequested", tokens:[{
  tokenIndex:0, text, start:0, length:text.length, renderedText:text, status:"Ambiguous", observations:[], inferences:[], readings:[{
    reading:{ form:text, lemma:text, partOfSpeech:"Съществително:41,Женски" }, reviewedEvidence:[], owners:[
      { entryId:"owner-1", realizations:[realization(1)] }, { entryId:"owner-2", realizations:[realization(4)] }
    ]
  }]
}] });
const analysis = (text = "вълна", lemmas = ["вълна"]) => ({ text, tokens:[{ kind:"Word", text, start:0, length:text.length }],
  readingInventory:{ occurrences:[{ tokenIndex:0, readingSetIndex:0 }], sets:[{ canonicalReadings:lemmas.map(lemma => ({
    raw:{ form:text, lemma, partOfSpeech:"Съществително:41,Женски" },
    features:{ partOfSpeech:"Noun", number:"Singular", gender:"Feminine" }
  })) }] }, lexical:{ metadataAvailable:true, tokens:[{ start:0, length:text.length,
    owners:{ entries:[{ entryId:"meaning-owner", lexemeKey:"вълна (вода)", meaning:["Движение по водната повърхност."] }] } }] },
  phonetics:{ syllableCandidates:["въл", "на"], lexicalRhymeTailCandidates:["ълна"] } });
function deferred() { let resolve; return { promise:new Promise(done => { resolve = done; }), resolve }; }

class Element {
  constructor(doc, tag) { this.ownerDocument = doc; this.tagName = tag; this.children = []; this.listeners = new Map(); this.value = ""; this.open = false; }
  set textContent(value) { this.value = String(value); this.children = []; }
  get textContent() { return this.value + this.children.map(item => item.textContent).join(""); }
  append(...items) { this.children.push(...items); }
  replaceChildren(...items) { this.value = ""; this.children = items; }
  addEventListener(name, action) { this.listeners.set(name, [...(this.listeners.get(name) ?? []), action]); }
  removeEventListener(name, action) { this.listeners.set(name, (this.listeners.get(name) ?? []).filter(value => value !== action)); }
  dispatch(name) { for (const action of this.listeners.get(name) ?? []) action({ target:this }); }
  click() { if (!this.disabled) this.dispatch("click"); }
  *all() { for (const child of this.children) { yield child; yield* child.all(); } }
}
function fixture(handler) {
  const doc = { createElement(tag) { return new Element(doc, tag); } };
  const nodes = Object.fromEntries(["wordDetails", "wordBody", "readingDetails", "readingButton", "readingBody"].map(key => [key, doc.createElement("div")]));
  const calls = [], transport = { post(operation, payload, options) {
    calls.push({ operation, payload, options });
    return Promise.resolve(handler(operation, payload, options));
  }, cancel() {} };
  const tools = createWordTools({ ...nodes, transport });
  const bind = (text = "вълна", revision = 1, token = { index:0, text, start:0, length:text.length, kind:"Word" }, packageId = "package-a") =>
    tools.setContext({ text, revision, token, packageId });
  const buttons = host => [...host.all()].filter(item => item.tagName === "button");
  return { ...nodes, tools, calls, bind, buttons, open() { nodes.wordDetails.open = true; nodes.wordDetails.dispatch("toggle"); } };
}

test("word tools request only on demand and reuse complete readings, phonetics, accents and per-lemma forms", async () => {
  await stressAlternatives(stressed().tokens[0]);
  const f = fixture((operation, payload) => reply(envelope(operation === "analyze-text" ? analysis(payload.text)
    : operation === "stress" ? stressed(payload.text) : [{ form:"вълните", tag:"мн.ч.,Членуван" }], "package-a",
    operation === "generate-forms" ? { possibleTruncation:true } : {})));
  f.bind();
  assert.equal(f.calls.length, 0);
  f.open(); await drain();
  assert.deepEqual(f.calls.map(call => call.operation), ["analyze-text", "stress"]);
  assert.match(f.wordBody.textContent, /вълна · съществително/);
  assert.match(f.wordBody.textContent, /въл · на/);
  assert.match(f.wordBody.textContent, /вълна \(вода\)/);
  assert.match(f.wordBody.textContent, /Движение по водната повърхност/);
  assert.match(f.wordBody.textContent, /въ̀лна/);
  assert.match(f.wordBody.textContent, /вълна̀/);
  f.wordDetails.dispatch("toggle"); await drain();
  assert.equal(f.calls.length, 2, "Reopening the same word does not duplicate its request.");
  f.buttons(f.wordBody).find(item => item.textContent.startsWith("Форми на")).click(); await drain();
  assert.equal(f.calls[2].operation, "generate-forms");
  assert.equal(f.calls[2].payload.text, "вълна");
  assert.match(f.wordBody.textContent, /вълните/);
  assert.match(f.wordBody.textContent, /ограничени от API-то/);
});

test("late word analysis after an edit never updates the new occurrence or starts stress", async () => {
  const wait = deferred(), f = fixture(() => wait.promise);
  f.bind(); f.open(); await drain();
  f.bind("дума", 2);
  wait.resolve(reply(envelope(analysis()))); await drain();
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].options.current(), false);
  assert.doesNotMatch(f.wordBody.textContent, /вълна/);
  assert.match(f.wordBody.textContent, /Покажи речниковите прочити/);
});

test("different occurrence of identical spelling invalidates word demand without clearing the text reading", async () => {
  const wait = deferred(), f = fixture(operation => operation === "analyze-text" ? wait.promise : reply(envelope(stressed("вълна вълна"))));
  const first = { index:0, text:"вълна", start:0, length:5 }, second = { index:2, text:"вълна", start:6, length:5 };
  f.bind("вълна вълна", 1, first); f.readingButton.click(); await drain(); f.open(); await drain();
  f.bind("вълна вълна", 1, second);
  wait.resolve(reply(envelope(analysis()))); await drain();
  assert.equal(f.calls.find(call => call.operation === "analyze-text").options.current(), false);
  assert.match(f.readingBody.textContent, /вълна вълна/);
  assert.doesNotMatch(f.wordBody.textContent, /Звуков строеж/);
});

test("full-text stress is independent, read only, and owner choice carries its exact occurrence", async () => {
  const f = fixture((operation, payload) => {
    assert.equal(operation, "stress");
    const data = stressed(payload.text);
    if (payload.stressSelection) { data.renderedText = "въ̀лна"; data.selectionStatus = "Matched"; }
    return reply(envelope(data));
  });
  f.bind(); assert.equal(f.calls.length, 0);
  f.readingButton.click(); await drain();
  assert.equal(f.calls.length, 1);
  assert.match(f.readingBody.textContent, /двусмислено/);
  const options = f.buttons(f.readingBody);
  assert.equal(options.length, 2);
  options[1].click(); await drain();
  assert.deepEqual(f.calls[1].payload.stressSelection, {
    tokenIndex:0, entryId:"owner-2", form:"вълна", fullTag:"Съществително:41,Женски"
  });
  assert.match(f.readingBody.textContent, /речников прочит съвпадна/);
  assert.match(f.readingBody.textContent, /въ̀лна/);
});

test("an edit or a package change rejects stale full-text stress and old owner buttons", async () => {
  const pending = [], f = fixture(() => { const item = deferred(); pending.push(item); return item.promise; });
  f.bind(); f.readingButton.click(); await drain(); f.bind("дума", 2);
  pending[0].resolve(reply(envelope(stressed()))); await drain();
  assert.doesNotMatch(f.readingBody.textContent, /вълна/);
  f.readingButton.click(); await drain();
  pending[1].resolve(reply(envelope(stressed("дума"), "other-package"))); await drain();
  assert.match(f.readingBody.textContent, /друг речников пакет/);
  assert.equal(f.buttons(f.readingBody).length, 0);
});

test("retained owner and form controls cannot request data for a replaced occurrence", async () => {
  const f = fixture((operation, payload) => reply(envelope(operation === "analyze-text" ? analysis(payload.text) : stressed(payload.text))));
  f.bind(); f.open(); await drain(); f.readingButton.click(); await drain();
  const oldOwner = f.buttons(f.readingBody)[0], oldForms = f.buttons(f.wordBody)[0], count = f.calls.length;
  f.bind("дума", 2);
  oldOwner.click(); oldForms.click(); await drain();
  assert.equal(f.calls.length, count);
});

test("coherent stress alternatives preserve absolute UTF-16 positions and never merge accents", async () => {
  const token = stressed().tokens[0]; token.start = 9;
  token.readings[0].owners[0].realizations[0].marks[0].utf16Position += 9;
  token.readings[0].owners[1].realizations[0].marks[0].utf16Position += 9;
  const alternatives = await stressAlternatives(token);
  assert.deepEqual(alternatives.map(row => row.text), ["въ̀лна", "вълна̀"]);
  assert.equal(alternatives.some(row => row.text === "въ̀лна̀"), false);
  assert.deepEqual(alternatives.map(row => row.selection.entryId), ["owner-1", "owner-2"]);
});

test("word stress from another package is rejected without discarding the valid dictionary inventory", async () => {
  const f = fixture(operation => reply(envelope(operation === "analyze-text" ? analysis() : stressed(),
    operation === "analyze-text" ? "package-a" : "package-b")));
  f.bind(); f.open(); await drain();
  assert.match(f.wordBody.textContent, /вълна · съществително/);
  assert.match(f.wordBody.textContent, /друг речников пакет/);
  assert.doesNotMatch(f.wordBody.textContent, /въ̀лна/);
});

test("disposing the tools leaves late answers and retained trigger buttons inactive", async () => {
  const wait = deferred(), f = fixture(() => wait.promise);
  f.bind(); f.open(); await drain(); f.tools.dispose();
  wait.resolve(reply(envelope(analysis()))); await drain();
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].options.current(), false);
});

test("two homograph lemma form requests keep distinct pending keys and both answers", async () => {
  const pending = new Map(), f = fixture((operation, payload) => {
    if (operation !== "generate-forms") return reply(envelope(operation === "analyze-text" ? analysis("вълна", ["вълна", "вълнен"]) : stressed()));
    const wait = deferred(); pending.set(payload.text, wait); return wait.promise;
  });
  f.bind(); f.open(); await drain();
  const buttons = f.buttons(f.wordBody).filter(item => item.textContent.startsWith("Форми на"));
  buttons[0].click(); buttons[1].click(); await drain();
  const calls = f.calls.filter(call => call.operation === "generate-forms");
  assert.equal(new Set(calls.map(call => call.options.key)).size, 2);
  pending.get("вълна").resolve(reply(envelope([{ form:"вълни" }])));
  pending.get("вълнен").resolve(reply(envelope([{ form:"вълнени" }]))); await drain();
  assert.match(f.wordBody.textContent, /вълни/);
  assert.match(f.wordBody.textContent, /вълнени/);
});
