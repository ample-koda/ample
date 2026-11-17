// GUI flow 4: one mixer for the whole song, as the "Mixer" tab of the bottom panel: a strip for every mixer, bus and master block of the song (faders and pans written into the
// song like the Advanced panel does), and the level of the song that is playing on the right. Everything it shows is read from the song again whenever the song changes.
// Nothing here is state of the screen only: gain, pan, mute and solo are the `gains`, `pan`, `mutes` and `solos` of the mixer block, the master fader is the `limiter` of the master.
import { el, showMenu, toast } from './ui.js';
import { S, edit, flat, subscribe } from './store.js';
import { effectChainEdit } from '../lib/effect-chain-edit.mjs';
import { playerState, playerLoudness, resetPlayerLoudness } from './player.js';
import { outputLevels } from './playback-volume.js';
import { blockLevels } from './play-fx.js';
import { chainActive, chainLoudness, chainLevels, chainPreview, chainState, onChain, startChain, stopChain } from './live-chain.js';
import { knob } from './studio/knob.js';

const STRIP_TYPES = ['mixer', 'bus', 'master', 'volume'];
const parts = (value, fallback) => String(value || fallback).replace(/^"|"$/g, '').trim().split(/\s+/);
const dbOf = (level) => (level > 0.00001 ? Math.max(-60, 20 * Math.log10(level)) : -60);
let bars = null;
const stripMeters = [];  // { meter, id, master } of every bus, volume and master strip
const chanMeters = [];   // { meter, src } of every input of every strip
let frame = 0;
const showEmpty = new Set();   // mixer ids whose trailing empty inputs are shown (a view choice only; lost on reload)

const hueOf = (id) => [...String(id)].reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 7);
const SCOPE_KEY = 'ample.mix.scope';
const scopeGet = () => { try { return localStorage.getItem(SCOPE_KEY) || ''; } catch { return ''; } };
const scopeSet = (v) => { try { localStorage.setItem(SCOPE_KEY, v); } catch { /* none */ } };

// A small glyph per kind of source block (a text symbol, so it follows the theme colours).
const GLYPH = { drums: '◉', bass: '≋', piano: '♫', synth: '∿', gm: '♪', sampler: '▦', 'import-audio': '▤', 'ai-voice': '◖', choir: '◖', 'ace-music': '✦', mixer: '⊞', bus: '⊞', volume: '▮' };
const glyphOf = (tpl) => GLYPH[tpl] || '♪';
const NOT_EFFECT = new Set(['mixer', 'bus', 'master', 'send', 'volume']);
const isEffect = (n) => !NOT_EFFECT.has(n.tpl) && n.ins?.some((p) => p[1] === 'audio') && n.outs?.some((p) => p[1] === 'audio');
/** The source of one mixer input: walks back through the audio effects in front of the mixer. Gives { link, source, chain } (chain: the effect blocks from the source towards the mixer). */
export function laneOf(graph, mixerId, index) {
  const link = graph.links.find((l) => l.to === mixerId && l.inp === index && l.type === 'audio' && !l.disabled);
  if (!link) return null;
  const byId = new Map(graph.nodes.map((n) => [n.id, n]));
  const chain = [], sends = [], seen = new Set();
  let node = byId.get(link.from);
  while (node && (isEffect(node) || node.tpl === 'send') && !seen.has(node.id)) {
    seen.add(node.id);
    (node.tpl === 'send' ? sends : chain).unshift(node);
    const back = graph.links.find((l) => l.to === node.id && l.inp === node.ins.findIndex((p) => p[1] === 'audio') && !l.disabled);
    node = back ? byId.get(back.from) : null;
  }
  return { link, source: node || chain[0], chain: node ? chain : chain.slice(1), sends };
}
const laneCount = (node) => Math.max(2, Math.min(16, Math.round(Number(String(node.params.inputs ?? '4').replace(/"/g, ''))) || 4));
const fill = (list, n, dflt) => Array.from({ length: n }, (_, i) => list[i] ?? dflt);
const panNumber = (p) => { const v = String(p || 'c').toLowerCase(); return v === 'l' ? -32 : v === 'r' ? 32 : v === 'c' ? 0 : Number(v) || 0; };
const panText = (n) => (n <= -32 ? 'l' : n >= 32 ? 'r' : n === 0 ? 'c' : String(n));
const panLabel = (n) => (n === 0 ? 'C' : n < 0 ? `L${-n}` : `R${n}`);
const DB_TICKS = [12, 0, -12, -24, -36, -60];

/** A meter of two bars (left, right) with a peak-hold line over each: the line stays at the highest level for a moment and then falls. Click it to clear the hold. */
function makeMeter(cls) {
  const bars2 = [el('b'), el('b')], peaks = [el('s', { className: 'mixpk' }), el('s', { className: 'mixpk' })];
  const root = el('i', { className: cls, 'aria-hidden': 'true', title: 'Level; the line is the peak (click to clear it)' }, [...bars2, ...peaks]);
  const state = { bars: bars2, peaks, hold: [-60, -60], since: [0, 0] };
  root.onclick = () => { state.hold = [-60, -60]; peaks.forEach((p) => { p.style.bottom = '0%'; }); };
  return { root, state };
}
function paintMeter(m, left, right, now, peaks = null) {
  [left, right].forEach((level, k) => {
    const lvl = dbOf(level), db = peaks ? Math.max(lvl, dbOf(peaks[k])) : lvl;
    m.bars[k].style.height = `${Math.round((lvl + 60) / 60 * 100)}%`;
    if (db >= m.hold[k]) { m.hold[k] = db; m.since[k] = now; } else if (now - m.since[k] > 1200) m.hold[k] = Math.max(db, m.hold[k] - 0.6);
    m.peaks[k].style.bottom = `${Math.round((m.hold[k] + 60) / 60 * 100)}%`;
    m.peaks[k].classList.toggle('hot', m.hold[k] > -1);
  });
}
const faderScale = (lo, hi) => el('div', { className: 'mixticks', 'aria-hidden': 'true' }, DB_TICKS.filter((d) => d >= lo && d <= hi).map((d) => el('span', { textContent: String(d), style: `bottom:${((d - lo) / (hi - lo) * 100).toFixed(1)}%` })));
const writeGains = (node, gains) => edit([{ type: 'setParameter', block: node.id, name: 'gains', value: `"${gains.join(' ')}"` }]);
const writeFlags = (node, name, flags) => { chainPreview(node.id, name, `"${flags.join(' ')}"`); return edit([{ type: 'setParameter', block: node.id, name, value: `"${flags.join(' ')}"` }]); };
const writePan = (node, pans) => { chainPreview(node.id, 'pan', `"${pans.join(' ')}"`); edit([{ type: 'setParameter', block: node.id, name: 'pan', value: `"${pans.join(' ')}"` }]); };

// The effects between a source and its mixer input, shown as chips under the fader. Every change is an ordinary edit of the song (the same helper the Blocks view uses), so Undo and Redo work.
const FX_CHOICES = [['peq', 'EQ'], ['compressor', 'Compressor'], ['reverb', 'Reverb'], ['delay', 'Delay']];
const hasEnabled = (tpl) => (S.tpl[tpl]?.parameters || []).some((p) => p.name === 'enabled');
const openBlock = (id) => document.querySelector(`.node[data-id="${id}"] button[aria-label="Advanced"]`)?.click();
async function chainEdit(lane, graph, action) {
  const result = effectChainEdit(lane.source, lane.chain, graph.links, graph.nodes, S.tpl, action);
  if (result.error) { toast(result.error); return; }
  await edit(result.ops);
}
function fxBox(lane, graph, mixerId, i) {
  const box = el('div', { className: 'mixfx', role: 'group', 'aria-label': `${mixerId} input ${i + 1} effects` });
  for (const [k, fx] of lane.chain.entries()) {
    const off = String(fx.params.enabled ?? 'on').replace(/"/g, '') === 'off';
    const row = [];
    if (hasEnabled(fx.tpl)) row.push(el('button', { type: 'button', className: `mixfxbyp${off ? '' : ' on'}`, 'aria-pressed': String(!off), 'aria-label': `${fx.id} ${off ? 'switch on' : 'bypass'}`, title: off ? 'Bypassed: click to switch the effect on' : 'On: click to bypass it (enabled off)', onclick: () => edit([{ type: 'setParameter', block: fx.id, name: 'enabled', value: off ? 'on' : 'off' }]) }));
    row.push(el('button', { type: 'button', className: 'mixfxname', textContent: fx.title || fx.id, title: `${fx.tpl}: open the block`, onclick: () => openBlock(fx.id) }));
    const tools = el('span', { className: 'mixfxtools' }, [
      el('button', { type: 'button', textContent: '‹', 'aria-label': `${fx.id} earlier`, title: 'Move earlier in the chain', disabled: k === 0, onclick: () => chainEdit(lane, graph, { kind: 'move', id: fx.id, delta: -1 }) }),
      el('button', { type: 'button', textContent: '›', 'aria-label': `${fx.id} later`, title: 'Move later in the chain', disabled: k === lane.chain.length - 1, onclick: () => chainEdit(lane, graph, { kind: 'move', id: fx.id, delta: 1 }) }),
      el('button', { type: 'button', textContent: '×', 'aria-label': `${fx.id} remove`, title: 'Remove this effect from the chain', onclick: () => chainEdit(lane, graph, { kind: 'remove', id: fx.id }) })]);
    box.append(el('div', { className: `mixfxchip${off ? ' off' : ''}`, 'data-fx': fx.id }, [el('div', { className: 'mixfxrow' }, row), tools]));
  }
  const add = el('button', { type: 'button', className: 'mixfxadd', textContent: '+', 'aria-label': `${mixerId} input ${i + 1} add effect`, title: 'Add an effect between the source and the mixer' });
  add.onclick = (ev) => { ev.stopPropagation(); const r = add.getBoundingClientRect(); showMenu(r.left, r.bottom, FX_CHOICES.filter(([id]) => S.tpl[id]).map(([id, label]) => ({ label, fn: () => chainEdit(lane, graph, { kind: 'add', type: id }) }))); };
  box.append(add);
  return box;
}

// Sends: a per-input send block (source -> send.audio1; send.through -> mixer input; send.send -> a shared bus -> reverb or delay -> a free input of the mixer).
// All of it is written in the song with the blocks the language has; nothing here is state of the screen.
const SEND_KINDS = [['reverb', 'Rev', 'Reverb'], ['delay', 'Dly', 'Delay']];
const OFF_DB = -60;
const sendInfo = (graph, send) => {
  const l = graph.links.find((x) => x.from === send.id && x.outName === 'send');
  const bus = l && graph.nodes.find((n) => n.id === l.to && n.tpl === 'bus');
  const o = bus && graph.links.find((x) => x.from === bus.id);
  return { bus, kind: (o && graph.nodes.find((n) => n.id === o.to)?.tpl) || null };
};
const findBus = (graph, kind) => graph.nodes.find((n) => n.tpl === 'bus' && graph.links.some((l) => l.from === n.id && graph.nodes.find((m) => m.id === l.to)?.tpl === kind));
const freeName = (names, base) => { let name = base, k = 2; while (names.has(name)) name = `${base}-${k++}`; names.add(name); return name; };
const freeInput = (graph, node) => { const used = new Set(graph.links.filter((l) => l.to === node.id).map((l) => l.inp)); let k = 0; while (used.has(k)) k++; return k; };
const growOps = (node, count) => (count <= laneCount(node) ? [] : [
  { type: 'setParameter', block: node.id, name: 'inputs', value: String(count) },
  { type: 'setParameter', block: node.id, name: 'gains', value: `"${fill(parts(node.params.gains, '0'), count, '0').join(' ')}"` },
  { type: 'setParameter', block: node.id, name: 'pan', value: `"${fill(parts(node.params.pan, 'c'), count, 'c').join(' ')}"` },
  ...['mutes', 'solos'].filter((name) => node.params[name] !== undefined).map((name) => ({ type: 'setParameter', block: node.id, name, value: `"${fill(parts(node.params[name], 'off'), count, 'off').join(' ')}"` }))]);
/** The edits that put a send of an input to a shared reverb or delay bus into the song (the bus and its effect are made on the first send). */
export function sendOps(graph, mixer, lane, kind, level) {
  const names = new Set(graph.nodes.map((n) => n.id));
  const ops = [];
  let bus = findBus(graph, kind);
  let busName = bus?.id;
  if (!bus) {
    busName = freeName(names, `${kind}-bus`);
    const fx = freeName(names, `${kind}-return`);
    const slot = freeInput(graph, mixer);
    if (slot >= 16) return { error: 'The mixer has no free input for the return of the bus (16 at most).' };
    ops.push(...growOps(mixer, slot + 1), { type: 'addBlock', blockType: 'bus', name: busName }, { type: 'addBlock', blockType: kind, name: fx }, { type: 'setParameter', block: fx, name: 'mix', value: '100%' },
      { type: 'connect', from: busName, output: 'audio', to: fx, input: 'audio' }, { type: 'connect', from: fx, output: 'audio', to: mixer.id, input: `audio${slot + 1}` });
  }
  const busSlot = bus ? freeInput(graph, bus) : 0;
  if (busSlot >= 16) return { error: 'The bus has no free input (16 at most).' };
  if (bus) ops.push(...growOps(bus, busSlot + 1));
  const send = freeName(names, `${lane.source.id}-send-${kind}`);
  ops.push({ type: 'addBlock', blockType: 'send', name: send }, { type: 'setParameter', block: send, name: 'levels', value: `"${level} ${OFF_DB} ${OFF_DB} ${OFF_DB}"` },
    { type: 'disconnect', to: mixer.id, input: lane.link.inName },
    { type: 'connect', from: lane.link.from, output: lane.link.outName, to: send, input: 'audio1' },
    { type: 'connect', from: send, output: 'through', to: mixer.id, input: lane.link.inName },
    { type: 'connect', from: send, output: 'send', to: busName, input: `audio${busSlot + 1}` });
  return { ops };
}
let sendQueue = Promise.resolve();
function sendKnobs(node, i, lane, graph) {
  const row = el('div', { className: 'mixsends', role: 'group', 'aria-label': `${node.id} input ${i + 1} sends` });
  for (const [kind, short, long] of SEND_KINDS) {
    const existing = lane.sends.find((sd) => sendInfo(graph, sd).kind === kind);
    const fresh = () => { const g = flat(); const mixer = g.nodes.find((n) => n.id === node.id); const ln = mixer && laneOf(g, node.id, i); return mixer && ln ? { g, mixer, ln, sd: ln.sends.find((x) => sendInfo(g, x).kind === kind) } : null; };
    const level = existing ? Number.parseFloat(parts(existing.params.levels, '-12')[0]) : OFF_DB;
    const k = knob({ label: short, value: Number.isFinite(level) ? Math.max(OFF_DB, Math.min(0, level)) : OFF_DB, min: OFF_DB, max: 0, step: 1, size: 28, defaultValue: OFF_DB, format: (v) => (v <= OFF_DB ? 'off' : `${v}`),
      onCommit: (v) => { sendQueue = sendQueue.then(async () => {
        const now = fresh();
        if (!now) return;
        if (now.sd) { const rest = parts(now.sd.params.levels, '-12 -12 -12 -12'); rest[0] = String(v); await edit([{ type: 'setParameter', block: now.sd.id, name: 'levels', value: `"${rest.join(' ')}"` }]); return; }
        if (v <= OFF_DB) return;
        const r = sendOps(now.g, now.mixer, now.ln, kind, v);
        if (r.error) toast(r.error); else await edit(r.ops);
      }).catch(() => {}); } });
    k.classList.add('mixsend'); k.title = `${long} send: how much of this input goes to the shared ${long.toLowerCase()} bus (drag, arrows, wheel; double-click: off)`;
    k.querySelector('svg').setAttribute('aria-label', `${node.id} input ${i + 1} ${kind} send`);
    k.addEventListener('dblclick', (e) => { e.stopPropagation(); if (existing) edit([{ type: 'setParameter', block: existing.id, name: 'levels', value: `"${OFF_DB} ${parts(existing.params.levels, '-12 -12 -12 -12').slice(1).join(' ')}"` }]); }, true);
    row.append(k);
  }
  return row;
}

function laneStrip(node, i, ctx) {
  const { gains, pans, graph, n } = ctx;
  const lane = laneOf(graph, node.id, i);
  const src = lane?.source;
  const name = src ? (src.title || src.id) : '';
  const readout = el('output', { textContent: `${Number(gains[i]) || 0} dB` });
  const range = el('input', { type: 'range', className: 'mixfader', min: '-60', max: '12', step: '1', value: String(Number(gains[i]) || 0), 'aria-label': `${node.id} input ${i + 1} gain${name ? ` (${name})` : ''}`, 'aria-valuetext': `${Number(gains[i]) || 0} dB`, title: 'Gain in dB (double-click: 0 dB)' });
  // while the fader moves it is heard at once when the live chain of this mixer plays; the song is written on release (change)
  range.oninput = () => { readout.textContent = `${range.value} dB`; range.setAttribute('aria-valuetext', `${range.value} dB`); const live = [...gains]; live[i] = range.value; chainPreview(node.id, 'gains', `"${live.join(' ')}"`); };
  range.onchange = () => { gains[i] = range.value; writeGains(node, gains); };
  range.ondblclick = () => { range.value = '0'; range.dispatchEvent(new Event('change')); readout.textContent = '0 dB'; };
  const panKnob = knob({ label: `${node.id} input ${i + 1} pan`, value: panNumber(pans[i]), min: -32, max: 32, step: 1, size: 34, defaultValue: 0, format: panLabel, onCommit: (v) => { pans[i] = panText(v); writePan(node, pans); } });
  panKnob.classList.add('mixpan'); panKnob.title = 'Pan: drag, arrow keys or wheel; double-click: centre';
  panKnob.addEventListener('dblclick', (e) => { e.stopPropagation(); pans[i] = 'c'; writePan(node, pans); }, true);   // capture: before the knob opens its typing field
  // mute and solo are the `mutes` and `solos` of the block (one on/off per input): the same words the server and the live mix read; a solo silences every input without one, a mute wins
  const muted = ctx.mutes[i] === 'on', soloed = ctx.solos[i] === 'on', heard = !muted && (!ctx.anySolo || soloed);
  const mute = el('button', { type: 'button', className: `mixmute${muted ? ' on' : ''}`, textContent: 'M', title: muted ? 'Unmute this input' : 'Mute this input (its gain stays as it is)', 'aria-pressed': String(muted), 'aria-label': `${node.id} input ${i + 1} mute` });
  mute.onclick = () => { const next = [...ctx.mutes]; next[i] = muted ? 'off' : 'on'; writeFlags(node, 'mutes', next); };
  const solo = el('button', { type: 'button', className: `mixsolo${soloed ? ' on' : ''}`, textContent: 'S', title: soloed ? 'End the solo of this input' : 'Solo: with a solo on, only the inputs with solo on are heard', 'aria-pressed': String(soloed), 'aria-label': `${node.id} input ${i + 1} solo` });
  solo.onclick = () => { const next = [...ctx.solos]; next[i] = soloed ? 'off' : 'on'; writeFlags(node, 'solos', next); };
  const meter = makeMeter('mixchm');   // wide: one bar for the left side, one for the right
  chanMeters.push({ meter: meter.state, src: lane?.link.from });
  const head = el('div', { className: 'mixhead', title: src ? `${name} (${src.tpl}) feeds input ${i + 1}` : `input ${i + 1}: nothing connected` }, [
    el('span', { className: 'mixicon', textContent: src ? glyphOf(src.tpl) : '·', 'aria-hidden': 'true' }),
    el('span', { className: 'mixsrc', textContent: src ? name : '—' })]);
  const attrs = { className: `mixchan${heard ? '' : ' muted'}${src ? '' : ' empty'}`, 'data-input': String(i), 'data-source': src?.id || '' };
  if (src) attrs.style = `--mc:hsl(${hueOf(src.id)} 60% 58%)`;
  return el('div', attrs, [head, panKnob, el('div', { className: 'mixfw' }, [faderScale(-60, 12), range, meter.root]), readout, el('div', { className: 'mixms' }, [mute, solo]), ...(lane && node.tpl === 'mixer' && src && src.tpl !== 'bus' ? [sendKnobs(node, i, lane, graph)] : []), ...(lane ? [fxBox(lane, graph, node.id, i)] : [])]);
}

function stripFor(node, graph) {
  const links = graph.links;
  const out = links.find((l) => l.from === node.id);
  const strip = el('div', { className: `mixstrip mixstrip-${node.tpl}`, 'data-block': node.id, style: `--mc:hsl(${hueOf(node.id)} 60% 58%)` }, [el('strong', { className: 'mixname', textContent: node.title || node.id }), el('span', { className: 'note', textContent: out ? `${node.tpl} → ${out.to}` : node.tpl })]);
  if (node.tpl === 'mixer' || node.tpl === 'bus') {
    const n = laneCount(node);
    const ctx = { gains: fill(parts(node.params.gains, '0 0 0 0'), n, '0'), pans: fill(parts(node.params.pan, 'c c c c'), n, 'c'), graph, n, mutes: fill(parts(node.params.mutes, 'off'), n, 'off').map((x) => x.toLowerCase()), solos: fill(parts(node.params.solos, 'off'), n, 'off').map((x) => x.toLowerCase()) };
    ctx.anySolo = ctx.solos.includes('on');
    const faders = el('div', { className: 'mixfaders' });
    // trailing inputs with nothing connected (and no mute or solo) are not drawn: a small toggle shows them
    let shown = n;
    while (shown > 1 && !laneOf(graph, node.id, shown - 1) && ctx.mutes[shown - 1] !== 'on' && ctx.solos[shown - 1] !== 'on') shown--;
    const hidden = n - shown, open = showEmpty.has(node.id);
    for (let i = 0; i < (open ? n : shown); i++) faders.append(laneStrip(node, i, ctx));
    const empties = hidden === 0 ? null : el('button', { type: 'button', className: 'mixempties', textContent: open ? '− hide empty' : `+ ${hidden} empty input${hidden === 1 ? '' : 's'}`, 'aria-expanded': String(open), 'aria-label': `${node.id} ${open ? 'hide' : 'show'} ${hidden} empty inputs`, title: open ? 'Hide the empty inputs at the end' : 'Show the inputs at the end that have nothing connected' });
    if (empties) empties.onclick = () => { if (open) showEmpty.delete(node.id); else showEmpty.add(node.id); renderMixerView(true); };
    const clear = el('button', { type: 'button', className: 'mixclear', textContent: 'Clear solo', disabled: !ctx.anySolo, title: 'End every solo of this mixer', 'aria-label': `${node.id} clear solos` });
    clear.onclick = () => writeFlags(node, 'solos', ctx.solos.map(() => 'off'));
    const live = chainActive() && chainState().target === node.id;
    const liveButton = (el('button', { type: 'button', className: `mixlive${live ? ' on' : ''}`, textContent: live ? '■ Stop live' : '▶ Hear live', 'aria-pressed': String(live), 'aria-label': `Hear ${node.id} live`,
      title: live ? 'Stop the live mix' : 'Loop the built sounds of the inputs through this mixer in the browser: faders, pans, mutes and solos are heard while you move them (approximate, the build is the truth)',
      onclick: () => (live ? stopChain() : startChain(node.id)) }));
    strip.append(el('div', { className: 'mixtools' }, [clear, liveButton, ...(empties ? [empties] : [])]), faders);
  } else {
    // bus, volume and master: its own fader (the gain of the block, written into the song like the inputs) and a meter of what the block gives
    // a master block has no gain: its level is the ceiling of its limiter (-12..0 dB, the default is -1); the other strips have a gain
    const isMaster = node.tpl === 'master', pname = isMaster ? 'limiter' : 'gain', lo = isMaster ? -12 : -60, hi = isMaster ? 0 : 12;
    const gain = Number.parseFloat(String(node.params[pname] ?? (isMaster ? '-1' : '0')).replace(/"/g, '')) || 0;
    const readout = el('output', { textContent: `${gain} dB` });
    const range = el('input', { type: 'range', className: 'mixfader mixstripfader', min: String(lo), max: String(hi), step: '1', value: String(gain), 'aria-label': isMaster ? `${node.id} limiter ceiling` : `${node.id} gain`, 'aria-valuetext': `${gain} dB`, title: isMaster ? 'Level of the master: the ceiling of its limiter in dB (double-click: -1 dB)' : 'Gain in dB (double-click: 0 dB)' });
    range.oninput = () => { readout.textContent = `${range.value} dB`; range.setAttribute('aria-valuetext', `${range.value} dB`); };
    range.onchange = () => edit([{ type: 'setParameter', block: node.id, name: pname, value: isMaster ? `"${range.value} dB"` : range.value }]);
    range.ondblclick = () => { range.value = isMaster ? '-1' : '0'; range.dispatchEvent(new Event('change')); readout.textContent = `${range.value} dB`; };
    const meter = makeMeter('mixchm mixstripm');
    const entry = { meter: meter.state, id: node.id, master: node.tpl === 'master' };
    stripMeters.push(entry);
    const body = el('div', { className: 'mixmasterbody' }, [el('div', { className: 'mixchan mixsingle' }, [el('div', { className: 'mixfw' }, [faderScale(lo, hi), range, meter.root]), readout])]);
    strip.append(body);
    if (isMaster) {
      const target = Number.parseFloat(String(node.params.target ?? '-14 LUFS').replace(/"/g, ''));
      const val = (cls, label) => { const o = el('output', { className: cls, textContent: '—' }); return [o, el('span', { textContent: label })]; };
      const [sOut, sLab] = val('mixlufs-s', 'LUFS short-term'), [iOut, iLab] = val('mixlufs-i', 'LUFS integrated'), [pOut, pLab] = val('mixpeakval', 'peak dB');
      const reset = el('button', { type: 'button', className: 'mixlufs-reset', textContent: 'Reset', title: 'Start the integrated loudness and the peak over', 'aria-label': 'Reset loudness and peak', onclick: () => { resetPlayerLoudness(); meter.root.click(); } });
      entry.lufs = { s: sOut, i: iOut, p: pOut, target: Number.isFinite(target) ? target : null };
      body.append(el('div', { className: 'mixlufs', role: 'group', 'aria-label': 'Loudness of the master' }, [el('div', {}, [sOut, sLab]), el('div', {}, [iOut, iLab]), el('div', {}, [pOut, pLab]), ...(entry.lufs.target === null ? [] : [el('span', { className: 'note', textContent: `target ${entry.lufs.target} LUFS` })]), reset]));
    }
    const open = el('button', { type: 'button', textContent: 'Controls', title: 'Open the advanced controls of this block' });
    open.onclick = () => document.querySelector(`.node[data-id="${node.id}"] button[aria-label="Advanced"]`)?.click();
    strip.append(open);
  }
  return strip;
}

/** The level of the song that is playing (the player reads it from the output): two bars that follow it. */
function outputMeter() {
  const left = el('i', { className: 'mixbar' }), right = el('i', { className: 'mixbar' });
  bars = { left, right };
  return el('div', { className: 'mixstrip mixmeter' }, [el('strong', { className: 'mixname', textContent: 'Output' }), el('span', { className: 'note', textContent: 'what you hear' }), el('div', { className: 'mixmeterbody' }, [el('div', { className: 'mixscale', 'aria-hidden': 'true' }, ['0', '-12', '-24', '-36', '-60'].map((t) => el('span', { textContent: t }))), el('div', { className: 'mixbars', role: 'img', 'aria-label': 'Level of the song that is playing' }, [left, right])])]);
}

function animate() {
  cancelAnimationFrame(frame);
  const tick = () => {
    const box = document.querySelector('#b-mixer.on');
    if (!box || !bars) return;
    const now = performance.now();
    const state = playerState();
    const out = state.playing ? outputLevels() : { left: 0, right: 0 };   // the shared output: what is audible, whichever player makes it (take, loop, Hear it)
    const set = (bar, level) => { bar.style.height = `${Math.round((dbOf(level) + 60) / 60 * 100)}%`; };
    const left = out.left, right = out.right;
    set(bars.left, left); set(bars.right, right);
    const levels = state.playing ? blockLevels() : null;
    for (const m of chanMeters) {
      const own = chainActive() && m.src ? chainLevels(m.src) : null;   // the live chain taps every input itself, left and right
      if (own) { paintMeter(m.meter, own.left, own.right, now); continue; }
      const level = levels && m.src ? levels.get(m.src) || 0 : 0;
      paintMeter(m.meter, level, level, now);
    }
    for (const m of stripMeters) {
      if (m.master) {
        paintMeter(m.meter, left, right, now, state.playing ? [state.leftPeak || 0, state.rightPeak || 0] : null);
        if (m.lufs) {
          const ld = state.playing ? playerLoudness() : (chainActive() ? chainLoudness(m.id) : null);
          const fmt = (x) => (Number.isFinite(x) ? x.toFixed(1) : '—');
          m.lufs.s.textContent = fmt(ld?.shortTerm); m.lufs.i.textContent = fmt(ld?.integrated);
          m.lufs.p.textContent = fmt(Math.max(...m.meter.hold) > -60 ? Math.max(...m.meter.hold) : NaN);
          const over = m.lufs.target !== null && Number.isFinite(ld?.shortTerm) ? ld.shortTerm - m.lufs.target : 0;
          m.lufs.s.classList.toggle('hot', over > 1); m.lufs.s.classList.toggle('ok', Number.isFinite(ld?.shortTerm) && Math.abs(over) <= 1);
        }
      }
      else { const live = chainActive() ? chainLevels(m.id) : null; if (live) paintMeter(m.meter, live.left, live.right, now); else { const own = levels?.get(m.id) || 0; paintMeter(m.meter, own, own, now); } }
    }
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);
}

export function renderMixerView(force = false) {
  const box = document.querySelector('#b-mixer');
  if (!box) return;
  if (!force && box.querySelector('input:focus, select:focus')) return;   // never redraw under a fader that is being used
  chanMeters.length = 0; stripMeters.length = 0;
  const focusLabel = box.contains(document.activeElement) && document.activeElement.tagName === 'svg' ? document.activeElement.getAttribute('aria-label') : null;   // a knob that is used with the keys keeps its focus
  const keepLeft = box.querySelector('.mixview')?.scrollLeft || 0;   // a redraw must not throw the row back to the start
  const graph = flat();
  // the strips follow the song; the master comes last, next to the meter of the output
  const all = [...graph.nodes.filter((n) => STRIP_TYPES.includes(n.tpl) && n.tpl !== 'master'), ...graph.nodes.filter((n) => n.tpl === 'master')];
  let scope = scopeGet();
  if (!all.some((n) => n.id === scope)) scope = '';
  const nodes = scope ? all.filter((n) => n.id === scope) : all;
  const picker = el('select', { className: 'mixscope', 'aria-label': 'Which mixer to show', title: 'All mixers, or one mixer block with its inputs and output' }, [el('option', { value: '', textContent: 'All mixers' }), ...all.filter((n) => n.tpl !== 'master').map((n) => el('option', { value: n.id, textContent: n.title || n.id, selected: n.id === scope }))]);
  picker.onchange = () => { scopeSet(picker.value); renderMixerView(true); };
  box.replaceChildren(...(all.length > 1 ? [el('div', { className: 'mixscopebar' }, [picker])] : []), el('div', { className: 'mixview' }, [
    ...nodes.map((n) => stripFor(n, graph)),
    ...(nodes.length ? [] : [el('p', { className: 'note', textContent: 'This song has no mixer, bus or master block yet.' })]),
    outputMeter(),
  ]));
  const row = box.querySelector('.mixview');
  if (row && keepLeft) row.scrollLeft = keepLeft;
  if (focusLabel) [...box.querySelectorAll('svg[aria-label]')].find((x) => x.getAttribute('aria-label') === focusLabel)?.focus({ preventScroll: true });
  animate();
}

export function initMixerView() {
  document.querySelector('#btabs button[data-t="mixer"]')?.addEventListener('click', () => renderMixerView(true));
  subscribe(() => { if (document.querySelector('#b-mixer.on')) renderMixerView(); });
  onChain(() => { if (document.querySelector('#b-mixer.on')) renderMixerView(true); });   // the Hear live button and the meters follow the chain
}
