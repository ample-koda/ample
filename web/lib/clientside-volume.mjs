const decibels = (text) => {
  const value = Number.parseFloat(String(text ?? '').replace(/dB/gi, '').trim());
  return Number.isFinite(value) ? Math.max(-60, Math.min(48, value)) : 0;
};

/** Applies AMPLE's scalar gain, mono fold and channel mutes. Automated gain remains an explicit server fallback. */
export function volumeAudio(audio, params) {
  if (params.enabled === 'off') return audio;
  if (/^(?:auto|fadein|fadeout)\s*\(/i.test(String(params.gain || ''))) throw new Error('Automated volume requires the server audio path.');
  const left = audio.channels[0], right = audio.channels[1] || left, channels = [new Float32Array(left.length), new Float32Array(left.length)];
  const gain = 10 ** (decibels(params.gain) / 20), leftGain = gain * 10 ** (decibels(params.left_gain) / 20), rightGain = gain * 10 ** (decibels(params.right_gain) / 20);
  for (let i = 0; i < left.length; i++) {
    const l = params.mono === 'on' ? (left[i] + right[i]) / 2 : left[i], r = params.mono === 'on' ? (left[i] + right[i]) / 2 : right[i];
    channels[0][i] = params.left === 'off' ? 0 : Math.max(-1, Math.min(1, l * leftGain));
    channels[1][i] = params.right === 'off' ? 0 : Math.max(-1, Math.min(1, r * rightGain));
  }
  return { sampleRate: audio.sampleRate, channels };
}
