// Decorative backgrounds behind the native textarea. Source text is never normalized or edited here.
const POS_CLASSES = Object.freeze({
  Noun: 'c-noun', Verb: 'c-verb', Adjective: 'c-adj', Adverb: 'c-adv', ProperNoun: 'c-name',
  PersonalPronoun: 'c-pron', Pronoun: 'c-pron', Numeral: 'c-num',
  Preposition: 'c-func', Conjunction: 'c-func', Particle: 'c-func', Interjection: 'c-func'
});
const states = new WeakMap(), empty = new Set();
const indices = value => value?.has ? value : value ? new Set(value) : empty;

export function posClass(token) {
  if (token.kind !== 'Word') return '';
  if (token.known === false) return 'c-unknown';
  const types = new Set(token.pos ?? []);
  return types.size > 1 ? 'c-multi' : POS_CLASSES[[...types][0]] ?? 'c-other';
}

function validate(text, tokens) {
  let cursor = 0;
  const seen = new Set();
  const splitsPair = offset => offset > 0 && offset < text.length &&
    text.charCodeAt(offset - 1) >= 0xD800 && text.charCodeAt(offset - 1) <= 0xDBFF &&
    text.charCodeAt(offset) >= 0xDC00 && text.charCodeAt(offset) <= 0xDFFF;
  for (const token of tokens) {
    const {start, length, index} = token;
    if (!Number.isInteger(start) || !Number.isInteger(length) || !Number.isInteger(index) ||
      start < cursor || length < 0 || start + length > text.length || seen.has(index) ||
      typeof token.text !== 'string' || text.slice(start, start + length) !== token.text ||
      splitsPair(start) || splitsPair(start + length))
      throw new Error('Невалидни позиции в текстовия отговор.');
    seen.add(index); cursor = start + length;
  }
}

/** Change only the previously selected and newly selected span; never move native editor focus. */
export function setBackdropSelection(container, index) {
  const state = states.get(container);
  if (!state || state.pending) return false;
  const previous = state.spans.get(state.selectedIndex), current = state.spans.get(index);
  if (previous === current) return !!current;
  if (previous) previous.node.className = previous.className;
  if (current) current.node.className = current.className + ' is-selected';
  state.selectedIndex = current ? index : null;
  return !!current;
}

function pendingBackdrop(container, text, previous) {
  if (previous?.pending && previous.text === text) return;
  const old = previous?.text ?? '', unchanged = old === text;
  let left = 0, right = 0;
  if (previous?.highlights?.length && !unchanged) {
    while (left < old.length && left < text.length && old[left] === text[left]) left++;
    while (right < old.length - left && right < text.length - left &&
      old[old.length - right - 1] === text[text.length - right - 1]) right++;
  }
  const shift = text.length - old.length, highlights = [];
  for (const row of previous?.highlights ?? []) {
    // Strict inequalities also neutralize the words touching either edit boundary.
    // Source intervals, rather than matching surfaces, distinguish repeated words.
    if (unchanged || row.end < left) highlights.push(row);
    else if (row.start > old.length - right)
      highlights.push({...row, start: row.start + shift, end: row.end + shift});
  }
  if (!highlights.length) {
    if (previous?.plain) {
      previous.plain.nodeValue = text;
      states.set(container, {text, pending: true, plain: previous.plain, highlights});
    } else {
      const plain = container.ownerDocument.createTextNode(text);
      container.replaceChildren(plain);
      states.set(container, {text, pending: true, plain, highlights});
    }
    return;
  }
  const doc = container.ownerDocument, fragment = doc.createDocumentFragment();
  let cursor = 0;
  for (const row of highlights) {
    if (row.start > cursor) fragment.append(doc.createTextNode(text.slice(cursor, row.start)));
    row.node.className = 'editor-token ' + row.posClassName;
    fragment.append(row.node);
    cursor = row.end;
  }
  if (cursor < text.length) fragment.append(doc.createTextNode(text.slice(cursor)));
  container.replaceChildren(fragment);
  states.set(container, {text, pending: true, highlights});
}

/** During editing, retain only exact unchanged POS spans; all actionable styles expire. */
export function renderEditorBackdrop(container, text, tokens = [], options = {}) {
  if (typeof text !== 'string' || !Array.isArray(tokens)) throw new Error('Невалиден текстов отговор.');
  const pending = options.pending === true, previous = states.get(container);
  const warnings = indices(options.warningIndices), suggestions = indices(options.suggestedIndices);
  container.setAttribute('aria-hidden', 'true');
  if (pending) {
    pendingBackdrop(container, text, previous);
    return;
  }
  if (previous && !previous.pending && previous.text === text && previous.tokens === tokens &&
    previous.warnings === warnings && previous.suggestions === suggestions) {
    setBackdropSelection(container, options.selectedIndex);
    return;
  }
  // Validate the entire plan before replacing a previously good backdrop.
  validate(text, tokens);
  const doc = container.ownerDocument, fragment = doc.createDocumentFragment(), spans = new Map(), highlights = [];
  let cursor = 0;
  for (const token of tokens) {
    if (token.kind === 'Whitespace' || token.length === 0) continue;
    if (token.start > cursor) fragment.append(doc.createTextNode(text.slice(cursor, token.start)));
    const span = doc.createElement('span');
    const posClassName = posClass(token), classes = ['editor-token', posClassName];
    if (warnings.has(token.index)) classes.push('w-warning');
    if (suggestions.has(token.index)) classes.push('w-advice');
    const className = classes.filter(Boolean).join(' ');
    span.className = className;
    span.textContent = text.slice(token.start, token.start + token.length);
    span.setAttribute('data-token-index', String(token.index));
    spans.set(token.index, {node: span, className});
    if (posClassName) highlights.push({node: span, start: token.start,
      end: token.start + token.length, posClassName});
    fragment.append(span); cursor = token.start + token.length;
  }
  if (cursor < text.length) fragment.append(doc.createTextNode(text.slice(cursor)));
  container.replaceChildren(fragment);
  states.set(container, {text, tokens, pending, spans, highlights, warnings, suggestions, selectedIndex: null});
  setBackdropSelection(container, options.selectedIndex);
}

/** No layout reads or measurements: CSS supplies identical text and wrapping geometry. */
export function syncEditorScroll(textarea, backdrop) {
  backdrop.scrollTop = textarea.scrollTop;
  backdrop.scrollLeft = textarea.scrollLeft;
}
