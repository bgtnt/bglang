// One shared HTTP lane; budgets come from this host, never from a language guess.
export const MAX_TEXT_LENGTH = 16384;

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
      includeNominalConstructions: heuristics,
      includeGrammarEvidence: heuristics,
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
    if ((!enabled && !force) || composing || !text.trim() || text.length > MAX_TEXT_LENGTH) return;
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

  function edit(value, options = heuristics, reason = 'text') {
    text = value;
    heuristics = options;
    lastSelection = selection();
    lastTarget = target();
    revision++;
    if (timer !== null) clearTimer(timer);
    timer = null;
    lane.cancel('live');
    invalidate(text, revision, {reason, heuristics, target: lastTarget});
    if (!text.trim()) {
      clear();
      state('empty');
      return;
    }
    if (text.length > MAX_TEXT_LENGTH) {
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
      if (!sameSelection(lastSelection, selection())) edit(text, heuristics, 'selection');
    },
    refreshTarget() {
      if (lastTarget !== target()) edit(text, heuristics, 'target');
    },
    flush() {
      clear();
      send(true);
    },
    compositionStart() {
      composing = true;
      edit(text, heuristics, 'composition');
    },
    compositionEnd(value) {
      composing = false;
      edit(value, heuristics, 'composition');
    },
    setEnabled(value) {
      enabled = value;
      edit(text, heuristics, 'enabled');
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

// As in /demo: no replacement whose span would split a surrogate pair or detach combining marks.
export function replaceableSpan(text, start, length) {
  const end = start + length;
  if (!Number.isInteger(start) || !Number.isInteger(length) || start < 0 || length <= 0 || end > text.length) return false;
  const splits = offset => offset > 0 && offset < text.length && /[\uD800-\uDBFF]/.test(text[offset - 1]) && /[\uDC00-\uDFFF]/.test(text[offset]);
  return !splits(start) && !splits(end) && !/^\p{M}/u.test(text.slice(start, start + 2)) && !/^\p{M}/u.test(text.slice(end, end + 2));
}


export const GRAMMAR_RULE_META = {
  'local_exact_attribute_agreement.v1': {
    code: 'R1', title: 'Съгласуване в именната група',
    controllerRole: 'опорното съществително', dependentRole: 'определението',
    mismatchVerb: 'не е съгласувано с'
  },
  'grammar.article.first_prepositional_nominal_member.v1': {
    code: 'R2', title: 'Място на определителния член в именната група',
    controllerRole: 'предлога', dependentRole: 'именната група'
  },
  'grammar.article.first_initial_nominal_member.v1': {
    code: 'R2', title: 'Място на определителния член в именната група',
    controllerRole: 'опорното съществително', dependentRole: 'именната група'
  },
  'clause_fusion_agreement.v1': {
    code: 'R3', title: 'Съгласуване на групата на подлога и групата на сказуемото',
    controllerRole: 'групата на подлога', dependentRole: 'групата на сказуемото'
  },
  'grammar.agreement.copular_finite.v1': {
    code: 'R3', title: 'Съгласуване на групата на подлога и групата на сказуемото',
    controllerRole: 'групата на подлога', dependentRole: 'глаголната връзка'
  },
  'grammar.agreement.analytic_finite.v1': {
    code: 'R3', title: 'Съгласуване на групата на подлога и групата на сказуемото',
    controllerRole: 'групата на подлога', dependentRole: 'спомагателния глагол'
  },
  'grammar.agreement.copular_adjective.v1': {
    code: 'R4', title: 'Съгласуване на сказуемното определение с групата на подлога',
    controllerRole: 'групата на подлога', dependentRole: 'сказуемното прилагателно'
  },
  'grammar.agreement.passive_participle.v1': {
    code: 'R4', title: 'Съгласуване на страдателното причастие с групата на подлога',
    controllerRole: 'групата на подлога', dependentRole: 'страдателното причастие'
  },
  'grammar.agreement.active_participle_controller.v1': {
    code: 'R4', title: 'Съгласуване на деятелното причастие с групата на подлога',
    controllerRole: 'групата на подлога', dependentRole: 'причастието на -л'
  },
  'grammar.agreement.auxiliary_active_participle.v1': {
    code: 'R5', title: 'Съгласуване в сложната глаголна група',
    controllerRole: 'спомагателният глагол', dependentRole: 'причастието на -л',
    mismatchVerb: 'не е съгласуван с'
  },
  'grammar.full_article.clause_subject.v1': {
    code: 'R6', title: 'Пълен член на групата на подлога',
    controllerRole: 'групата на сказуемото', dependentRole: 'групата на подлога'
  },
  'grammar.short_article.exact_prepositional_group.v1': {
    code: 'R7', title: 'Кратък член на не-подлог (предложна група или допълнение)',
    controllerRole: 'управляващата дума', dependentRole: 'именната група'
  }
};

const SUBJECT_PREDICATE_LANE_ALIASES = new Set([
  'closed_exact_agreement.v1',
  'pronoun_verb_agreement',
  'licensed_personal_pronoun_prefix_agreement.v1',
  'licensed_subordinate_pronoun_prefix_agreement.v1',
  'local_finite_clause_roles.v1',
  'clause_fusion_agreement.v1'
]);

const SUBJECT_PREDICATE_GRAMMAR_RULES = new Set([
  'clause_fusion_agreement.v1',
  'grammar.agreement.copular_finite.v1',
  'grammar.agreement.analytic_finite.v1'
]);

const GRAMMAR_LABELS = {Singular:'ед. ч.', Plural:'мн. ч.', Count:'бройна форма', Masculine:'м. р.', Feminine:'ж. р.', Neuter:'ср. р.',
  First:'1 л.', Second:'2 л.', Third:'3 л.', Indefinite:'нечленувано', Definite:'членувано',
  Full:'пълен член', Short:'кратък член'};

function bindGrammarEnv(data) {
  const grammar = data?.grammar;
  if (typeof data?.text !== 'string' || !Array.isArray(data.tokens) ||
      !Array.isArray(grammar?.rules) || !Array.isArray(grammar.matches)) return null;
  const rules = new Map(grammar.rules.filter(rule => typeof rule?.id === 'string').map(rule => [rule.id, rule]));
  const occurrences = new Map();
  const inventory = data.readingInventory;
  for (const occurrence of Array.isArray(inventory?.occurrences) ? inventory.occurrences : []) {
    if (!occurrence) continue;
    const index = occurrence.tokenIndex;
    occurrences.set(index, occurrences.has(index) ? null : occurrence);
  }
  function sourceWord(index) {
    if (!Number.isInteger(index) || index < 0) return null;
    const token = data.tokens[index];
    return token?.kind === 'Word' && replaceableSpan(data.text, token.start, token.length) &&
      token.text === data.text.slice(token.start, token.start + token.length) ? token : null;
  }
  function readingFeatures(ref, allowed) {
    if (!allowed.has(ref?.tokenIndex) || !Number.isInteger(ref.completeReadingIndex) || ref.completeReadingIndex < 0) return null;
    const token = sourceWord(ref.tokenIndex), occurrence = occurrences.get(ref.tokenIndex);
    if (!token || !occurrence || occurrence.start !== token.start || occurrence.length !== token.length ||
        occurrence.text !== token.text || !Number.isInteger(occurrence.readingSetIndex) || occurrence.readingSetIndex < 0 ||
        !Array.isArray(inventory.sets)) return null;
    const set = inventory.sets[occurrence.readingSetIndex], rows = set?.supportReadings ?? set?.canonicalReadings;
    const features = Array.isArray(rows) ? rows[ref.completeReadingIndex]?.features : null;
    return features && typeof features === 'object' && !Array.isArray(features) ? features : null;
  }
  function boundConflicts(match) {
    if (!Array.isArray(match.controllerTokenIndices) || !match.controllerTokenIndices.length ||
        !Array.isArray(match.dependentTokenIndices) || !match.dependentTokenIndices.length ||
        !Array.isArray(match.conflicts) || !match.conflicts.length) return null;
    const controllers = new Set(match.controllerTokenIndices), dependents = new Set(match.dependentTokenIndices);
    if ([...controllers].some(index => !sourceWord(index)) || [...dependents].some(index => {
      const token = sourceWord(index);
      return !token || token.start < match.start || token.start + token.length > match.start + match.length;
    })) return null;
    const result = [];
    for (const conflict of match.conflicts) {
      if (!dependents.has(conflict?.tokenIndex) || !Array.isArray(conflict.actual) || !conflict.actual.length ||
          !Array.isArray(conflict.expectedFrom) || !conflict.expectedFrom.length) return null;
      const target = new Set([conflict.tokenIndex]);
      const actual = conflict.actual.map(ref => readingFeatures(ref, target));
      const expected = conflict.expectedFrom.map(ref => readingFeatures(ref, controllers));
      if (actual.includes(null) || expected.includes(null)) return null;
      result.push({tokenIndex: conflict.tokenIndex, actual, expected});
    }
    return result;
  }
  function features(readings, constraint, matchReason = null, isExpected = false) {
    if (isExpected && constraint === 'PersonNumber' && matchReason === 'coordinated_subject_clause_mismatch') {
      return '3 л. · мн. ч.';
    }
    return [...new Set(readings.map(value => {
      const articleLabel = key => constraint === 'MasculineArticle' && key === 'Definite' ? 'кратък член' : GRAMMAR_LABELS[key];
      const fields = constraint === 'ArticlePlacement' || constraint === 'MasculineArticle' ? [value?.article] :
        constraint === 'PersonNumber' ? [value?.person ?? (value?.partOfSpeech && value.partOfSpeech !== 'Verb' ? 'Third' : null), value?.number] :
        [value?.number, value?.gender];
      return fields.map(key => constraint === 'ArticlePlacement' || constraint === 'MasculineArticle' ? articleLabel(key) : GRAMMAR_LABELS[key])
        .filter(Boolean).join(' · ');
    }).filter(Boolean))].join(' / ');
  }
  function groupSlice(indices) {
    if (!Array.isArray(indices) || !indices.length) return null;
    const tokens = indices.map(sourceWord);
    if (tokens.some(token => !token)) return null;
    const start = Math.min(...tokens.map(token => token.start));
    const end = Math.max(...tokens.map(token => token.start + token.length));
    return replaceableSpan(data.text, start, end - start) ? data.text.slice(start, end) : null;
  }
  return {grammar, rules, sourceWord, boundConflicts, features, groupSlice};
}

export function describeGrammarViolation(data, records, start, length) {
  const env = bindGrammarEnv(data);
  if (!env || !Number.isInteger(start) || !Number.isInteger(length)) return null;
  const recordRules = new Set((Array.isArray(records) ? records : []).map(item => item?.record?.ruleId).filter(Boolean));
  const recordCodes = new Set((Array.isArray(records) ? records : []).map(item => item?.record?.code).filter(Boolean));
  const hasSubjectPredicateAlias = [...recordRules].some(id => SUBJECT_PREDICATE_LANE_ALIASES.has(id))
    || (!recordRules.size && (recordCodes.has('pronoun_verb_agreement_mismatch') || recordCodes.has('productive_morphology_agreement_mismatch')));

  function findExplanation(allowAlias) {
    for (const match of env.grammar.matches) {
      const rule = env.rules.get(match?.ruleId);
      if (!rule || match.assessment !== 'Mismatch' || !replaceableSpan(data.text, match.start, match.length)) continue;
      if (allowAlias) {
        if (!SUBJECT_PREDICATE_GRAMMAR_RULES.has(rule.id) &&
            !(recordRules.has('clause_fusion_agreement.v1') && recordCodes.has(rule.diagnosticCode))) continue;
      } else {
        if (recordRules.size && !recordRules.has(rule.id)) continue;
        if (!recordRules.size && recordCodes.size && !recordCodes.has(rule.diagnosticCode)) continue;
      }
      const conflicts = env.boundConflicts(match);
      if (!conflicts) continue;
      const targetConflict = conflicts.find(conflict => {
        const token = env.sourceWord(conflict.tokenIndex);
        return token && token.start === start && token.length === length;
      });
      if (!targetConflict) continue;
      const meta = GRAMMAR_RULE_META[rule.id];
      const controllerText = env.groupSlice(match.controllerTokenIndices);
      const dependentText = env.groupSlice(match.dependentTokenIndices);
      if (!meta || !controllerText || !dependentText) continue;
      const actual = env.features(targetConflict.actual, rule.constraint, match.reason, false);
      const expected = env.features(targetConflict.expected, rule.constraint, match.reason, true);
      const header = 'Противоречи на правило ' + meta.code + ' (' + meta.title + '): ';
      if (rule.id === 'grammar.full_article.clause_subject.v1') {
        const repl = match.repairs?.[0]?.edits?.[0]?.replacement;
        return header + meta.dependentRole + ' „' + dependentText + '“ пред ' + meta.controllerRole + ' „' + controllerText +
          '“ изисква пълен член' + (repl ? ' („' + repl + '“)' : '') + '. Правило: ' + rule.explanation;
      }
      if (rule.constraint === 'MasculineArticle' || rule.constraint === 'ArticlePlacement') {
        const repl = match.repairs?.[0]?.edits?.[0]?.replacement;
        return header + meta.dependentRole + ' „' + dependentText + '“ при ' + meta.controllerRole + ' „' + controllerText +
          '“ нарушава правилото за членуване' + (repl ? ' (очаква се „' + repl + '“)' : '') + '. Правило: ' + rule.explanation;
      }
      return header + meta.controllerRole + ' „' + controllerText + '“' + (expected ? ' (' + expected + ')' : '') +
        ' ' + (meta.mismatchVerb ?? 'не е съгласувана с') + ' ' + meta.dependentRole + ' „' + dependentText + '“' + (actual ? ' (' + actual + ')' : '') +
        '. Правило: ' + rule.explanation;
    }
    return null;
  }

  return findExplanation(false) ?? (hasSubjectPredicateAlias ? findExplanation(true) : null);
}

function disjointSubjectPredicateWarnings(data, existingRecords) {
  const env = bindGrammarEnv(data);
  if (!env) return [];
  const coveredSpans = [];
  for (const record of Array.isArray(existingRecords) ? existingRecords : []) {
    if (!record || !WARNING_CODES.has(record.code) || !Number.isInteger(record.start) || !Number.isInteger(record.length) || record.length <= 0) continue;
    coveredSpans.push([record.start, record.start + record.length]);
  }
  const coveredPredicates = new Set();
  const extra = [];
  for (const match of env.grammar.matches) {
    if (match?.assessment !== 'Ambiguous' || match.reason !== 'disjoint_subject_predicate_locus'
      || !replaceableSpan(data.text, match.start, match.length)) continue;
    const rule = env.rules.get(match.ruleId), meta = GRAMMAR_RULE_META[rule?.id];
    if (!rule || !meta || !Array.isArray(match.dependentTokenIndices) || match.dependentTokenIndices.length !== 1
      || !Array.isArray(match.controllerTokenIndices) || match.controllerTokenIndices.length < 2) continue;
    const predIndex = match.dependentTokenIndices[0], predToken = env.sourceWord(predIndex);
    if (!predToken || coveredPredicates.has(predIndex)) continue;
    const predEnd = predToken.start + predToken.length;
    if (coveredSpans.some(([s, e]) => s < predEnd && e > predToken.start)) continue;
    const conflicts = env.boundConflicts(match);
    if (!conflicts || conflicts.length < 2) continue;
    coveredPredicates.add(predIndex);
    const candidateDescriptions = match.controllerTokenIndices.map(ctrlIdx => {
      const tok = env.sourceWord(ctrlIdx);
      const conflictIdx = match.conflicts.findIndex(c => Array.isArray(c?.expectedFrom) && c.expectedFrom.some(r => r?.tokenIndex === ctrlIdx));
      const feat = conflictIdx >= 0 ? env.features(conflicts[conflictIdx].expected, rule.constraint, match.reason, true) : '';
      return '„' + tok.text + '“' + (feat ? ' (' + feat + ')' : '');
    });
    const actualFeat = env.features(conflicts[0].actual, rule.constraint, match.reason, false);
    const predDesc = '„' + predToken.text + '“' + (actualFeat ? ' (' + actualFeat + ')' : '');
    const summary = 'Противоречи на правило ' + meta.code + ' (' + meta.title + '): двусмислен подлог — нито един от кандидатите за подлог ('
      + candidateDescriptions.join(' и ') + ') не се съгласува със сказуемото ' + predDesc
      + '; автоматична замяна не се избира. Правило: ' + rule.explanation;
    const expectedRefs = [], seenRefs = new Set();
    for (const c of match.conflicts) {
      for (const ref of c?.expectedFrom || []) {
        const refKey = ref?.tokenIndex + ':' + ref?.completeReadingIndex + ':' + ref?.graphCandidateIndex;
        if (!seenRefs.has(refKey)) { seenRefs.add(refKey); expectedRefs.push(ref); }
      }
    }
    const actualRefs = Array.isArray(match.conflicts[0]?.actual) ? [...match.conflicts[0].actual] : [];
    const diagnostic = {
      code: 'disjoint_subject_predicate_locus',
      start: predToken.start,
      length: predToken.length,
      ruleId: match.ruleId,
      basis: 'grammar.ambiguous.disjoint_subject_predicate_locus',
      projectedText: null,
      readings: [...expectedRefs, ...actualRefs]
    };
    extra.push({diagnostic, summary});
  }
  return extra;
}

export function renderGrammarReview(data, doc) {
  const env = bindGrammarEnv(data);
  if (!env) return null;
  const {grammar, rules, sourceWord, boundConflicts, features, groupSlice} = env;
  const reason = {
    multi_token_article_repair_not_measured:'Преместването на члена изисква съвместна проверка на няколко думи.',
    article_placement_unresolved:'Поправката трябва да отчете и мястото на члена.',
    no_verified_single_token_repair:'Поправката изисква допълнителна проверка.',
    coordinated_subject_clause_mismatch:'Съчиненият подлог от няколко части изисква сказуемо в множествено число.'
  };
  const qualifications = {
    masculine_article_form_requires_role:'Формата на мъжкия член зависи от ролята.',
    clause_relation_unverified:'Проверете и съгласуването със сказуемото.',
    controller_gender_unmeasured:'Родът зависи от лицето или предмета, за който се отнася формата.'
  };
  const node = (tag, value) => {
    const item = doc.createElement(tag);
    if (value !== undefined) item.textContent = value;
    return item;
  };
  function sourceLink(rule) {
    const source = node('a', rule.sourceSection);
    try {
      const url = new URL(rule.sourceUrl);
      if (url.protocol === 'https:') source.setAttribute('href', url.href);
    } catch {}
    return source;
  }
  function repairedText(match, repair) {
    if (!Array.isArray(repair?.edits) || !repair.edits.length || repair.edits.some(edit => !edit)) return null;
    const edits = [...repair.edits].sort((a, b) => b.start - a.start);
    let end = match.start + match.length;
    let result = data.text.slice(match.start, end);
    for (const edit of edits) {
      const token = sourceWord(edit.tokenIndex);
      if (!token || token.start !== edit.start || token.length !== edit.length ||
          edit.start < match.start || edit.start + edit.length > end || typeof edit.replacement !== 'string' ||
          !edit.replacement) return null;
      const offset = edit.start - match.start;
      result = result.slice(0, offset) + edit.replacement + result.slice(offset + edit.length);
      end = edit.start;
    }
    return result;
  }
  const section = node('section');
  const ambiguous = grammar.matches.filter(match => match?.assessment === 'Ambiguous').length;
  const unassessed = grammar.matches.filter(match => match?.assessment === 'Unassessed').length;
  section.append(node('h3', 'Граматични конструкции'),
    node('p', grammar.matches.length + ' разпознати · ' + ambiguous + ' двусмислени · ' + unassessed + ' неизмерени'));
  const catalog = node('details');
  catalog.append(node('summary', 'Каталог на правилата (' + rules.size + ')'));
  const ruleList = node('ul');
  for (const rule of rules.values()) {
    const meta = GRAMMAR_RULE_META[rule.id];
    const prefix = meta ? meta.code + ' (' + meta.title + ') · ' : '';
    const item = node('li', prefix + rule.pattern + ' · ' + rule.id + ' · ' + rule.explanation + ' ');
    item.append(sourceLink(rule));
    ruleList.append(item);
  }
  catalog.append(ruleList);
  section.append(catalog);
  for (const match of grammar.matches) {
    const rule = rules.get(match?.ruleId);
    if (!rule || match.assessment !== 'Mismatch' || !replaceableSpan(data.text, match.start, match.length)) continue;
    const conflicts = boundConflicts(match);
    if (!conflicts) continue;
    const meta = GRAMMAR_RULE_META[rule.id];
    const item = node('div');
    item.append(node('p', '„' + data.text.slice(match.start, match.start + match.length) + '“ · ' +
      (meta ? meta.code + ' · ' + meta.title + ' · ' : '') + rule.id));
    const controllerText = groupSlice(match.controllerTokenIndices);
    const dependentText = groupSlice(match.dependentTokenIndices);
    if (meta && controllerText && dependentText) {
      item.append(node('p', 'Конструкция: ' + meta.controllerRole + ' „' + controllerText + '“ ↔ ' +
        meta.dependentRole + ' „' + dependentText + '“'));
    }
    item.append(node('p', rule.explanation), sourceLink(rule));
    const article = rule.constraint === 'ArticlePlacement' || rule.constraint === 'MasculineArticle';
    for (const conflict of conflicts) {
      const actual = features(conflict.actual, rule.constraint, match.reason, false);
      const expected = features(conflict.expected, rule.constraint, match.reason, true);
      if (actual) item.append(node('p', 'Изписано · ' + actual + (expected && !article ? ' | Контролер · ' + expected : '')));
    }
    const repairs = (Array.isArray(match.repairs) ? match.repairs : []).map(repair => ({
      text: repairedText(match, repair), notes: Array.isArray(repair?.qualifications)
        ? [...new Set(repair.qualifications.map(key => Object.hasOwn(qualifications, key) ? qualifications[key] :
          'Вариантът има условие, което изисква допълнителна проверка.'))] : []
    })).filter(repair => repair.text);
    if (repairs.length) {
      const variants = node('ul');
      for (const repair of repairs) variants.append(node('li', [repair.text, ...repair.notes].join(' · ')));
      item.append(node('p', 'Варианти за тази конструкция'), variants);
    } else if (reason[match.reason]) item.append(node('p', reason[match.reason]));
    section.append(item);
  }
  const conditional = grammar.matches.filter(match => match?.assessment === 'Ambiguous' && rules.has(match.ruleId) &&
    replaceableSpan(data.text, match.start, match.length) && boundConflicts(match));
  if (conditional.length) {
    const alternatives = node('details');
    alternatives.append(node('summary', 'Условни проверки (' + conditional.length + ')'));
    const list = node('ul');
    for (const match of conditional) {
      const controllersList = (match.controllerTokenIndices || [])
        .map(i => sourceWord(i)?.text)
        .filter(Boolean)
        .map(w => '„' + w + '“')
        .join(', ');
      const predWord = sourceWord(match.dependentTokenIndices?.[0])?.text;
      const scope = match.reason === 'disjoint_subject_predicate_locus'
        ? 'Двусмислен конфликт при избор на подлог' + (controllersList && predWord ? ' (кандидати ' + controllersList + ' спрямо „' + predWord + '“; без автоматична замяна)' : '')
        : match.reason === 'address_number_gender_unresolved' ? 'При обръщение към едно лице с установен род'
        : match.reason === 'competing_predicate_group' ? 'При прочит като глаголна група'
        : match.reason === 'competing_predicative_role' ? 'При прочит със сказуемно прилагателно'
        : 'При прочит като една именна група';
      list.append(node('li', scope + ' · „' + data.text.slice(match.start, match.start + match.length) +
        '“ · ' + rules.get(match.ruleId).explanation));
    }
    alternatives.append(list);
    section.append(alternatives);
  }
  return section;
}

const isCompatible = value => typeof value === 'string' && value.toLowerCase() === 'compatible';

// Resolve one response-local head witness. The inventory binds exact ordinals to their original occurrence;
// graph ordinals address that token's own analysis, candidate and full tag, never the replacement inventory.
function supportedHead(support, head, data, analysisFor) {
  const token = data.tokens[head], inventory = data.readingInventory;
  if (!Array.isArray(inventory?.occurrences) || !Array.isArray(inventory.sets)) return null;
  const occurrence = inventory.occurrences.find(row => row?.tokenIndex === head);
  if (!occurrence || occurrence.start !== token.start || occurrence.length !== token.length || occurrence.text !== token.text
    || !Number.isInteger(occurrence.readingSetIndex) || occurrence.readingSetIndex < 0) return null;
  const set = inventory.sets[occurrence.readingSetIndex], analysis = analysisFor(token);
  if (!set || typeof set.lookupText !== 'string' || !set.lookupText.length || analysis?.word !== set.lookupText
    || !Array.isArray(set.canonicalReadings) || ((set.supportKey == null) !== (set.supportReadings == null))
    || (set.supportReadings != null && (!Array.isArray(set.supportReadings) || set.canonicalReadings.length !== 0))) return null;
  const rows = set.supportReadings ?? set.canonicalReadings, key = set.supportReadings?.length > 0 ? set.supportKey : set.canonicalKey;
  if (!Array.isArray(rows) || !Array.isArray(analysis.readings) || analysis.readings.length > rows.length
    || typeof key !== 'string' || !key.length || analysis.normalizedWord !== key) return null;
  const exact = support?.reading, graph = support?.graphReading;
  if (exact != null) {
    if (graph != null || exact.tokenIndex !== head || !Number.isInteger(exact.completeReadingIndex) || exact.completeReadingIndex < 0) return null;
    const reading = rows[exact.completeReadingIndex], raw = reading?.raw, features = reading?.features;
    // The server retains raw forms (including stress) and permits lookup aliases. Only displayed ordinals have
    // a second raw witness; later ordinals belong to the complete inventory, not the truncated display prefix.
    const displayed = analysis.readings[exact.completeReadingIndex];
    if (typeof raw?.form !== 'string' || !raw.form.length || (exact.completeReadingIndex < analysis.readings.length
      && (!displayed || ['form', 'lemma', 'partOfSpeech', 'score', 'legacyTypeId', 'legacyBaseId', 'legacyWordId', 'source']
        .some(field => raw[field] !== displayed[field]))) || features?.partOfSpeech !== 'Noun'
      || features.hasUnrecognizedFeatures === true || features.hasConflictingFeatures === true
      || (features.number !== 'Plural' && (features.number !== 'Singular' || !['Masculine', 'Feminine', 'Neuter'].includes(features.gender)))) return null;
    return {features, graph: false};
  }
  if (!graph || graph.tokenIndex !== head || !Number.isInteger(graph.graphCandidateIndex) || graph.graphCandidateIndex < 0
    || !Number.isInteger(graph.fullTagIndex) || graph.fullTagIndex < 0 || rows.length !== 0
    || set.canonicalReadings.length !== 0 || analysis.readings.length !== 0) return null;
  const candidates = analysis.graphCandidates;
  if (analysis.graphSearchIncomplete === true || analysis.graphExactRootCandidatesComplete !== true || !Array.isArray(candidates)) return null;
  const candidate = candidates[graph.graphCandidateIndex], tags = candidate?.fullTags;
  // The current graph producer emits normalized candidate words; use its response keys without a UI normalizer.
  if (typeof candidate?.word !== 'string' || (candidate.word !== set.canonicalKey && candidate.word !== set.supportKey)
    || candidate?.isConditional === true || candidate?.conditionalRootProgramId != null || candidate?.conditionalRootBaseFullTag != null
    || candidate?.conditionalRootSupportCount != null || candidate?.conditionalSupportingLemmas != null || candidate?.conditionalSupportingLemmasRef != null
    || (candidate?.unmetExactSelfReadingRequirements != null && (!Array.isArray(candidate.unmetExactSelfReadingRequirements)
      || candidate.unmetExactSelfReadingRequirements.length > 0))
    || typeof candidate?.derivedLemma !== 'string' || !candidate.derivedLemma.trim() || !Array.isArray(tags)
    || typeof tags[graph.fullTagIndex] !== 'string' || !tags[graph.fullTagIndex].trim()) return null;
  return {features: null, graph: true};
}

// One alternative is supported when its phrase is Compatible and a support of the phrase's own head has a Compatible
// member for the reviewed word whose replacement readings address this alternative's readings (the /demo rule).
// Both the reviewed word and the complete head must lie inside the phrase's original source span.
export function supportedNominalPhrase(row, review, data, analysisFor) {
  const phrase = row.phrase, head = phrase?.headTokenIndex, token = data.tokens[head], readings = row.alternative?.readings;
  if (!isCompatible(phrase?.assessment) || !Number.isInteger(head) || token?.kind !== 'Word'
    || !Array.isArray(phrase.headSupports) || !Array.isArray(readings) || !Number.isInteger(phrase.start)
    || !Number.isInteger(phrase.length) || phrase.start < 0 || phrase.start > review.start
    || review.start + review.length > phrase.start + phrase.length || phrase.start + phrase.length > data.text.length
    || !replaceableSpan(data.text, token.start, token.length) || token.start < phrase.start
    || token.start + token.length > phrase.start + phrase.length || data.text.slice(token.start, token.start + token.length) !== token.text) return null;
  for (const support of phrase.headSupports) {
    const witness = supportedHead(support, head, data, analysisFor);
    if (!witness) continue;
    const member = (Array.isArray(support.members) ? support.members : []).find(item => item?.tokenIndex === review.tokenIndex
      && isCompatible(item.assessment) && Array.isArray(item.replacementReadings)
      && item.replacementReadings.some(index => Number.isInteger(index) && index >= 0 && index < readings.length));
    if (member) return {start: phrase.start, length: phrase.length, head, support, witness,
      modifier: Array.isArray(phrase.modifierTokenIndices) && phrase.modifierTokenIndices.includes(review.tokenIndex)};
  }
  return null;
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
  return start < end ? {
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

const tokenViews = new WeakMap();

export function renderTokenText(target, text, tokens, classify, select) {
  const doc = target.ownerDocument;
  let cursor = 0;
  // Validate every source span, including non-word tokens, before touching live DOM.
  for (const token of tokens) {
    const end = token.start + token.length;
    if (!Number.isInteger(token.start) || !Number.isInteger(token.length) || token.start < cursor || token.length < 0
      || end > text.length) throw new Error('Невалидни позиции в текстовия отговор.');
    cursor = end;
  }
  const plan = [];
  cursor = 0;
  tokens.forEach((token, index) => {
    if (token.kind !== 'Word' || !token.length) return;
    plan.push({key: 'gap:' + token.start, text: text.slice(cursor, token.start)});
    plan.push({key: 'word:' + token.start, text: text.slice(token.start, token.start + token.length),
      className: 'live-token ' + classify(index), index});
    cursor = token.start + token.length;
  });
  plan.push({key: 'tail', text: text.slice(cursor)});
  // The complete plan, including classification, is valid before any mutation.
  const previous = tokenViews.get(target) || [];
  const available = new Map(previous.filter(row => row.node.parentNode === target).map(row => [row.key, row]));
  const next = [], active = doc.activeElement;
  for (const item of plan) {
    let row = available.get(item.key);
    if (!row) {
      const node = item.index === undefined ? doc.createTextNode('') : doc.createElement('button');
      row = {key: item.key, node, binding: null};
      if (item.index !== undefined) {
        node.type = 'button';
        node.setAttribute('aria-pressed', 'false');
        node.addEventListener('click', () => {
          if (row.binding && !node.disabled) row.binding.select(row.binding.index);
        });
      }
    }
    if (item.index === undefined) {
      if (row.node.nodeValue !== item.text) row.node.nodeValue = item.text;
    } else {
      if (row.node.textContent !== item.text) row.node.textContent = item.text;
      if (row.node.className !== item.className) row.node.className = item.className;
      row.node.dataset.tokenIndex = String(item.index);
      row.node.disabled = false;
      row.binding = {index: item.index, select};
    }
    next.push(row);
  }
  let position = target.firstChild;
  for (const row of next) {
    if (row.node !== position) target.insertBefore(row.node, position);
    position = row.node.nextSibling;
  }
  while (position) {
    const old = position;
    position = old.nextSibling;
    target.removeChild(old);
  }
  const retained = new Set(next);
  for (const row of previous) if (!retained.has(row)) {
    row.binding = null;
    if (row.node.nodeType === 1) row.node.disabled = true;
  }
  tokenViews.set(target, next);
  if (active && target.contains(active) && doc.activeElement !== active) active.focus({preventScroll: true});
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

export const UNIQUE_SPELLING_CODE = 'spelling_unknown_word_unique_distance_one_candidate';

// Shared presentation classification across /demo and /demo-lab. Unknown codes remain informational.
export const WARNING_CODES = new Set([
  'missing_terminal_punctuation',
  'sentence_initial_lowercase',
  'mixed_script_ocr_known_candidate',
  'digit_shape_ocr_known_candidate',
  'accent_sensitive_i_grave',
  'joined_verb_negation_should_be_separate',
  'pronoun_verb_agreement_mismatch',
  'unseparated_finite_predications',
  'preposition_missing_complement',
  'full_article_expected_for_subject_candidate',
  'short_article_expected_after_preposition_candidate',
  'nominal_article_on_noninitial_member',
  'productive_morphology_agreement_mismatch',
  'productive_attribute_agreement_mismatch',
  'productive_predicative_agreement_mismatch',
  'auxiliary_participle_agreement_mismatch',
  'quantified_noun_form_mismatch',
  'personal_numeral_selection_mismatch',
  'particle_participle_verb_form_mismatch',
  'pronoun_case_governance_mismatch',
  'deictic_temporal_tense_mismatch',
  'disjoint_subject_predicate_locus'
]);
export const UNSUPPORTED_CODES = new Set(['full_short_article_check_not_ready']);
const AGREEMENT_CODES = new Set([
  'pronoun_verb_agreement_mismatch',
  'productive_morphology_agreement_mismatch',
  'productive_attribute_agreement_mismatch',
  'productive_predicative_agreement_mismatch',
  'auxiliary_participle_agreement_mismatch',
  'quantified_noun_form_mismatch',
  'personal_numeral_selection_mismatch',
  'particle_participle_verb_form_mismatch',
  'pronoun_case_governance_mismatch',
  'deictic_temporal_tense_mismatch',
  'disjoint_subject_predicate_locus'
]);
const REPLACEABLE_CODES = new Set([
  UNIQUE_SPELLING_CODE,
  'joined_verb_negation_should_be_separate',
  'accent_sensitive_i_grave',
  'mixed_script_ocr_known_candidate',
  'digit_shape_ocr_known_candidate'
]);

export const MESSAGES = {
  unseparated_finite_predications: 'Две лични глаголни форми в проверената конструкция изискват свързване или пунктуационно разделяне.',
  productive_morphology_agreement_mismatch: 'Несъгласуване по лице/число в поддържаната конструкция.',
  pronoun_verb_agreement_mismatch: 'Несъгласуване по лице/число между местоимение и глагол в поддържаната конструкция.',
  productive_attribute_agreement_mismatch: 'Несъгласуване по род/число между определение и съществително в поддържаната конструкция.',
  productive_predicative_agreement_mismatch: 'Несъгласуване по число или род със свързания подлог.',
  auxiliary_participle_agreement_mismatch: 'Спомагателният глагол и причастието на -л се разминават по число.',
  quantified_noun_form_mismatch: 'След числителното се очаква съответната бройна или множествена форма на съществителното.',
  personal_numeral_selection_mismatch: 'Числителното не съответства по лице/нелице на съществителното в бройната конструкция.',
  particle_participle_verb_form_mismatch: 'Неправилна глаголна форма в сложната глаголна конструкция или на мястото на личното сказуемо.',
  pronoun_case_governance_mismatch: 'Неправилна падежна форма на личното местоимение в тази синтактична позиция.',
  deictic_temporal_tense_mismatch: 'Времето на глаголната форма противоречи на темпоралното наречие в същото просто изречение.',
  disjoint_subject_predicate_locus: 'Сказуемото не се съгласува с нито един от възможните подлози в изречението; изборът на подлог е двусмислен.',
  full_article_expected_for_subject_candidate: 'Ако думата е подлог, се очаква пълен член.',
  short_article_expected_after_preposition_candidate: 'В проверената предложна група се очаква кратък член.',
  nominal_article_on_noninitial_member: 'Членът се поставя на първото определение в тази именна група.',
  preposition_missing_complement: 'След предлога не е намерена дума, към която да се отнася.',
  [UNIQUE_SPELLING_CODE]: 'Няма точен речников прочит. Намерената близка форма е предложение за преглед.',
  joined_verb_negation_should_be_separate: 'Ако е отрицателна глаголна форма, „не“ се пише отделно.',
  accent_sensitive_i_grave: 'Ако е местоимение, се пише „ѝ“.',
  mixed_script_ocr_known_candidate: 'Думата смесва кирилица и латиница; възможна грешка при въвеждане или разпознаване.',
  digit_shape_ocr_known_candidate: 'Цифра на мястото на буква; възможна грешка при разпознаване.',
  missing_terminal_punctuation: 'Изречението няма завършващ препинателен знак.',
  sentence_initial_lowercase: 'Изречението започва с малка буква.',
  full_short_article_check_not_ready: 'Проверката за членуване е частична; тук няма установено решение.',
  clause_missing_predicate: 'Не е намерено сказуемо в проверения фрагмент; възможно заглавие.',
  missing_comma_before_subordinate_conjunction: 'Възможно е да липсва запетая пред подчинено изречение.',
  missing_space_after_punctuation: 'Липсва интервал след препинателния знак.',
  space_before_punctuation: 'Има интервал пред препинателния знак.',
  style_repetition_evidence: 'В текста има повторена речникова форма.',
  reviewed_word_formation_evidence: 'Сложна или производна дума.',
  hyphenated_degree_morphology_evidence: 'Степенувана форма с „по-“ или „най-“.',
  contextual_morphology_reading_evidence: 'Контекстно сведение за прочита на думата.'
};
export const GENERIC_MESSAGE = 'Друго наблюдение от анализа.';
const RELATIVE_PRONOUN_SURFACES = new Set(['който', 'която', 'което', 'които']);
export const RELATIVE_ANTECEDENT_MESSAGE =
  'Относителното местоимение не се съгласува по род или число с определяемото си.';

export function diagnosticMessage(d, sourceText = '') {
  if (d && d.code === 'pronoun_case_governance_mismatch' && typeof sourceText === 'string' &&
      Number.isInteger(d.start) && Number.isInteger(d.length) && d.start >= 0 && d.length > 0 &&
      d.start + d.length <= sourceText.length) {
    const surface = sourceText.slice(d.start, d.start + d.length)
      .normalize('NFD').replace(/[\u0300\u0301]/g, '').normalize('NFC').trim().toLowerCase();
    if (RELATIVE_PRONOUN_SURFACES.has(surface)) {
      return RELATIVE_ANTECEDENT_MESSAGE;
    }
  }
  return d ? MESSAGES[d.code] : undefined;
}

const SEVERITY_ORDER = {warning: 0, unsupported: 1, information: 2};
const LIVE_LABELS = {
  warning: 'Предупреждение',
  'unsupported-check': 'Неподдържана проверка',
  information: 'Информация'
};
const LIVE_COMPACT_CODES = new Set([
  'missing_terminal_punctuation',
  'full_short_article_check_not_ready',
  'style_repetition_evidence',
  'reviewed_word_formation_evidence',
  'hyphenated_degree_morphology_evidence',
  'contextual_morphology_reading_evidence'
]);

export function graphSupportLabel(supports) {
  return supports.some(support => Array.isArray(support?.graphReadings) && support.graphReadings.length > 0)
    ? ' · Графова опора (хипотеза)' : '';
}

export function matchCase(original, form) {
  const letters = (text, cyrillic) => {
    const result = [];
    for (const char of text) {
      if (/^\p{M}$/u.test(char) && result.length) continue;
      if (!/^[\p{Ll}\p{Lu}]$/u.test(char) || (cyrillic && !/^\p{Script=Cyrillic}$/u.test(char))) return null;
      result.push(char);
    }
    return result;
  };
  const source = letters(original, false), candidate = letters(form, true);
  if (!source || source.length < 2 || !candidate?.length || !candidate.every(char => /^\p{Ll}$/u.test(char))) return form;
  const caps = source.every(char => /^\p{Lu}$/u.test(char));
  const title = /^\p{Lu}$/u.test(source[0]) && source.slice(1).every(char => /^\p{Ll}$/u.test(char));
  if (!caps && !title) return form;
  let index = 0, result = '';
  for (const char of form) {
    if (/^\p{M}$/u.test(char)) { result += char; continue; }
    const proposed = caps || index++ === 0 ? char.toUpperCase() : char;
    if (!/^\p{Script=Cyrillic}$/u.test(proposed) || !/^[\p{Ll}\p{Lu}]$/u.test(proposed)) return form;
    result += proposed;
  }
  return result;
}

function diagnosticSources(data) {
  const v = data?.validation;
  return [
    ['diagnostics', data?.diagnostics], ['validation', v?.diagnostics], ['spacing', v?.spacingDiagnostics],
    ['comma', v?.subordinateCommaDiagnostics], ['fragment', v?.fragmentDiagnostics]
  ];
}

function supportTokens(records) {
  const indices = new Set();
  for (const {record} of records) {
    for (const list of [record.readings, record.graphReadings, record.supports]) {
      for (const ref of Array.isArray(list) ? list : []) if (Number.isInteger(ref?.tokenIndex)) indices.add(ref.tokenIndex);
    }
  }
  return [...indices].sort((a, b) => a - b);
}

// Returns returned diagnostic records only (evidence codes never become messages), grouped by code and span.
export function collectChecks(data) {
  const text = typeof data?.text === 'string' ? data.text : '';
  const groups = new Map();
  const allRawRecords = [];
  for (const [source, list] of diagnosticSources(data)) {
    for (const record of Array.isArray(list) ? list : []) {
      if (!record || typeof record.code !== 'string') continue;
      allRawRecords.push(record);
      const key = record.code + '\u0001' + record.start + '\u0001' + record.length;
      let group = groups.get(key);
      if (!group) groups.set(key, group = {code: record.code, start: record.start, length: record.length, records: []});
      group.records.push({source, record});
    }
  }
  const checks = [];
  for (const group of groups.values()) {
    const {code, start, length, records} = group;
    const validSpan = Number.isInteger(start) && Number.isInteger(length) && start >= 0 && length >= 0 && start + length <= text.length;
    const original = validSpan ? text.slice(start, start + length) : '';
    const severity = WARNING_CODES.has(code) ? 'warning' : UNSUPPORTED_CODES.has(code) ? 'unsupported' : 'information';
    const projected = records.map(({record}) => record.projectedText).find(value => typeof value === 'string' && value.length) ?? null;
    const words = AGREEMENT_CODES.has(code)
      ? supportTokens(records).map(index => data.tokens?.[index]?.text).filter(word => typeof word === 'string' && word.length)
      : [];
    const grammarMessage = describeGrammarViolation(data, records, start, length);
    checks.push({
      code, start, length, severity, validSpan, records, projected, words,
      known: Object.hasOwn(MESSAGES, code),
      message: diagnosticMessage({code, start, length}, text) ?? GENERIC_MESSAGE,
      grammarMessage,
      graph: records.some(({record}) => Array.isArray(record.graphReadings) && record.graphReadings.length > 0),
      replaceable: REPLACEABLE_CODES.has(code) && projected !== null && validSpan && replaceableSpan(text, start, length),
      replacement: projected === null ? null : matchCase(original, projected),
      original
    });
  }
  for (const {diagnostic, summary} of disjointSubjectPredicateWarnings(data, allRawRecords)) {
    const {code, start, length} = diagnostic;
    const records = [{source: 'grammar', record: diagnostic}];
    checks.push({
      code, start, length, severity: 'warning', validSpan: true, records, projected: null,
      words: supportTokens(records).map(index => data.tokens?.[index]?.text).filter(word => typeof word === 'string' && word.length),
      known: true,
      message: MESSAGES[code],
      grammarMessage: summary,
      graph: false,
      replaceable: false,
      replacement: null,
      original: text.slice(start, start + length)
    });
  }
  return checks.sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]
    || (Number.isInteger(a.start) ? a.start : Infinity) - (Number.isInteger(b.start) ? b.start : Infinity)
    || a.code.localeCompare(b.code));
}

// Shared word-underline rule across /demo and /demo-lab. Zero-length checks stay in the list without marking a neighbour.
export function warningTokens(data, checks) {
  const marked = new Set();
  for (const check of checks) {
    if (check.severity !== 'warning' || !check.validSpan || check.length === 0) continue;
    (data?.tokens || []).forEach((token, index) => {
      if (token.kind === 'Word' && check.start < token.start + token.length && check.start + check.length > token.start) marked.add(index);
    });
  }
  return marked;
}

export function diagnosticPresentation(data, token = null) {
  const validation = data?.validation;
  const original = Array.isArray(data?.diagnostics) ? data.diagnostics : [];
  const mirroredValidation = diagnostic => original.some(old =>
    old.code === diagnostic.code && old.start === diagnostic.start && old.length === diagnostic.length
    && (diagnostic.basis === 'existing_diagnostic=' + (old.ruleId ?? '') + ';bounded_article_advice'
      || typeof diagnostic.basis === 'string'
        && diagnostic.basis.startsWith('reviewed_lexical_quantity;complete_exact_readings;' + (old.ruleId ?? '') + ';')));
  const seen = new Set();
  const rawRecords = [];
  for (const [source, list] of diagnosticSources(data)) {
    for (const diagnostic of Array.isArray(list) ? list : []) {
      if (source === 'validation' && mirroredValidation(diagnostic)) continue;
      const key = JSON.stringify(diagnostic);
      if (seen.has(key)) continue;
      seen.add(key);
      rawRecords.push(diagnostic);
    }
  }
  const messages = rawRecords.map(diagnostic => {
    const classification = UNSUPPORTED_CODES.has(diagnostic.code)
      ? 'unsupported-check' : WARNING_CODES.has(diagnostic.code) ? 'warning' : 'information';
    const label = LIVE_LABELS[classification];
    const grammarNote = describeGrammarViolation(data, [{record: diagnostic}], diagnostic.start, diagnostic.length);
    const defaultNote = LIVE_COMPACT_CODES.has(diagnostic.code)
      ? ''
      : (diagnosticMessage(diagnostic, typeof data?.text === 'string' ? data.text : '') ?? '');
    const summary = grammarNote ?? defaultNote;
    const description = summary ? ' · ' + summary : '';
    return {
      diagnostic, classification, label, summary: summary || null,
      text: label + ' · ' + diagnostic.code + ' · ' + diagnostic.start + '+' + diagnostic.length
        + graphSupportLabel([diagnostic]) + description
    };
  });
  for (const {diagnostic, summary} of disjointSubjectPredicateWarnings(data, rawRecords)) {
    const classification = 'warning', label = LIVE_LABELS[classification];
    messages.push({
      diagnostic, classification, label, summary,
      text: label + ' · ' + diagnostic.code + ' · ' + diagnostic.start + '+' + diagnostic.length + ' · ' + summary
    });
  }
  const strongest = token && ['warning', 'unsupported-check', 'information'].find(classification =>
    messages.some(row => row.classification === classification
      && row.diagnostic.length > 0
      && row.diagnostic.start < token.start + token.length
      && row.diagnostic.start + row.diagnostic.length > token.start));
  const coverage = validation === null ? 'Проверките не са върнати.'
    : validation === undefined ? 'Проверките липсват в отговора.'
      : validation.available === true
        ? 'Кандидатни проверки в ограничения обхват; липсата на съобщение не удостоверява граматична правилност.'
        : 'Проверките са недостъпни.' + (validation.unavailableReason ? ' ' + validation.unavailableReason : '');
  return {
    messages,
    tokenClass: strongest ? 'check-' + strongest : validation?.available === true ? 'check-empty' : 'check-unavailable',
    coverage
  };
}

export function csharpStringLiteral(value) {
  let out = '"';
  const text = String(value ?? '');
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code === 0x22) out += '\\"';
    else if (code === 0x5c) out += '\\\\';
    else if (code === 0x00) out += '\\0';
    else if (code === 0x08) out += '\\b';
    else if (code === 0x0c) out += '\\f';
    else if (code === 0x0a) out += '\\n';
    else if (code === 0x0d) out += '\\r';
    else if (code === 0x09) out += '\\t';
    else if (code >= 0xd800 && code <= 0xdbff) {
      const next = i + 1 < text.length ? text.charCodeAt(i + 1) : 0;
      if (next >= 0xdc00 && next <= 0xdfff) {
        out += text[i] + text[i + 1];
        i++;
      } else {
        out += '\\u' + code.toString(16).padStart(4, '0');
      }
    } else if ((code >= 0xdc00 && code <= 0xdfff) || code < 0x20 || (code >= 0x7f && code <= 0x9f) || code === 0x2028 || code === 0x2029) {
      out += '\\u' + code.toString(16).padStart(4, '0');
    } else {
      out += text[i];
    }
  }
  return out + '"';
}

const EXPLORER_CSHARP_METHODS = {
  'analyze-word': 'AnalyzeWord',
  'analyze-text': 'AnalyzeText',
  'generate-forms': 'GenerateForms',
  'derive-words': 'DeriveWords',
  'decompose-word': 'DecomposeWord'
};

export function explorerCSharpSnippet(operation, text, {heuristics = true, effectiveLimit = null, token = null} = {}) {
  const literal = csharpStringLiteral(text);
  if (operation === 'analyze-text' || token) {
    const enabled = Boolean(heuristics);
    const usings = [
      ...(token ? ['using System.Linq;'] : []),
      'using System.Threading;',
      'using BgLang.Api;',
      'using BgLang.Contracts;'
    ].join('\n');
    const optionsBlock = enabled
      ? [
          'var options = new TextContextOptions',
          '{',
          '    IncludeAdditionalRoleHeuristics = true,',
          '    IncludeSubordinateCommaDiagnostics = true,',
          '    IncludeFragmentDiagnostics = true,',
          '    IncludeSpacingDiagnostics = true,',
          '    IncludeWordConstructions = true,',
          '    IncludeNominalConstructions = true,',
          '    IncludeGrammarEvidence = true',
          '};'
        ].join('\n')
      : 'var options = new TextContextOptions();';
    const callVar = token ? 'text' : 'result';
    const lines = [
      usings,
      '',
      'using var session = BulgarianLanguageSession.Open("manifest.json");',
      optionsBlock,
      `var ${callVar} = session.AnalyzeText(${literal}, includeUnknownContextProperNames: ${enabled}, options, CancellationToken.None);`
    ];
    if (token) {
      lines.push(
        `var token = text.Tokens.Single(t => t.Start == ${token.start} && t.Length == ${token.length});`,
        'var analysis = token.Analysis;'
      );
    }
    return lines.join('\n');
  }
  const method = EXPLORER_CSHARP_METHODS[operation];
  const bound = effectiveLimit ? `, ${effectiveLimit}` : '';
  return `using BgLang.Api;\n\nusing var session = BulgarianLanguageSession.Open("manifest.json");\nvar result = session.${method}(${literal}${bound});`;
}
