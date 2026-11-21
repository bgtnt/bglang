import { renderGraph, roleName, edgeName, statusName } from "./graph.js";
import { createAnalysisLane, draftSentence, groupTree, replacementRange, tokenAt } from "./live.js";
import { renderEditorBackdrop, setBackdropSelection, syncEditorScroll } from "./editor-view.js";
import { reviewIndex, renderChecks, renderVariants } from "./reviews.js";
import { createPostLane } from "../live-client.js";
import { installCompletion } from "./completion.js";
import { createWordTools } from "./word-tools.js";

const genderNames = { Masculine: "мъжки", Feminine: "женски", Neuter: "среден" };
const ui = Object.fromEntries(Array.from(document.querySelectorAll("[id]"), el => [el.id, el]));
const transport = createPostLane({ fetchImpl: (path, options) => fetch(new URL(".." + path, document.baseURI), options) });
const wordTools = createWordTools({ wordDetails: ui["word-tools"], wordBody: ui["word-tools-body"],
  readingDetails: ui["stress-reading"], readingButton: ui["stress-read"], readingBody: ui["stress-reading-body"], transport });
let invalidateSearch;
let snapshot, selected, view, errorText, raf = 0;
let online = false;
let chart, markChecks, activeIndex, detailJob, detailIndex, selectionFrame = 0, undo;
const posLabels = { Noun:"съществително", Adjective:"прилагателно", Verb:"глагол", ProperNoun:"собствено име",
  PersonalPronoun:"лично местоимение", Pronoun:"местоимение", Adverb:"наречие", Numeral:"числително",
  Preposition:"предлог", Conjunction:"съюз", Particle:"частица", Interjection:"междуметие" };
let paintedRevision = -1;

function schedule() {
  if (!raf) raf = requestAnimationFrame(draw);
}

function inspect(index) {
  const token = view?.tokens.find(t => t.index === index);
  if (!token) return;
  const job = lane.current();
  if (detailJob === job && detailIndex === index && !view.draft) return;
  detailJob = job; detailIndex = index;
  ui["word-title"].textContent = token.text;
  if (view.draft) {
    ui.detail.textContent = "Редакцията очаква анализ.";
    return;
  }
  const lemmata = token.lemmas ?? [];
  wordTools.setContext({ text: job.text, revision: job.revision, token: token.kind === "Word" ? token : null });
  ui.morph.textContent = [(token.pos ?? []).map(p => posLabels[p] ?? p).join(" / "),
    lemmata.length && "Лема · " + lemmata.join(", "), token.known === false && "Без точен речников прочит"]
    .filter(Boolean).join(" · ");
  renderVariants(ui.variants, snapshot.reviews.byToken.get(index) ?? [], v => applyVariant(job, v));
  const words = new Map(view.tokens.map(t => [t.index, t.text]));
  const facts = new Set(token.roles.map(r => {
    const support = (r.contextTokenIndices ?? []).filter(i => i !== index && words.has(i)).map(i => "„" + words.get(i) + "“");
    const gender = genderNames[r.contextualGender];
    return [roleName(r.role), statusName(r.status), r.ruleId, gender && "В тази група се съгласува в " + gender + " род",
      support.length && "Опора в " + support.join(", ")]
      .filter(Boolean).join(" · ");
  }));
  for (const other of view.tokens) {
    if (other.index === index) continue;
    for (const r of other.roles) {
      if (r.contextTokenIndices?.includes(index))
        facts.add(`Опора за „${other.text}“ · ${roleName(r.role)} · ${statusName(r.status)}`);
    }
  }
  for (const edge of view.edges) {
    if (edge.from === index || edge.to === index)
      facts.add(`${words.get(edge.from)} → ${words.get(edge.to)} · ${edgeName(edge.label)} · ${statusName(edge.status)}${edge.ruleId ? " · " + edge.ruleId : ""}`);
  }
  const list = document.createElement("ul");
  for (const fact of facts) {
    const item = document.createElement("li");
    item.textContent = fact;
    list.append(item);
  }
  if (token.tags?.length) {
    const item = document.createElement("li");
    item.textContent = "Речникови описания · " + token.tags.join(" / ");
    list.prepend(item);
  }
  if (list.childElementCount) ui.detail.replaceChildren(list);
  else ui.detail.textContent = "За този елемент анализаторът не е върнал синтактична роля или връзка.";
}

function clearDetail() {
  detailJob = null; detailIndex = null;
  const job = lane.current();
  wordTools.clear({ text:job.text, revision:job.revision });
  ui["word-title"].textContent = "Избери дума";
  ui.morph.textContent = "";
  ui.variants.replaceChildren();
  ui.detail.textContent = "Постави курсора в текста или избери дума от схемата.";
}

function currentResult() {
  return snapshot?.job === lane.current() && snapshot.text === ui.text.value;
}

function placeEditorRange(start, length) {
  ui.text.focus({ preventScroll:true });
  ui.text.setSelectionRange(start, start + length);
  syncFromEditor();
  const el = ui.backdrop.querySelector(".is-selected");
  if (el) {
    const word = el.getBoundingClientRect(), box = ui.text.getBoundingClientRect();
    if (word.top < box.top) ui.text.scrollTop -= box.top - word.top + 8;
    else if (word.bottom > box.bottom) ui.text.scrollTop += word.bottom - box.bottom + 8;
    syncEditorScroll(ui.text, ui.backdrop);
  }
}

function selectFromGraph(index) {
  if (!currentResult()) return;
  const token = snapshot.tree.tokens[index];
  if (token && view === snapshot.groups.get(token.sentence)) placeEditorRange(token.start, token.length);
}

function syncFromEditor() {
  if (!currentResult()) return;
  const start = ui.text.selectionStart;
  const token = tokenAt(snapshot.tree.tokens, start, ui.text.selectionEnd - start);
  const index = token?.index ?? null;
  const sentence = token?.sentence ?? selected;
  if (activeIndex === index && selected === sentence) return;
  activeIndex = index;
  selected = sentence;
  setBackdropSelection(ui.backdrop, index);
  schedule();
}

function applyVariant(job, variant) {
  if (!currentResult() || snapshot.job !== job) return;
  const original = ui.text.value;
  const range = replacementRange(original, variant.edits);
  if (range) replaceRange(range);
}

function replaceRange(range) {
  const original = ui.text.value;
  if (range.original !== undefined && original.slice(range.start, range.end) !== range.original) return false;
  if (original.length - (range.end - range.start) + range.replacement.length > ui.text.maxLength) return false;
  ui.text.focus({ preventScroll:true });
  ui.text.setSelectionRange(range.start, range.end);
  const after = original.slice(0, range.start) + range.replacement + original.slice(range.end);
  const change = { original, after, start:range.start, end:range.end, native:false };
  undo = change;
  // As in Live Lab, an insertText editor command preserves the browser's native undo stack.
  try { change.native = document.execCommand("insertText", false, range.replacement); } catch { /* use setRangeText */ }
  if (ui.text.value === original) {
    change.native = false;
    ui.text.setRangeText(range.replacement, range.start, range.end, "select");
  }
  if (ui.text.value !== after) { undo = null; edit(); return false; }
  if (Number.isInteger(range.caret)) ui.text.setSelectionRange(range.caret, range.caret);
  else ui.text.setSelectionRange(range.start, range.start + range.replacement.length);
  edit();
  return true;
}
ui.undo.addEventListener("click", () => {
  if (!undo || ui.text.value !== undo.after) return;
  const old = undo; undo = null;
  ui.text.focus({ preventScroll:true });
  if (old.native) {
    try { document.execCommand("undo"); } catch { /* restore only the verified replacement */ }
  }
  if (ui.text.value === old.after) {
    const replacedLength = old.end - old.start + old.after.length - old.original.length;
    ui.text.setRangeText(old.original.slice(old.start, old.end), old.start, old.start + replacedLength, "select");
  }
  ui.text.setSelectionRange(old.start, old.end);
  edit();
});

function draw() {
  raf = 0;
  const job = lane.current();
  const firstFrame = paintedRevision !== job.revision;
  if (firstFrame || detailJob && detailJob !== job) clearDetail();
  paintedRevision = job.revision;
  const confirmed = snapshot && snapshot.job === job;
  ui.graph.classList.toggle("provisional", !confirmed);
  ui.graph.dataset.revision = String(job.revision);
  ui.graph.dataset.phase = confirmed ? "analyzed" : "draft";
  if (!confirmed) {
    ui.punctuation.hidden = true;
    ui.sentence.disabled = true;
    if (job.text.trim()) {
      ui.diagnostics.inert = true;
      ui.diagnostics.classList.add("previous");
      if (!snapshot) ui.diagnostics.textContent = "Изчакване на текущия анализ.";
    } else {
      ui.navigation.hidden = true;
      ui.diagnostics.textContent = "—";
    }
    ui.state.textContent = errorText || (!job.text.trim() ? "Въведи текст." :
      online ? "Редакция · запазените линии са от предишния анализ." : "Локална редакция · изчакване на връзка.");
    renderEditorBackdrop(ui.backdrop, job.text, [], { pending:true });
    view = { ...draftSentence(snapshot, job.text, ui.text.selectionEnd), draft: true };
  } else {
    const { tree, groups } = snapshot;
    if (!snapshot.displayed) {
      const commas = [...snapshot.reviews.punctuation.values()];
      ui.punctuation.hidden = commas.length === 0;
      if (commas.length) {
        const row = commas[0];
        ui.punctuation.textContent = commas.length === 1 ? "Провери запетаята пред „" + job.text.slice(row.start, row.start + row.length) + "“" :
          "Провери запетаите (" + commas.length + ")";
        ui.punctuation.onclick = () => {
          if (currentResult() && snapshot.job === job) placeEditorRange(row.start, row.length);
        };
      }
      const caret = ui.text.selectionStart;
      const token = tokenAt(tree.tokens, caret, ui.text.selectionEnd - caret);
      activeIndex = token?.index ?? null;
      selected = token?.sentence ?? (tree.sentences.findLast(s => s.start <= caret) ?? tree.sentences[0])?.index;
      ui.diagnostics.inert = false;
      ui.diagnostics.classList.remove("previous");
      markChecks = renderChecks(ui.diagnostics, snapshot.reviews.locations, job.text, row => {
        if (currentResult() && snapshot.job === job) placeEditorRange(row.start, row.length);
      });
      renderEditorBackdrop(ui.backdrop, job.text, tree.tokens, { selectedIndex:activeIndex,
        warningIndices:snapshot.reviews.warnings, suggestedIndices:snapshot.reviews.advised });
      syncEditorScroll(ui.text, ui.backdrop);
      // A token clicked in the preview may have moved or disappeared.
      clearDetail();
    }
    ui.navigation.hidden = tree.sentences.length < 2;
    ui.sentence.disabled = false;
    ui.sentence.value = selected == null ? "" : String(selected + 1);
    ui.sentence.max = tree.sentences.length;
    ui.count.textContent = "от " + tree.sentences.length;
    view = groups.get(selected) ?? { tokens: tree.tokens.filter(t => t.kind !== "Whitespace"), edges: [] };
    const count = view.linkCount ?? 0;
    const supports = view.edges.filter(e => e.status === "evidence").length;
    ui.state.textContent = tree.status === "unavailable" ? "Синтактичният анализ е недостъпен за този текст." :
      `Частичен разбор · ${count} ${count === 1 ? "локална връзка" : "локални връзки"} · ${supports} ${supports === 1 ? "опора за роля" : "опори за роли"}`;
  }
  const caret = ui.text.selectionEnd;
  const followed = document.activeElement === ui.text && (firstFrame || confirmed && !snapshot.displayed)
    ? view.tokens.findLast(t => t.start < caret) ?? view.tokens[0] : undefined;
  chart = renderGraph(ui.graph, view.tokens, view.edges, selectFromGraph, followed?.index);
  if (confirmed) {
    markChecks?.(activeIndex == null ? null : snapshot.tree.tokens[activeIndex]);
    chart.selectToken(activeIndex, { focus:false, reveal:true });
    if (activeIndex == null) clearDetail();
    else inspect(activeIndex);
  }
  if (confirmed) snapshot.displayed = true;
}


let connection;
const lane = createAnalysisLane(
  (revision, text) => connection.invoke("Analyze", revision, text),
  (job, { tree }) => {
    snapshot = { job, text: job.text, tree, groups: groupTree(tree), reviews: reviewIndex(tree, job.text) };
    errorText = "";
    schedule();
  },
  (_job, err) => { errorText = err.message || "Неуспешен анализ."; schedule(); }
);

function edit(composing = false) {
  errorText = "";
  if (undo && undo.after !== ui.text.value) undo = null;
  ui.undo.hidden = !undo;
  const previous = lane.current();
  if (lane.edit(ui.text.value, composing) !== previous) invalidateSearch?.();
  schedule();
}
ui.text.addEventListener("input", e => edit(e.isComposing));
ui.text.addEventListener("compositionend", () => edit());
ui.text.addEventListener("scroll", () => syncEditorScroll(ui.text, ui.backdrop), { passive:true });
for (const name of ["selectionchange", "select", "click", "keyup"]) {
  ui.text.addEventListener(name, () => {
    if (!selectionFrame) selectionFrame = requestAnimationFrame(() => { selectionFrame = 0; syncFromEditor(); });
  });
}
for (const button of document.querySelectorAll(".editor [data-example]")) button.addEventListener("click", () => {
  ui.text.value = button.dataset.example;
  ui.text.focus();
  ui.text.setSelectionRange(0, 0);
  edit();
});
ui.sentence.addEventListener("change", () => {
  if (!ui.sentence.value || !ui.sentence.validity.valid) { ui.sentence.value = String(selected + 1); return; }
  if (!currentResult()) return;
  const next = snapshot.groups.get(ui.sentence.valueAsNumber - 1)?.tokens[0];
  if (next) placeEditorRange(next.start, next.length);
});
ui.text.setSelectionRange(0, 0);
installCompletion({ editor:ui.text, button:ui["completion-button"], target:ui["completion-target"],
  host:ui["completion-menu"], transport, getRevision:() => lane.current().revision, replace:replaceRange });
ui.text.addEventListener("keydown", event => {
  if (event.ctrlKey && event.code === "Space" && !event.isComposing && !event.altKey && !event.metaKey)
    ui["completion-menu"].closest("details").open = true;
});
ui.workshop.addEventListener("toggle", async function openWorkshop() {
  if (!ui.workshop.open) return;
  ui.workshop.removeEventListener("toggle", openWorkshop);
  try {
    const { initExplorer } = await import("../demo.js");
    invalidateSearch = initExplorer(transport, { text:ui.text, revision:() => lane.current().revision,
      select(start, length) { placeEditorRange(start, length); ui.text.scrollIntoView({block:"center"}); } });
    ui["workshop-tools"].inert = false;
  } catch {
    ui["text-status"].textContent = "Работилницата не се зареди. Отвори я отново.";
    ui.workshop.addEventListener("toggle", openWorkshop);
  }
});
document.querySelector('a[href="#workshop"]').addEventListener("click", () => { ui.workshop.open = true; });
fetch(new URL("../api/v2/", document.baseURI)).then(r => r.ok ? r.json() : null)
  .then(metadata => { if (metadata) transport.configureBudget(metadata.limits?.requestsPerMinutePerIp); })
  .catch(() => {});
edit();

function setConnection(connected, label) {
  online = connected;
  ui.connection.dataset.live = String(online);
  ui.connection.textContent = label;
  lane.connect(connected);
  schedule();
}
if (window.signalR) {
  connection = new signalR.HubConnectionBuilder()
    .withUrl(new URL("../hubs/syntax", document.baseURI).href,
      { transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true })
    .withAutomaticReconnect()
    .build();
  let retryDelay = 1000;
  async function start() {
    try {
      await connection.start();
      retryDelay = 1000;
      setConnection(true, "SignalR · свързан");
    } catch {
      setConnection(false, "SignalR · повторно свързване…");
      setTimeout(start, retryDelay);
      retryDelay = Math.min(retryDelay * 2, 10000);
    }
  }
  connection.onreconnecting(() => setConnection(false, "SignalR · повторно свързване…"));
  connection.onreconnected(() => setConnection(true, "SignalR · свързан"));
  connection.onclose(() => { setConnection(false, "SignalR · прекъснат"); setTimeout(start, retryDelay); });
  void start();
} else {
  setConnection(false, "Клиентът SignalR не се зареди.");
}
