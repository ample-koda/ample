// The keys of AMPLE in one place (GUI flow 10): a registry of commands (id, label, default keys, what it does), the overrides of the person (localStorage `ample.keymap`:
// { "<command id>": ["Ctrl+K"] }; there is no screen for them yet, the structure is ready for remapping), the sheet that lists them (? or F1) and the command palette (Ctrl+K).
import { el, toast } from './ui.js';
import { S, flat, notify, select } from './store.js';

const commands = new Map();
const STORE = 'ample.keymap';
const readOverrides = () => { try { return JSON.parse(localStorage.getItem(STORE) || '{}') || {}; } catch { return {}; } };

/** @param {{ id: string, label: string, group?: string, keys?: string[], run: () => void }} command */
export function registerCommand(command) { commands.set(command.id, { group: 'General', keys: [], ...command }); }
export const listCommands = () => [...commands.values()];
/** The keys a command answers to: the person's own, else the defaults. */
export function keysOf(id) { const own = readOverrides()[id]; return Array.isArray(own) ? own : commands.get(id)?.keys ?? []; }
/** For a later screen of the settings: remap one command (an empty list switches it off, null gives the defaults back). */
export function setKeys(id, keys) {
  const all = readOverrides();
  if (keys === null) delete all[id]; else all[id] = keys;
  try { localStorage.setItem(STORE, JSON.stringify(all)); } catch { /* storage may be blocked */ }
}
export function runCommand(id) { const command = commands.get(id); if (command) command.run(); return Boolean(command); }

/** "Ctrl+K", "Shift+/", "F5": the text of a key press, the same way keys are written in the registry. */
export function keyText(event) {
  const key = event.key === ' ' ? 'Space' : event.key.length === 1 ? event.key.toUpperCase() : event.key;
  return [event.ctrlKey || event.metaKey ? 'Ctrl' : '', event.altKey ? 'Alt' : '', event.shiftKey && (key.length > 1 || event.ctrlKey || event.altKey || event.metaKey) ? 'Shift' : '', key].filter(Boolean).join('+');
}

const typingIn = (target) => target instanceof HTMLElement && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName) || !!target.closest?.('.cm-editor'));
const clickTab = (name) => { const tab = [...document.querySelectorAll('#btabs button[data-t]')].find((b) => b.dataset.t === name); if (tab) tab.click(); else toast('That view is not available here.'); };
const selectedNode = () => flat().nodes.find((n) => S.selSet?.has?.(n.id) || n.id === S.sel);

registerCommand({ id: 'palette', label: 'Command palette: go to a block or run a command (Ctrl+K stays Koda)', keys: ['Ctrl+Shift+P'], run: () => openPalette() });
registerCommand({ id: 'help.keys', label: 'Show the keys', keys: ['?', 'F1'], run: () => openSheet() });
registerCommand({ id: 'view.playlist', group: 'Views', label: 'Playlist (the arrangement)', keys: ['F5'], run: () => clickTab('arr') });
registerCommand({ id: 'view.sequencer', group: 'Views', label: 'Sequencer of the selected block (or the first one)', keys: ['F6'], run: () => {
  const node = selectedNode()?.tpl === 'sequencer' ? selectedNode() : flat().nodes.find((n) => n.tpl === 'sequencer');
  if (!node) { toast('There is no sequencer block in this song.'); return; }
  import('./sequencer.js').then((m) => m.openSequencer(node.id));
} });
registerCommand({ id: 'view.jam', group: 'Views', label: 'AMPLE Jam of the selected clip', keys: ['F7'], run: () => {
  const button = [...document.querySelectorAll('#right button')].find((b) => /AMPLE Jam/.test(b.textContent));
  if (button) button.click(); else toast('Select a MIDI clip first.');
} });
registerCommand({ id: 'view.browser', group: 'Views', label: 'Block library: search', keys: ['F8', '/'], run: () => { const box = document.querySelector('#lib-q'); if (box) { box.focus(); box.select?.(); } } });
registerCommand({ id: 'view.mixer', group: 'Views', label: 'Mixer', keys: ['F9'], run: () => clickTab('mixer') });
registerCommand({ id: 'build', group: 'Song', label: 'Build the song', keys: [], run: () => document.querySelector('#btn-build')?.click() });

// keys that work without this registry (listed on the sheet so they can be found; they cannot be remapped yet)
const FIXED = [['Space', 'Play or stop the song'], ['Ctrl+B', 'Build'], ['Ctrl+Z / Ctrl+Y', 'Undo / redo'], ['Esc', 'Close the window that was used last'], ['Delete', 'Delete the selected block or clip']];

let overlay = null;
const closeOverlay = () => { overlay?.remove(); overlay = null; };

export function openSheet() {
  closeOverlay();
  const groups = {};
  for (const command of listCommands()) (groups[command.group] ||= []).push(command);
  const rows = Object.entries(groups).flatMap(([group, list]) => [el('h4', { textContent: group }), ...list.map((c) => el('div', { className: 'keyrow' }, [el('span', { textContent: c.label }), el('span', { className: 'keys' }, keysOf(c.id).map((k) => el('kbd', { textContent: k })))]))]);
  overlay = el('div', { id: 'keysheet', className: 'cmdk-overlay', role: 'dialog', 'aria-label': 'Keys' }, [el('div', { className: 'cmdk-box keysheet' }, [
    el('h3', { textContent: 'Keys' }), ...rows, el('h4', { textContent: 'Always there' }),
    ...FIXED.map(([k, label]) => el('div', { className: 'keyrow' }, [el('span', { textContent: label }), el('span', { className: 'keys' }, [el('kbd', { textContent: k })])])),
    el('p', { className: 'note', textContent: 'Your own keys are kept in this browser (ample.keymap); a screen to change them comes later. Esc closes this sheet.' }),
  ])]);
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) closeOverlay(); });
  document.body.append(overlay);
}

export function openPalette() {
  closeOverlay();
  const input = el('input', { id: 'cmdk-q', type: 'text', placeholder: 'Go to a block or run a command...', 'aria-label': 'Command palette', autocomplete: 'off' });
  const list = el('div', { className: 'cmdk-list', role: 'listbox' });
  let items = [], at = 0;
  const entries = () => [
    ...listCommands().map((c) => ({ label: c.label, hint: keysOf(c.id).join(' / '), kind: 'command', run: () => c.run() })),
    ...flat().nodes.map((n) => ({ label: n.title || n.id, hint: `${n.tpl} block`, kind: 'block', run: () => { select(n.id); notify(); document.dispatchEvent(new CustomEvent('ample:reveal', { detail: n.id })); } })),
  ];
  const draw = () => {
    const q = input.value.trim().toLowerCase();
    items = entries().filter((e) => !q || e.label.toLowerCase().includes(q) || e.hint.toLowerCase().includes(q)).slice(0, 40);
    at = Math.min(at, Math.max(0, items.length - 1));
    list.replaceChildren(...items.map((e, i) => { const row = el('div', { className: `cmdk-item${i === at ? ' on' : ''}`, role: 'option', 'aria-selected': String(i === at), 'data-kind': e.kind }, [el('span', { textContent: e.label }), el('span', { className: 'note', textContent: e.hint })]); row.onmousedown = (ev) => { ev.preventDefault(); choose(i); }; return row; }));
  };
  const choose = (i) => { const item = items[i]; closeOverlay(); item?.run(); };
  input.oninput = () => { at = 0; draw(); };
  input.onkeydown = (e) => {
    if (e.key === 'ArrowDown') { at = Math.min(items.length - 1, at + 1); draw(); e.preventDefault(); }
    else if (e.key === 'ArrowUp') { at = Math.max(0, at - 1); draw(); e.preventDefault(); }
    else if (e.key === 'Enter') { e.preventDefault(); choose(at); }
  };
  overlay = el('div', { id: 'cmdk', className: 'cmdk-overlay', role: 'dialog', 'aria-label': 'Command palette' }, [el('div', { className: 'cmdk-box' }, [input, list])]);
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay) closeOverlay(); });
  document.body.append(overlay);
  draw(); input.focus();
}

export function initKeymap() {
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && overlay) { closeOverlay(); event.preventDefault(); event.stopPropagation(); return; }
    if (event.defaultPrevented || document.querySelector('dialog:modal')) return;
    const text = keyText(event), typing = typingIn(event.target);
    if (typing && !/^(Ctrl\+|F\d)/.test(text)) return;   // a plain key (such as ?) is text while someone types
    const command = listCommands().find((c) => keysOf(c.id).includes(text));
    if (!command) return;
    event.preventDefault(); event.stopPropagation();
    command.run();
  }, true);
}
