// gate as it was before it moved onto dynamics-core.mjs: the reference the new code must equal bit for bit (test: dynamics-core.test.mjs).
const pause = async () => {};
const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/db|ms/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

/** AMPLE linked-channel gate with its peak envelope, hold counter and attack/release ramp. */
export async function gateAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, length = audio.channels[0].length;
  const threshold = 10 ** (Math.max(-80, Math.min(0, number(params.threshold, -50))) / 20);
  const attack = Math.max(1, Math.trunc(Math.max(1, Math.min(200, number(params.attack, 5))) * rate / 1000));
  const hold = Math.trunc(Math.max(0, Math.min(1000, number(params.hold, 80))) * rate / 1000);
  const release = Math.max(1, Math.trunc(Math.max(5, Math.min(2000, number(params.release, 150))) * rate / 1000));
  const output = audio.channels.map(() => new Float32Array(length));
  let gain = 0, envelope = 0, open = 0;
  for (let i = 0; i < length; i++) {
    if ((i & 0xFFFF) === 0) { signal?.throwIfAborted(); await pause(); }
    let level = 0;
    for (const channel of audio.channels) level = Math.max(level, Math.abs(channel[i]));
    envelope = level > envelope ? level : envelope - (envelope / Math.max(1, release / 4));
    if (envelope > threshold) open = hold;
    else if (open > 0) open--;
    const target = open > 0 || envelope > threshold ? 1 : 0;
    gain += target > gain ? Math.max(1 / 32768, (target - gain) / attack) : (target - gain) / release;
    gain = Math.max(0, Math.min(1, gain));
    for (let channel = 0; channel < audio.channels.length; channel++) output[channel][i] = audio.channels[channel][i] * gain;
  }
  return { sampleRate: rate, channels: output };
}
