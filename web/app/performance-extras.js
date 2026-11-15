// The parts of the Performance workspace that sit under the lanes: the set readiness (issue 272), the two decks with crossfader and cue (issue 278) and the
// rehearsal check of the device load (issue 281). They are built from a small context that the workspace gives, so this file never reads the store or the
// session itself. @ample:performance-extras
import { el } from './ui.js';
import { createDeckRig, cueRoutingSupported, listOutputs } from './performance-decks.js';
import { describeDeckBeat, describeDrift } from '../lib/deck-math.mjs';
import { READY_LIVE, READY_PREPARED, UNAVAILABLE, prepareSet } from '../lib/performance-set.mjs';
import { describeCheck, estimateLoad, summarizeLoad } from '../lib/performance-load.mjs';

const megabytes = (bytes) => `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
const bytesOf = (buffer) => buffer.length * buffer.numberOfChannels * 4;

/**
 * @typedef {object} ExtrasContext
 * @property {() => ({ name: string|null, clips: Array<{ id: string }> }|null)} busiest the scene that plays the most clips together
 * @property {(id: string) => object|null} planOf the plan of the live chain of a clip, or null for a built take
 * @property {() => string[]} filteredLanes the lanes with a filter that a fallback can take off
 * @property {() => Array<{ id: string, label: string }>} clips every clip on the lanes (one entry per block)
 * @property {() => Array<{ id: string, label: string }>} choices the blocks that have a built take (what a deck can load)
 * @property {(id: string) => Promise<AudioBuffer>} bufferOf decodes the take of a block (cached)
 * @property {() => number} tempo
 * @property {() => number} beatsPerBar
 * @property {(text: string) => void} toast
 */

/** The state shared by the sections of one workspace: the last readiness report, the deck rig and the last load check. */
export function createExtras(context) {
  let report = null;
  let rig = null;
  let load = null;   // { lines, level, measured }
  const listeners = new Set();
  const changed = () => listeners.forEach((fn) => fn());

  // ---------------------------------------------------------------- set readiness (issue 272)
  async function prepare() {
    const clips = context.clips();
    if (!clips.length) { context.toast('Put clips on the lanes first.'); return; }
    const entries = [];
    for (const clip of clips) {
      try {
        const buffer = await context.bufferOf(clip.id);
        const chain = context.planOf?.(clip.id) ?? null;
        entries.push({
          id: clip.id,
          // a built take needs no live chain (an audio take decoded in memory); a live chain was made from its sources and impulses, which are in memory now
          plan: chain ?? { ok: false, reason: 'this clip is a built take, not a live chain' },
          assets: new Set(chain?.ok ? chain.items.flatMap((item) => (item.kind === 'source' ? [`chain:${item.take}${item.role ? `:${item.role}` : ''}`] : item.tpl === 'convolution' ? [`impulse:${item.id}`] : [])) : []),
          prepared: { key: `chain:${clip.id}`, bytes: bytesOf(buffer) },
        });
      } catch (error) {
        entries.push({ id: clip.id, plan: { ok: false, reason: error.message || 'its take could not be loaded' }, assets: new Set(), prepared: null });
      }
    }

    report = { ...prepareSet(entries), labels: new Map(clips.map((c) => [c.id, c.label])), at: new Date() };
    changed();
  }

  function readinessSection() {
    const button = el('button', { type: 'button', textContent: 'Prepare the set', title: 'Load every clip of the lanes into memory and say which ones can keep playing without the server', onclick: () => prepare().catch((e) => context.toast(e.message || String(e))) });
    const rows = report
      ? report.clips.map((clip) => {
        const word = clip.state === READY_LIVE ? 'ready live' : clip.state === READY_PREPARED ? 'ready as prepared audio' : 'unavailable';
        return el('li', { className: `perf-ready perf-ready-${clip.state}` }, [`${report.labels.get(clip.id) ?? clip.id}: ${word}${clip.state === UNAVAILABLE ? ` (${clip.reason})` : clip.bytes ? `, ${megabytes(clip.bytes)}` : ''}`]);
      })
      : [];
    const summary = !report
      ? 'Prepare the set while you are connected: you then see which clips keep playing if the connection drops.'
      : report.ok
        ? `Every clip is ready (${megabytes(report.bytes)} in memory): launching, switching and stopping keep working without the server in this session.`
        : 'Some clips are not ready: they need the server, so do not rely on them if the connection may drop.';
    const online = typeof navigator !== 'undefined' && navigator.onLine === false ? ' This browser is offline right now.' : '';
    return el('details', { className: 'perf-set' }, [
      el('summary', { textContent: 'Prepare the set (offline)' }),
      el('p', { className: 'note', role: 'status', textContent: summary + online }),
      button,
      ...(rows.length ? [el('ul', { className: 'perf-ready-list' }, rows)] : []),
    ]);
  }

  // ---------------------------------------------------------------- decks (issue 278)
  const ensureRig = () => {
    if (!rig) rig = createDeckRig({ bpm: context.tempo(), beatsPerBar: context.beatsPerBar() });
    return rig;
  };

  const deckReaders = new Map();   // deck id -> repaint of its beat readout
  function deckPanel(id) {
    const choices = context.choices();
    const pick = el('select', { 'aria-label': `Deck ${id}: block`, disabled: !choices.length }, [
      el('option', { value: '', textContent: choices.length ? 'Choose a built block…' : 'Build a block first' }),
      ...choices.map((c) => el('option', { value: c.id, textContent: c.label })),
    ]);
    const status = el('span', { className: 'note', role: 'status' });
    const beatRead = el('output', { className: 'perf-deck-beat note', 'aria-label': `Deck ${id}: beat`, textContent: describeDeckBeat(null, 0) });
    const downbeat = el('input', { type: 'number', min: '0', step: '0.25', value: '0', 'aria-label': `Deck ${id}: first beat at (beats into the material)`, title: 'Where the first beat of the material lies, in beats from its start; the deck starts there, so it lines up with the bar' });
    deckReaders.set(id, () => beatRead.textContent = describeDeckBeat(rig?.state().phase[id] ?? null, loopOf));
    let loopOf = 0;
    const start = el('button', { type: 'button', textContent: 'Start at the bar', disabled: true, onclick: () => { ensureRig().start(id, { onBar: true }); status.textContent = 'Starts on the next bar.'; } });
    const stop = el('button', { type: 'button', textContent: 'Stop at the bar', onclick: () => { rig?.stop(id, { onBar: true }); status.textContent = 'Stops on the next bar.'; } });
    const cue = el('label', {}, [el('input', { type: 'checkbox', 'aria-label': `Deck ${id}: cue`, disabled: !cueRoutingSupported(), onchange: (event) => ensureRig().setCue(id, event.target.checked) }), ' Cue']);
    pick.onchange = async () => {
      if (!pick.value) return;
      status.textContent = 'Loading…';
      try {
        const buffer = await context.bufferOf(pick.value);
        const beatSeconds = 60 / context.tempo();
        // material at the set tempo: the loop is a whole number of beats, and it starts on its first beat
        const loopBeats = Math.max(1, Math.round(buffer.duration / beatSeconds));
        loopOf = loopBeats;
        ensureRig().load(id, { buffer, loopBeats, downbeatBeats: Math.max(0, Number(downbeat.value) || 0) });
        start.disabled = false;
        status.textContent = `Loaded: ${loopBeats} beats at ${context.tempo()} BPM.`;
      } catch (error) { status.textContent = error.message || String(error); }
    };
    return el('div', { className: 'perf-deck', role: 'group', 'aria-label': `Deck ${id}` }, [el('strong', { textContent: `Deck ${id}` }), pick, el('label', {}, ['First beat at ', downbeat]), start, stop, cue, beatRead, status]);
  }

  function decksSection() {
    const crossfader = el('input', { type: 'range', min: '0', max: '1', step: '0.01', value: String(rig?.state().crossfade ?? 0), 'aria-label': 'Crossfader: Deck A to Deck B', oninput: (event) => ensureRig().setCrossfade(Number(event.target.value)) });
    const routing = el('p', { className: 'note perf-routing', role: 'status', textContent: rig ? rig.state().routing.message : (cueRoutingSupported() ? 'Pick a headphone output for cue. Until then cue stays silent.' : 'Independent cue output is not available on this device: cue is off, so nothing private can reach the room.') });
    const outputs = el('select', { 'aria-label': 'Cue output (headphones)', disabled: !cueRoutingSupported() }, [el('option', { value: '', textContent: cueRoutingSupported() ? 'Choose the headphone output…' : 'Not available on this device' })]);
    if (cueRoutingSupported()) {
      outputs.onfocus = async () => {
        if (outputs.options.length > 1) return;
        for (const device of await listOutputs()) outputs.append(el('option', { value: device.deviceId, textContent: device.label || `Output ${outputs.options.length}` }));
      };
      outputs.onchange = async () => { routing.textContent = (await ensureRig().setCueDevice(outputs.value || null)).message; };
    }

    const driftRead = el('output', { className: 'perf-drift note', role: 'status', 'aria-label': 'Drift between the decks', textContent: describeDrift(null, context.tempo()) });
    const poll = setInterval(() => {
      if (!driftRead.isConnected) { clearInterval(poll); return; }
      if (!rig) return;
      deckReaders.forEach((paint) => paint());
      driftRead.textContent = describeDrift(rig.state().drift, context.tempo());
    }, 200);
    return el('details', { className: 'perf-decks' }, [
      el('summary', { textContent: 'Decks and crossfader' }),
      el('p', { className: 'note', textContent: 'Two prepared loops at the tempo of the song, started on the bar. Material must already be at that tempo and its first beat is declared per deck (there is no beat matching); the readouts show the beat of each deck and the drift between them.' }),
      deckPanel('A'), deckPanel('B'),
      el('label', { className: 'perf-xfade' }, ['A ', crossfader, ' B']),
      driftRead,
      el('div', { className: 'perf-cue-row' }, [outputs]),
      routing,
    ]);
  }

  // ---------------------------------------------------------------- the rehearsal check (issue 281)
  async function check(buffers, scene = null, bypassable = []) {
    const AudioContextClass = globalThis.AudioContext;
    const supported = Boolean(AudioContextClass && 'renderCapacity' in AudioContextClass.prototype);
    const estimate = estimateLoad(buffers.length);
    if (!AudioContextClass || !supported) {
      load = { lines: describeCheck({ supported: false, estimate, clips: buffers.length, scene, bypassable }), level: 'estimate', measured: false };
      changed();
      return;
    }

    // a context of its own with its output muted: the audience hears nothing of the check
    const ctx = new AudioContextClass();
    const samples = [];
    try {
      const mute = ctx.createGain();
      mute.gain.value = 0;
      mute.connect(ctx.destination);
      const sources = buffers.map((buffer) => { const source = ctx.createBufferSource(); source.buffer = buffer; source.loop = true; source.connect(mute); return source; });
      await ctx.resume();
      ctx.renderCapacity.addEventListener('update', (event) => samples.push({ average: event.averageLoad, peak: event.peakLoad }));
      ctx.renderCapacity.start({ updateInterval: 0.25 });
      sources.forEach((source) => source.start());
      await new Promise((resolve) => setTimeout(resolve, 4000));
      sources.forEach((source) => { try { source.stop(); } catch { /* stopped */ } });
      ctx.renderCapacity.stop();
    } finally {
      await ctx.close().catch(() => {});
    }

    const summary = summarizeLoad(samples);
    load = { lines: describeCheck({ supported: true, summary, estimate, clips: buffers.length, scene, bypassable }), level: summary.measured ? summary.level : 'estimate', measured: summary.measured };
    changed();
  }

  function rehearsalSection(options = {}) {
    const running = options.running === true;
    const button = el('button', { type: 'button', textContent: running ? 'Checking…' : 'Check this device', disabled: running, title: 'Play the clips of the busiest scene together for four seconds with the sound muted, and measure how much room the device has', onclick: async () => {
      const busiest = context.busiest();
      if (!busiest) { context.toast('Put clips on the lanes first.'); return; }
      try { await check(await Promise.all(busiest.clips.map((c) => context.bufferOf(c.id))), busiest.name, context.filteredLanes()); } catch (error) { context.toast(error.message || String(error)); }
    } });
    return el('details', { className: 'perf-rehearsal' }, [
      el('summary', { textContent: 'Rehearsal check (load of this device)' }),
      el('p', { className: 'note', textContent: 'Optional. Measures the real load where the browser can, otherwise it says that only an estimate exists. While a set plays, a load that stays high takes the lane filters off (the header says so).' }),
      button,
      ...(load ? load.lines.map((line) => el('p', { className: 'note', role: 'status', textContent: line })) : []),
    ]);
  }

  return {
    readinessSection,
    decksSection,
    rehearsalSection,
    /** A short persistent word for the header of the workspace, or an empty text before any check. */
    loadWord() { return load ? (load.measured ? `Load: ${load.level} (measured)` : 'Load: estimate only') : ''; },
    onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    /** Ends the decks (leaving the workspace or another song). */
    dispose() { rig?.dispose(); rig = null; report = null; },
  };
}
