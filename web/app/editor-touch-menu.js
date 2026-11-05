// A menu for the text editor on a touch screen: copy, cut, paste and select all, shown by a long press.
// The editor draws its own selection, so the browser has no selection of its own to put its menu on: a long press on selected text started to drag the text and no menu came.
// Used by the editors of Studio and of Remix (the same editor); it only needs the CodeMirror view. It lives in the folder of Remix, which the public Remix serves.
// @ample:samples

const HOLD_MS = 500, MOVE_PX = 10;

const STYLE = `
.editor-touch-menu { position: fixed; z-index: 100; display: flex; gap: 2px; padding: 4px; border-radius: 12px; background: var(--panel2, #1c2230); border: 1px solid var(--line-strong, #3a4558); box-shadow: 0 6px 22px rgba(0, 0, 0, .55); }
.editor-touch-menu button { padding: 10px 14px; border: 0; border-radius: 8px; background: transparent; color: var(--text, #e8ecff); font: inherit; font-size: 15px; cursor: pointer; }
.editor-touch-menu button:disabled { opacity: .35; }
.editor-touch-menu button:not(:disabled):active { background: var(--panel, #12141c); }
`;
function addStyle() {
  if (document.getElementById('editor-touch-menu-style')) return;
  const style = document.createElement('style');
  style.id = 'editor-touch-menu-style';
  style.textContent = STYLE;
  document.head.append(style);
}

/**
 * Puts the touch menu on an editor.
 * @param {{ state: any, dispatch: Function, dom: HTMLElement, focus: Function }} view the CodeMirror view
 * @returns {() => void} removes it again
 */
export function installTouchMenu(view) {
  addStyle();
  let timer = 0, start = null, menu = null, lastTouch = 0;

  const close = () => { menu?.remove(); menu = null; };
  const selected = () => { const { from, to } = view.state.selection.main; return view.state.sliceDoc(from, to); };

  async function copy(cut) {
    const text = selected();
    if (!text) return;
    try { await navigator.clipboard.writeText(text); } catch { fallbackCopy(text); }
    if (cut) { const { from, to } = view.state.selection.main; view.dispatch({ changes: { from, to, insert: '' }, userEvent: 'delete.cut' }); }
  }

  async function paste() {
    let text = '';
    try { text = await navigator.clipboard.readText(); } catch { /* the browser asked and was refused, or has no clipboard: nothing to paste */ }
    if (!text) return;
    const { from, to } = view.state.selection.main;
    view.dispatch({ changes: { from, to, insert: text }, selection: { anchor: from + text.length }, userEvent: 'input.paste' });
  }

  function fallbackCopy(text) {
    const box = document.createElement('textarea');
    box.value = text; box.style.position = 'fixed'; box.style.opacity = '0';
    document.body.append(box); box.select();
    try { document.execCommand('copy'); } catch { /* nothing more to try */ }
    box.remove();
  }

  function show(x, y) {
    close();
    const has = selected().length > 0;
    menu = document.createElement('div');
    menu.className = 'editor-touch-menu';
    menu.setAttribute('role', 'menu');
    const item = (label, enabled, action) => {
      const button = document.createElement('button');
      button.type = 'button'; button.textContent = label; button.disabled = !enabled; button.setAttribute('role', 'menuitem');
      button.onpointerdown = (event) => event.preventDefault();   // keep the selection while the finger lands on the button
      button.onclick = async () => { await action(); close(); view.focus(); };
      menu.append(button);
    };
    item('Copy', has, () => copy(false));
    item('Cut', has, () => copy(true));
    item('Paste', true, paste);
    item('Select all', true, () => view.dispatch({ selection: { anchor: 0, head: view.state.doc.length } }));
    document.body.append(menu);
    const box = menu.getBoundingClientRect();
    menu.style.left = `${Math.max(6, Math.min(x - box.width / 2, window.innerWidth - box.width - 6))}px`;
    menu.style.top = `${Math.max(6, y - box.height - 18)}px`;   // above the finger, so that the hand does not cover it
  }

  const onDown = (event) => {
    if (event.pointerType !== 'touch') return;
    lastTouch = Date.now();
    start = { x: event.clientX, y: event.clientY };
    clearTimeout(timer);
    timer = setTimeout(() => { if (start) show(start.x, start.y); start = null; }, HOLD_MS);
  };
  const onMove = (event) => { if (start && Math.hypot(event.clientX - start.x, event.clientY - start.y) > MOVE_PX) { clearTimeout(timer); start = null; } };
  const onEnd = () => { clearTimeout(timer); start = null; };
  const onContext = (event) => {
    // the long press of a touch screen also sends a contextmenu event: it is ours then (a mouse keeps the menu of the browser)
    if (Date.now() - lastTouch > 1500) return;
    event.preventDefault();
    show(event.clientX, event.clientY);
  };
  const onDragStart = (event) => { if (Date.now() - lastTouch < 1500) event.preventDefault(); };   // the selected text is not dragged away by the long press
  const onOutside = (event) => { if (menu && !menu.contains(event.target)) close(); };

  view.dom.addEventListener('pointerdown', onDown, true);
  view.dom.addEventListener('pointermove', onMove, true);
  view.dom.addEventListener('pointerup', onEnd, true);
  view.dom.addEventListener('pointercancel', onEnd, true);
  view.dom.addEventListener('contextmenu', onContext, true);
  view.dom.addEventListener('dragstart', onDragStart, true);
  document.addEventListener('pointerdown', onOutside, true);
  return () => {
    close();
    view.dom.removeEventListener('pointerdown', onDown, true); view.dom.removeEventListener('pointermove', onMove, true);
    view.dom.removeEventListener('pointerup', onEnd, true); view.dom.removeEventListener('pointercancel', onEnd, true);
    view.dom.removeEventListener('contextmenu', onContext, true); view.dom.removeEventListener('dragstart', onDragStart, true);
    document.removeEventListener('pointerdown', onOutside, true);
  };
}

/**
 * On a touch screen, selecting text must not pop the keyboard up: while a selection exists the editor asks for no keyboard (inputmode none) and `onSelect` is called (close a list of suggestions);
 * a short tap puts the keyboard back, so typing works as before.
 * @param {{ state: any, hasFocus: boolean, contentDOM: HTMLElement }} view the CodeMirror view
 * @param {() => void} onSelect called when text gets selected
 * @returns {() => void} removes it again
 */
export function installKeyboardGuard(view, onSelect = () => {}) {
  if (!globalThis.matchMedia?.('(pointer: coarse)').matches) return () => {};
  const dom = view.contentDOM;
  let down = null, frame = 0;
  const allow = () => dom.removeAttribute('inputmode');
  const check = () => {
    cancelAnimationFrame(frame);
    frame = requestAnimationFrame(() => {   // after the editor has taken the new selection into its state
      if (!view.hasFocus || view.state.selection.main.empty) return;
      dom.setAttribute('inputmode', 'none');
      onSelect();
    });
  };
  const onDown = (event) => { down = { t: performance.now(), x: event.clientX, y: event.clientY }; };
  const onUp = (event) => {
    if (down && performance.now() - down.t < 300 && Math.hypot(event.clientX - down.x, event.clientY - down.y) < MOVE_PX) allow();   // a tap: typing again
    down = null;
  };
  document.addEventListener('selectionchange', check);
  dom.addEventListener('pointerdown', onDown, true);
  dom.addEventListener('pointerup', onUp, true);
  dom.addEventListener('blur', allow);
  return () => { document.removeEventListener('selectionchange', check); dom.removeEventListener('pointerdown', onDown, true); dom.removeEventListener('pointerup', onUp, true); dom.removeEventListener('blur', allow); cancelAnimationFrame(frame); allow(); };
}
