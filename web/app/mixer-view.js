// GUI flow 4: one mixer for the whole song, as the "Mixer" tab of the bottom panel: a strip for every mixer, bus and master block of the song (faders and pans written into the
// song like the Advanced panel does), and the level of the song that is playing on the right. Everything it shows is read from the song again whenever the song changes.
import { el } from './ui.js';
import { edit, flat, subscribe } from './store.js';
import { playerState } from './player.js';

const STRIP_TYPES = ['mixer', 'bus', 'master', 'volume'];
const parts = (value, fallback) => String(value || fallback).replace(/^"|"$/g, '').trim().split(/\s+/);
const dbOf = (level) => (level > 0.00001 ? Math.max(-60, 20 * Math.log10(level)) : -60);
let bars = null;
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
      faders.append(el('div', { className: 'mixchan' }, [el('span', { className: 'note mixsrc', title: src ? `from ${src.from}` : `input ${i + 1} (nothing connected)`, textContent: src ? src.from : `${i + 1}` }), pan, range, readout]));
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
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);
}

export function renderMixerView() {
  const box = document.querySelector('#b-mixer');
  if (!box) return;
  if (box.querySelector('input:focus, select:focus')) return;   // never redraw under a fader that is being used
  const graph = flat();
  const all = graph.nodes.filter((n) => STRIP_TYPES.includes(n.tpl));
  let scope = scopeGet();
  if (!all.some((n) => n.id === scope)) scope = '';
  const nodes = scope ? all.filter((n) => n.id === scope) : all;
  const picker = el('select', { className: 'mixscope', 'aria-label': 'Which mixer to show', title: 'All mixers, or one mixer block with its inputs and output' }, [el('option', { value: '', textContent: 'All mixers' }), ...all.filter((n) => n.tpl !== 'master').map((n) => el('option', { value: n.id, textContent: n.title || n.id, selected: n.id === scope }))]);
  picker.onchange = () => { scopeSet(picker.value); renderMixerView(); };
  box.replaceChildren(el('div', { className: 'mixview' }, [
    ...(all.length > 1 ? [picker] : []),
    ...nodes.map((n) => stripFor(n, graph.links)),
    ...(nodes.length ? [] : [el('p', { className: 'note', textContent: 'This song has no mixer, bus or master block yet.' })]),
    outputMeter(),
  ]));
  animate();
}

export function initMixerView() {
  document.querySelector('#btabs button[data-t="mixer"]')?.addEventListener('click', () => renderMixerView());
  subscribe(() => { if (document.querySelector('#b-mixer.on')) renderMixerView(); });
}
