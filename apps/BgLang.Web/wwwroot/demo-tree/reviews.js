import { WARNING_CODES, diagnosticMessage, GRAMMAR_RULE_META } from "../live-client.js";
import { replacementRange, tokenAt } from "./live.js";

const sources = { spelling: "Речников вариант", nominal: "Вариант по именната група",
  grammar: "Граматично правило", diagnostic: "Предложена поправка" };

const qualification = { masculine_article_form_requires_role: "Членът зависи от синтактичната роля.",
  clause_relation_unverified: "Връзката между частите на изречението остава непотвърдена.",
  controller_gender_unmeasured: "Родът на опорната дума остава неустановен." };

// Index once per analysis. Moving the caret never invokes an analyzer.
export function reviewIndex(tree, text) {
  const warnings = new Set(), advised = new Set(), byToken = new Map(), locations = new Map(), punctuation = new Map();
  const key = (start, length) => start + "/" + length;
  const checked = [...tree.diagnostics];
  for (const conflict of tree.conflictWarnings ?? []) {
    if (GRAMMAR_RULE_META[conflict.ruleId] && !checked.some(d => WARNING_CODES.has(d.code) && d.length > 0 &&
        d.start < conflict.start + conflict.length && d.start + d.length > conflict.start)) checked.push(conflict);
  }
  for (const d of checked) {
    const row = { start: d.start, length: d.length, message: diagnosticMessage(d, text) ?? d.message, warning: WARNING_CODES.has(d.code) };
    const position = key(row.start, row.length);
    const existing = locations.get(position);
    if (existing) { existing.message += " · " + row.message; existing.warning ||= row.warning; }
    else locations.set(position, row);
    const first = tokenAt(tree.tokens, d.start, d.length);
    if (d.code === "missing_comma_before_subordinate_conjunction" && first?.kind === "Word" &&
        first.start === d.start && first.length === d.length) {
      advised.add(first.index);
      punctuation.set(first.index, row);
    }
    if (d.code === "spurious_comma_inside_compound_conjunction" && first?.kind === "Punctuation" &&
        first.text === "," && first.start === d.start && first.length === d.length) {
      warnings.add(first.index);
      punctuation.set(first.index, row);
    }
    if (first && row.warning && d.length > 0) {
      for (let i = first.index; i < tree.tokens.length && tree.tokens[i].start < d.start + d.length; i++)
        if (tree.tokens[i].kind === "Word") warnings.add(tree.tokens[i].index);
    }
  }
  for (const variant of tree.suggestions ?? []) {
    const range = replacementRange(text, variant.edits);
    if (!range) continue;
    const targets = new Set(variant.tokenIndices ?? []);
    for (const edit of variant.edits) {
      const token = tokenAt(tree.tokens, edit.start, edit.length);
      if (token) { targets.add(token.index); advised.add(token.index); }
    }
    for (const index of targets) {
      if (!byToken.has(index)) byToken.set(index, []);
      byToken.get(index).push(variant);
    }
    const first = variant.edits[0], position = key(first.start, first.length);
    if (!locations.has(position)) locations.set(position, { start: first.start, length: first.length,
      message: sources[variant.kind] ?? "Вариант за преглед", warning: false });
  }
  return { warnings, advised, byToken, punctuation, locations: [...locations.values()].sort((a,b) => a.start - b.start || a.length - b.length) };
}

function button(text, click) {
  const el = document.createElement("button");
  el.type = "button"; el.textContent = text; el.addEventListener("click", click);
  return el;
}

// Small visible lists keep the input frame cheap even for a document with many suggestions.
function paged(host, rows, empty, make, pageSize) {
  host.replaceChildren();
  if (!rows.length) { host.textContent = empty; return; }
  let shown = 0;
  const more = button("Покажи още", append);
  function append() {
    const fragment = document.createDocumentFragment();
    const end = Math.min(rows.length, shown + pageSize);
    for (; shown < end; shown++) fragment.append(make(rows[shown]));
    more.remove(); host.append(fragment);
    if (shown < rows.length) { more.textContent = "Още (" + (rows.length - shown) + ")"; host.append(more); }
  }
  append();
}

export function renderChecks(host, locations, text, choose) {
  let page = 0, selected;
  const size = 8;
  const overlaps = (row, token) => token && (row.length ?
    row.start < token.start + token.length && row.start + row.length > token.start :
    row.start >= token.start && row.start <= token.start + token.length);
  const visible = new Map();
  function mark() {
    for (const [row, el] of visible) el.setAttribute("aria-current", String(Boolean(overlaps(row, selected))));
  }
  function show() {
    visible.clear();
    if (!locations.length) { host.textContent = "Няма върнати предупреждения или варианти."; return; }
    const fragment = document.createDocumentFragment();
    for (const row of locations.slice(page * size, (page + 1) * size)) {
      const original = text.slice(row.start, row.start + row.length);
      const el = button([original, row.message].filter(Boolean).join(" — "), () => choose(row));
      el.classList.toggle("warning", row.warning);
      el.dataset.start = String(row.start); el.dataset.length = String(row.length);
      visible.set(row,el); fragment.append(el);
    }
    if (locations.length > size) {
      const controls = document.createElement("div"); controls.className = "check-pages";
      const back = button("Предишни", () => { page--; show(); });
      const next = button("Следващи", () => { page++; show(); });
      back.disabled = page === 0; next.disabled = (page + 1) * size >= locations.length;
      controls.append(back, " " + (page + 1) + " / " + Math.ceil(locations.length / size) + " ", next);
      fragment.append(controls);
    }
    host.replaceChildren(fragment); mark();
  }
  show();
  return token => {
    if (selected === token) return;
    selected = token;
    const row = token && locations.findIndex(item => overlaps(item, token));
    if (Number.isInteger(row) && row >= 0 && Math.floor(row / size) !== page) { page = Math.floor(row / size); show(); }
    else mark();
  };
}

export function renderVariants(host, variants, apply) {
  // Equal edits from two analyzers keep both sources in a single actionable row.
  const distinct = new Map();
  for (const v of variants) {
    const key = JSON.stringify(v.edits.map(e => [e.start, e.length, e.replacement]));
    let label = GRAMMAR_RULE_META[v.ruleId]?.title ?? sources[v.kind] ?? "Вариант за преглед";
    if (v.assessment === "Supported") label += " с контекстна опора";
    else if (v.kind === "nominal" || v.assessment === "NoCompatiblePair") label += " без потвърдено съгласуване";
    if (!distinct.has(key)) distinct.set(key, { variant: v, sources: new Set(), conditional: true, conditions: new Set() });
    const row = distinct.get(key);
    row.sources.add(label); row.conditional &&= v.conditional;
    for (const q of v.qualifications ?? []) row.conditions.add(q);
  }
  paged(host, [...distinct.values()], "", ({ variant, sources: group, conditional, conditions }) => {
    const row = document.createElement("div"); row.className = "variant";
    const preview = document.createElement("strong");
    preview.textContent = variant.edits.map(e => e.original + " → " + e.replacement).join(" · ");
    const basis = document.createElement("small");
    basis.textContent = [...group].join(" · ") + (conditional ? " · за преценка" : "");
    if (conditions.size) basis.textContent += " · " + [...conditions].map(q => qualification[q] ?? q).join(" ");
    row.append(preview, basis, button("Приложи", () => apply(variant)));
    return row;
  }, 3);
}
