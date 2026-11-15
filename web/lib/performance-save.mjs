// Saving a performance as a song (phase 4): what was launched and stopped during a session becomes a song whose blocks are the clips as they were heard (an import-audio block for every
// run of a clip, placed on the bars where it played, looping like the clip did), summed by one bus per lane and one bus for the set. The sound of a
// clip is the audio of the clip itself (a built take, a pattern, a live
// chain), so the saved song plays what was heard whatever the source blocks do on the timeline. What the DJ did with the mix of a lane (volume, pan,
// filter, mute, solo) is automation on the bus of the lane and on a
// pan and a filter block after it (phase 5). Pure, so node --test checks it.
import { autoText, moved } from './performance-mixlog.mjs';

/** A little less than a hundredth of a beat: a launch that is this near a bar line is on it. */
export const EPS = 0.02;
/** A bus takes sixteen inputs. */
export const BUS_INPUTS = 16;

/** A name for a block: letters, digits, _ and -, never empty and starting with a letter. */
export function slug(text, fallback = 'clip') {
  const clean = String(text ?? '').normalize('NFKD').replace(/[^A-Za-z0-9_-]+/g, '-').replace(/-+/g, '-').replace(/^[-_]+|[-_]+$/g, '');
  const name = clean || fallback;
  return /^[A-Za-z]/.test(name) ? name : `c-${name}`;
}

/**
 * The runs of a capture on the bars of the saved song: the first launch is at the start of bar 1 and everything else keeps its distance to it.
 * Returns { beatsPerBar, runs: [{ clip, lane, label, bar, bars, offsetBeats, exact, stopBar, once, endSeconds }] } with the bar (counted from 1)
 * where the run starts, the number of bars it lasts, the beats into that bar where it really
 * started (0 on the bar line) and whether it also stopped on a bar line (`exact`). A loop that stopped inside a bar has `stopBar`, the (fractional)
 * bar of the stop: the saved song silences it there with a volume step.
 * `capture.accents` are the runs of one-shot and momentary lanes ({ clip, lane, from, to, endSeconds }): they play once (`once`), and `endSeconds`,
 * when it is there, is how much of the sound was heard before it was cut.
 */
export function saveModel(capture, labelOf = (id) => String(id)) {
  const bpb = Number(capture?.beatsPerBar) > 0 ? Number(capture.beatsPerBar) : 4;
  const good = (r) => Number.isFinite(r.from) && Number.isFinite(r.to) && r.to > r.from;
  const raw = [...(capture?.runs ?? []).filter(good), ...(capture?.accents ?? []).filter(good).map((r) => ({ ...r, once: true }))];
  if (!raw.length) return { beatsPerBar: bpb, runs: [], approximate: 0, once: 0, shiftBeats: 0 };
  const t0 = Math.min(...raw.map((r) => r.from));
  const runs = raw.map((r) => {
    const from = r.from - t0, to = r.to - t0;
    const startBar = Math.floor((from + EPS) / bpb);
    const into = from - startBar * bpb;
    const endBar = Math.max(startBar + 1, Math.ceil((to - EPS) / bpb));
    const exact = r.once ? true : Math.abs(to - endBar * bpb) < EPS;
    return {
      clip: r.clip, lane: r.lane ?? String(r.clip), label: labelOf(r.clip),
      bar: startBar + 1, bars: endBar - startBar,
      offsetBeats: into < EPS ? 0 : Math.round(into * 1000) / 1000,
      exact, once: Boolean(r.once),
      ...(exact ? {} : { stopBar: Math.round((1 + to / bpb) * 10000) / 10000 }),
      ...(r.once && Number.isFinite(r.endSeconds) ? { endSeconds: Math.round(r.endSeconds * 1000) / 1000 } : {}),
    };
  }).sort((a, b) => a.bar - b.bar || a.lane.localeCompare(b.lane));
  // shiftBeats: the beat of the first launch, bar 1 of the saved song
  return { beatsPerBar: bpb, runs, approximate: runs.filter((r) => !r.exact).length, once: runs.filter((r) => r.once).length, shiftBeats: t0 };
}

const quote = (text) => `"${String(text).replace(/\\/g, '/').replace(/"/g, '')}"`;

/**
 * The text of the saved song. `files` maps a clip id to the path of its WAV in the project. A lane that played more than sixteen runs gets more than
 * one bus (a bus takes sixteen inputs); the set bus takes
 * sixteen of those, which is what a performance of up to sixteen lanes needs.
 */
export function songText({ title, source, when, tempo, model, files, mix = new Map(), heardFile = '' }) {
  const names = new Set();
  const unique = (base) => { let name = base, n = 2; while (names.has(name)) name = `${base}-${n++}`; names.add(name); return name; };
  const counter = new Map();
  const blocks = [], byLane = new Map();
  for (const run of model.runs) {
    const stem = slug(run.label, 'clip');
    counter.set(stem, (counter.get(stem) ?? 0) + 1);
    const block = unique(`${stem}-${counter.get(stem)}`);
    const file = files.get(run.clip);
    if (!file) continue;
    // a loop plays on until it is stopped; a one-shot or a momentary sound plays once, and a cut one only as long as it was held (`end`, seconds in the file)
    const extra = `${run.offsetBeats ? `\n  offset ${run.offsetBeats} beats` : ''}${run.endSeconds ? `\n  end ${run.endSeconds}` : ''}`;
    blocks.push(`import-audio ${block} at ${run.bar} for ${run.bars} {\n  file ${quote(file)}\n  loop ${run.once ? 'off' : 'on'}${extra}\n}\n`);
    let out = block;
    if (run.stopBar !== undefined) {
      // a loop that stopped inside a bar: the volume steps down to silence at that moment (a few milliseconds of ramp, as short as the stop of the browser is)
      const ramp = 0.003 / ((60 / tempo) * model.beatsPerBar), at = run.stopBar, before = Math.round((at - ramp) * 10000) / 10000;
      out = unique(`${block}-stop`);
      blocks.push(`volume ${out} {\n  gain auto(${before > run.bar ? `${run.bar}:0, ${before}:0` : `${run.bar}:0`}, ${at}:-60)\n  from ${block}.audio -> audio\n}\n`);
    }

    if (!byLane.has(run.lane)) byLane.set(run.lane, []);
    byLane.get(run.lane).push(out);
  }

  const lanes = [];
  const busText = (name, inputs, extra = '') => `bus ${name} {\n  inputs ${Math.max(2, inputs.length)}\n${extra}${inputs.map((b, k) => `  from ${b}.audio -> audio${k + 1}`).join('\n')}\n}\n`;
  for (const [lane, list] of byLane) {
    const stem = `lane-${slug(lane, 'lane')}`, curves = mix.get(lane), mixed = curves && moved(curves);
    // the volume of the lane is on its bus; a lane of more than sixteen runs has a bus for every sixteen and one that sums them, which carries it
    const chunks = [];
    for (let i = 0; i < list.length; i += BUS_INPUTS) chunks.push({ name: unique(i === 0 ? stem : `${stem}-${i / BUS_INPUTS + 1}`), inputs: list.slice(i, i + BUS_INPUTS) });
    const parts = [];
    // the volume of the lane: a volume block, because the gain of a bus is not automated
    const gain = mixed && (curves.gain.points ? autoText(curves.gain.points) : curves.gain.value !== 0 ? `${curves.gain.value} dB` : null);
    let tail;
    if (chunks.length === 1) { tail = chunks[0].name; parts.push(busText(tail, chunks[0].inputs)); }
    else { for (const c of chunks) parts.push(busText(c.name, c.inputs)); tail = unique(`${stem}-sum`); parts.push(busText(tail, chunks.map((c) => c.name))); }
    if (gain) {
      const block = unique(`${stem}-volume`);
      parts.push(`volume ${block} {
  gain ${gain}
  from ${tail}.audio -> audio
}
`);
      tail = block;
    }

    if (mixed && (curves.pan.points || curves.pan.value !== 0)) {
      const block = unique(`${stem}-pan`);
      parts.push(`pan ${block} {\n  pan ${curves.pan.points ? autoText(curves.pan.points) : curves.pan.value}\n  from ${tail}.audio -> audio\n}\n`);
      tail = block;
    }

    if (mixed && (curves.cutoff.points || curves.cutoff.value !== 20000)) {
      const block = unique(`${stem}-filter`);
      parts.push(`filter ${block} {\n  cutoff ${curves.cutoff.points ? autoText(curves.cutoff.points) : `${curves.cutoff.value} Hz`}\n  from ${tail}.audio -> audio\n}\n`);
      tail = block;
    }

    lanes.push({ name: tail, lane, text: parts.join('\n') });
  }

  if (lanes.length > BUS_INPUTS) throw new Error(`The set has ${lanes.length} busses of lanes: more than a bus takes (${BUS_INPUTS}).`);
  const set = unique('set');
  const setText = `bus ${set} {\n  inputs ${Math.max(2, lanes.length)}\n${lanes.map((l, k) => `  from ${l.name}.audio -> audio${k + 1}`).join('\n')}\n}\n`;
  const head = [
    `composition ${quote(title)} { ample 1  tempo ${tempo}  master-seed 1 }`,
    '',
    `# Saved from a performance${source ? ` of "${String(source).replace(/"/g, '')}"` : ''}${when ? ` on ${when}` : ''}: `
      + `every block is a clip of the set as it was heard, on the bars where it played.`,
    '# The set has no master: the sound of the clips is summed and left as it was. Add a master when you want it levelled.',
    ...(heardFile ? [`# What you heard while playing the set is saved as ${quote(heardFile)}: add an import-audio block of it at bar 1 to listen to the two side by side.`] : []),
    '',
  ].join('\n');
  return `${head}\n${blocks.join('\n')}\n${lanes.map((l) => l.text).join('\n')}\n${setText}`;
}
