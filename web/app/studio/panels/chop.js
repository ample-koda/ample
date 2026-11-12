// Studio panel of the chop block: the recording with its slices (click to add a cut, right-click to take one away), the pattern as steps, a play bar that works the sound out in the browser (a knob is heard
// while it plays) and a knob for every setting. @ample:chop
import { getContext } from '../../../lib/audio-engine.mjs';
import { el } from '../../ui.js';
import { S, build, flat } from '../../store.js';
import { onPlayer, stop as stopSong } from '../../player.js';
import { chopAudio, chopSettings, parsePattern, sliceStarts } from '../../../lib/clientside-chop.mjs';
import { takeAudio } from './take-audio.js';
import { control, controls } from './helpers.js';
import { registerStudioPanel } from './registry.js';

const MAX_SECONDS = 40, COLORS = ['#4ad6ff', '#ff9f4a', '#7be07b', '#ff6fa8', '#c79bff', '#ffd84a'];
const NAMES = ['slices', 'sensitivity', 'gap', 'pattern', 'step', 'gate', 'fit', 'repeat'];
const clean = (v) => String(v ?? '').replace(/^"|"$/g, '').trim();

const live = { ctx: null, source: null, startedAt: 0, loop: true, show: null, stop: null, watch: 0, generation: 0, timer: 0, say: null };
onPlayer((state) => { if (state.playing && live.source) live.stop?.(); });

function chopPanel(context) {
  const params = context.params, values = Object.fromEntries(NAMES.map((name) => [name, params[name]]));
  const tempo = () => Number(S.doc?.project?.tempo) || 120;
  const sourceBlock = () => flat().links.find((link) => link.to === context.block && link.inName === 'in' && link.type === 'audio' && !link.disabled)?.from || null;
  let audio = null, mono = null, starts = [], problem = '';
  const canvas = el('canvas', { className: 'chop-wave', role: 'img', 'aria-label': 'The recording and its slices: click to add a cut, right-click a cut to take it away', title: 'Click: add a cut here · Right-click a cut: take it away', height: 120 });
  const steps = el('div', { className: 'chop-steps', 'aria-label': 'The steps of the pattern' });
  const pattern = el('input', { type: 'text', className: 'chop-pattern', value: clean(params.pattern), placeholder: '1 2 3 4 3 4*2 -', disabled: context.readOnly, 'aria-label': 'Pattern', title: 'Steps separated by spaces: a slice number, - for a rest, 3*2 plays slice 3 twice inside the step' });
  const info = el('output', { className: 'chop-info' });
  const status = el('output', { className: 'vocoder-status', textContent: 'Press Play to hear it: a knob is heard while it plays.' });
  const play = el('button', { type: 'button', className: 'vocoder-play chop-play', textContent: '▶ Play', 'aria-pressed': 'false', title: 'Play the chopped sound; a knob is heard at once' });
  const loopButton = el('button', { type: 'button', className: live.loop ? 'on' : '', textContent: '↻ Loop', 'aria-pressed': String(live.loop) });
  const autoButton = el('button', { type: 'button', className: 'chop-auto', textContent: 'Find the words', disabled: context.readOnly, title: 'Throw the cuts away and let the block find the words from the level of the sound' });

  const paramsNow = () => ({ ...params, ...values });
  const refresh = () => {
    problem = '';
    try { starts = mono ? sliceStarts(mono, audio.sampleRate, paramsNow()) : []; } catch (error) { problem = error.message; starts = []; }
    let list = [];
    if (mono) { try { list = parsePattern(values.pattern, Math.max(1, starts.length)); } catch (error) { problem ||= error.message; } }
    steps.replaceChildren(...list.map((step) => el('span', { className: 'chop-step' + (step.slice < 0 ? ' rest' : ''), style: step.slice < 0 ? '' : `background:${COLORS[step.slice % COLORS.length]}`, textContent: step.slice < 0 ? '–' : String(step.slice + 1) + (step.times > 1 ? '×' + step.times : '') })));
    const settings = chopSettings(paramsNow()), beats = list.length * settings.step * settings.repeat;
    info.textContent = problem || (mono ? `${starts.length} slice${starts.length === 1 ? '' : 's'} · ${list.length} steps · ${Math.round(beats * 100) / 100} beats (${Math.round(beats / 4 * 100) / 100} bars)` : 'Build the recording to see its slices.');
    info.classList.toggle('bad', Boolean(problem));
    canvas.dataset.slices = starts.join(','); canvas.dataset.frames = String(mono?.length ?? 0); canvas.dataset.rate = String(audio?.sampleRate ?? 0);
    draw();
  };
  const draw = () => {
    const width = Math.max(240, Math.floor(canvas.parentElement?.clientWidth || 640)), height = 120, dpr = window.devicePixelRatio || 1;
    if (canvas.width !== Math.round(width * dpr)) { canvas.width = Math.round(width * dpr); canvas.height = Math.round(height * dpr); canvas.style.width = width + 'px'; canvas.style.height = height + 'px'; }
    const g = canvas.getContext('2d'), styles = getComputedStyle(document.documentElement);
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.fillStyle = styles.getPropertyValue('--panel2').trim() || '#222'; g.fillRect(0, 0, width, height);
    if (!mono) { g.fillStyle = 'rgba(255,255,255,.5)'; g.font = '12px system-ui'; g.fillText('Build the recording to see it here', 12, height / 2); return; }
    const per = mono.length / width, mid = height / 2;
    g.fillStyle = styles.getPropertyValue('--acc').trim() || '#4ad6ff'; g.globalAlpha = 0.8;
    for (let x = 0; x < width; x++) {
      let lo = 0, hi = 0;
      for (let i = Math.floor(x * per); i < Math.min(mono.length, Math.floor((x + 1) * per)); i += Math.max(1, Math.floor(per / 24))) { lo = Math.min(lo, mono[i]); hi = Math.max(hi, mono[i]); }
      g.fillRect(x, mid - hi * (mid - 6) * 1.6, 1, Math.max(1, (hi - lo) * (mid - 6) * 1.6));
    }
    g.globalAlpha = 1;
    starts.forEach((at, index) => {
      const x = Math.round(at / mono.length * width), end = index + 1 < starts.length ? starts[index + 1] / mono.length * width : width;
      g.fillStyle = COLORS[index % COLORS.length]; g.globalAlpha = 0.16; g.fillRect(x, 0, Math.max(1, end - x), height); g.globalAlpha = 1;
      g.fillRect(x, 0, 2, height); g.font = '700 12px system-ui'; g.fillText(String(index + 1), x + 5, 14);
    });
  };
  // a cut is added or taken away: the slices become a list of times in the block
  const commitStarts = (list) => {
    const text = list.map((at) => Math.round(at / audio.sampleRate * 1000) / 1000).join(' ');
    values.slices = text; context.params.slices = text; context.commit('slices', text); refresh();
  };
  const atOf = (event) => { const rect = canvas.getBoundingClientRect(); return Math.round(Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)) * mono.length); };
  canvas.onclick = (event) => { if (!mono || context.readOnly || problem) return; const at = atOf(event); if (starts.some((s) => Math.abs(s - at) < audio.sampleRate * 0.03)) return; commitStarts([...starts, at].sort((a, b) => a - b)); };
  canvas.oncontextmenu = (event) => {
    event.preventDefault();
    if (!mono || context.readOnly || starts.length < 2) return;
    const at = atOf(event); let best = 0;
    starts.forEach((s, i) => { if (Math.abs(s - at) < Math.abs(starts[best] - at)) best = i; });
    commitStarts(starts.filter((_, i) => i !== best));
  };
  autoButton.onclick = () => { values.slices = 'auto'; context.params.slices = 'auto'; context.commit('slices', 'auto'); refresh(); };
  pattern.onchange = () => {
    try { parsePattern(pattern.value, Math.max(1, starts.length)); } catch (error) { info.textContent = error.message; info.classList.add('bad'); return; }
    values.pattern = pattern.value.trim(); context.params.pattern = values.pattern; context.commit('pattern', values.pattern); refresh();
  };

  // ---- playing
  const show = (on) => { play.textContent = on ? '■ Stop' : '▶ Play'; play.setAttribute('aria-pressed', String(on)); };
  const stopLive = () => { try { live.source?.stop(); } catch { /* already stopped */ } live.source = null; clearInterval(live.watch); live.watch = 0; live.show?.(false); };
  live.stop = stopLive; live.show = show;
  live.say = (text) => { status.textContent = text; }; show(Boolean(live.source));
  const start = async (keep) => {
    const generation = ++live.generation;
    if (!audio) { live.say(sourceBlock() ? 'Build the recording first.' : 'Connect a recording to the input.'); return; }
    if (problem) { live.say(problem); return; }
    live.say('Working…');
    try {
      const sound = await chopAudio(audio, paramsNow(), tempo());
      if (generation !== live.generation) return;
      const ctx = (live.ctx ||= getContext()), buffer = ctx.createBuffer(sound.channels.length, sound.channels[0].length, sound.sampleRate);
      sound.channels.forEach((data, i) => buffer.copyToChannel(data, i));
      const offset = keep && live.source ? (ctx.currentTime - live.startedAt) % buffer.duration : 0;
      try { live.source?.stop(); } catch { /* already stopped */ }
      stopSong();
      const source = ctx.createBufferSource();
      source.buffer = buffer; source.loop = live.loop; source.connect(ctx.destination);
      live.source = source; live.startedAt = ctx.currentTime - offset;
      source.onended = () => { if (live.source === source) stopLive(); };
      source.start(0, offset);
      if (!live.watch) { let misses = 0; live.watch = setInterval(() => { misses = document.querySelector('.studio-chop-panel') ? 0 : misses + 1; if (misses >= 2) live.stop?.(); }, 300); }
      live.show(true); live.say('Playing · a knob is heard while it plays.');
    } catch (error) { live.say(error.message || String(error)); live.show(false); }
  };
  play.onclick = () => { if (live.source) { live.generation++; stopLive(); status.textContent = 'Stopped.'; return; } start(false); };
  loopButton.onclick = () => { live.loop = !live.loop; loopButton.setAttribute('aria-pressed', String(live.loop)); loopButton.classList.toggle('on', live.loop); if (live.source) live.source.loop = live.loop; };
  const change = (key) => (next) => { values[key] = next; refresh(); if (live.source) { clearTimeout(live.timer); live.timer = setTimeout(() => start(true), 200); } };

  // the recording: its take is decoded once and the slices are worked out from it. The takes of the last build reach the page a moment after it has loaded, so the window looks for a few seconds
  const block = sourceBlock();
  const load = (take) => takeAudio(take, MAX_SECONDS).then((decodedAudio) => {
    audio = decodedAudio; mono = new Float32Array(audio.channels[0].length);
    for (const channel of audio.channels) for (let i = 0; i < mono.length; i++) mono[i] += channel[i] / audio.channels.length;
    refresh();
  }).catch((error) => { info.textContent = error.message || String(error); info.classList.add('bad'); });
  if (!block) info.textContent = 'Connect a recording to the input.';
  else if (S.build.takes?.[block]) load(S.build.takes[block]);
  else {
    info.textContent = 'Build the recording to see its slices.';
    status.append(' ', el('button', { type: 'button', textContent: 'Build ' + block, onclick: () => build(block).catch((error) => { status.textContent = error.message || String(error); }) }));
    let tries = 0;
    const look = setInterval(() => { const take = S.build.takes?.[block]; if (take) { clearInterval(look); status.textContent = 'Press Play to hear it: a knob is heard while it plays.'; load(take); } else if (++tries > 40 || !document.querySelector('.studio-chop-panel')) clearInterval(look); }, 400);
  }
  requestAnimationFrame(refresh);
  const wave = el('div', { className: 'chop-wave-box' }, [canvas]);
  const watcher = new ResizeObserver(draw);
  requestAnimationFrame(() => { if (wave.isConnected) watcher.observe(wave); });
  return el('section', { className: 'studio-effect-panel studio-chop-panel', 'aria-label': 'Chop controls' }, [
    el('div', { className: 'rewind-bar vocoder-bar' }, [play, loopButton, autoButton, status]),
    wave, el('div', { className: 'chop-pattern-row' }, [el('label', { textContent: 'Pattern ' }, [pattern]), info]), steps,
    controls([
      control(context, params, 'sensitivity', 'Sensitivity', 0, 100, '%', 'linear', 82, change('sensitivity')),
      control(context, params, 'gap', 'Gap', 20, 1000, 'ms', 'log', 82, change('gap')),
      control(context, params, 'step', 'Step', 0.0625, 16, '', 'log', 82, change('step')),
      control(context, params, 'gate', 'Gate', 5, 100, '%', 'linear', 82, change('gate')),
      control(context, params, 'fit', 'Fit', 0, 100, '%', 'linear', 82, change('fit')),
      control(context, params, 'repeat', 'Repeat', 1, 64, '', 'log', 82, change('repeat')),
    ]),
    el('p', { className: 'note', textContent: 'The recording is cut where the words are (or where you click) and the slices are put on the beat in the order of the pattern: 1 2 3 4 plays them in turn, - is a rest, 3*2 plays slice 3 twice inside its step. Through a vocoder this gives a rhythmic robot voice.' }),
  ]);
}

registerStudioPanel('chop', chopPanel);
