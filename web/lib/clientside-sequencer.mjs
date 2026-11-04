import { parseLanes, parseSeq, rowsOf, sourceBar } from './seq-model.mjs';
import { pause } from './yield-slice.mjs';

const MAX_NOTES = 20000;
const MAX_FRAMES = 16 * 1024 * 1024;

export function sequencePreviewPlan(text, laneText, firstBar, bars) {
  const parsed = parseSeq(text);
  if (parsed.error || !parsed.seq.channels.length || !parsed.seq.clips.length) return null;
  const { seq } = parsed;
  if (seq.channels.some((channel) => !['notes', 'bass', 'piano'].includes(channel.type) || channel.settings.set?.includes('/') || channel.settings.sample || channel.settings.file)) return null;
  const lanes = parseLanes(laneText, seq.channels);
  const rows = new Map(seq.patterns.map((pattern) => [pattern.name, rowsOf(seq, pattern)]));
  const solo = seq.channels.some((channel) => channel.settings.solo === 'on');
  const channels = [];
  for (const channel of seq.channels) {
    if (channel.settings.mute === 'on' || solo && channel.settings.solo !== 'on') continue;
    const shift = Number(channel.settings.shift ?? (channel.type === 'bass' ? '-12' : '0'));
    const pan = Number(channel.settings.pan ?? '0');
    const volume = Number.parseFloat(String(channel.settings.vol ?? '0').replace(/dB/gi, ''));
    if (!Number.isInteger(shift) || !Number.isFinite(pan) || !Number.isFinite(volume)) return null;
    const notes = [];
    for (let bar = firstBar; bar < firstBar + bars; bar++) {
      const songBar = sourceBar(seq, bar);
      for (const clip of seq.clips) {
        if (songBar < clip.at || songBar >= clip.at + clip.bars) continue;
        const pattern = seq.patterns.find((item) => item.name === clip.pattern);
        if (!pattern) return null;
        const patternBar = (songBar - clip.at) % pattern.bars;
        const row = rows.get(pattern.name)?.[channel.name] || '';
        for (const token of row.trim().split(/\s+/).filter(Boolean)) {
          const match = /^(\d+):(\d+(?:\.\d{1,6})?):(\d+(?:\.\d{1,6})?):(\d+):(\d+)$/.exec(token);
          if (!match) return null;
          const [, noteBar, beat, beats, pitch, velocity] = match;
          if (Number(noteBar) !== patternBar + 1) continue;
          notes.push({ bar, beat: Number(beat), beats: Number(beats), pitch: Number(pitch) + shift, velocity: Number(velocity), pan: Math.round(Math.max(-32, Math.min(32, pan)) * 100 / 32) });
          if (notes.length > MAX_NOTES) return null;
        }
      }
    }
    channels.push({ channel, notes, lanes: Object.entries(lanes).filter(([, names]) => names.includes(channel.name)).map(([lane]) => Number(lane)) });
  }
  return channels.length ? { channels, lanes: Object.keys(lanes).map(Number) } : null;
}

export function sequencerWorkingSetBytes(frames, laneCount) {
  return Math.ceil(frames) * Float32Array.BYTES_PER_ELEMENT * 2 * (laneCount + 2);
}

export async function renderSequenceChannels(plan, params, renderNotes, sampleRate, firstBar, bars, samplesPerBar, signal) {
  const frames = bars * samplesPerBar;
  if (frames > MAX_FRAMES || sequencerWorkingSetBytes(frames, plan.lanes.length) > 128 * 1024 * 1024) throw new Error('The local sequencer preview exceeds its memory budget.');
  const outputs = new Map([['mix', [new Float32Array(frames), new Float32Array(frames)]]]);
  for (const lane of plan.lanes) outputs.set(`lane${lane}`, [new Float32Array(frames), new Float32Array(frames)]);
  for (const { channel, notes, lanes } of plan.channels) {
    signal.throwIfAborted();
    if (!notes.length) continue;
    const defaultWave = channel.type === 'bass' ? 'saw' : channel.type === 'piano' ? 'triangle' : 'square';
    const volume = Number.parseFloat(String(channel.settings.vol ?? '0').replace(/dB/gi, '')) || 0;
    const gain = Math.max(-36, Math.min(48, Number.parseFloat(String(params.gain ?? '0').replace(/dB/gi, '')) + volume - 26));
    const audioChannels = await renderNotes(notes, { wave: channel.settings.wave || defaultWave, attack: 4, decay: 1, sustain: 100, release: 50, cutoff: 20000, gain: String(gain) }, sampleRate, firstBar, bars, samplesPerBar, signal);
    const sourceLeft = audioChannels?.[0], sourceRight = audioChannels?.[1] || sourceLeft;
    if (!sourceLeft) throw new Error(`The local tone renderer returned no audio for sequencer channel “${channel.name}”.`);
    for (const name of ['mix', ...lanes.map((lane) => `lane${lane}`)]) {
      const target = outputs.get(name);
      if (!target) throw new Error(`The local sequencer has no buffer for output “${name}”.`);
      for (let start = 0; start < frames; start += 32768) {
        signal.throwIfAborted();
        const end = Math.min(frames, start + 32768);
        for (let i = start; i < end; i++) { target[0][i] += sourceLeft[i] || 0; target[1][i] += sourceRight[i] || 0; }
        await pause();
      }
    }
  }
  const count = params.channels === 'mono' ? 1 : 2;
  return Object.fromEntries([...outputs].map(([name, channels]) => [name, { sampleRate, channels: count === 1 ? [Float32Array.from(channels[0], (value, index) => (value + channels[1][index]) * 0.5)] : channels }]));
}
