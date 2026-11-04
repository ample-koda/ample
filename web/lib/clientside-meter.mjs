import { pause } from './yield-slice.mjs';

const format = (db, unit = 'dBFS') => Number.isFinite(db) ? `${db.toFixed(1)} ${unit}` : 'silence';
const pcmValue = (sample) => Math.max(-32768, Math.min(32767, Math.round(sample * 32768)));

function coefficients(rate, frequency, gain, q) {
  const k = Math.tan(Math.PI * frequency / rate), vh = 10 ** (gain / 20), vb = vh ** 0.4996667741545416;
  const a0 = 1 + k / q + k * k;
  return [
    [(vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0],
    [1, -2, 1, 2 * (k * k - 1) / (1 + k / q + k * k), (1 - k / q + k * k) / (1 + k / q + k * k)],
  ];
}

async function lufs(channels, rate, signal) {
  const block = rate * 400 / 1000 | 0, hop = rate * 100 / 1000 | 0, length = channels[0].length;
  if (length < block) return null;
  const shelf = coefficients(rate, 1681.974450955533, 3.999843853973347, 0.7071752369554196)[0];
  const high = coefficients(rate, 38.13547087602444, 0, 0.5003270373238773)[1];
  const rings = channels.map(() => new Float64Array(block)), rolling = channels.map(() => 0);
  const states = channels.map(() => [{ x1: 0, x2: 0, y1: 0, y2: 0 }, { x1: 0, x2: 0, y1: 0, y2: 0 }]);
  const energies = [];
  const apply = (x, c, s) => {
    const y = c[0] * x + c[1] * s.x1 + c[2] * s.x2 - c[3] * s.y1 - c[4] * s.y2;
    s.x2 = s.x1; s.x1 = x; s.y2 = s.y1; s.y1 = y;
    return y;
  };
  for (let i = 0; i < length; i++) {
    if ((i & 0xFFFF) === 0) signal?.throwIfAborted();
    const slot = i % block;
    for (let ch = 0; ch < channels.length; ch++) {
      const [s1, s2] = states[ch], weighted = apply(apply(pcmValue(channels[ch][i]) / 32768, shelf, s1), high, s2), energy = weighted * weighted;
      rolling[ch] += energy - rings[ch][slot]; rings[ch][slot] = energy;
    }
    if (i >= block - 1 && (i - block + 1) % hop === 0) energies.push(rolling.reduce((sum, value) => sum + value, 0) / block);
    if ((i & 0xFFFF) === 0) await pause();
  }
  const absolute = energies.filter((energy) => energy > 0 && -0.691 + 10 * Math.log10(energy) > -70);
  if (!absolute.length) return null;
  const loudness = (energy) => -0.691 + 10 * Math.log10(energy), relative = loudness(absolute.reduce((sum, energy) => sum + energy, 0) / absolute.length) - 10;
  const gated = absolute.filter((energy) => loudness(energy) > relative);
  return gated.length ? loudness(gated.reduce((sum, energy) => sum + energy, 0) / gated.length) : null;
}

/** AMPLE meter's pass-through audio and peak/RMS/LUFS/crest/clipped report. */
export async function meterAudio(audio, params, signal) {
  signal?.throwIfAborted();
  if (params.enabled === 'off') return { audio, report: null };
  let squareSum = 0, clipped = 0, peakSample = 0, count = 0, processed = 0;
  const channelPeaks = audio.channels.map(() => 0), length = audio.channels[0].length;
  for (let i = 0; i < length; i++) {
    for (let ch = 0; ch < audio.channels.length; ch++) {
      const value = pcmValue(audio.channels[ch][i]); squareSum += value * value; peakSample = Math.max(peakSample, Math.abs(value)); channelPeaks[ch] = Math.max(channelPeaks[ch], Math.abs(value)); count++;
      if (value === -32768 || value === 32767) clipped++;
    }
    if ((++processed & 0xFFFF) === 0) { signal?.throwIfAborted(); await pause(); }
  }
  const peak = peakSample ? 20 * Math.log10(peakSample / 32768) : Number.NEGATIVE_INFINITY;
  const rms = count && squareSum ? 10 * Math.log10(squareSum / count / (32768 * 32768)) : Number.NEGATIVE_INFINITY;
  const integrated = await lufs(audio.channels, audio.sampleRate, signal);
  const crest = Number.isFinite(peak) && Number.isFinite(rms) ? `${(peak - rms).toFixed(1)} dB` : 'n/a';
  let channelInfo = ' | mono';
  if (audio.channels.length > 1) {
    let sumL = 0, sumR = 0, cross = 0;
    for (let i = 0; i < audio.channels[0].length && i < audio.channels[1].length; i++) {
      const left = pcmValue(audio.channels[0][i]), right = pcmValue(audio.channels[1][i]);
      sumL += left ** 2; sumR += right ** 2; cross += left * right;
      if ((i & 0xFFFF) === 0) { signal?.throwIfAborted(); await pause(); }
    }
    const correlation = sumL > 0 && sumR > 0 ? cross / Math.sqrt(sumL * sumR) : 1;
    channelInfo = ` | stereo L ${format(channelPeaks[0] ? 20 * Math.log10(channelPeaks[0] / 32768) : Number.NEGATIVE_INFINITY)} R ${format(channelPeaks[1] ? 20 * Math.log10(channelPeaks[1] / 32768) : Number.NEGATIVE_INFINITY)} | correlation ${correlation.toFixed(2)}`;
  }
  return { audio, peakDbfs: peak, lufs: integrated, report: `peak ${format(peak)} | rms ${format(rms)} | loudness ${integrated === null ? 'silence' : `${integrated.toFixed(1)} LUFS`} | crest ${crest} | clipped ${clipped} | ${(audio.channels[0].length / audio.sampleRate).toFixed(1)} s${channelInfo}` };
}
