// The storage clean-up as a modal in the spirit of the old disk defragmenter: every little square is a slice of the take data.
// It opens at once and first looks at your takes: a scanner sweeps over the squares and, one block at a time, shows what it found.
// Blue squares stay (protected), orange ones go (the clean-up gives them back), yellow ones are temporary files. While the clean-up
// runs a reading head sweeps over the orange squares and each one turns into free space, so you see what is being reclaimed.
import { api } from './api.js';
import { el, toast } from './ui.js';

const COLS = 36, ROWS = 12, TOTAL = COLS * ROWS;

const hashOf = (text) => [...String(text)].reduce((h, c) => (Math.imul(h, 31) + c.charCodeAt(0)) >>> 0, 7);
const seeded = (seed) => {
  let t = seed >>> 0;
  return () => { t += 0x6D2B79F5; let r = Math.imul(t ^ (t >>> 15), 1 | t); r ^= r + Math.imul(r ^ (r >>> 7), 61 | r); return ((r ^ (r >>> 14)) >>> 0) / 4294967296; };
};
const calm = () => typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/** Splits TOTAL squares over the lines by bytes (largest remainder); every non-empty line gets at least one square. */
export function allocate(lines, total = TOTAL) {
  const live = lines.filter((l) => l.bytes > 0);
  const sum = live.reduce((s, l) => s + l.bytes, 0);
  if (!sum) return live.map((l) => ({ ...l, cells: 0 }));
  const raw = live.map((l) => ({ ...l, cells: Math.max(1, Math.floor((l.bytes / sum) * total)), rest: ((l.bytes / sum) * total) % 1 }));
  let spare = total - raw.reduce((s, l) => s + l.cells, 0);
  for (const l of [...raw].sort((a, b) => b.rest - a.rest)) { if (spare <= 0) break; l.cells += 1; spare -= 1; }
  while (spare < 0) { const big = raw.reduce((a, b) => (b.cells > a.cells ? b : a)); if (big.cells <= 1) break; big.cells -= 1; spare += 1; }
  return raw;
}

/**
 * The squares of the picture: protected data packed at the front, the rest scattered behind it like a fragmented disk.
 * Each square is { kind: 'p' | 'r' | 't', name }.
 */
export function layout({ protectedLines, reclaimLines, tempBytes }, seed = 1) {
  const groups = allocate([
    ...protectedLines.map((l) => ({ kind: 'p', name: l.name, bytes: l.bytes })),
    ...reclaimLines.map((l) => ({ kind: 'r', name: l.name, bytes: l.bytes })),
    ...(tempBytes > 0 ? [{ kind: 't', name: 'temp', bytes: tempBytes }] : []),
  ]);
  const cells = groups.flatMap((g) => Array.from({ length: g.cells }, () => ({ kind: g.kind, name: g.name })));
  const packed = cells.filter((c) => c.kind === 'p');
  const front = packed.slice(0, Math.floor(packed.length * 0.55));
  const tail = [...cells.filter((c) => c.kind !== 'p'), ...packed.slice(front.length)];
  const random = seeded(seed);
  for (let i = tail.length - 1; i > 0; i--) { const j = Math.floor(random() * (i + 1)); [tail[i], tail[j]] = [tail[j], tail[i]]; }
  return [...front, ...tail];
}

/**
 * Opens the clean-up modal at once.
 * planPromise: resolves to the dry run (the modal scans while it waits); or plan: a plan or stand-in that is already there;
 * overview: the storage overview; reasons: reason name -> label; size: bytes -> text; running: show the progress of a running clean-up;
 * onFinished(state): the modal ended ({ state: 'done' | 'cancelled' | 'failed' | 'closed' }).
 */
export function openDefrag({ plan = null, planPromise = null, overview, reasons, size, running = false, onFinished = () => {} }) {
  document.querySelector('#st-defrag')?.remove();
  const dlg = el('dialog', { id: 'st-defrag', className: 'defrag scanning', 'aria-label': 'Storage clean-up' });
  const grid = el('div', { className: 'defrag-grid', role: 'img', style: `grid-template-columns:repeat(${COLS},1fr)`, 'aria-label': 'Storage map: looking at your takes...' },
    Array.from({ length: TOTAL }, () => el('i', { className: 'dc dc-u' })));
  const squares = [...grid.children];
  // the live (role=status) text changes at most every 400 ms so a screen reader is not flooded; the final text is set directly
  let lastSaid = 0;
  const say = (text) => { const now = performance.now(); if (now - lastSaid < 400) return; lastSaid = now; status.textContent = text; };

  const legendItem = (cls, text) => el('span', { className: 'defrag-key' }, [el('i', { className: `dc ${cls}` }), ` ${text}`]);
  const legend = el('div', { className: 'defrag-legend', hidden: true });
  const sub = el('p', { className: 'defrag-sub', id: 'st-impact-line', hidden: true });
  const table = el('table', { className: 'tbl defrag-table', id: 'st-impact' });
  const details = el('details', { hidden: true }, [el('summary', { textContent: 'The details' }), table]);
  const status = el('p', { id: 'st-running', className: 'defrag-status', role: 'status', textContent: 'Looking at your takes...' });
  const progress = el('progress', { id: 'st-progress', max: 100, value: 0, hidden: true });
  const runButton = el('button', { id: 'st-run', type: 'button', className: 'primary', hidden: true, textContent: 'Clean up' });
  const notNow = el('button', { id: 'st-notnow', type: 'button', textContent: 'Not now', onclick: () => dlg.close() });
  const cancel = el('button', { id: 'st-cancel', type: 'button', textContent: 'Cancel', hidden: true, onclick: async () => {
    cancel.disabled = true; cancel.textContent = 'Cancelling...';   // until the state changes
    try { await api.post('/api/storage/cancel', {}); } catch (e) { cancel.disabled = false; cancel.textContent = 'Cancel'; toast(e.message); }
  } });
  const close = el('button', { id: 'st-close', type: 'button', className: 'primary', textContent: 'Close', hidden: true, onclick: () => dlg.close() });
  const x = el('button', { type: 'button', className: 'defrag-x', textContent: '×', title: 'Close', 'aria-label': 'Close', onclick: () => dlg.close() });
  dlg.append(
    el('div', { className: 'defrag-title' }, [el('span', { textContent: 'Storage clean-up' }), x]),
    el('div', { className: 'defrag-body' }, [
      el('p', { className: 'defrag-lead', textContent: 'Blue stays, orange goes. Nothing that is protected (current, chosen, baked, tagged or recent takes, and finals) is ever touched.' }),
      grid, legend, status, sub, details, progress,
      el('div', { className: 'row defrag-buttons' }, [runButton, notNow, cancel, close]),
    ]),
  );

  let cells = [], goIndexes = [], reclaimed = 0, polling = false, gives = 0, current = null, scanning = true;

  // while the dry run is being worked out a scanner sweeps over the grey squares
  const scan = async () => {
    let i = 0;
    while (scanning && dlg.isConnected) {
      squares.forEach((s) => s.classList.remove('dc-head'));
      squares[i % TOTAL].classList.add('dc-head');
      i += 3;
      await pause(18);
    }
    squares.forEach((s) => s.classList.remove('dc-head'));
  };

  const prepare = (p) => {
    current = p;
    gives = (p.bytes || 0) + (p.tempBytes || 0);
    const reclaimNames = new Set((p.byReason || []).map((r) => r.name));
    const lines = (overview.byReason || []).filter((r) => !reclaimNames.has(r.name) && r.name !== 'superseded' && r.name !== 'missing');
    const sum = lines.reduce((s, r) => s + r.bytes, 0);
    const protectedLines = sum > 0 ? lines.map((r) => ({ name: r.name, bytes: Math.round((r.bytes / sum) * (p.protectedBytes || sum)) })) : [{ name: 'current', bytes: p.protectedBytes || 0 }];
    cells = layout({ protectedLines, reclaimLines: (p.byReason || []).map((r) => ({ name: r.name, bytes: r.bytes })), tempBytes: p.tempBytes || 0 }, hashOf(p.fingerprint || 'ample'));
    // fewer groups than squares (a tiny project): the rest stays "unused" blue so the picture is always full
    while (cells.length < TOTAL) cells.push({ kind: 'p', name: 'current' });
    goIndexes = cells.map((c, i) => (c.kind === 'p' ? -1 : i)).filter((i) => i >= 0);
    squares.forEach((sq, i) => { sq.title = cells[i].kind === 'p' ? `Stays: ${reasons[cells[i].name] || cells[i].name}` : cells[i].kind === 'r' ? `Goes: ${reasons[cells[i].name] || cells[i].name}` : 'Goes: temporary files'; });
    grid.setAttribute('aria-label', `Storage map: ${size(p.protectedBytes || 0)} stays, ${size(gives)} can be given back.`);
    legend.replaceChildren(legendItem('dc-p', 'Stays (protected)'), legendItem('dc-r', 'Goes (gets reclaimed)'), ...(p.tempBytes ? [legendItem('dc-t', 'Temporary files')] : []), legendItem('dc-gone', 'Free again'));
    sub.textContent = `Gives back ${size(gives)}: free space goes from ${size(p.freeBefore || 0)} to ${size(p.freeAfter || 0)}. Stays protected: ${size(p.protectedBytes || 0)}.`;
    table.replaceChildren(el('tr', {}, ['Impact', 'Takes', 'Size'].map((t) => el('th', { textContent: t }))));
    (p.byReason || []).forEach((r) => table.append(el('tr', {}, [el('td', { textContent: reasons[r.name] || r.name }), el('td', { textContent: String(r.takes) }), el('td', { textContent: size(r.bytes) })])));
    if (p.tempItems) table.append(el('tr', {}, [el('td', { textContent: 'Temporary files (finished AI tasks, older than a day)' }), el('td', { textContent: String(p.tempItems) }), el('td', { textContent: size(p.tempBytes) })]));
    runButton.textContent = `Clean up ${size(gives)}`;
    runButton.onclick = async () => {
      // at once: the button goes off and the view shows it is starting up, before the server has answered
      if (runButton.disabled) return;
      const label = runButton.textContent;
      runButton.disabled = true; runButton.textContent = 'Starting...'; runButton.setAttribute('aria-busy', 'true');
      notNow.disabled = true; dlg.classList.add('starting');
      status.hidden = false; status.id = 'st-running'; status.textContent = 'Starting the clean-up...';
      progress.hidden = false; progress.removeAttribute('value');   // an indeterminate bar until the first progress arrives
      try { await api.post('/api/storage/clean', { fingerprint: p.fingerprint }); dlg.classList.remove('starting'); progress.value = 0; showRunning(); watch(); }
      catch (e) {
        dlg.classList.remove('starting'); runButton.disabled = false; runButton.textContent = label; runButton.removeAttribute('aria-busy'); notNow.disabled = false;
        status.hidden = true; progress.hidden = true; progress.value = 0;
        toast(`${e.code ? `${e.code}: ` : ''}${e.message}`);
      }
    };
  };

  const paint = (i) => { squares[i].className = `dc dc-${cells[i].kind}`; };

  // one block at a time: the scanner moves over the squares and each one takes its colour as it is looked at
  const reveal = async () => {
    const nothing = !current.takes && !current.tempItems && !gives;
    const per = Math.max(1, Math.ceil(TOTAL / 140));
    let goingBytes = 0;
    const each = goIndexes.length ? gives / goIndexes.length : 0;
    for (let i = 0; i < TOTAL; i++) {
      if (!dlg.isConnected) return;
      paint(i);
      if (cells[i].kind !== 'p') goingBytes += each;
      if (!calm() && i % per === 0) {
        squares.forEach((s) => s.classList.remove('dc-head'));
        if (i + 1 < TOTAL) squares[i + 1].classList.add('dc-head');
        say(nothing ? 'Looking at your takes...' : `Looking at your takes... ${size(goingBytes)} can go so far`);
        await pause(14);
      }
    }
    squares.forEach((s) => s.classList.remove('dc-head'));
    dlg.classList.remove('scanning');
    legend.hidden = false;
    status.textContent = nothing ? 'Nothing to clean up: every take is protected or recent.' : 'Done looking.';
    if (nothing) {
      status.id = 'st-nothing'; status.textContent = 'Nothing to clean up: every take is protected or recent.';
      close.hidden = false; notNow.hidden = true;
      return;
    }
    status.hidden = true; sub.hidden = false; details.hidden = false; runButton.hidden = false;
  };

  const sweep = (fraction) => {
    const count = Math.min(goIndexes.length, Math.floor(fraction * goIndexes.length));
    for (let k = reclaimed; k < count; k++) { squares[goIndexes[k]].classList.remove('dc-head'); squares[goIndexes[k]].classList.add('dc-gone'); }
    reclaimed = Math.max(reclaimed, count);
    squares.forEach((s) => s.classList.remove('dc-head'));
    if (reclaimed < goIndexes.length) squares[goIndexes[reclaimed]].classList.add('dc-head');
  };
  function showRunning() {
    dlg.classList.add('running'); runButton.hidden = true; notNow.hidden = true; cancel.hidden = false; status.hidden = false; status.id = 'st-running'; progress.hidden = false; sub.hidden = false; details.hidden = false; legend.hidden = false;
  }
  const finish = (state, text) => {
    dlg.classList.remove('running'); dlg.classList.add(state.state === 'done' ? 'done' : 'ended');
    cancel.hidden = true; close.hidden = false; progress.hidden = true; status.hidden = false; status.textContent = text;
    squares.forEach((s) => s.classList.remove('dc-head'));
    if (state.state === 'done') sweep(1);
    onFinished(state);
  };
  const apply = (s) => {
    // the first steps often free next to nothing (takes whose data is already gone), so the picture follows the steps as well as the bytes
    const byBytes = s.totalBytes ? s.bytes / s.totalBytes : 0, bySteps = s.total ? s.done / s.total : 0;
    const fraction = Math.min(1, Math.max(byBytes, bySteps));
    progress.value = Math.round(fraction * 100);
    say(`Cleaning up: ${size(s.bytes)} of ${size(s.totalBytes)} (${s.done} of ${s.total} steps)`);
    sweep(fraction);
  };
  async function watch() {
    if (polling) return;
    polling = true;
    try {
      for (;;) {
        const s = await api.get('/api/storage/status');
        if (s.state === 'running') { apply(s); await pause(600); if (!dlg.isConnected) return; continue; }
        if (s.state === 'done') finish(s, `All tidy! ${size(s.bytes)} given back.`);
        else if (s.state === 'cancelled') finish(s, `Stopped after ${size(s.bytes)}; nothing is damaged.`);
        else if (s.state === 'failed') finish(s, `Clean-up failed: ${s.error}`);
        else finish(s, 'Nothing is running.');
        return;
      }
    } catch (e) { toast(e.message); } finally { polling = false; }
  }

  dlg.addEventListener('close', () => { scanning = false; if (dlg.classList.contains('running')) toast('The clean-up keeps running; progress is on the Storage page.'); dlg.remove(); if (!dlg.classList.contains('done') && !dlg.classList.contains('ended')) onFinished({ state: 'closed' }); });
  document.body.append(dlg);
  dlg.showModal();

  (async () => {
    try {
      if (running && plan) {
        scanning = false; prepare(plan); squares.forEach((_, i) => paint(i)); dlg.classList.remove('scanning'); showRunning(); watch();
        return;
      }
      if (!calm()) scan();
      const p = plan || await planPromise;
      scanning = false;
      if (!dlg.isConnected) return;
      prepare(p);
      await reveal();
    } catch (e) {
      scanning = false;
      if (dlg.isConnected) dlg.close();
      toast(`${e.code ? `${e.code}: ` : ''}${e.message}`);
    }
  })();
  return dlg;
}
