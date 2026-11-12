// GUI flow 4: one mixer for the whole song, as the "Mixer" tab of the bottom panel: a strip for every mixer, bus and master block of the song (faders and pans written into the
// song like the Advanced panel does), and the level of the song that is playing on the right. Everything it shows is read from the song again whenever the song changes.
import { el } from './ui.js';
import { edit, flat, subscribe } from './store.js';
import { playerState } from './player.js';
import { blockLevels } from './play-fx.js';

const STRIP_TYPES = ['mixer', 'bus', 'master', 'volume'];
const parts = (value, fallback) => String(value || fallback).replace(/^"|"$/g, '').trim().split(/\s+/);
const dbOf = (level) => (level > 0.00001 ? Math.max(-60, 20 * Math.log10(level)) : -60);
let bars = null;
const chanMeters = [];   // { bar, src } of every input of every strip
const MUTE_KEY = 'ample.mix.mute';   // "block#input": the gain before the mute, so it can be put back
const muteMap = () => { try { return JSON.parse(localStorage.getItem(MUTE_KEY) || '{}'); } catch { return {}; } };
const muteSave = (m) => { try { localStorage.setItem(MUTE_KEY, JSON.stringify(m)); } catch { /* none */ } };
const MUTED_DB = '-60';
let frame = 0;

const hueOf = (id) => [...String(id)].reduce((h, c) => (h * 31 + c.charCodeAt(0)) % 360, 7);
const SCOPE_KEY = 'ample.mix.scope';
const scopeGet = () => { try { return localStorage.getItem(SCOPE_KEY) || ''; } catch { return ''; } };
const scopeSet = (v) => { try { localStorage.setItem(SCOPE_KEY, v); } catch { /* none */ } };

function stripFor(node, links) {
  const out = links.find((l) => l.from === node.id);
  const strip = el('div', { className: `mixstrip mixstrip-${node.tpl}`, 'data-block': node.id, style: `--mc:hsl(${hueOf(node.id)} 60% 58%)` }, [el('strong', { className: 'mixname', textContent: node.title || node.id }), el('span', { className: 'note', textContent: out ? `${node.tpl} → ${out.to}` : node.tpl })]);
  if (node.tpl === 'mixer' || node.tpl === 'bus') {
    const gains = parts(node.params.gains, '0 0 0 0'), pans = parts(node.params.pan, 'c c c c');
    const faders = el('div', { className: 'mixfaders' });
    for (let i = 0; i < 4; i++) {
      const readout = el('output', { textContent: `${Number(gains[i]) || 0} dB` });
      const range = el('input', { type: 'range', className: 'mixfader', min: '-60', max: '12', step: '1', value: String(Number(gains[i]) || 0), 'aria-label': `${node.id} input ${i + 1} gain`, title: 'Gain in dB (double-click: 0 dB)' });
      range.oninput = () => { readout.textContent = `${range.value} dB`; };
      range.onchange = () => { gains[i] = range.value; edit([{ type: 'setParameter', block: node.id, name: 'gains', value: `"${gains.join(' ')}"` }]); };
      range.ondblclick = () => { range.value = '0'; range.dispatchEvent(new Event('change')); readout.textContent = '0 dB'; };
      const pan = el('select', { className: 'mixpan', 'aria-label': `${node.id} input ${i + 1} pan`, title: 'Pan' }, [['l', 'L'], ['c', 'C'], ['r', 'R']].map(([v, t]) => el('option', { value: v, textContent: t, selected: (pans[i] || 'c').toLowerCase() === v })));
      pan.onchange = () => { pans[i] = pan.value; edit([{ type: 'setParameter', block: node.id, name: 'pan', value: `"${pans.join(' ')}"` }]); };
      const src = links.find((l) => l.to === node.id && l.inp === i);
      const muted = muteMap()[`${node.id}#${i}`] !== undefined && Number(gains[i]) <= -60;
      const others = [0, 1, 2, 3].filter((k) => k !== i);
      const soloed = !muted && others.every((k) => muteMap()[`${node.id}#${k}`] !== undefined && Number(gains[k]) <= -60);
      const setMutes = (want) => { const m = muteMap(); for (const [k, mute] of Object.entries(want)) { const key = `${node.id}#${k}`; if (mute) { if (m[key] === undefined) m[key] = gains[k]; gains[k] = MUTED_DB; } else { gains[k] = m[key] ?? '0'; delete m[key]; } } muteSave(m); edit([{ type: 'setParameter', block: node.id, name: 'gains', value: `"${gains.join(' ')}"` }]); };
      const mute = el('button', { type: 'button', className: `mixmute${muted ? ' on' : ''}`, textContent: 'M', title: muted ? 'Unmute (back to the level it had)' : 'Mute this input (its gain goes to -60 dB)', 'aria-pressed': String(muted), 'aria-label': `${node.id} input ${i + 1} mute` });
      mute.onclick = () => setMutes({ [i]: !muted });
      const solo = el('button', { type: 'button', className: `mixsolo${soloed ? ' on' : ''}`, textContent: 'S', title: soloed ? 'End solo (every input back to its level)' : 'Solo: mute all the other inputs of this mixer', 'aria-pressed': String(soloed), 'aria-label': `${node.id} input ${i + 1} solo` });
      solo.onclick = () => setMutes(soloed ? Object.fromEntries([0, 1, 2, 3].map((k) => [k, false])) : Object.fromEntries([0, 1, 2, 3].map((k) => [k, k !== i])));
      const meter = el('i', { className: 'mixchm', 'aria-hidden': 'true' }, [el('b')]);
      chanMeters.push({ bar: meter.firstChild, src: src?.from });
      faders.append(el('div', { className: `mixchan${muted ? ' muted' : ''}` }, [el('span', { className: 'note mixsrc', title: src ? `from ${src.from}` : `input ${i + 1} (nothing connected)`, textContent: src ? src.from : `${i + 1}` }), pan, el('div', { className: 'mixfw' }, [range, meter]), readout, el('div', { className: 'mixms' }, [mute, solo])]));
    }
    strip.append(faders);
  } else {
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
    const state = playerState();
    const set = (bar, level) => { bar.style.height = `${Math.round((dbOf(level) + 60) / 60 * 100)}%`; };
    set(bars.left, state.playing ? state.leftLevel || 0 : 0); set(bars.right, state.playing ? state.rightLevel || 0 : 0);
    const levels = state.playing ? blockLevels() : null;
    for (const m of chanMeters) set(m.bar, levels && m.src ? levels.get(m.src) || 0 : 0);
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);
}

export function renderMixerView(force = false) {
  const box = document.querySelector('#b-mixer');
  if (!box) return;
  if (!force && box.querySelector('input:focus, select:focus')) return;   // never redraw under a fader that is being used
  chanMeters.length = 0;
  const keepLeft = box.querySelector('.mixview')?.scrollLeft || 0;   // a redraw must not throw the row back to the start
  const graph = flat();
  const all = graph.nodes.filter((n) => STRIP_TYPES.includes(n.tpl));
  let scope = scopeGet();
  if (!all.some((n) => n.id === scope)) scope = '';
  const nodes = scope ? all.filter((n) => n.id === scope) : all;
  const picker = el('select', { className: 'mixscope', 'aria-label': 'Which mixer to show', title: 'All mixers, or one mixer block with its inputs and output' }, [el('option', { value: '', textContent: 'All mixers' }), ...all.filter((n) => n.tpl !== 'master').map((n) => el('option', { value: n.id, textContent: n.title || n.id, selected: n.id === scope }))]);
  picker.onchange = () => { scopeSet(picker.value); renderMixerView(true); };
  box.replaceChildren(el('div', { className: 'mixview' }, [
    ...(all.length > 1 ? [picker] : []),
    ...nodes.map((n) => stripFor(n, graph.links)),
    ...(nodes.length ? [] : [el('p', { className: 'note', textContent: 'This song has no mixer, bus or master block yet.' })]),
    outputMeter(),
  ]));
  const row = box.querySelector('.mixview');
  if (row && keepLeft) row.scrollLeft = keepLeft;
  animate();
}

export function initMixerView() {
  document.querySelector('#btabs button[data-t="mixer"]')?.addEventListener('click', () => renderMixerView(true));
  subscribe(() => { if (document.querySelector('#b-mixer.on')) renderMixerView(); });
}
