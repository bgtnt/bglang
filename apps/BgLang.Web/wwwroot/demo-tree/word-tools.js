import { createPostLane, completeReadings } from "../live-client.js";

// The larger Lab module is loaded only after an explicit word/stress request, outside the typing path.
let helpers, helpersPromise;
const loadHelpers = () => helpersPromise ??= import("../demo-lab/demo.js").then(value => (helpers = value)).catch(error => {
  helpersPromise = null;
  throw error;
});

const stressNames = { Unknown:"неизвестно", Supported:"потвърдено", Inferred:"изведено", Ambiguous:"двусмислено",
  Incomplete:"частично", Conflict:"противоречиво", Unclassified:"некласифицирано", InvalidInput:"невалиден вход" };
const origins = { InputObservation:"написано в текста", SingleVowelInference:"една гласна", Editorial:"редакторско",
  ReviewedExact:"прегледано", LegacyStored:"речник", LegacyCarried:"речник, пренесено",
  LegacyDisplayInferred:"речник, по изписването", DerivationalRulePrediction:"словообразувателна прогноза" };
const stressNote = "Ударението е лексикално сведение, а не прогноза за изговора в изречението. Алтернативните прочити са показани поотделно.";

// Keep every coherent realization separate; combining alternative marks would invent an accent.
function stressRows(token) {
  const rows = [];
  function add(realization, lemma, selection = null) {
    rows.push({ text:helpers.stressedForm(token.text, token.start, realization) ?? "без точна позиция",
      origin:origins[realization.origin] ?? realization.origin, status:stressNames[realization.status] ?? realization.status,
      lemma, selection });
  }
  for (const item of [...(token.observations ?? []), ...(token.inferences ?? [])]) add(item, "");
  for (const reading of token.readings ?? []) {
    for (const item of reading.reviewedEvidence ?? []) add(item, reading.reading?.lemma ?? "");
    for (const owner of reading.owners ?? [])
      for (const item of owner.realizations ?? []) add(item, reading.reading?.lemma ?? "", {
        tokenIndex:token.tokenIndex, entryId:owner.entryId, form:reading.reading.form, fullTag:reading.reading.partOfSpeech ?? null
      });
  }
  return rows;
}

export async function stressAlternatives(token) {
  await loadHelpers();
  return stressRows(token);
}

export function createWordTools({ wordDetails, wordBody, readingDetails, readingButton, readingBody,
  transport = createPostLane() }) {
  const doc = wordBody.ownerDocument;
  let context = { text:"", revision:null, token:null, packageId:null }, word, reading, disposed = false;
  const forms = new Map(), formKeys = new Set();
  function el(tag, text, className) {
    const item = doc.createElement(tag);
    if (text !== undefined) item.textContent = text;
    if (className) item.className = className;
    return item;
  }
  function button(text, action) {
    const item = el("button", text);
    item.type = "button";
    item.addEventListener("click", action);
    return item;
  }
  const sameText = (a, b) => a.text === b.text && a.revision === b.revision && a.packageId === b.packageId;
  const sameWord = (a, b) => sameText(a, b) && a.token?.index === b.token?.index &&
    a.token?.start === b.token?.start && a.token?.length === b.token?.length && a.token?.text === b.token?.text;
  const fresh = (binding, forWord = false) => !disposed && (forWord ? sameWord(binding, context) : sameText(binding, context));
  function validToken(token, text) {
    return token && (token.kind == null || token.kind === "Word") && Number.isInteger(token.index) && Number.isInteger(token.start) &&
      Number.isInteger(token.length) && token.start >= 0 && token.length > 0 &&
      token.start + token.length <= text.length && typeof token.text === "string" &&
      text.slice(token.start, token.start + token.length) === token.text;
  }
  async function request(operation, payload, binding, key, forWord = false) {
    const current = () => fresh(binding, forWord);
    await loadHelpers();
    if (!current()) return null;
    const result = await transport.post(operation, payload, { key, current });
    if (!current() || result.stale) return null;
    if (result.error || !result.http?.ok) throw new Error(result.retryAfterMs
      ? "Хостът поиска пауза. Опитай отново след малко." : "Заявката не успя. Опитай отново.");
    const envelope = result.response, data = envelope?.data;
    if (binding.packageId && helpers.packageOf(envelope) !== binding.packageId)
      throw new Error("Отговорът е от друг речников пакет. Изчакай нов анализ.");
    if (operation === "analyze-text" ? data?.text !== payload.text || !helpers.responseShapeValid(envelope) ||
      !Array.isArray(data.readingInventory?.occurrences) || !Array.isArray(data.readingInventory?.sets)
      : !helpers.answerShapeValid(operation, data)) throw new Error("Отговорът е невалиден и не е показан.");
    if (operation === "stress" && data.text !== payload.text) throw new Error("Отговорът е за друг текст и не е показан.");
    return envelope;
  }
  function showStress(host, data, choose) {
    for (const token of data.tokens) {
      const alternatives = stressRows(token), box = el("details"), title = el("summary",
        `${token.renderedText ?? token.text} · ${stressNames[token.status] ?? token.status}`);
      box.append(title);
      if (!alternatives.length) box.append(el("p", "Няма сведение за ударението на този прочит."));
      const list = el("ul");
      for (const row of alternatives) {
        const item = el("li", `${row.text} · ${row.lemma ? row.lemma + " · " : ""}${row.origin} · ${row.status}`);
        if (choose && row.selection?.entryId) item.append(button("Покажи текста с този прочит", () => choose(row.selection)));
        list.append(item);
      }
      box.append(list);
      host.append(box);
    }
    host.append(el("p", stressNote, "note"));
  }
  function renderWord() {
    wordBody.replaceChildren();
    if (!context.token) { wordBody.append(el("p", "Избери дума от текущия анализ.")); return; }
    if (!word) {
      wordBody.append(button("Покажи речниковите прочити", () => loadWord()));
      return;
    }
    if (word.loading) { wordBody.append(el("p", "Зареждане на речниковите прочити…", "loading")); return; }
    if (word.error) {
      wordBody.append(el("p", word.error), button("Опитай отново", () => loadWord(true)));
      return;
    }
    const data = word.analysis.data;
    const rows = data.tokens.flatMap((token, index) => token.kind === "Word" ? completeReadings(data, index) ?? [] : []);
    if (!rows.length) wordBody.append(el("p", "Няма точен речников прочит. Графовите хипотези не са речникови леми."));
    for (const group of helpers.readingGroups(rows)) {
      const block = el("section");
      block.append(el("h3", group.lemma + " · " + group.posName));
      const list = el("ul");
      for (const line of group.lines) list.append(el("li", (line.text || "без измерени признаци") +
        (line.count > 1 ? ` · ${line.count} речникови записа` : "") + (line.tag ? " · " + line.tag : "")));
      block.append(list);
      const answer = forms.get(group.lemma);
      if (answer?.loading) block.append(el("p", "Зареждане на формите…"));
      else if (answer?.data) {
        const formList = el("ul");
        for (const row of answer.data) formList.append(el("li", row.form + (row.tag ? " · " + row.tag : "")));
        block.append(formList);
        if (answer.coverage?.possibleTruncation || answer.coverage?.hostTruncated)
          block.append(el("p", "Показаните форми са ограничени от API-то.", "note"));
      } else {
        if (answer?.error) block.append(el("p", answer.error));
        const active = word;
        block.append(button("Форми на „" + group.lemma + "“", () => {
          if (word === active && fresh(active.binding, true)) loadForms(group.lemma);
        }));
      }
      wordBody.append(block);
    }
    const lexical = data.lexical?.tokens?.find(item => item.start === 0 && item.length === data.text.length);
    const owners = (lexical?.owners?.entries ?? []).filter(entry => Array.isArray(entry.meaning) && entry.meaning.length);
    wordBody.append(el("h3", "Значения"));
    if (!owners.length) wordBody.append(el("p", data.lexical?.metadataAvailable === false
      ? "Речниковите значения не са налични в този пакет." : "Няма значения в отговора за тази дума."));
    for (const [index, owner] of owners.entries()) {
      const block = el("details");
      block.append(el("summary", owners.length === 1 ? "Покажи речниковата статия" : "Речникова статия " + (index + 1)));
      const list = el("ol");
      for (const meaning of owner.meaning) list.append(el("li", meaning));
      block.append(list);
      if (owner.lexemeKey ?? owner.entryId) block.append(el("small", owner.lexemeKey ?? owner.entryId));
      wordBody.append(block);
    }
    if (lexical?.owners?.truncated) wordBody.append(el("p", "Списъкът с речникови единици е съкратен от API-то.", "note"));
    const phonetics = data.phonetics;
    if (phonetics) {
      wordBody.append(el("h3", "Звуков строеж"));
      wordBody.append(el("p", "Предложени срички · " + (Array.isArray(phonetics.syllableCandidates)
        ? phonetics.syllableCandidates.join(" · ") : "няма данни")));
      const rhymes = [...new Set([phonetics.explicitRhymeTailCandidates, phonetics.inferredSingleVowelRhymeTailCandidates,
        phonetics.reviewedExactRhymeTailCandidates, phonetics.lexicalRhymeTailCandidates].flatMap(rows => Array.isArray(rows) ? rows : []))];
      if (rhymes.length) wordBody.append(el("p", "Възможни римни части · " + rhymes.join(" / ")));
    }
    wordBody.append(el("h3", "Ударение на думата"));
    if (word.stress) showStress(wordBody, word.stress.data);
    else if (word.stressError) wordBody.append(el("p", word.stressError), button("Потърси ударението отново", () => loadWordStress(word.binding)));
    else wordBody.append(el("p", "Зареждане на ударението…"));
  }
  async function loadWord(force = false) {
    if (disposed || !context.token || word?.loading || word && !force) return;
    const binding = { ...context }, pending = { binding, loading:true };
    word = pending; renderWord();
    try {
      const analysis = await request("analyze-text", { text:binding.token.text }, binding, "tree-word", true);
      if (!analysis || word !== pending) return;
      word = { binding, analysis }; renderWord();
      loadWordStress(binding);
    } catch (error) {
      if (fresh(binding, true) && word === pending) { word = { error:error.message }; renderWord(); }
    }
  }
  async function loadWordStress(binding) {
    if (!word?.analysis || !fresh(binding, true)) return;
    const active = word;
    try {
      const result = await request("stress", { text:binding.token.text }, binding, "tree-word-stress", true);
      if (!result || word !== active) return;
      if (helpers.packageOf(result) !== helpers.packageOf(word.analysis)) throw new Error("Речниковият пакет се промени. Отвори прочитите отново.");
      word.stress = result; word.stressError = null;
    } catch (error) { if (word === active && fresh(binding, true)) word.stressError = error.message; }
    if (word === active && fresh(binding, true)) renderWord();
  }
  async function loadForms(lemma) {
    if (!word?.analysis || forms.get(lemma)?.loading) return;
    const binding = { ...context }, analysis = word.analysis;
    forms.set(lemma, { loading:true }); renderWord();
    try {
      const key = "tree-forms:" + lemma;
      formKeys.add(key);
      const answer = await request("generate-forms", { text:lemma }, binding, key, true);
      if (!answer || !fresh(binding, true)) return;
      if (helpers.packageOf(answer) !== helpers.packageOf(analysis)) throw new Error("Речниковият пакет се промени. Отвори прочитите отново.");
      forms.set(lemma, answer);
    } catch (error) { if (fresh(binding, true)) forms.set(lemma, { error:error.message }); }
    if (fresh(binding, true)) renderWord();
  }
  function renderReading() {
    readingButton.disabled = !context.text.trim() || reading?.loading === true;
    readingBody.replaceChildren();
    if (!reading) { readingBody.append(el("p", "Покажи отделен прочит на текущия текст с ударения.")); return; }
    if (reading.loading) { readingBody.append(el("p", "Зареждане на ударенията…", "loading")); return; }
    if (reading.error) { readingBody.append(el("p", reading.error)); return; }
    readingBody.append(el("p", reading.answer.data.renderedText, "stress-text"));
    if (reading.selection) readingBody.append(el("p", reading.answer.data.selectionStatus === "Matched"
      ? "Избраният речников прочит съвпадна. Състоянието на думата показва дали ударението е установено."
      : "Избраният прочит не се свърза с тази дума; не е наложено ударение.", "note"));
    const active = reading;
    showStress(readingBody, reading.answer.data, selection => {
      if (reading === active && fresh(active.binding)) loadReading(selection);
    });
  }
  async function loadReading(selection = null) {
    if (disposed || !context.text.trim() || reading?.loading) return;
    const binding = { ...context }, pending = { binding, selection, loading:true };
    reading = pending; renderReading();
    try {
      const answer = await request("stress", { text:binding.text, ...(selection ? { stressSelection:selection } : {}) }, binding, "tree-reading");
      if (!answer || reading !== pending) return;
      reading = { binding, selection, answer }; renderReading();
    } catch (error) {
      if (fresh(binding) && reading === pending) { reading = { error:error.message }; renderReading(); }
    }
  }
  const openWord = () => { if (wordDetails.open) loadWord(); };
  const readClick = () => loadReading();
  wordDetails.addEventListener("toggle", openWord);
  readingButton.addEventListener("click", readClick);
  renderWord(); renderReading();
  return {
    setContext(next) {
      if (disposed) return;
      const accepted = { text:next.text ?? "", revision:next.revision, packageId:next.packageId ?? null, token:null };
      if (validToken(next.token, accepted.text)) accepted.token = next.token;
      const textChanged = !sameText(context, accepted), wordChanged = !sameWord(context, accepted);
      context = accepted;
      if (wordChanged) {
        for (const key of ["tree-word", "tree-word-stress", ...formKeys]) transport.cancel?.(key);
        formKeys.clear(); word = null; forms.clear(); renderWord();
      }
      if (textChanged) { transport.cancel?.("tree-reading"); reading = null; renderReading(); }
    },
    clear(next) { this.setContext({ ...next, token:null }); },
    dispose() {
      disposed = true;
      for (const key of ["tree-word", "tree-word-stress", "tree-reading", ...formKeys]) transport.cancel?.(key);
      wordDetails.removeEventListener("toggle", openWord);
      readingButton.removeEventListener("click", readClick);
    }
  };
}
