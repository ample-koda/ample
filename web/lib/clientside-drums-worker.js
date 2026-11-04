import { StableRng } from './stable-rng.mjs';

const clip = (value) => Math.max(-32768, Math.min(32767, value));
const sequence = (text) => text.trim().split(/[\s,]+/).filter(Boolean).flatMap((entry) => {
  const [name, count] = entry.split('*');
  return Array.from({ length: Number(count || 1) }, () => name);
});

self.onmessage = ({ data }) => {
  try {
    const { params, seed, sampleRate, firstBar, bars, activeBars, beatsPerBar = 4 } = data;   // the worker has its own module state: the beats of the bar come in the message
    const tempoMilli = Math.round((Number(params.tempo) || 120) * 1000), samplesPerBar = Math.floor((beatsPerBar * 60 * sampleRate * 1000) / tempoMilli), steps = Math.max(1, Math.round(4 * beatsPerBar));
    const output = new Int16Array(bars * samplesPerBar), rng = new StableRng(BigInt(seed)), step = Math.floor(samplesPerBar / steps);
    const song = sequence(String(params.song || ''));
    const hit = (start, length, tonal, amplitude) => {
      let phase = 0;
      for (let index = 0; index < length && start + index < output.length; index++) {
        const envelope = Math.trunc(amplitude * (length - index) / length);
        let sample;
        if (tonal) {
          phase = (phase + 60000000 - Math.trunc(index * 40000000 / length)) >>> 0;
          const x = phase >>> 16, triangle = x < 32768 ? (x * 2) - 32768 : ((65535 - x) * 2) - 32768;
          sample = Math.trunc(triangle * envelope / 32768);
        } else sample = Math.trunc((Number(rng.next() >> 48n) - 32768) * envelope / 32768);
        output[start + index] = clip(output[start + index] + sample);
      }
    };
    for (const bar of activeBars) {
      const start = (bar - firstBar) * samplesPerBar, selected = song.length ? song[((bar - firstBar) % song.length + song.length) % song.length] : 'pattern';
      const pattern = selected === 'pattern' ? String(params.pattern || 'beat-A') : selected;
      const kicks = pattern === 'beat-B' ? [0, 6, 8, 14] : [0, 8];
      for (let index = 0; index < steps; index++) {
        const at = start + (index * step);
        if (kicks.includes(index)) hit(at, step * 4, true, 30000);
        if (index === 4 || index === 12) hit(at, step * 3, false, 18000);
        if (index % 2 === 0) hit(at, step, false, 6000);
      }
    }
    const channel = Float32Array.from(output, (sample) => sample < 0 ? sample / 32768 : sample / 32767);
    self.postMessage({ channels: [channel.buffer] }, [channel.buffer]);
  } catch (error) { self.postMessage({ error: error.message || 'The built-in drum preview failed.' }); }
};
