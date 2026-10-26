// One shared HTTP lane; budgets come from this host, never from a language guess.

export function createPostLane({
  fetchImpl = globalThis.fetch,
  now = () => performance.now(),
  wallNow = () => Date.now(),
  setTimer = setTimeout,
  clearTimer = clearTimeout,
  requestsPerMinute = 30
} = {}) {
  const pending = new Map();
  let active = false,
    timer = null,
    lastStart = -Infinity,
    cooldown = 0,
    interval;

  function configureBudget(value) {
    interval = 60000 / (Number.isInteger(value) && value > 0 ? value : 30) + 25;
    if (timer !== null) {
      clearTimer(timer);
      timer = null;
    }
    pump();
  }

  function emit(job, state, extra = {}) {
    if (job.current()) job.state?.(state, extra);
  }

  function cancel(key) {
    const old = pending.get(key);
    if (old) {
      pending.delete(key);
      old.resolve({
        stale: true
      });
    }
  }

  async function pump() {
    if (active || timer !== null || !pending.size) return;
    const wait = Math.max(lastStart + interval, cooldown) - now();
    if (wait > 0) {
      timer = setTimer(() => {
        timer = null;
        pump();
      }, wait);
      return;
    }
    const [key, job] = pending.entries().next().value;
    pending.delete(key);
    if (!job.current()) {
      job.resolve({
        stale: true
      });
      pump();
      return;
    }
    active = true;
    lastStart = now();
    emit(job, 'working');
    try {
      const http = await fetchImpl('/api/v2/' + job.operation, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(job.payload)
      });
      const header = http.headers?.get('Retry-After');
      let retryAfterMs = 0;
      if (header !== null && header !== undefined) {
        const seconds = Number(header);
        retryAfterMs = /^\d+(\.\d+)?$/.test(header.trim()) ? seconds * 1000 : Math.max(0, Date.parse(header) - wallNow());
        if (!Number.isFinite(retryAfterMs)) retryAfterMs = 0;
      }
      if (http.status === 429 || http.status === 503) retryAfterMs = Math.max(retryAfterMs, interval);
      // Even a stale response owns the global transport cooldown.
      cooldown = Math.max(cooldown, now() + retryAfterMs);
      const response = await http.json();
      const elapsedMs = now() - lastStart;
      const stale = !job.current();
      emit(job, http.ok ? 'ready' : 'error', {
        httpStatus: http.status,
        retryAfterMs
      });
      job.resolve({
        http,
        response,
        elapsedMs,
        retryAfterMs,
        stale
      });
    } catch (error) {
      emit(job, 'error');
      job.resolve({
        error,
        stale: !job.current()
      });
    } finally {
      active = false;
      pump();
    }
  }
  configureBudget(requestsPerMinute);
  return {
    configureBudget,
    cancel,
    post(operation, payload, {
      key = 'manual',
      current = () => true,
      state
    } = {}) {
      cancel(key);
      return new Promise(resolve => {
        const job = {
          operation,
          payload,
          current,
          state,
          resolve
        };
        pending.set(key, job);
        emit(job, 'waiting');
        pump();
      });
    }
  };
}

export function livePayload(text, heuristics = true, continuation = null) {
  return {
    text,
    evidenceFormat: 'shared-analyses-v1',
    includeUnknownContextProperNames: heuristics,
    context: {
      includeAdditionalRoleHeuristics: heuristics,
      includeSubordinateCommaDiagnostics: heuristics,
      includeFragmentDiagnostics: heuristics,
      includeSpacingDiagnostics: heuristics,
      includeWordConstructions: heuristics,
      ...(continuation ? {continuation: {caret: continuation.caret, limit: continuation.limit,
        ...(continuation.target == null ? {} : {target: continuation.target})}} : {})
    }
  };
}

export function createLiveController({
  lane,
  commit,
  invalidate,
  state,
  setTimer = setTimeout,
  clearTimer = clearTimeout,
  debounceMs = 150,
  maxWaitMs = 600,
  readSelection = null,
  readContinuationTarget = null,
  continuationLimit = 12
}) {
  let revision = 0,
    text = '',
    heuristics = true,
    composing = false,
    enabled = true,
    timer = null,
    maxTimer = null,
    lastSelection = null,
    lastTarget = null;

  const selection = () => {
    const value = readSelection?.();
    return value ? {start: value.start, end: value.end} : null;
  };
  const sameSelection = (a, b) => a === null ? b === null : b !== null && a.start === b.start && a.end === b.end;
  const target = () => readContinuationTarget?.() ?? null;

  function clear() {
    if (timer !== null) clearTimer(timer);
    if (maxTimer !== null) clearTimer(maxTimer);
    timer = null;
    maxTimer = null;
    lane.cancel('live');
  }

  function send(force = false) {
    clear();
    if ((!enabled && !force) || composing || !text.trim() || text.length > 4096) return;
    const expected = revision, capturedSelection = selection(), capturedTarget = target();
    const snapshot = capturedSelection ? continuationSnapshot(text, capturedSelection.start, capturedSelection.end, expected, capturedTarget) : null;
    lastSelection = capturedSelection;
    lastTarget = capturedTarget;
    const current = () => revision === expected && !composing && (enabled || force)
      && sameSelection(capturedSelection, selection()) && capturedTarget === target();
    lane.post('analyze-text', livePayload(text, heuristics, snapshot ? {caret: snapshot.caret, limit: continuationLimit,
      ...(capturedTarget === null ? {} : {target: capturedTarget})} : null), {
      key: 'live',
      current,
      state
    }).then(result => {
      if (current() && !result.stale) commit(result, expected, snapshot);
    });
  }

  function edit(value, options = heuristics) {
    text = value;
    heuristics = options;
    lastSelection = selection();
    lastTarget = target();
    revision++;
    if (timer !== null) clearTimer(timer);
    timer = null;
    lane.cancel('live');
    invalidate(text, revision);
    if (!text.trim()) {
      clear();
      state('empty');
      return;
    }
    if (text.length > 4096) {
      clear();
      state('oversize');
      return;
    }
    if (composing) {
      clear();
      state('composing');
      return;
    }
    if (!enabled) {
      clear();
      state('paused');
      return;
    }
    state('waiting');
    if (maxTimer === null) maxTimer = setTimer(() => send(), maxWaitMs);
    timer = setTimer(() => send(), debounceMs);
  }
  return {
    edit,
    refreshSelection() {
      if (!sameSelection(lastSelection, selection())) edit(text);
    },
    refreshTarget() {
      if (lastTarget !== target()) edit(text);
    },
    flush() {
      clear();
      send(true);
    },
    compositionStart() {
      composing = true;
      edit(text);
    },
    compositionEnd(value) {
      composing = false;
      edit(value);
    },
    setEnabled(value) {
      enabled = value;
      edit(text);
    },
    get revision() {
      return revision;
    },
    get text() {
      return text;
    }
  };
}

export function completeReadings(data, tokenIndex) {
  const inventory = data.readingInventory;
  const occurrence = inventory?.occurrences.find(row => row.tokenIndex === tokenIndex);
  const set = occurrence === undefined ? null : inventory.sets[occurrence.readingSetIndex];
  return set ? set.supportReadings ?? set.canonicalReadings : null;
}

export function caretWord(tokens, caret) {
  return tokens.findIndex(token => token.kind === 'Word' && token.start <= caret && caret <= token.start + token.length);
}

export function completionSpan(text, caret, revision) {
  if (!Number.isInteger(caret) || caret < 0 || caret > text.length) return null;
  const wordPart = character => /[\p{L}\p{M}-]/u.test(character);
  let start = caret,
    end = caret;
  for (const char of Array.from(text.slice(0, caret)).reverse()) {
    if (!wordPart(char)) break;
    start -= char.length;
  }
  for (const char of Array.from(text.slice(caret))) {
    if (!wordPart(char)) break;
    end += char.length;
  }
  return start < caret ? {
    text,
    caret,
    start,
    end,
    prefix: text.slice(start, caret),
    revision
  } : null;
}

export function applyCompletion(text, caret, revision, snapshot, form) {
  if (!snapshot || snapshot.text !== text || snapshot.caret !== caret || snapshot.revision !== revision || typeof form !== 'string') return null;
  return {
    text: text.slice(0, snapshot.start) + form + text.slice(snapshot.end),
    caret: snapshot.start + form.length
  };
}

export function renderTokenText(target, text, tokens, classify, select) {
  const doc = target.ownerDocument,
    fragment = doc.createDocumentFragment();
  let cursor = 0;
  tokens.forEach((token, index) => {
    const end = token.start + token.length;
    if (!Number.isInteger(token.start) || !Number.isInteger(token.length) || token.start < cursor || token.length < 0
      || end > text.length) throw new Error('Невалидни позиции в текстовия отговор.');
    fragment.append(doc.createTextNode(text.slice(cursor, token.start)));
    const raw = text.slice(token.start, end);
    if (token.kind === 'Word' && token.length) {
      const button = doc.createElement('button');
      button.type = 'button';
      button.className = 'live-token ' + classify(index);
      button.textContent = raw;
      button.dataset.tokenIndex = String(index);
      button.setAttribute('aria-pressed', 'false');
      button.addEventListener('click', () => select(index));
      fragment.append(button);
    } else fragment.append(doc.createTextNode(raw));
    cursor = end;
  });
  fragment.append(doc.createTextNode(text.slice(cursor)));
  target.replaceChildren(fragment);
}

// Contextual continuations keep the raw editor snapshot. No normalized offsets are applied.
export function continuationSnapshot(text, selectionStart, selectionEnd, revision, requestTarget = null) {
  if (typeof text !== 'string' || !Number.isInteger(revision) || revision < 0
    || !Number.isInteger(selectionStart) || selectionStart !== selectionEnd
    || selectionStart < 0 || selectionStart > text.length) return null;
  if (requestTarget !== null && !['Finite', 'Noun', 'Auto'].includes(requestTarget)) return null;
  // A UTF-16 boundary is insufficient inside a surrogate pair or combining/emoji cluster.
  if (typeof Intl.Segmenter !== 'function') return null;
  const boundaries = new Set([text.length]);
  for (const item of new Intl.Segmenter('bg', {granularity: 'grapheme'}).segment(text)) boundaries.add(item.index);
  if (!boundaries.has(selectionStart)) return null;
  const word = completionSpan(text, selectionStart, revision);
  const snapshot = word ?? {text, caret: selectionStart, start: selectionStart, end: selectionStart, prefix: '', revision};
  if (!boundaries.has(snapshot.start) || !boundaries.has(snapshot.end)) return null;
  return Object.freeze(requestTarget === null ? snapshot : {...snapshot, requestTarget});
}

export function bindContinuation(snapshot, evidence) {
  const actualTarget = evidence?.target ?? 'Finite';
  const requestedTarget = snapshot?.requestTarget ?? 'Finite';
  const noun = actualTarget === 'Noun';
  if (!snapshot || !['Finite', 'Noun'].includes(actualTarget)
    || (requestedTarget !== 'Auto' && requestedTarget !== actualTarget)
    || evidence?.version !== (noun ? 'nominal_continuation.v1' : 'finite_continuation.v1')
    || evidence.available !== true
    || evidence.candidateSource !== (noun ? 'exact_package_nominal_vocabulary' : 'exact_package_finite_vocabulary')
    || evidence.ranking !== 'dictionary_score'
    || evidence.caret !== snapshot.caret || evidence.start !== snapshot.start
    || !Number.isInteger(evidence.length) || evidence.length !== snapshot.end - snapshot.start
    || evidence.prefix !== snapshot.prefix || !Array.isArray(evidence.suggestions)) return null;
  const expectations = noun ? evidence.nominalExpectations : evidence.expectations;
  if (!Array.isArray(expectations) || !expectations.length
    || (noun ? !Array.isArray(evidence.expectations) || evidence.expectations.length !== 0
      : evidence.nominalExpectations != null)) return null;
  for (const row of evidence.suggestions) {
    if (typeof row.form !== 'string' || !row.form.length || !Array.isArray(row.readings)
      || !Array.isArray(row.matches) || !row.matches.length) return null;
    for (const match of row.matches) {
      if (!Number.isInteger(match.readingIndex) || match.readingIndex < 0 || match.readingIndex >= row.readings.length
        || !Array.isArray(match.expectationIndices) || !match.expectationIndices.length
        || match.expectationIndices.some(index => !Number.isInteger(index) || index < 0 || index >= expectations.length)) return null;
    }
  }
  const forms = evidence.suggestions.map(row => row.form);
  return Object.freeze({...snapshot, forms: Object.freeze(forms)});
}

export function applyContinuation(text, selectionStart, selectionEnd, revision, snapshot, form, composing = false, currentTarget = null) {
  if (composing || !snapshot || !snapshot.forms?.includes(form) || (snapshot.requestTarget ?? null) !== currentTarget) return null;
  const current = continuationSnapshot(text, selectionStart, selectionEnd, revision, currentTarget);
  if (!current || current.text !== snapshot.text || current.caret !== snapshot.caret || current.revision !== snapshot.revision
    || current.start !== snapshot.start || current.end !== snapshot.end || current.prefix !== snapshot.prefix) return null;
  return {text: text.slice(0, snapshot.start) + form + text.slice(snapshot.end), caret: snapshot.start + form.length};
}
