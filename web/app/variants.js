import { registerPlaybackAudio } from './playback-volume.js';
// Variants: make several takes of a block, listen, score, keep, discard, pick; A/B compare with synchronised playback.
// @ample:variants @ample:compare

import { api } from './api.js';
import { S, edit, flat, notify } from './store.js';
import { $, el, showMenu, toast } from './ui.js';
import { drawPeaks, download, peaksOf, stop, toggle } from './player.js';

const KINDS = { both: 'variant', performance: 'resing', melody: 'rewrite' };
const enc = encodeURIComponent;
const vs = { block: null, count: 4, vary: 'both', data: null, status: '', fields: [], fieldsReady: false, proposal: [], proposalKey: '', running: null, currentIndex: 0, a: null, b: null, match: true };
const ab = { A: registerPlaybackAudio(new Audio()), B: registerPlaybackAudio(new Audio()), which: 'A', timer: 0, rms: { A: 0, B: 0 } };

const scoreKey = (take) => `ample.score:${S.project ? S.project.id : ''}:${take}`;
const load = (k) => { try { return localStorage.getItem(k) || ''; } catch { return ''; } };
const store = (k, v) => { try { localStorage.setItem(k, v); } catch { /* storage may be blocked */ } };

const targets = () => {
  const nodes = flat().nodes;
  const dyn = nodes.filter((n) => n.dyn);
  return dyn.length ? dyn : nodes.filter((n) => n.outs.some((o) => o[1] === 'audio'));
};

export function openVariants(block, vary) {
  vs.block = block;
  if (vary) vs.vary = vary === 'resing' ? 'performance' : 'melody';
  vs.data = null;
  document.querySelectorAll('#btabs button[data-t]').forEach((b) => b.classList.toggle('on', b.dataset.t === 'var'));
  document.querySelectorAll('.bsec').forEach((s) => s.classList.toggle('on', s.id === 'b-var'));
  $('#bottom').classList.remove('min');
  refresh().then(renderVariants);
}

async function refresh() {
  if (!vs.block || !S.path) return;
  try {
    vs.data = await api.get(`/api/control/block?path=${enc(S.path)}&block=${enc(vs.block)}`);
    const supported = vs.data.variantFields || [];
    if (!vs.fieldsReady) { vs.fields = supported.filter((field) => field.kind === 'seed' || field.kind === 'renderer-seed').map((field) => field.name); vs.fieldsReady = true; }
    else vs.fields = vs.fields.filter((name) => supported.some((field) => field.name === name));
    await updateProposal();
  } catch (e) { vs.data = null; vs.status = e.message; }
}

async function updateProposal() {
  const key = JSON.stringify([vs.block, vs.count, KINDS[vs.vary], vs.fields]);
  if (key === vs.proposalKey) return;
  vs.proposalKey = key;
  vs.proposal = [];
  if (!vs.fields.length || !vs.data) return;
  try { const result = await api.post('/api/control/variants/preview', { path: S.path, block: vs.block, count: vs.count, kind: KINDS[vs.vary], fields: vs.fields }); if (key === vs.proposalKey) vs.proposal = result.fields || []; }
  catch (e) { if (key === vs.proposalKey) vs.status = e.message; }
}

async function make() {
  if (vs.running) return;
  if (!vs.fields.length) { vs.status = 'Select at least one field to vary.'; renderVariants(); return; }
  const request = { path: S.path, block: vs.block, count: vs.count, kind: KINDS[vs.vary], fields: [...vs.fields], baseVersion: S.doc.version };
  const controller = new AbortController();
  vs.running = controller;
  vs.status = `Preparing ${request.count} takes of ${request.block}...`;
  renderVariants();
  try {
    await updateProposal();
    if (!vs.proposal.length) throw new Error('AMPLE could not prepare the field changes; no takes were rendered.');
    for (let index = 0; index < request.count; index++) {
      if (S.path !== request.path || S.doc.version !== request.baseVersion) throw new Error('The song changed; remaining takes were stopped. Completed takes remain available in the list.');
      vs.currentIndex = index;
      if (controller.signal.aborted) break;
      vs.status = `Making take ${index + 1} of ${request.count} of ${request.block}...`;
      renderVariants();
      await api.post('/api/control/variants', { ...request, count: 1, startIndex: index }, controller.signal);
      await refresh();
      vs.status = `Take ${index + 1} of ${request.count} ready${index + 1 < request.count ? '; starting the next...' : '.'}`;
      renderVariants();
    }
    if (controller.signal.aborted) vs.status = 'Stopped. Completed takes remain available in the list.';
    else vs.status = `${request.count} takes built. Each card lists its changes; repeating this request gives the same field values.`;
  } catch (e) {
    vs.status = e.name === 'AbortError' ? 'Stopped. Completed takes remain available in the list.' : `${e.code ? `${e.code}: ` : ''}${e.message}`;
  } finally {
    if (vs.running === controller) vs.running = null;
    await refresh();
    renderVariants();
  }
}

async function act(path, body) {
  try { await api.post(path, { path: S.path, block: vs.block, ...body }); } catch (e) { toast(e.message); }
  await refresh();
  renderVariants();
  notify();
}

async function pick(v) {
  const changes = (v.changes || []).filter((change) => change.name !== '$seed');
  if (changes.length) {
    const ops = changes.map((change) => ({ type: 'setParameter', block: vs.block, name: change.name, value: change.to }));
    if (!await edit(ops)) return;
  }
  await act('/api/control/select', { take: v.takeId });
}

export function renderVariants() {
  const box = $('#b-var');
  if (!box) return;
  const list = targets();
  if (!vs.block || !list.some((n) => n.id === vs.block)) vs.block = list.length ? list[0].id : null;
  if (vs.block && !vs.data && vs.loadedFor !== `${S.path}:${vs.block}`) { vs.loadedFor = `${S.path}:${vs.block}`; refresh().then(renderVariants); }
  box.innerHTML = '';
  box.append(el('p', { className: 'note', textContent: 'Variants of ' }));
  const sel = el('select', { style: 'width:auto' });
  list.forEach((n) => sel.append(el('option', { value: n.id, textContent: n.title, selected: n.id === vs.block })));
  sel.id = 'v-target';
  sel.disabled = !!vs.running;
  sel.onchange = () => { vs.block = sel.value; vs.data = null; vs.fields = []; vs.fieldsReady = false; vs.proposalKey = ''; refresh().then(renderVariants); };
  box.firstChild.append(sel, document.createTextNode('. Scores 0-100 are yours; measured scorers are opt-in plugins.'));
  const row = el('div', { className: 'row' });
  const vary = el('select', { id: 'v-vary', style: 'width:auto', title: 'What should differ between the variants?' });
  [['both', 'Variant'], ['performance', 'Re-sing (seed path)'], ['melody', 'Re-write (seed path)']].forEach(([v, l]) => vary.append(el('option', { value: v, textContent: l, selected: vs.vary === v })));
  vary.disabled = !!vs.running;
  vary.onchange = () => { vs.vary = vary.value; vs.status = ''; vs.proposalKey = ''; updateProposal().then(renderVariants); };
  const count = el('select', { id: 'v-count', style: 'width:auto' });
  [2, 4, 8, 20].forEach((c) => count.append(el('option', { value: c, textContent: `${c} takes`, selected: vs.count === c })));
  count.disabled = !!vs.running;
  count.onchange = () => { vs.count = Number(count.value); vs.status = ''; vs.proposalKey = ''; updateProposal().then(renderVariants); };
  row.append(el('button', { id: 'v-make', className: 'primary', textContent: 'Make variants', disabled: !vs.block || !!S.peek || !!vs.running, onclick: make }), vary, count);
  if (vs.running) row.append(el('button', { id: 'v-cancel-current', textContent: 'Cancel current take', onclick: () => { vs.status = `Cancelling take ${vs.currentIndex + 1}...`; vs.running?.abort(); renderVariants(); } }));
  row.append(el('span', { id: 'v-status', className: 'note', textContent: vs.status }));
  box.append(row);
  if (vs.data?.variantFields) {
    const fields = el('fieldset', { className: 'variant-fields' });
    fields.append(el('legend', { textContent: 'What should vary?' }));
    vs.data.variantFields.forEach((field) => {
      const input = el('input', { type: 'checkbox', checked: vs.fields.includes(field.name), style: 'width:auto' });
      input.disabled = !!vs.running;
      input.onchange = () => { vs.fields = input.checked ? [...vs.fields, field.name] : vs.fields.filter((name) => name !== field.name); vs.status = ''; vs.proposalKey = ''; updateProposal().then(renderVariants); };
      fields.append(el('label', { className: 'variant-field' }, [input, `${field.label} (${field.name})`]));
    });
    fields.append(el('p', { className: 'note', textContent: 'The mode above selects the seed derivation path; these checks select the fields that change. Overrides are temporary, so song text and locked source seeds stay unchanged.' }));
    if (vs.data.unsupportedVariantFields?.length) fields.append(el('p', { className: 'note', textContent: `Not varied automatically: ${vs.data.unsupportedVariantFields.map((field) => `${field.label} (${field.name})`).join(', ')}. Free text and file fields need explicit values.` }));
    if (vs.proposal.length) {
      const table = el('div', { className: 'variant-proposal' });
      vs.proposal.forEach((changes, index) => table.append(el('div', { className: 'note', textContent: `#${index + 1}: ${changes.map((change) => `${change.label} ${change.from} → ${change.to}`).join(' · ')}` })));
      fields.append(table);
      if (vs.fields.some((name) => new Set(vs.proposal.map((changes) => changes.find((change) => change.name === name)?.to)).size < vs.count)) fields.append(el('p', { className: 'note', textContent: 'Some selected fields have fewer distinct values than the requested takes, so their proposed values repeat.' }));
    } else if (!vs.fields.length) fields.append(el('p', { className: 'note', textContent: 'Choose one or more fields; with none selected, no takes will be made.' }));
    box.append(fields);
  }
  const grid = el('div', { id: 'vgrid' });
  box.append(grid);
  if (vs.data) {
    const sel2 = vs.data.selected;
    vs.data.variants.filter((v) => v.state !== 'discarded').forEach((v) => {
      const picked = sel2 === v.takeId;
      const flag = load(scoreKey(v.takeId) + ':flag');
      const c = el('div', { className: `card${picked ? ' picked' : v.state === 'kept' ? ' kept' : ''}${flag === 'rej' || v.state === 'discarded' ? ' rej' : ''}` });
      c.dataset.take = v.takeId;
      const cvs = el('canvas');
      cvs.width = 180;
      cvs.height = 34;
      c.append(cvs);
      peaksOf(v.takeId, 180).then((p) => drawPeaks(cvs, p.peaks, getComputedStyle(document.documentElement).getPropertyValue('--acc').trim())).catch(() => {});
      c.append(el('div', { className: 'note', textContent: `#${v.index + 1}  ${v.takeId.slice(0, 8)}  ${v.state}` }));
      const changes = v.changes || [];
      if (changes.length) c.append(el('div', { className: 'note variant-differences', textContent: changes.map((change) => `${change.label}: ${change.from} → ${change.to}`).join(' · ') }));
      const r1 = el('div', { className: 'row' });
      const score = el('input', { type: 'number', min: 0, max: 100, placeholder: '0-100', value: load(scoreKey(v.takeId)) });
      score.onchange = () => store(scoreKey(v.takeId), score.value === '' ? '' : String(Math.max(0, Math.min(100, Number(score.value)))));
      r1.append(el('button', { textContent: '▶', 'aria-label': 'Play or pause', title: 'Play or pause', onclick: () => toggle(v.takeId).catch((e) => { if (!e.toasted) toast(e.message); }) }), el('button', { textContent: '■', 'aria-label': 'Stop', title: 'Stop', onclick: stop }), score);
      [['rej', '✕'], ['maybe', '?'], ['good', '✓']].forEach(([f, t]) => r1.append(el('button', { textContent: t, title: f, onclick: () => { store(scoreKey(v.takeId) + ':flag', flag === f ? '' : f); renderVariants(); } })));
      c.append(r1);
      const r2 = el('div', { className: 'row' });
      r2.append(
        el('button', { textContent: v.state === 'kept' ? 'kept ✓' : 'keep', onclick: () => act('/api/control/variants/keep', { take: v.takeId }) }),
        el('button', { textContent: 'discard', onclick: () => act('/api/control/variants/discard', { take: v.takeId }) }),
        el('button', { textContent: 'apply & pick', className: 'primary', title: picked ? 'Apply the listed parameter values to block Properties. This take is already selected.' : 'Apply the listed parameter values to block Properties, then select this take. The renderer seed is kept with the take.', onclick: () => pick(v) }),
        el('button', { textContent: '▾', 'aria-label': 'Download', title: 'Download', onclick: (e) => { e.stopPropagation(); const r = e.currentTarget.getBoundingClientRect(); showMenu(r.left, r.bottom, [{ label: 'Download WAV', fn: () => download(v.takeId, 'wav') }, { label: 'Download MP3', fn: () => download(v.takeId, 'mp3') }]); } }),
      );
      const r3 = el('div', { className: 'row' });
      r3.append(el('button', { textContent: vs.a === v.takeId ? 'A ✓' : 'A', title: 'Compare: first take', onclick: () => { vs.a = vs.a === v.takeId ? null : v.takeId; renderVariants(); } }), el('button', { textContent: vs.b === v.takeId ? 'B ✓' : 'B', title: 'Compare: second take', onclick: () => { vs.b = vs.b === v.takeId ? null : v.takeId; renderVariants(); } }));
      c.append(r2, r3);
      grid.append(c);
    });
    if (!vs.data.variants.length) grid.append(el('p', { className: 'note', textContent: 'No variants yet. "Make variants" renders several takes with seeds derived from the master seed.' }));
  }
  if (vs.a && vs.b) box.append(abBar());
}

/* ---------- A/B compare with synchronised playback ---------- */
function abBar() {
  const bar = el('div', { className: 'row', id: 'ab-bar', style: 'margin-top:8px' });
  const label = el('span', { id: 'ab-which', className: 'note', textContent: `listening to ${ab.which}` });
  const match = el('input', { type: 'checkbox', id: 'ab-match', checked: vs.match, style: 'width:auto' });
  match.onchange = () => { vs.match = match.checked; applyAb(); };
  bar.append(
    el('b', { textContent: 'A/B compare' }),
    el('button', { id: 'ab-play', textContent: '▶ play both', onclick: abPlay }),
    el('button', { id: 'ab-switch', textContent: 'switch A/B', onclick: abSwitch }),
    el('button', { textContent: 'stop', onclick: abStop }),
    el('label', {}, [match, ' match loudness']),
    label,
  );
  return bar;
}

async function abLoad(slot, takeId) {
  const r = await api.get(`/api/takes/${enc(takeId)}`);
  if (!r.audioDataId) throw new Error('A take has no audio.');
  ab[slot].src = `/api/data/${r.audioDataId}`;
  ab.rms[slot] = (await peaksOf(takeId, 64)).rms || 0;
}

function applyAb() {
  const quiet = Math.min(ab.rms.A || 1, ab.rms.B || 1);
  ['A', 'B'].forEach((s) => {
    const gain = vs.match && ab.rms[s] > 0 ? Math.min(1, quiet / ab.rms[s]) : 1;
    ab[s].volume = ab.which === s ? gain : 0;
  });
}

async function abPlay() {
  try {
    await Promise.all([abLoad('A', vs.a), abLoad('B', vs.b)]);
    ab.A.currentTime = 0;
    ab.B.currentTime = 0;
    applyAb();
    await Promise.all([ab.A.play(), ab.B.play()]);
    clearInterval(ab.timer);
    ab.timer = setInterval(() => { const o = ab.which === 'A' ? ab.B : ab.A, m = ab.which === 'A' ? ab.A : ab.B; if (Math.abs(o.currentTime - m.currentTime) > 0.04) o.currentTime = m.currentTime; }, 200);
  } catch (e) { toast(e.message); }
}

function abSwitch() {
  ab.which = ab.which === 'A' ? 'B' : 'A';
  applyAb();
  const l = $('#ab-which');
  if (l) l.textContent = `listening to ${ab.which}`;
}

function abStop() {
  clearInterval(ab.timer);
  ab.A.pause();
  ab.B.pause();
}

/** For tests: how far apart the two players are, in seconds. */
export const abDrift = () => Math.abs(ab.A.currentTime - ab.B.currentTime);
window.AMPLE_AB = { drift: abDrift, state: () => ({ which: ab.which, volumes: [ab.A.volume, ab.B.volume], playing: !ab.A.paused && !ab.B.paused, rms: ab.rms }) };
