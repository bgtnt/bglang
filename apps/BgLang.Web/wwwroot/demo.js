import {createPostLane} from "/live-client.js";
import {initLiveDemo} from "/live-demo.js";

const postLane = createPostLane();
async function requestApi(operation, payload) {
  const result = await postLane.post(operation, payload);
  if (result.error) throw result.error;
  if (result.stale) throw new Error("Заявката е заменена от по-ново действие.");
  return result;
}
const form = document.querySelector("#search");
const mode = document.querySelector("#mode");
const input = document.querySelector("#input");
const limitField = document.querySelector("#limit");
const limitControls = document.querySelector("#limit-controls");
const submit = document.querySelector("#submit");
const status = document.querySelector("#status");
const results = document.querySelector("#results");
const details = document.querySelector("#details");
const textForm = document.querySelector("#text-search");
const textSubmit = document.querySelector("#text-submit");
let busy = false;
let shownDonorResponse;
let shownDonorRef;

function lazyJson(selector, value) {
  const output = document.querySelector(selector);
  const view = output.closest("details");
  view.open = false;
  output.textContent = "";
  let rendered = false;
  view.ontoggle = () => {
    if (view.open && !rendered) {
      output.textContent = JSON.stringify(value, null, 2);
      rendered = true;
    }
  };
}

function clearDonors() {
  document.querySelector("#donor-viewer").hidden = true;
  document.querySelector("#donor-list").textContent = "";
  shownDonorResponse = undefined;
  shownDonorRef = undefined;
}

function donorSet(response, reference) {
  return ["shared-donors-v1", "shared-analyses-v1"].includes(response.evidenceFormat)
    && Number.isInteger(reference) && reference >= 0
    && Array.isArray(response.donorSets?.[reference]) ? response.donorSets[reference] : null;
}

function tokenAnalysis(token, response) {
  if (response.evidenceFormat !== "shared-analyses-v1") return token.analysis;
  const reference = token.analysisRef;
  if (reference === null) return null;
  if (!Number.isInteger(reference) || reference < 0 || !Array.isArray(response.wordAnalyses)
      || reference >= response.wordAnalyses.length || !response.wordAnalyses[reference])
    throw new Error("Невалидна reference към wordAnalyses в HTTP отговора.");
  return response.wordAnalyses[reference];
}

function showDonors(response, reference, context, inlineDonors) {
  const donors = inlineDonors ?? donorSet(response, reference);
  const donorKey = inlineDonors ?? reference;
  const viewer = document.querySelector("#donor-viewer");
  viewer.hidden = false;
  const label = inlineDonors ? "подкрепящи леми" : `donorSets[${reference}]`;
  document.querySelector("#donor-title").textContent = `Пълен списък: ${label} (${donors?.length ?? "не е върнат"} леми)`;
  document.querySelector("#donor-context").textContent = context;
  if (shownDonorResponse !== response || shownDonorRef !== donorKey) {
    document.querySelector("#donor-list").textContent = donors
      ? donors.join("; ") || "Няма върнати подкрепящи леми в този списък."
      : "Невалидна reference към donorSets; списъкът не може да бъде показан.";
    shownDonorResponse = response;
    shownDonorRef = donorKey;
  }
  viewer.scrollIntoView({ behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth", block: "start" });
}

function setBusy(value) {
  busy = value;
  submit.disabled = value;
  textSubmit.disabled = value;
}

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function action(parent, label, operation, text) {
  const button = element("button", label);
  button.type = "button";
  button.addEventListener("click", () => { if (!busy) search(operation, text); });
  parent.append(button);
}

function card(title, parent = results) {
  const node = element("article", undefined, "card");
  node.append(element("h2", title));
  parent.append(node);
  return node;
}

function limited(word) {
  const labels = { graphSearchIncomplete: "Незавършено графово търсене",
    graphCandidatesTruncated: "Ограничен брой графови резултати",
    candidateSearchLimited: "Ограничено морфологично търсене",
    candidateEvidenceLimited: "Ограничена проверка на морфологичното evidence",
    knownBaseSearchLimited: "Ограничено търсене по известна основа",
    baseTypeSearchLimited: "Ограничено търсене по тип на основата",
    baseTypeEvidenceLimited: "Ограничена проверка на evidence за типа на основата" };
  return Object.keys(labels).filter(flag => word[flag]).map(flag => `${labels[flag]} (${flag})`);
}

function showGraphEvidence(parent, path, derivation, response) {
  const view = element("details");
  view.append(element("summary", "Графов път, правила и evidence"));
  let rendered = false;
  view.addEventListener("toggle", () => {
    if (!view.open || rendered) return;
    rendered = true;
    view.append(element("p", `Състояния: ${path.fromState ?? "не е върнато"} → ${path.toState ?? "не е върнато"}`));
    if (derivation) {
      view.append(element("p", `Лема преди крайното склонение/спрежение: ${path.derivedLemma ?? "не е върната"}. Тя е отделна от коренната основа.`));
      view.append(element("p", "Идентификаторите са отделни върнати списъци; API не дава междинна дума за всяка стъпка.", "tag"));
      for (const [label, ids] of [["Ребра", path.edgeIds], ["Правила", path.ruleIds], ["Приложения", path.applicationIds]])
        view.append(element("p", `${label}: ${(ids || []).join(" → ") || "няма върнати IDs"}`, "tag"));
      if (path.isConditional) {
        const context = `Условна програма: ${path.conditionalRootProgramId ?? "не е върната"}; изискван base tag: ${path.conditionalRootBaseFullTag ?? "не е върнат"}.`;
        view.append(element("p", context, "tag"));
        const reference = path.conditionalSupportingLemmasRef;
        const donors = donorSet(response, reference);
        view.append(element("p", `Подкрепящи леми (${path.conditionalRootSupportCount ?? "брой не е върнат"}); общ списък donorSets[${reference ?? "не е върнат"}] (${donors?.length ?? "не е върнат"} леми).`, "tag"));
        if (donors) {
          const button = element("button", "Покажи пълния списък с подкрепящи леми");
          button.type = "button";
          button.addEventListener("click", () => showDonors(response, reference, `${path.base} → ${path.word}. ${context}`));
          view.append(button);
        } else view.append(element("p", "Няма валидна reference към пълния donor списък в този HTTP отговор.", "warning"));
        for (const requirement of path.unmetExactSelfReadingRequirements || [])
          view.append(element("p", `Неизпълнено exact-self изискване: ${requirement.inputSurface}; ${requirement.fullTag}; ребро ${requirement.edgeId}.`, "warning"));
      }
    } else {
      view.append(element("p", `Нормализирана дума: ${path.normalizedWord}; правило: ${path.ruleId ?? "не е върнато"}; приложение: ${path.applicationId ?? "не е върнато"}.`, "tag"));
      view.append(element("p", `Transparency: ${path.transparency ?? "няма решение"}. ProductiveGeneration: ${path.productiveGeneration === true ? "допуснато" : path.productiveGeneration === false ? "не е допуснато" : "няма решение"}.`, "tag"));
      view.append(element("p", path.hasEvidence
        ? "Това е избрано структурно разлагане; не изброява всички пътища и не е речников прочит."
        : "Не е върнато структурно разлагане; празният отговор не доказва липса.", "tag"));
    }
    for (const segment of path.segments || [])
      view.append(element("p", `Сегмент „${segment.surface}“; canonical „${segment.canonical}“; ${segment.kind}; върнат UTF-16 [${segment.start}, ${segment.start + segment.length}); evidence ${segment.evidenceId ?? "не е върнато"}.`, "tag"));
    view.append(element("p", `Evidence codes: ${(path.evidenceCodes || []).join("; ") || "няма върнати"}; източник: ${path.source}.`, "tag"));
    view.append(element("p", `Confidence: ${path.confidence} — оценка от пакета, не вероятност за езикова правилност.`, "tag"));
  });
  parent.append(view);
}

function analysisSection(parent, title, count) {
  const section = element("section");
  section.setAttribute("aria-label", title);
  section.append(element("h3", `${title} (${count})`));
  parent.append(section);
  return section;
}

function showMorphologyCandidate(parent, candidate, response) {
  const line = element("div", undefined, "card");
  line.append(element("p", `Лема: ${candidate.lemma}`));
  line.append(element("p", `Tag на формата: ${candidate.fullTag}`, "tag"));
  line.append(element("p", `Tag на основата: ${candidate.baseFullTag}`, "tag"));
  line.append(element("p", `Правило: ${candidate.productiveRuleId}; дължина на съвпадащото окончание: ${candidate.matchedEndingLength}.`, "tag"));
  line.append(element("p", `Източник: ${candidate.source}`, "tag"));
  const reference = candidate.supportingLemmasRef;
  const inlineDonors = reference === undefined && Array.isArray(candidate.supportingLemmas)
    ? candidate.supportingLemmas : undefined;
  const donors = inlineDonors ?? donorSet(response, reference);
  line.append(element("p", `Подкрепящи леми: ${donors?.length ?? "списъкът не е върнат"}.`, "tag"));
  if (donors?.length) {
    const button = element("button", "Покажи пълния списък с подкрепящи леми");
    button.type = "button";
    button.addEventListener("click", () => showDonors(response, reference,
      `Лема: ${candidate.lemma}; ${candidate.fullTag}; правило: ${candidate.productiveRuleId}.`, inlineDonors));
    line.append(button);
  } else if (!donors) line.append(element("p", "Няма валиден пълен donor списък в този HTTP отговор.", "warning"));
  const shorter = candidate.shorterEndingCandidates || [];
  if (shorter.length) {
    const view = element("details");
    view.append(element("summary", `Evidence от по-къси окончания (${shorter.length})`));
    let rendered = false;
    view.addEventListener("toggle", () => {
      if (!view.open || rendered) return;
      rendered = true;
      for (const alternative of shorter) showMorphologyCandidate(view, alternative, response);
    });
    line.append(view);
  }
  parent.append(line);
}

function showAnalysis(word, response, parent = results) {
  const flags = limited(word);
  const readings = word.readings || [];
  const node = card(word.word, parent);
  node.append(element("p", "Морфологичните кандидати и графовите пътища са отделни хипотези, а не точни речникови прочити. Празна група не доказва езикова липса."));
  if (flags.length) node.append(element("p", `Ограничено търсене: ${flags.join(", ")}.`, "warning"));
  const exact = analysisSection(node, "Точни прочити в пакета", readings.length);
  if (!readings.length) exact.append(element("p", "Няма точен прочит сред върнатите резултати."));
  // Keep every reading and source tag. A lemma call itself cannot disambiguate homonymous paradigms.
  for (const reading of readings) {
    const line = element("div", undefined, "card");
    line.append(element("p", `Лема: ${reading.lemma}`));
    line.append(element("p", reading.partOfSpeech || "Без пълен tag", "tag"));
    line.append(element("p", `Източник: ${reading.source}`, "tag"));
    action(line, `Форми по лема „${reading.lemma}“`, "generate-forms", reading.lemma);
    exact.append(line);
  }
  for (const [field, title] of [
    ["knownBaseCandidates", "Морфологични кандидати с известна основа (KnownBaseCandidates)"],
    ["candidates", "Морфологични кандидати (Candidates)"],
    ["baseTypeCandidates", "Морфологични кандидати по тип на основата (BaseTypeCandidates)"]]) {
    const candidates = word[field] || [];
    const section = analysisSection(node, title, candidates.length);
    for (const candidate of candidates) showMorphologyCandidate(section, candidate, response);
  }
  const paths = word.graphCandidates || [];
  const graph = analysisSection(node, "Графови хипотези", paths.length);
  for (const candidate of paths) {
    const line = element("div", undefined, "card");
    line.append(element("p", `Граф: ${candidate.base} → ${candidate.word}`));
    line.append(element("p", candidate.isConditional
      ? "Графов път с условна основа; не е точен речников прочит."
      : "Графов път — без означена условна основа; не е точен речников прочит.", candidate.isConditional ? "warning" : undefined));
    line.append(element("p", (candidate.fullTags || []).join("; ") || "Не е върнат пълен morphology tag.", "tag"));
    showGraphEvidence(line, candidate, true, response);
    action(line, "Разлагане", "decompose-word", candidate.word);
    if (!candidate.isConditional) action(line, "Словообразуване от основата", "derive-words", candidate.base);
    else line.append(element("p", "DeriveWords изисква точна основа с tag; условният път не удостоверява такава основа.", "tag"));
    graph.append(line);
  }
  if (word.baseEvidence) {
    node.append(element("p", `Основа от evidence: ${word.baseEvidence.base || "не е установена"}.`));
    showGraphEvidence(node, word.baseEvidence, false, response);
  }
}

function render(operation, response) {
  const data = response.data;
  if (operation === "analyze-word") showAnalysis(data, response);
  else if (operation === "analyze-text") {
    const node = card("Текстов анализ");
    node.append(element("p", `${data.tokens.length} токена; ${data.diagnostics.length} диагностики. Избери дума за оригиналния ѝ анализ. Всички токени, source offsets и evidence са в JSON.`));
    const list = element("ul");
    const selection = element("section");
    for (const token of data.tokens) {
      const word = tokenAnalysis(token, response);
      if (!word) continue;
      const item = element("li");
      const flags = limited(word);
      const morphologyCount = (word.knownBaseCandidates || []).length + (word.candidates || []).length + (word.baseTypeCandidates || []).length;
      const button = element("button", `„${token.text}“ — UTF-16 [${token.start}, ${token.start + token.length}); ${word.readings.length} точни прочита; ${morphologyCount} морфологични кандидата; ${word.graphCandidates.length} графови хипотези${flags.length ? "; ограничена проверка" : ""}`);
      button.type = "button";
      button.addEventListener("click", () => {
        if (busy) return;
        clearDonors();
        selection.replaceChildren();
        card(`„${token.text}“ — UTF-16 [${token.start}, ${token.start + token.length})`, selection);
        showAnalysis(word, response, selection);
        showTokenDetails(token, data.text, response);
      });
      item.append(button);
      list.append(item);
    }
    node.append(list, selection);
  } else if (Array.isArray(data)) {
    const node = card(operation === "generate-forms" ? "Върнати форми" : "Върнати пътища");
    if (!data.length) node.append(element("p", "Няма върнати резултати. Този отговор не удостоверява пълно езиково отсъствие."));
    if (operation === "generate-forms") node.append(element("p", "Това е краен набор словоформи по лема от пакета, до лимита; не е списък на всички възможни новообразувания. Еднаквата лема може да има няколко парадигми. Всички върнати tags и източници се запазват."));
    else node.append(element("p", "Върнати са пътища от ограничено графово търсене. Те са evidence за приложени правила; не са пълен списък на възможните производни думи."));
    for (const row of data) {
      const line = element("div", undefined, "card");
      const surface = row.form || row.word;
      line.append(element("p", surface));
      line.append(element("p", row.tag || (row.fullTags || []).join("; ") || "Без пълен tag", "tag"));
      if (row.base) line.append(element("p", `Основа: ${row.base}${row.isConditional ? " (условна)" : ""}`));
      line.append(element("p", `Източник: ${row.source}`, "tag"));
      if (operation === "derive-words") showGraphEvidence(line, row, true, response);
      action(line, "Анализ на формата", "analyze-word", surface);
      action(line, "Разлагане", "decompose-word", surface);
      node.append(line);
    }
  } else {
    const node = card(data.word);
    node.append(element("p", data.hasEvidence ? `Основа: ${data.base || "не е установена"}` : "Не е върнато segmentation evidence."));
    showGraphEvidence(node, data, false, response);
  }
}

function updateExplorerControls() {
  const hasLimit = !["analyze-text", "decompose-word"].includes(mode.value);
  limitControls.hidden = !hasLimit;
  limitField.disabled = !hasLimit;
  limitField.value = mode.value === "generate-forms" ? "256" : "32";
  input.maxLength = mode.value === "analyze-text" ? 4096 : 256;
}

async function search(operation, text) {
  if (busy) return;
  if (mode.value !== operation) {
    mode.value = operation;
    updateExplorerControls();
  }
  if (!limitField.disabled && !limitField.reportValidity()) return;
  setBusy(true);
  results.replaceChildren();
  clearDonors();
  details.hidden = true;
  lazyJson("#json", null);
  lazyJson("#response-json", null);
  document.querySelector("#response-json-view").hidden = true;
  document.querySelector("#json-context").hidden = true;
  status.textContent = "Изпълнение…";
  input.value = text;
  const request = { text };
  if (!limitField.disabled) request.limit = Number(limitField.value);
  try {
    const {http, response} = await requestApi(operation, request);
    lazyJson("#json", response);
    document.querySelector("#detail-title").textContent = "Действителният HTTP отговор";
    details.hidden = false;
    if (!http.ok) {
      status.textContent = `HTTP ${http.status}: ${response.message || "Операцията не успя."}`;
      document.querySelector("#coverage").textContent = "Няма успешен езиков отговор.";
      document.querySelector("#code").textContent = "";
      return;
    }
    render(operation, response);
    const coverage = response.coverage;
    document.querySelector("#coverage").textContent = coverage.possibleTruncation
      ? `Достигнат е ефективният лимит ${response.limits.effective}. Възможни са пропуснати резултати; пълнотата не е доказана.`
      : coverage.complete === false ? "Kernel evidence показва ограничена или незавършена проверка."
      : "Пълнотата на езиковото търсене не е удостоверена от този public резултат.";
    const methods = { "analyze-word": "AnalyzeWord", "analyze-text": "AnalyzeText",
      "generate-forms": "GenerateForms", "derive-words": "DeriveWords", "decompose-word": "DecomposeWord" };
    const bound = request.limit ? `, ${response.limits.effective}` : "";
    document.querySelector("#code").textContent = `using BgLang.Api;\n\nusing var session = BulgarianLanguageSession.Open("manifest.json");\nvar result = session.${methods[operation]}(${JSON.stringify(text)}${bound});`;
    status.textContent = "Отговорът е получен. Избери прочит или щракни върху форма за нейния анализ.";
  } catch {
    status.textContent = "Няма достъпен HTTP отговор. Провери връзката и readiness на host-а.";
  } finally { setBusy(false); }
}

function evidenceLabel(evidence) {
  const kinds = { StoredReading: "речников прочит", GraphPath: "графов път",
    MorphologyCandidate: "морфологична хипотеза", KnownBaseCandidate: "хипотеза с известна основа",
    BaseTypeCandidate: "хипотеза за лема" };
  return `${evidence.lemma} — ${kinds[evidence.kind]}${evidence.isConditional ? "; условна основа" : ""}${evidence.tags.length ? "; " + evidence.tags.join(" / ") : ""}; източник: ${evidence.source}`;
}

function hitLabels(hit) {
  return [hit.ambiguous ? "алтернативни прочити/хипотези" : "",
    hit.unknown ? "без точен речников прочит" : "",
    hit.conditional ? "има условно evidence" : "",
    hit.incomplete ? "ограничена проверка" : ""].filter(Boolean);
}

function showTokenDetails(token, originalText, response) {
  details.hidden = false;
  document.querySelector("#detail-title").textContent = "Действителният AnalyzeText токен — JSON фрагмент от HTTP отговора";
  lazyJson("#json", token);
  lazyJson("#response-json", response);
  document.querySelector("#response-json-view").hidden = false;
  document.querySelector("#json-context").hidden = false;
  document.querySelector("#json-context").textContent = "Това е оригиналният токен от HTTP отговора. analysisRef сочи към wordAnalyses; donor references сочат към donorSets. Индексите започват от нула и важат само за този отговор. Отвори целия HTTP отговор по-долу за двете таблици и останалия текстов анализ.";
  document.querySelector("#coverage").textContent = limited(tokenAnalysis(token, response)).length
    ? "Този токен има kernel limit flags. Пълнотата на lemma търсенето не е доказана."
    : "Показан е оригиналният токен с неговия evidence; не е извършено контекстно разрешаване.";
  document.querySelector("#code").textContent = `using BgLang.Api;\nusing System.Linq;\n\nusing var session = BulgarianLanguageSession.Open("manifest.json");\nvar text = session.AnalyzeText(${JSON.stringify(originalText)});\nvar token = text.Tokens.Single(t => t.Start == ${token.start} && t.Length == ${token.length});\nvar analysis = token.Analysis;`;
}

function showHit(hit, response) {
  if (busy) return;
  const data = response.data;
  const token = data.textAnalysis.tokens[hit.tokenIndex];
  results.replaceChildren();
  clearDonors();
  const note = card(`„${hit.text}“ — UTF-16 [${hit.start}, ${hit.start + hit.length})`);
  note.append(element("p", hit.exactMatch ? "Точно изписване на заявката." : "Попадение по върната лема."));
  for (const label of hitLabels(hit)) note.append(element("p", label, "warning"));
  for (const match of hit.storedLemmaMatches.concat(hit.evidenceLemmaMatches)) {
    note.append(element("p", `Обща лема: ${match.lemma}`));
    note.append(element("p", "Заявка: " + match.queryEvidence.map(evidenceLabel).join("; "), "tag"));
    note.append(element("p", "Токен: " + match.tokenEvidence.map(evidenceLabel).join("; "), "tag"));
  }
  // Show the actual text-budget analysis, not a fresh AnalyzeWord call with a different budget.
  showAnalysis(tokenAnalysis(token, response), response);
  showTokenDetails(token, data.textAnalysis.text, response);
  results.scrollIntoView({ behavior: "smooth", block: "start" });
}

function markText(target, response, exact) {
  const data = response.data;
  const original = data.textAnalysis.text;
  const selected = data.matches.filter(hit => exact ? hit.exactMatch
    : hit.storedLemmaMatches.length || hit.evidenceLemmaMatches.length);
  target.replaceChildren();
  let position = 0;
  for (const hit of selected) {
    target.append(document.createTextNode(original.slice(position, hit.start)));
    const kind = exact ? "exact-hit" : hit.storedLemmaMatches.length ? "lemma-hit" : "hypothesis-hit";
    const button = element("button", original.slice(hit.start, hit.start + hit.length), "hit " + kind);
    button.type = "button";
    button.title = `UTF-16 [${hit.start}, ${hit.start + hit.length})${hitLabels(hit).length ? ": " + hitLabels(hit).join("; ") : ""}`;
    button.addEventListener("click", () => showHit(hit, response));
    target.append(button);
    position = hit.start + hit.length;
  }
  target.append(document.createTextNode(original.slice(position)));
  return selected.length;
}

async function compareText() {
  if (busy) return;
  setBusy(true);
  const textStatus = document.querySelector("#text-status");
  const comparison = document.querySelector("#text-comparison");
  comparison.hidden = true;
  details.hidden = true;
  results.replaceChildren();
  clearDonors();
  lazyJson("#text-json", null);
  lazyJson("#json", null);
  lazyJson("#response-json", null);
  document.querySelector("#response-json-view").hidden = true;
  document.querySelector("#json-context").hidden = true;
  textStatus.textContent = "Анализ на заявката и текста…";
  try {
    const {http, response} = await requestApi("find-forms-in-text", { text: document.querySelector("#source-text").value, query: document.querySelector("#query").value });
    if (!http.ok) { textStatus.textContent = `HTTP ${http.status}: ${response.message}`; return; }
    const data = response.data;
    comparison.hidden = false;
    lazyJson("#text-json", response);
    const queryEvidence = document.querySelector("#query-evidence");
    queryEvidence.replaceChildren(element("h2", `Върнати интерпретации за „${data.query}“`));
    if (data.queryAmbiguous) queryEvidence.append(element("p", "Има алтернативни прочити/хипотези. Използвани са всички върнати леми; контекстът не избира една от тях.", "warning"));
    if (!data.queryAnalysis.isKnown) queryEvidence.append(element("p", "Заявката няма точен речников прочит. Lemma evidence остава хипотеза.", "warning"));
    if (!data.queryLemmaEvidence.length) queryEvidence.append(element("p", "Не е върната лема за сравнение. Точните изписвания остават видими; няма доказателство за езикова липса."));
    for (const evidence of data.queryLemmaEvidence) queryEvidence.append(element("p", evidenceLabel(evidence), "tag"));
    const exactCount = markText(document.querySelector("#exact-text"), response, true);
    const lemmaCount = markText(document.querySelector("#lemma-text"), response, false);
    document.querySelector("#exact-count").textContent = `Точно изписване: ${exactCount}`;
    document.querySelector("#lemma-count").textContent = `По лема и evidence: ${lemmaCount}`;
    const warnings = ["Пълнотата на lemma търсенето не е доказана."];
    if (response.coverage.possibleTruncation) warnings.push("Достигнат е лимит 32 в query/word analysis; възможни са пропуснати интерпретации.");
    if (response.coverage.complete === false) warnings.push("Kernel flags показват ограничена проверка на поне една дума или на заявката.");
    document.querySelector("#text-coverage").textContent = warnings.join(" ");
    textStatus.textContent = `Сравнението за „${data.query}“ е готово: ${exactCount} точни изписвания; ${lemmaCount} токена с обща върната лема/evidence.`;
  } catch { textStatus.textContent = "Няма достъпен HTTP отговор за сравнението."; }
  finally { setBusy(false); }
}

form.addEventListener("submit", event => { event.preventDefault(); search(mode.value, input.value); });
textForm.addEventListener("submit", event => { event.preventDefault(); compareText(); });
mode.addEventListener("change", updateExplorerControls);
updateExplorerControls();
for (const button of document.querySelectorAll("[data-example]"))
  button.addEventListener("click", () => search(button.dataset.mode, button.dataset.example));

const liveDemo = initLiveDemo({lane:postLane,tokenAnalysis,donorSet,showTokenDetails,search});

fetch("/api/v2/").then(async response => {
  if (!response.ok) throw new Error("metadata unavailable");
  const metadata = await response.json();
  liveDemo.metadata(metadata);
  document.querySelector("#identity").textContent = `API ${metadata.apiVersion}; host ${metadata.versions.host}; public API ${metadata.versions.publicApi}; пакет ${metadata.package.packageId}; manifest SHA-256 ${metadata.package.manifestSha256}.`;
  status.textContent = "Готово за заявка.";
}).catch(() => { status.textContent = "Неуспешно зареждане на идентичността на host-а."; });
