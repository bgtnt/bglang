import { livePayload, continuationSnapshot, bindContinuation, applyContinuation } from "../live-client.js";

// Completion is an explicit, independent request. Editing only invalidates its current snapshot.
export function installCompletion({ editor, button, target, host, transport, getRevision, replace, limit = 12 }) {
  const doc = editor.ownerDocument;
  const key = "tree-completion", listId = host.id + "-list";
  let generation = 0, composing = false, captured, binding, options = [], active = -1;
  const mode = () => target.value;
  const current = (snapshot, job = generation) => job === generation && !composing &&
    doc.activeElement === editor && snapshot?.text === editor.value && snapshot.caret === editor.selectionStart &&
    editor.selectionStart === editor.selectionEnd && snapshot.revision === getRevision() && snapshot.requestTarget === mode();

  function close() {
    generation++;
    captured = binding = undefined;
    options = []; active = -1;
    transport.cancel(key);
    host.hidden = true;
    host.replaceChildren();
    editor.setAttribute("aria-expanded", "false");
    editor.removeAttribute("aria-activedescendant");
  }

  function render(notice = "") {
    const make = (tag, text, className) => {
      const node = doc.createElement(tag);
      node.textContent = text;
      if (className) node.className = className;
      return node;
    };
    const nodes = [make("p", notice || (binding?.requestTarget === "Noun" ?
      "Съществителни, съвместими с контекста пред курсора" : "Речникови форми, съвместими с контекста пред курсора"), "completion-title")];
    if (options.length) {
      const list = doc.createElement("div");
      list.id = listId;
      list.className = "completion-options";
      list.setAttribute("role", "listbox");
      list.setAttribute("aria-label", "Съвместими речникови форми");
      const renderedGeneration = generation;
      options.forEach((form, index) => {
        const option = make("button", form, "completion-option");
        option.type = "button";
        option.id = host.id + "-option-" + index;
        option.setAttribute("role", "option");
        option.setAttribute("aria-selected", String(index === active));
        option.addEventListener("pointerdown", event => event.preventDefault());
        option.addEventListener("click", () => { if (renderedGeneration === generation) accept(form); });
        list.append(option);
      });
      nodes.push(list, make("p", "Ред по речникова оценка; текстът след курсора не е проверен. ↓ избира, Enter или Tab приема, Esc затваря.", "completion-note"));
    }
    host.replaceChildren(...nodes);
    host.hidden = false;
    editor.setAttribute("aria-expanded", "true");
    editor.setAttribute("aria-controls", host.id);
    if (active >= 0) editor.setAttribute("aria-activedescendant", host.id + "-option-" + active);
    else editor.removeAttribute("aria-activedescendant");
  }

  function accept(form) {
    if (!current(captured) || !binding) return false;
    const applied = applyContinuation(editor.value, editor.selectionStart, editor.selectionEnd, getRevision(),
      binding, form, composing, mode());
    if (!applied || applied.text.length > editor.maxLength && editor.maxLength >= 0) return false;
    const change = { start: binding.start, end: binding.end, original: editor.value.slice(binding.start, binding.end),
      replacement: form, caret: applied.caret };
    if (!replace(change)) return false;
    close();
    return true;
  }

  function request() {
    if (composing || !editor.value.trim()) return false;
    const snapshot = continuationSnapshot(editor.value, editor.selectionStart, editor.selectionEnd, getRevision(), mode());
    if (!snapshot) return false;
    close();
    editor.focus({ preventScroll: true });
    captured = snapshot;
    const job = generation;
    render("Търсене на съвместими речникови форми…");
    void transport.post("analyze-text", livePayload(snapshot.text, true,
      { caret: snapshot.caret, limit, target: snapshot.requestTarget }), { key, current: () => current(snapshot, job) })
      .then(result => {
        if (!current(snapshot, job) || result.stale) return;
        if (result.error || !result.http?.ok) {
          render("Продълженията са недостъпни. Опитай отново.");
          return;
        }
        binding = bindContinuation(snapshot, result.response?.data?.continuation);
        const original = snapshot.text.slice(snapshot.start, snapshot.end);
        options = binding ? [...new Set(binding.forms)].filter(form => form !== original).slice(0, limit) : [];
        active = -1;
        render(options.length ? "" : "Няма върнати съвместими форми за това място.");
      });
    return true;
  }

  function check() { if (captured && !current(captured)) close(); }
  editor.addEventListener("input", close);
  editor.addEventListener("compositionstart", () => { composing = true; close(); });
  editor.addEventListener("compositionend", () => { composing = false; close(); });
  editor.addEventListener("blur", close);
  for (const name of ["select", "click", "keyup"]) editor.addEventListener(name, check);
  doc.addEventListener("selectionchange", check);
  target.addEventListener("change", close);
  button.addEventListener("pointerdown", event => event.preventDefault());
  button.addEventListener("click", request);
  editor.addEventListener("keydown", event => {
    if (composing || event.isComposing || event.keyCode === 229) return;
    if (event.ctrlKey && !event.altKey && !event.metaKey && (event.key === " " || event.code === "Space")) {
      if (request()) event.preventDefault();
      return;
    }
    if (host.hidden) return;
    const plain = !event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey;
    if (event.key === "Escape") { event.preventDefault(); close(); }
    else if (plain && event.key === "ArrowDown" && options.length && current(captured)) {
      event.preventDefault(); active = (active + 1) % options.length; render();
    } else if (plain && event.key === "ArrowUp" && active >= 0 && current(captured)) {
      event.preventDefault(); active--; render();
    } else if (plain && (event.key === "Enter" || event.key === "Tab") && active >= 0) {
      if (accept(options[active])) event.preventDefault(); else check();
    }
  });
  editor.setAttribute("aria-autocomplete", "list");
  close();
  return { request, close };
}
