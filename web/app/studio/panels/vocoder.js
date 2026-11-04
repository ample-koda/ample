// Studio panel of the vocoder: a play bar (the voice through the carrier, worked out in the browser, so a knob is heard while it plays), the bands of the voice and of the carrier (bars),
// and a knob for every setting. @ample:vocoder
import { el } from '../../ui.js';
import { S, build, flat } from '../../store.js';
import { onPlayer, stop as stopSong } from '../../player.js';
import { takeAudio } from './take-audio.js';
import { vocodeAudio } from '../../../lib/clientside-vocoder.mjs';
import { control, controls, number, plot, svg } from './helpers.js';
import { registerStudioPanel } from './registry.js';

const FLOOR = 40, CEIL = 20000, MAX_SECONDS = 40;
const NAMES = ['bands', 'low', 'high', 'width', 'shift', 'attack', 'release', 'sibilance', 'mix', 'gain'];
const xOf = (hz) => 20 + (Math.log(Math.min(CEIL, Math.max(FLOOR, hz)) / FLOOR) / Math.log(CEIL / FLOOR)) * 480;

// the player lives outside the panel (the panel is built again after every change of the song)
const live = { ctx: null, source: null, startedAt: 0, loop: true, show: null, stop: null, watch: 0, generation: 0, timer: 0, say: null };
onPlayer((state) => { if (state.playing && live.source) live.stop?.(); });

function vocoderPanel(context) {
  const params = context.params;
  const shown = { bands: number(params, 'bands', 20), low: number(params, 'low', 120), high: number(params, 'high', 7500), width: number(params, 'width', 140), shift: number(params, 'shift', 0) };
  const values = Object.fromEntries(NAMES.map((name) => [name, params[name]]));
  const graph = plot('Vocoder bands: the bands of the voice (top) and of the carrier (bottom)', (root) => {
    const voice = svg('g', { class: 'studio-vocoder-voice' }), carrier = svg('g', { class: 'studio-vocoder-carrier' });
    const label = (y, text) => { const t = svg('text', { x: 24, y, class: 'studio-graph-label' }); t.textContent = text; return t; };
    const draw = () => {
      voice.replaceChildren(); carrier.replaceChildren();
      const bands = Math.min(40, Math.max(4, Math.round(shown.bands))), low = Math.max(40, shown.low), high = Math.max(low * 1.5, shown.high);
      const spacing = Math.log2(high / low) / Math.max(1, bands - 1), factor = 2 ** (Math.min(24, Math.max(-24, shown.shift)) / 12), wide = Math.min(3, Math.max(0.5, shown.width / 100));
      const half = (2 ** (spacing * wide / 2) - 2 ** (-spacing * wide / 2)) / 2;
      for (let b = 0; b < bands; b++) {
        const centre = low * 2 ** (spacing * b);
        for (const [group, hz, top] of [[voice, centre, 34], [carrier, centre * factor, 126]]) {
          const left = xOf(hz * (1 - half / 2)), right = xOf(hz * (1 + half / 2));
          group.append(svg('rect', { x: left, y: top, width: Math.max(2, right - left - 1), height: 70, rx: 2, class: 'studio-vocoder-band', 'data-band': b }));
        }
      }
    };
    draw(); root.append(voice, carrier, label(26, 'Voice (modulator)'), label(120, 'Carrier'), label(214, '40 Hz … 20 kHz'));
    root.redraw = draw;
  });

  // ---- the play bar
  const status = el('output', { className: 'vocoder-status', textContent: 'Press Play to hear it: a knob is heard while it plays.' });
  const play = el('button', { type: 'button', className: 'vocoder-play', textContent: '▶ Play', 'aria-pressed': 'false', title: 'Play the voice through the carrier; a knob is heard at once' });
  const loopButton = el('button', { type: 'button', className: live.loop ? 'on' : '', textContent: '↻ Loop', 'aria-pressed': String(live.loop) });
  const show = (on) => { play.textContent = on ? '■ Stop' : '▶ Play'; play.setAttribute('aria-pressed', String(on)); };
  const stopLive = () => { try { live.source?.stop(); } catch { /* already stopped */ } live.source = null; clearInterval(live.watch); live.watch = 0; live.show?.(false); };
  live.stop = stopLive;
  live.show = show;
  live.say = (text) => { status.textContent = text; };
  show(Boolean(live.source));
  const sources = () => {
    const links = flat().links.filter((link) => link.to === context.block && link.type === 'audio' && !link.disabled);
    return ['modulator', 'carrier'].map((name) => links.find((link) => link.inName === name)?.from || null);
  };
  // works the sound out from the values on the knobs and plays it, from where it was (keep) or from the start
  const start = async (keep) => {
    const generation = ++live.generation, [voiceBlock, carrierBlock] = sources();
    const voiceTake = voiceBlock && S.build.takes?.[voiceBlock], carrierTake = carrierBlock && S.build.takes?.[carrierBlock];
    if (!voiceTake || !carrierTake) {
      live.say(voiceBlock && carrierBlock ? 'Build the blocks that feed this one first.' : 'Connect a voice to the modulator input and a sound to the carrier input.');
      if (voiceBlock && carrierBlock) {
        const missing = !voiceTake ? voiceBlock : carrierBlock;
        status.append(' ', el('button', { type: 'button', textContent: 'Build ' + missing, onclick: () => build(missing).then(() => start(false)).catch((error) => { live.say(error.message || String(error)); }) }));
      }
      return;
    }
    live.say('Working…');
    try {
      const [voice, carrier] = await Promise.all([takeAudio(voiceTake, MAX_SECONDS), takeAudio(carrierTake, MAX_SECONDS)]);
      const sound = await vocodeAudio(voice, carrier, { ...params, ...values });
      if (generation !== live.generation) return;
      const ctx = (live.ctx ||= new (window.AudioContext || window.webkitAudioContext)()), buffer = ctx.createBuffer(sound.channels.length, sound.channels[0].length, sound.sampleRate);
      sound.channels.forEach((data, i) => buffer.copyToChannel(data, i));
      const offset = keep && live.source ? (ctx.currentTime - live.startedAt) % buffer.duration : 0;
      try { live.source?.stop(); } catch { /* already stopped */ }
      stopSong();
      const source = ctx.createBufferSource();
      source.buffer = buffer; source.loop = live.loop; source.connect(ctx.destination);
      live.source = source; live.startedAt = ctx.currentTime - offset;
      source.onended = () => { if (live.source === source) stopLive(); };
      source.start(0, offset);
      if (!live.watch) { let misses = 0; live.watch = setInterval(() => { misses = document.querySelector('.studio-vocoder-panel') ? 0 : misses + 1; if (misses >= 2) live.stop?.(); }, 300); }
      live.show(true);
      live.say((voice.long || carrier.long ? 'Playing the first ' + MAX_SECONDS + ' s' : 'Playing') + ' · a knob is heard while it plays.');
    } catch (error) { live.say(error.message || String(error)); live.show(false); }
  };
  play.onclick = () => { if (live.source) { live.generation++; stopLive(); status.textContent = 'Stopped.'; return; } start(false); };
  loopButton.onclick = () => { live.loop = !live.loop; loopButton.setAttribute('aria-pressed', String(live.loop)); loopButton.classList.toggle('on', live.loop); if (live.source) live.source.loop = live.loop; };

  // a knob: the bars follow it, and while it plays the sound is worked out again (after a short pause) and carries on from the same place
  const change = (key) => (next) => {
    values[key] = next;
    if (key in shown) { shown[key] = next; graph.redraw(); }
    if (live.source) { clearTimeout(live.timer); live.timer = setTimeout(() => start(true), 200); }
  };
  return el('section', { className: 'studio-effect-panel studio-vocoder-panel', 'aria-label': 'Vocoder controls' }, [el('div', { className: 'rewind-bar vocoder-bar' }, [play, loopButton, status]), graph, controls([
    control(context, params, 'bands', 'Bands', 4, 40, '', 'linear', 112, change('bands')),
    control(context, params, 'low', 'Low', 40, 2000, 'Hz', 'log', 82, change('low')),
    control(context, params, 'high', 'High', 1000, 16000, 'Hz', 'log', 82, change('high')),
    control(context, params, 'width', 'Width', 50, 300, '%', 'linear', 82, change('width')),
    control(context, params, 'shift', 'Shift', -24, 24, 'st', 'linear', 82, change('shift')),
    control(context, params, 'attack', 'Attack', 0.1, 100, 'ms', 'log', 82, change('attack')),
    control(context, params, 'release', 'Release', 1, 1000, 'ms', 'log', 82, change('release')),
    control(context, params, 'sibilance', 'Sibilance', 0, 100, '%', 'linear', 82, change('sibilance')),
    control(context, params, 'mix', 'Mix', 0, 100, '%', 'linear', 82, change('mix')),
    control(context, params, 'gain', 'Gain', -36, 12, 'dB', 'linear', 82, change('gain')),
  ]), el('p', { className: 'note', textContent: 'The voice on the modulator input is split into these bands; its loudness per band sets the loudness of the same band of the carrier, so the carrier speaks. Shift moves the lower row against the upper one. More bands is clearer speech, fewer is a rougher robot.' })]);
}

registerStudioPanel('vocoder', vocoderPanel);
