import { replaceableSpan } from "../live-client.js";

// All offsets are UTF-16 offsets in this exact revision. Text repetitions have distinct indices.
export function tokenAt(tokens, start, length = 0) {
  let lo = 0, hi = tokens.length;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (tokens[mid].start + tokens[mid].length <= start) lo = mid + 1;
    else hi = mid;
  }
  const inside = tokens[lo];
  if (inside && inside.kind !== "Whitespace" && (length ? inside.start < start + length : inside.start <= start)) return inside;
  if (length) {
    for (let i = lo; i < tokens.length && tokens[i].start < start + length; i++)
      if (tokens[i].kind !== "Whitespace") return tokens[i];
  } else if (lo && tokens[lo - 1].start + tokens[lo - 1].length === start && tokens[lo - 1].kind !== "Whitespace")
    return tokens[lo - 1];
  return null;
}

// Validate the entire variant before constructing one contiguous, atomic textarea edit.
export function replacementRange(text, edits) {
  if (!Array.isArray(edits) || !edits.length) return null;
  const ordered = [...edits].sort((a, b) => a.start - b.start);
  const start = ordered[0].start;
  let end = start;
  let replacement = "";
  for (const e of ordered) {
    if (!replaceableSpan(text, e.start, e.length) || e.start < end || typeof e.replacement !== "string" ||
        text.slice(e.start, e.start + e.length) !== e.original) return null;
    replacement += text.slice(end, e.start) + e.replacement;
    end = e.start + e.length;
  }
  return replacement === text.slice(start, end) ? null : { start, end, replacement };
}

// One invocation can be running. Edits replace its successor, including while reconnecting.
export function createAnalysisLane(invoke, accept, fail) {
  let current = { revision: 0, text: "" };
  let online = false;
  let active;
  let sent = -1;
  async function pump() {
    if (!online || active || !current.text.trim() || current.composing || current.revision === sent) return;
    const job = current;
    active = job;
    sent = job.revision;
    try {
      const result = await invoke(job.revision, job.text);
      if (active === job && current === job && result.revision === job.revision) accept(job, result);
    } catch (error) {
      if (active === job && current === job) fail(job, error);
    } finally {
      if (active === job) { active = undefined; void pump(); }
    }
  }
  return {
    edit(text, composing = false) {
      if (text !== current.text || composing !== current.composing) {
        current = { revision: current.revision + 1, text, composing };
        void pump();
      }
      return current;
    },
    connect(value) {
      if (online === value) return;
      online = value;
      active = undefined;
      sent = -1;
      if (online) {
        current = { ...current }; // Invalidate completions from the previous connection.
        void pump();
      }
    },
    current: () => current
  };
}

// Reuse the original indices only for display elements completely outside the edit.
// During this frame every retained arc is explicitly marked as previous evidence.
export function draftSentence(previous, text, caret) {
  const old = previous?.text ?? "";
  let left = 0;
  let right = 0;
  while (left < old.length && left < text.length && old[left] === text[left]) left++;
  while (right < old.length - left && right < text.length - left &&
    old[old.length - right - 1] === text[text.length - right - 1]) right++;
  const delta = text.length - old.length;
  const oldStart = pos => pos <= left ? pos : pos >= text.length - right ? pos - delta : left;
  const cursor = oldStart(caret);
  const row = previous?.tree.sentences.findLast(s => s.start <= cursor) ?? previous?.tree.sentences[0];
  let start = 0;
  let end = text.length;
  if (row) {
    const finish = row.start + row.length;
    start = row.start <= left ? row.start : row.start >= old.length - right ? row.start + delta : left;
    end = finish < left ? finish : finish >= old.length - right ? finish + delta : text.length - right;
    start = Math.min(start, caret);
    end = Math.max(end, caret);
  }
  const oldTokens = new Map();
  for (const token of (row && previous.groups.get(row.index)?.tokens) || []) {
    if (token.start + token.length <= left) oldTokens.set(token.start, token);
    else if (token.start >= old.length - right) oldTokens.set(token.start + delta, token);
  }
  const tokens = [];
  const retained = new Set();
  for (const match of text.slice(start, end).matchAll(/[\p{L}\p{M}\p{N}]+|[^\s]/gu)) {
    const at = start + match.index;
    const original = oldTokens.get(at);
    if (original && original.text === match[0]) retained.add(original.index);
    tokens.push({ index: original?.text === match[0] ? original.index : -tokens.length - 1,
      text: match[0], start: at, length: match[0].length, kind: original?.kind ?? "Word", status: "pending", roles: [] });
  }
  const edges = ((row && previous.groups.get(row.index)?.edges) || [])
    .filter(edge => retained.has(edge.from) && retained.has(edge.to))
    .map(edge => ({ ...edge, status: "unassessed" }));
  return { tokens, edges };
}

export function groupTree(tree) {
  const groups = new Map(tree.sentences.map(s => [s.index, { tokens: [], edges: [] }]));
  const byToken = new Map();
  for (const token of tree.tokens) {
    if (token.kind === "Whitespace") continue;
    const group = groups.get(token.sentence);
    if (group) { group.tokens.push(token); byToken.set(token.index, group); }
  }
  for (const edge of tree.edges) {
    const group = byToken.get(edge.from);
    if (group && group === byToken.get(edge.to)) group.edges.push(edge);
  }
  // A single-target role can cite other tokens as evidence. These dotted lines
  // name evidence for that role; they do not assign a syntactic governor.
  for (const group of groups.values()) {
    const targetCount = new Map();
    for (const token of group.tokens) {
      for (const role of token.roles ?? []) {
        if (role.source === "syntax") targetCount.set(role.alternative, (targetCount.get(role.alternative) ?? 0) + 1);
      }
    }
    group.linkCount = new Set(group.edges.map(e => [e.from, e.to, e.label].join("/"))).size;
    for (const token of group.tokens) {
      for (const role of token.roles ?? []) {
        if (role.source !== "syntax" || targetCount.get(role.alternative) !== 1) continue;
        for (const from of role.contextTokenIndices ?? []) {
          if (from !== token.index && byToken.get(from) === group)
            group.edges.push({ from, to: token.index, label: "role_support_" + role.role, status: "evidence",
              source: role.source, ruleId: role.ruleId, alternative: role.alternative });
        }
      }
    }
  }
  return groups;
}
