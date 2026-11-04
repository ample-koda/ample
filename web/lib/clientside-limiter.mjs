import { pause } from './yield-slice.mjs';
const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/db|ms/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

/** AMPLE linked-channel lookahead limiter; float math approximates its Q15 gain path. */
export async function limiterAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, length = audio.channels[0].length;
  const ceilingDb = Math.max(-24, Math.min(0, number(params.ceiling, -1)));
  const ceiling = 10 ** (ceilingDb / 20) * 32767 / 32768;
  const look = Math.max(1, Math.trunc(Math.max(1, Math.min(20, number(params.lookahead, 5))) * rate / 1000));
  const release = Math.max(1, Math.trunc(Math.max(10, Math.min(1000, number(params.release, 100))) * rate / 1000));
  const total = length + look, queueIndex = new Int32Array(look + 2), queueValue = new Float64Array(look + 2), floorRing = new Float64Array(look + 1);
  const channels = audio.channels.map(() => new Float32Array(length));
  let head = 0, tail = 0, previous = 1, floorSum = 0;
  const capacity = queueIndex.length;
  for (let i = 0; i < total; i++) {
    if ((i & 0xFFFF) === 0) { signal?.throwIfAborted(); await pause(); }
    let level = 0;
    if (i < length) for (const channel of audio.channels) level = Math.max(level, Math.abs(channel[i] * 32768));
    const wanted = level > ceiling * 32768 ? ceiling * 32768 / level : 1;
    previous = wanted < previous ? wanted : Math.min(wanted, previous + Math.max(1 / 32768, (1 - previous) / release));
    while (tail > head) {
      const last = (tail - 1) % capacity;
      if (queueValue[last] < previous) break;
      tail--;
    }
    const slot = tail % capacity;
    queueIndex[slot] = i; queueValue[slot] = previous; tail++;
    while (head < tail && queueIndex[head % capacity] < i - look) head++;
    const floor = queueValue[head % capacity];
    floorSum += floor;
    if (i >= look) {
      const old = (i - look) % floorRing.length;
      floorSum -= floorRing[old];
      const sample = i - look, gain = floorSum / look;
      for (let channel = 0; channel < audio.channels.length; channel++) channels[channel][sample] = audio.channels[channel][sample] * gain;
    }
    floorRing[i % floorRing.length] = floor;
  }
  return { sampleRate: rate, channels };
}
