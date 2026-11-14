// The bar of the live chain: which blocks the sound passes through, a bypass switch for each, a wide stereo meter for each and Stop. It exists only while the chain plays.
// @ample:live-chain-ui
import { el } from './ui.js';
import { chainState, chainLevels, chainLoudness, chainLoad, loopBars, onChain, setLoopBars, setChainBypass, setCompareBuilt, setSafeMode, stopChain } from './live-chain.js';
import { flat } from './store.js';
import { toggleSpectrum, spectrumBlock } from './live-spectrum-ui.js';

let bar = null, frame = 0, cpuBadge = null;
const meters = new Map();   // block -> { left, right, holdLeft, holdRight }
const dbWidth = (peak) => (peak > 1e-5 ? Math.max(0, Math.min(100, (20 * Math.log10(peak) + 60) / 60 * 100)) : 0);

function paint(state) {
  if (!state.active) { bar?.remove(); bar = null; meters.clear(); cancelAnimationFrame(frame); return; }
  const titleOf = (id) => flat().nodes.find((n) => n.id === id)?.title || id;
  if (!bar) { bar = el('section', { className: 'livechain', 'aria-label': 'Live chain', role: 'region' }); const bottom = document.getElementById('bbody'); if (bottom) bottom.before(bar); else document.body.append(bar); }
  meters.clear();
  const chips = state.items.filter((item) => item.kind !== 'source').map((item) => {
    const left = el('b'), right = el('b'), meter = el('span', { className: 'lcmeter', 'aria-hidden': 'true' }, [el('i', {}, [left]), el('i', {}, [right])]);
    meters.set(item.id, { left, right, holdLeft: 0, holdRight: 0 });
    const effect = item.kind === 'effect';
    const toggle = !effect && item.kind === 'local' ? el('span', { className: 'note', textContent: state.gliding?.includes(item.id) ? 'approximate while turning' : item.info ? `made here: ${item.info}` : 'made here', title: 'The browser makes this block itself while you tune it; it is swapped in without a gap' }) : effect ? el('button', { type: 'button', className: `lcbypass${item.bypassed ? ' on' : ''}`, textContent: item.cpuBypassed ? 'Bypassed (CPU)' : item.bypassed ? 'Bypassed' : 'On', 'aria-pressed': String(item.bypassed), 'aria-label': `Bypass ${titleOf(item.id)} in the live chain`, title: 'Bypass this block: the sound crossfades past it (the block stays in the chain)', onclick: () => setChainBypass(item.id, !item.bypassed) }) : el('span', { className: 'note', textContent: 'sum' });
    const spectrum = effect ? el('button', { type: 'button', className: 'lcspecbtn' + (spectrumBlock() === item.id ? ' on' : ''), textContent: '~', 'aria-label': `Spectrum and scope of ${titleOf(item.id)}, before and after`, 'aria-pressed': String(spectrumBlock() === item.id), title: 'Show the spectrum (or scope) before and after this block', onclick: () => toggleSpectrum(item.id) }) : null;
    const loud = item.tpl === 'master' ? el('span', { className: 'lcloud', title: 'Momentary loudness (400 ms, BS.1770) and the distance to the target of the master block; the target is a reference, nothing is changed', 'aria-live': 'off' }) : null;
    if (loud) meters.get(item.id).loud = loud;
    return el('span', { className: `lcchip${item.bypassed ? ' bypassed' : ''}`, 'data-block': item.id }, [el('strong', { textContent: titleOf(item.id), title: `${item.tpl} ${item.id}` }), toggle, ...(spectrum ? [spectrum] : []), ...(loud ? [loud] : []), meter]);
  });
  const comparing = Boolean(state.comparingBuilt || state.comparePending);
  let compareText = 'A/B vs build';
  if (state.comparePending) compareText = 'Loading the build (click to cancel)';
  else if (state.comparingBuilt) compareText = 'Hearing the build';
  const compare = el('button', {
    type: 'button',
    className: 'lccompare' + (comparing ? ' on' : ''),
    textContent: compareText,
    'aria-pressed': String(comparing),
    'aria-label': 'Compare the live chain with the built sound of the last block',
    title: 'Switch between the live chain and the built take of the last block, at the same place of the loop',
    onclick: () => setCompareBuilt(!(state.comparingBuilt || state.comparePending)),
  });
  const stop = el('button', { type: 'button', className: 'lcstop', textContent: '■ Stop', 'aria-label': 'Stop the live chain', onclick: () => stopChain() });
  const sources = state.items.filter((item) => item.kind === 'source').map((item) => titleOf(item.id)).join(', ');
  const latency = el('span', { className: 'lclatency', textContent: state.latencyReport.label, title: state.latencyReport.tooltip, 'aria-label': state.latencyReport.tooltip, tabindex: '0' });
  const cpu = state.cpu;
  cpuBadge = el('span', { className: 'lccpu', tabindex: '0' });
  paintCpu(cpu);
  const safe = el('button', { type: 'button', className: 'lcsafe' + (cpu.mode === 'auto' ? ' on' : ''), textContent: cpu.mode === 'auto' ? 'Safe mode: auto' : 'Safe mode: off', 'aria-pressed': String(cpu.mode === 'auto'), title: 'When the load stays over 90 percent the costliest block is bypassed, with a notice. You bring it back with its button.', onclick: () => setSafeMode(cpu.mode === 'auto' ? 'off' : 'auto') });
  const range = el('select', { className: 'lcloop', 'aria-label': 'Loop range', title: 'Loop a few bars (from the bar you are in) or the whole take', onchange: (event) => setLoopBars(Number(event.target.value)) },
    [[0, 'Whole take'], [1, '1 bar'], [2, '2 bars'], [4, '4 bars'], [8, '8 bars']].map(([value, text]) => el('option', { value: String(value), textContent: text, selected: loopBars() === value })));
  bar.replaceChildren(el('span', { className: 'lctitle', textContent: 'Live chain', title: `Looping the built sound of ${sources}; approximate, the build is the truth${state.latency ? `; look-ahead latency ${(state.latency * 1000).toFixed(1)} ms` : ''}` }), ...chips, range, latency, cpuBadge, safe, compare, stop, ...(state.storedTakes?.length ? [el('span', { className: 'note lcstored', textContent: `Stored take of ${state.storedTakes.map((t) => `${titleOf(t.id)} (${new Date(t.created).toLocaleString([], { dateStyle: 'short', timeStyle: 'short' })})`).join(', ')}: made by a model, never built here.` })] : []), ...(state.stale?.length ? [el('span', { className: 'note lcstale', role: 'status', textContent: `Out of date: ${state.stale.map(titleOf).join(', ')} changed after it was built, so you hear the old build. Build it to update.` })] : []), ...(state.notes?.length ? [el('span', { className: 'note', textContent: state.notes[0] })] : []));
  cancelAnimationFrame(frame);
  const tick = () => {
    if (!bar) return;
    paintCpu(chainLoad());
    for (const [id, m] of meters) {
      if (m.loud) {
        const l = chainLoudness(id), target = Number.parseFloat(String(flat().nodes.find((n) => n.id === id)?.params?.target ?? ''));
        m.loud.textContent = l && Number.isFinite(l.momentary) ? `${l.momentary.toFixed(1)} LUFS${Number.isFinite(target) ? ` (${l.momentary - target >= 0 ? '+' : ''}${(l.momentary - target).toFixed(1)} to target)` : ''}` : '- LUFS';
      }
      const levels = chainLevels(id);
      if (!levels) continue;
      m.holdLeft = Math.max(dbWidth(levels.left), m.holdLeft - 1.2); m.holdRight = Math.max(dbWidth(levels.right), m.holdRight - 1.2);
      m.left.style.width = `${dbWidth(levels.left)}%`; m.right.style.width = `${dbWidth(levels.right)}%`;
      m.left.style.setProperty('--hold', `${m.holdLeft}%`); m.right.style.setProperty('--hold', `${m.holdRight}%`);
    }
    frame = requestAnimationFrame(tick);
  };
  frame = requestAnimationFrame(tick);
}

function paintCpu(cpu) {
  if (!cpuBadge) return;
  const text = `CPU ${cpu.estimated ? '~' : ''}${Math.round(cpu.load * 100)}%`;
  if (cpuBadge.textContent !== text) cpuBadge.textContent = text;
  cpuBadge.className = `lccpu ${cpu.level}`;
  cpuBadge.title = cpu.estimated ? 'Estimated from the blocks of the chain: this browser has no render-capacity meter.' : 'How busy the audio thread is (smoothed over about a second). Over 70 percent the sound may start to crackle.';
}

onChain(paint);
