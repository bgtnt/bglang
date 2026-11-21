// This view draws the supplied local evidence; it neither selects readings nor infers dependencies.
export const ROLE_NAMES = Object.freeze({
  Predicate: 'Сказуемо', Subject: 'Подлог', Object: 'Допълнение',
  Complement: 'Именна част на сказуемото', PrepositionalObject: 'Предложно допълнение',
  Attribute: 'Определение', Adverbial: 'Обстоятелствено пояснение',
  Possessive: 'Кратко притежателно местоимение', finite: 'Лична глаголна форма',
  imperative: 'Повелителна форма', participle: 'Причастие', converb: 'Деепричастие',
  unmeasured_verb: 'Глагол без измерена връзка', other: 'Неглаголен прочит',
  nominal_head: 'Опорна дума на именна група', nominal_modifier: 'Именен определител'
});

export const STATUS_NAMES = Object.freeze({
  linked: 'С локална връзка', selected: 'Избрано в контекста', evidence: 'Опора за кандидат', candidate: 'Кандидат', unlinked: 'Без локална връзка',
  pending: 'Очаква анализ', alternative: 'Алтернативен прочит', uncovered: 'Непокрит прочит',
  conflict: 'Несъгласуване', unassessed: 'Без оценка', compatible: 'Локално съвместимо',
  ambiguous: 'Нееднозначно', partial: 'Частичен анализ', unavailable: 'Недостъпен анализ'
});

export const EDGE_NAMES = Object.freeze({
  selected_subject: 'Подлог на сказуемото',
  future_marker: 'Маркер за бъдеще', auxiliary_participle: 'Спомагателен глагол и причастие',
  reflexive_clitic: 'Глагол и кратка форма „се“',
  conditional_auxiliary_participle: 'Условно наклонение със спомагателен глагол и причастие',
  future_marker_marker: 'Част от глаголната група с „ще“',
  conditional_auxiliary_participle_marker: 'Част от глаголната група в условно наклонение',
  comma_separation: 'Разделяне със запетая', coordinating_conjunction: 'Съчинителна връзка',
  da_boundary: 'Граница с „да“', coordinated_da_boundary: 'Съчинена граница с „да“',
  nominal_modifier: 'Именно определение', nominal_connector: 'Именен съюз',
  grammar_constraint: 'Граматично условие'
});

export const roleName = role => ROLE_NAMES[role] ?? String(role ?? 'Неустановена роля');
export const statusName = status => STATUS_NAMES[status] ?? String(status ?? 'Без оценка');
export function edgeName(label) {
  if (EDGE_NAMES[label]) return EDGE_NAMES[label];
  if (label?.startsWith('role_support_')) return 'Опора за ' + roleName(label.slice(13)).toLocaleLowerCase('bg');
  if (typeof label === 'string' && label.endsWith('_marker'))
    return 'Маркер: ' + (EDGE_NAMES[label.slice(0, -7)] ?? label.slice(0, -7));
  return String(label ?? 'Локална връзка');
}

// These are display page sizes, not analysis limits: every token and edge remains navigable.
const TOKEN_PAGE_SIZE = 32, EDGE_PAGE_SIZE = 24, ARC_PAGE_SIZE = 48;
const SVG_NS = 'http://www.w3.org/2000/svg';
const states = new WeakMap();
let nextId = 0;
const ink = 'var(--ink, #172033)', muted = 'var(--muted, #64748b)';
const surface = 'var(--surface, #fff)', accent = 'var(--accent, #5750a5)';
const color = status => status === 'conflict' ? 'var(--danger, #b42318)'
  : status === 'evidence' ? '#644985'
  : status === 'compatible' ? 'var(--teal, #087c70)'
  : ['pending', 'unlinked', 'unassessed'].includes(status) ? muted : accent;
function markSelection(state) {
  for (const [index, node] of state.tokenNodes) {
    const selected = index === state.selectedIndex;
    node.setAttribute('aria-pressed', String(selected));
    node.setAttribute('data-selected', String(selected));
  }
}

function revealSelection(state, index) {
  const node = state.tokenNodes.get(index);
  const rect = node?.children[1];
  if (!rect || !state.scroll) return;
  const x = Number(rect.getAttribute?.('x') ?? rect.attributes.x);
  const width = Number(rect.getAttribute?.('width') ?? rect.attributes.width);
  const left = state.scroll.scrollLeft, viewport = state.scroll.clientWidth;
  if (viewport && x < left) state.scroll.scrollLeft = Math.max(0, x - 12);
  else if (viewport && x + width > left + viewport)
    state.scroll.scrollLeft = x + width - viewport + 12;
}
const clip = (text, limit) => {
  const points = Array.from(String(text ?? '').slice(0, limit * 2 + 2));
  return points.length > limit ? points.slice(0, limit - 1).join('') + '…' : points.join('');
};

function html(doc, tag, text, className) {
  const node = doc.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function svg(doc, tag, attributes = {}, text) {
  const node = doc.createElementNS(SVG_NS, tag);
  for (const [name, value] of Object.entries(attributes)) node.setAttribute(name, String(value));
  if (text !== undefined) node.textContent = text;
  return node;
}

function button(doc, text, action, disabled = false) {
  const node = html(doc, 'button', text);
  node.type = 'button'; node.disabled = disabled;
  node.addEventListener('click', action);
  return node;
}

function layout(state) {
  const start = state.tokenPage * TOKEN_PAGE_SIZE;
  const visible = state.tokens.slice(start, start + TOKEN_PAGE_SIZE);
  const positions = new Map();
  let cursor = 24;
  const nodes = visible.map(token => {
    const label = clip(token.text, 26);
    const width = Math.max(token.kind === 'Word' ? 96 : 48, Array.from(label).length * 8 + 28);
    const node = {token, label, x: cursor, width, center: cursor + width / 2};
    cursor += width + 20;
    positions.set(token.index, node);
    return node;
  });
  const relevant = [], internal = [];
  let crossing = 0;
  for (const edge of state.edges) {
    const from = positions.get(edge.from), to = positions.get(edge.to);
    if (!from && !to) continue;
    relevant.push(edge);
    if (from && to) internal.push({edge, from, to}); else crossing++;
  }
  const distinct = new Map();
  for (const arc of internal) {
    const key = JSON.stringify([arc.edge.from, arc.edge.to, arc.edge.label, arc.edge.status]);
    const existing = distinct.get(key);
    if (existing) existing.count++; else distinct.set(key, {...arc, count: 1});
  }
  // The interval allocator keeps disjoint short arcs on the same level. Labels occupy space too.
  const arcs = [...distinct.values()].slice(0, ARC_PAGE_SIZE).sort((a, b) =>
    Math.abs(a.from.center - a.to.center) - Math.abs(b.from.center - b.to.center));
  const levels = [];
  for (const arc of arcs) {
    arc.label = clip(edgeName(arc.edge.label), 24) + (arc.count > 1 ? ' ×' + arc.count : '');
    arc.labelWidth = Array.from(arc.label).length * 6.5 + 14;
    arc.mid = (arc.from.center + arc.to.center) / 2;
    const left = Math.min(arc.from.center, arc.to.center, arc.mid - arc.labelWidth / 2) - 10;
    const right = Math.max(arc.from.center, arc.to.center, arc.mid + arc.labelWidth / 2) + 10;
    let level = levels.findIndex(segments => segments.every(([a, b]) => right < a || left > b));
    if (level === -1) { level = levels.length; levels.push([]); }
    levels[level].push([left, right]);
    arc.level = level;
  }
  const baseline = Math.max(80, levels.length * 34 + 42, state.pending ? state.previousBaseline : 0);
  return {start, nodes, arcs, relevant, internalCount: internal.length, crossing,
    distinctCount: distinct.size, width: Math.max(340, cursor + 4), height: baseline + 92, baseline};
}

function edgeDescription(state, edge) {
  const from = state.byIndex.get(edge.from), to = state.byIndex.get(edge.to);
  const occurrence = index => index >= 0 ? ' [' + (index + 1) + ']' : '';
  return '„' + clip(from?.text, 48) + '“' + occurrence(edge.from) + ' → „' + clip(to?.text, 48) +
    '“' + occurrence(edge.to) + ' · ' + edgeName(edge.label) + ' · ' + statusName(edge.status) +
    (edge.alternative ? ' · вариант ' + edge.alternative : '');
}

function draw(state) {
  const {container, doc} = state, view = layout(state);
  state.baseline = view.baseline;
  const root = html(doc, 'div', undefined, 'syntax-graph');
  const pages = Math.ceil(state.tokens.length / TOKEN_PAGE_SIZE);
  if (pages > 1) {
    const controls = html(doc, 'div', undefined, 'syntax-graph-controls');
    const change = direction => { state.tokenPage += direction; state.edgePage = 0; draw(state); };
    controls.append(button(doc, '← Предишни думи', () => change(-1), state.tokenPage === 0),
      html(doc, 'span', 'Токени ' + (view.start + 1) + '–' + (view.start + view.nodes.length) +
        ' от ' + state.tokens.length),
      button(doc, 'Следващи думи →', () => change(1), state.tokenPage === pages - 1));
    root.append(controls);
  }
  if (!view.nodes.length) {
    root.append(html(doc, 'p', 'Няма думи за изобразяване.', 'syntax-graph-empty'));
  } else {
    const scroll = html(doc, 'div', undefined, 'syntax-graph-scroll');
    scroll.setAttribute('tabindex', '0');
    scroll.setAttribute('role', 'region');
    scroll.setAttribute('aria-label', 'Схема с локални връзки. Превъртете за всички думи.');
    const chart = svg(doc, 'svg', {width: view.width, height: view.height,
      viewBox: '0 0 ' + view.width + ' ' + view.height, class: 'syntax-graph-svg',
      role: 'group', 'aria-label': 'Локални езикови връзки; посоката е от първата към втората опора.'});
    const defs = svg(doc, 'defs');
    for (const status of ['candidate', 'compatible', 'selected', 'evidence', 'conflict', 'ambiguous', 'unassessed']) {
      const marker = svg(doc, 'marker', {id: state.id + '-' + status, viewBox: '0 0 10 10',
        refX: 9, refY: 5, markerWidth: 6, markerHeight: 6, orient: 'auto'});
      marker.append(svg(doc, 'path', {d: 'M 0 0 L 10 5 L 0 10 Z', fill: color(status)}));
      defs.append(marker);
    }
    chart.append(defs);
    for (const arc of view.arcs) {
      const {edge, from, to} = arc, y = view.baseline, top = y - 30 - arc.level * 34;
      const direction = to.center > from.center ? 1 : -1;
      const fromX = from.center - direction * 3, toX = to.center + direction * 3;
      const group = svg(doc, 'g', {'data-edge-from': edge.from, 'data-edge-to': edge.to,
        'data-edge-status': edge.status});
      group.append(svg(doc, 'title', {}, edgeDescription(state, edge) +
        (arc.count > 1 ? ' · ' + arc.count + ' варианта; всички са в списъка.' : '')));
      group.append(svg(doc, 'path', {d: 'M ' + fromX + ' ' + y + ' V ' + (top + 8) +
        ' Q ' + fromX + ' ' + top + ' ' + (fromX + direction * 8) + ' ' + top +
        ' H ' + (toX - direction * 8) + ' Q ' + toX + ' ' + top + ' ' + toX + ' ' +
        (top + 8) + ' V ' + (y - 5), fill: 'none', stroke: color(edge.status), 'stroke-width': 1.7,
        'stroke-dasharray': edge.status === 'evidence' ? '2 4' : ['compatible', 'selected', 'conflict'].includes(edge.status) ? 'none' : '5 4',
        'marker-end': 'url(#' + state.id + '-' +
          (['candidate', 'compatible', 'selected', 'evidence', 'conflict', 'ambiguous'].includes(edge.status) ? edge.status : 'unassessed') + ')'}));
      group.append(svg(doc, 'rect', {x: arc.mid - arc.labelWidth / 2, y: top - 9,
        width: arc.labelWidth, height: 18, rx: 4, fill: surface}));
      group.append(svg(doc, 'text', {x: arc.mid, y: top + 4, 'text-anchor': 'middle',
        'font-size': 11, fill: color(edge.status)}, arc.label));
      chart.append(group);
    }
    state.tokenNodes = new Map();
    for (const node of view.nodes) {
      const {token, x, width, center} = node;
      const roles = token.roles ?? [], firstRole = roles[0];
      const hint = roles.length > 1 ? roles.length + ' роли / прочита'
        : firstRole ? roleName(firstRole.role) : statusName(token.status);
      const group = svg(doc, 'g', {class: 'syntax-token', tabindex: '0', role: 'button',
        'data-token-index': token.index, 'data-token-status': token.status,
        'aria-pressed': String(token.index === state.selectedIndex),
        'data-selected': String(token.index === state.selectedIndex),
        'aria-label': token.text + ' · ' + statusName(token.status) + '. Отвори подробности.'});
      group.append(svg(doc, 'title', {}, token.text + ' · ' + hint + ' · ' + statusName(token.status)));
      group.append(svg(doc, 'rect', {x, y: view.baseline, width, height: 58, rx: 9,
        fill: surface, stroke: color(token.status), 'stroke-width': 1.5,
        'stroke-dasharray': token.status === 'pending' ? '4 4' : 'none'}));
      group.append(svg(doc, 'text', {x: center, y: view.baseline + 24,
        'text-anchor': 'middle', 'font-size': 15, 'font-weight': 600, fill: ink}, node.label));
      group.append(svg(doc, 'text', {x: center, y: view.baseline + 44,
        'text-anchor': 'middle', 'font-size': 10, fill: muted}, clip(hint, Math.floor((width - 12) / 6))));
      if (token.index >= 0)
        group.append(svg(doc, 'text', {x: center, y: view.baseline + 76,
          'text-anchor': 'middle', 'font-size': 10, fill: muted}, String(token.index + 1)));
      const select = () => { state.api.selectToken(token.index, {reveal: false}); state.onToken?.(token.index); };
      group.addEventListener('click', select);
      group.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
          event.preventDefault(); select();
        }
      });
      state.tokenNodes.set(token.index, group);
      chart.append(group);
    }
    scroll.append(chart); root.append(scroll);
    state.nextScroll = scroll;
    let note = view.internalCount ? 'Линии в този изглед: ' + view.internalCount + '.'
      : 'Няма локални връзки между показаните токени.';
    if (view.internalCount > view.distinctCount)
      note += ' Еднаквите линии са обединени; вариантите остават в списъка.';
    if (view.distinctCount > view.arcs.length)
      note += ' Схемата показва първите ' + view.arcs.length + '; всички са в списъка.';
    if (view.crossing) note += ' Връзки към други части на изречението: ' + view.crossing + '; вижте списъка.';
    root.append(html(doc, 'p', note, 'syntax-graph-note'));
    if (view.relevant.length) {
      const details = html(doc, 'details', undefined, 'syntax-edge-details');
      details.open = state.detailsOpen;
      details.append(html(doc, 'summary', 'Връзки и варианти (' + view.relevant.length + ')'));
      const listHost = html(doc, 'div');
      let listRendered = false;
      function drawEdges() {
        listRendered = true;
        const list = html(doc, 'ol', undefined, 'syntax-edge-list');
        const edgeStart = state.edgePage * EDGE_PAGE_SIZE;
        list.start = edgeStart + 1;
        for (const edge of view.relevant.slice(edgeStart, edgeStart + EDGE_PAGE_SIZE)) {
          const item = html(doc, 'li');
          item.append(html(doc, 'span', edgeDescription(state, edge) + ' '),
            button(doc, 'Към първата опора', () => state.api.focusToken(edge.from)),
            button(doc, 'Към втората опора', () => state.api.focusToken(edge.to)));
          list.append(item);
        }
        const controls = html(doc, 'div', undefined, 'syntax-edge-controls');
        const change = direction => { state.edgePage += direction; drawEdges(); };
        if (view.relevant.length > EDGE_PAGE_SIZE)
          controls.append(button(doc, '← Предишни връзки', () => change(-1), state.edgePage === 0),
            html(doc, 'span', 'Връзки ' + (edgeStart + 1) + '–' +
              Math.min(edgeStart + EDGE_PAGE_SIZE, view.relevant.length) + ' от ' + view.relevant.length),
            button(doc, 'Следващи връзки →', () => change(1), edgeStart + EDGE_PAGE_SIZE >= view.relevant.length));
        listHost.replaceChildren(list, controls);
      }
      details.addEventListener('toggle', () => {
        state.detailsOpen = details.open;
        if (details.open && !listRendered) drawEdges();
      });
      if (details.open) drawEdges();
      details.append(listHost); root.append(details);
    }
  }
  if (state.invalidEdges)
    root.append(html(doc, 'p', 'Връзки с недостъпна опора: ' + state.invalidEdges + '.', 'syntax-graph-note'));
  const left = state.scroll?.scrollLeft ?? 0, top = state.scroll?.scrollTop ?? 0;
  // Assemble off-DOM, then replace once; typing never observes an empty intermediate chart.
  container.replaceChildren(root);
  state.scroll = state.nextScroll; state.nextScroll = null;
  if (state.scroll) {
    state.scroll.scrollLeft = left; state.scroll.scrollTop = top;
    const follow = view.nodes.find(n => n.token.index === state.follow);
    if (follow && state.scroll.clientWidth) {
      if (follow.x < left) state.scroll.scrollLeft = follow.x - 12;
      else if (follow.x + follow.width > left + state.scroll.clientWidth)
        state.scroll.scrollLeft = follow.x + follow.width - state.scroll.clientWidth + 12;
    }
    state.follow = undefined;
  }
}

/** Render an already selected sentence. Call after input handling, e.g. in requestAnimationFrame. */
export function renderGraph(container, tokens, edges, onToken, follow) {
  let state = states.get(container);
  if (state && state.tokensInput === tokens && state.edgesInput === edges) {
    state.onToken = onToken;
    return state.api;
  }
  const previous = state;
  const visibleTokens = (tokens ?? []).filter(token => token.kind !== 'Whitespace');
  const byIndex = new Map(visibleTokens.map(token => [token.index, token]));
  const validEdges = (edges ?? []).filter(edge => edge.from !== edge.to &&
    byIndex.has(edge.from) && byIndex.has(edge.to));
  state = {container, doc: container.ownerDocument, tokensInput: tokens, edgesInput: edges,
    tokens: visibleTokens, edges: validEdges, byIndex, onToken,
    invalidEdges: (edges?.length ?? 0) - validEdges.length, id: previous?.id ?? 'syntax-graph-' + ++nextId,
    tokenPage: Math.min(previous?.tokenPage ?? 0, Math.max(0, Math.ceil(visibleTokens.length / TOKEN_PAGE_SIZE) - 1)),
    edgePage: 0, detailsOpen: previous?.detailsOpen ?? false, scroll: previous?.scroll, tokenNodes: new Map(),
    selectedIndex: byIndex.has(previous?.selectedIndex) ? previous.selectedIndex : null};
  state.pending = visibleTokens.some(token => token.status === 'pending');
  const followed = follow == null ? -1 : visibleTokens.findIndex(token => token.index === follow);
  if (followed >= 0) { state.follow = follow; state.tokenPage = Math.floor(followed / TOKEN_PAGE_SIZE); }
  state.previousBaseline = previous?.baseline ?? 80;
  state.api = {
    selectToken(index, {focus = false, reveal = true} = {}) {
      if (index == null) { state.selectedIndex = null; markSelection(state); return true; }
      const position = state.tokens.findIndex(token => token.index === index);
      if (position < 0) return false;
      state.selectedIndex = index;
      const page = Math.floor(position / TOKEN_PAGE_SIZE);
      if (reveal && page !== state.tokenPage) { state.tokenPage = page; state.edgePage = 0; draw(state); }
      else markSelection(state);
      if (reveal) revealSelection(state, index);
      if (focus) state.tokenNodes.get(index)?.focus?.({preventScroll: true});
      return true;
    },
    focusToken(index) {
      if (!state.api.selectToken(index, {focus: true})) return false;
      state.onToken?.(index);
      return true;
    }
  };
  states.set(container, state);
  draw(state);
  return state.api;
}
