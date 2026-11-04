import { isAutomation, parseAuto } from './automation-model.mjs';
import { bendChannels } from './clientside-autotune.mjs';
import { absolute, parse } from './notes-model.mjs';
import { barBeats } from './meter.mjs';
export function pitchWorkingSetBytes(frames, channels, method) { return frames * channels * (method === 'wsola' ? 20 : 16); }

/** The move in semitones per sample of a pitch curve (semitones auto(bar:value, ...)), as the engine reads it: values in semitones, or percent of range when range is set. */
export function pitchCurve(text, rangeText, sampleRate, tempo, firstBar, length) {
  const points = parseAuto(text);
  if (!points) throw new Error('Only a pitch curve with points in bars is previewed here.');
  const samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round(tempo * 1000));
  const range = Math.min(48, Math.max(0, Number.parseInt(String(rangeText ?? '').replace(/^"|"$/g, ''), 10) || 0));
  // the engine joins the points in hundredths of a semitone (whole numbers)
  const cents = points.map((p) => ({ at: (p.bar - 1) * samplesPerBar, value: range > 0 ? Math.max(-100, Math.min(100, p.value)) * range : Math.max(-48, Math.min(48, p.value)) * 100 }));
  const curve = new Float64Array(length);
  let segment = 0;
  for (let i = 0; i < length; i++) {
    const absolute = (firstBar - 1) * samplesPerBar + i;
    while (segment + 1 < cents.length && absolute >= cents[segment + 1].at) segment++;
    if (absolute < cents[0].at || segment + 1 >= cents.length) { curve[i] = (absolute < cents[0].at ? cents[0].value : cents.at(-1).value) / 100; continue; }
    const a = cents[segment], b = cents[segment + 1];
    curve[i] = Math.trunc(a.value + (b.value - a.value) * (absolute - a.at) / Math.max(1, b.at - a.at)) / 100;
  }
  return curve;
}

export async function processPitchChannels(inputChannels, sampleRate, params, signal, { tempo = 120, firstBar = 1 } = {}) {
  const method = String(params.method || 'wsola').toLowerCase();
  if (isAutomation(params.semitones)) {
    const curve = pitchCurve(String(params.semitones), params.range, sampleRate, tempo, firstBar, inputChannels[0].length);
    signal?.throwIfAborted();
    return bendChannels(inputChannels, sampleRate, (position) => curve[Math.min(curve.length - 1, Math.max(0, Math.round(position)))], method === 'voice');
  }
  if (method === 'voice') throw new Error('Voice pitch-period detection remains server-rendered for preview.');
  const semitones = Number(params.semitones ?? '0');
  if (!Number.isFinite(semitones)) throw new Error('Pitch semitones must be a finite number.');
  signal?.throwIfAborted();
  const worker = new Worker(new URL('./clientside-pitch-worker.js', import.meta.url), { type: 'module' });
  let abort = () => {};
  try {
    return await new Promise((resolve, reject) => {
      abort = () => reject(new DOMException('The local pitch preview was cancelled.', 'AbortError'));
      signal?.addEventListener('abort', abort, { once: true });
      worker.onmessage = ({ data }) => data.error ? reject(new Error(data.error)) : resolve(data.channels.map((channel) => new Float32Array(channel)));
      worker.onerror = (event) => reject(new Error(event.message || 'The local pitch preview failed.'));
      const channels = inputChannels.map((input) => {
        const pcm = new Int16Array(input.length);
        for (let i = 0; i < input.length; i++) pcm[i] = Math.round(Math.max(-1, Math.min(1, input[i])) * (input[i] < 0 ? 32768 : 32767));
        return pcm;
      });
      worker.postMessage({ channels: channels.map((channel) => channel.buffer), cents: Math.round(semitones * 100), method, sampleRate }, channels.map((channel) => channel.buffer));
    });
  } finally { signal?.removeEventListener('abort', abort); worker.terminate(); }
}

/** The bend in semitones per sample of a midi-clip (its bend curve in percent of bend_range, 2 when empty, plus the ;bend= of single notes), or null when nothing bends. */
export function clipBendCurve(params, sampleRate, tempo, firstBar, length) {
  const clean = (v) => String(v ?? '').replace(/^"|"$/g, '').trim();
  const samplesPerBar = Math.floor(barBeats() * 60 * sampleRate * 1000 / Math.round(tempo * 1000));
  const curve = new Float64Array(length);
  let any = false;
  const bend = clean(params.bend);
  if (isAutomation(bend)) {
    const range = Math.min(48, Math.max(1, Number.parseInt(clean(params.bend_range), 10) || 2));
    const part = pitchCurve(bend, String(range), sampleRate, tempo, firstBar, length);
    for (let i = 0; i < length; i++) { curve[i] = part[i]; if (part[i]) any = true; }
  }
  const text = clean(params.notes);
  if (/;bend=-?[1-9]/.test(text)) {
    const ppq = Number(params.ppq) > 0 ? Number(params.ppq) : 1_000_000;
    for (const note of absolute(parse(text, ppq))) {
      if (!note.bend) continue;
      const from = Math.round(note.at / ppq * samplesPerBar / 4) - (firstBar - 1) * samplesPerBar, span = Math.max(1, Math.round(note.length / ppq * samplesPerBar / 4));
      for (let i = Math.max(0, from); i < Math.min(length, from + span); i++) curve[i] += Math.max(-2400, Math.min(2400, note.bend)) / 100 * (i - from) / span;
      any = true;
    }
  }
  return any ? curve : null;
}
