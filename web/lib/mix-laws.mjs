// The laws of the server (Effects.cs, PanTable.cs) that the browser previews must follow. Audited in local-preview chunk 2.1.

/** Pan gains like PanTable: position -32..32, left = sqrt((64 - i) / 64), right = sqrt(i / 64) with i = pan + 32 (a power split, not sin/cos).
 *  A stereo sound is balanced: both gains are divided by the centre gain and capped at 1 (the near side is left alone). */
export function panGains(pan, stereo) {
  const i = Math.max(-32, Math.min(32, Math.trunc(pan) || 0)) + 32;
  const left = Math.sqrt((64 - i) / 64), right = Math.sqrt(i / 64);
  return stereo ? { left: Math.min(1, left / Math.SQRT1_2), right: Math.min(1, right / Math.SQRT1_2) } : { left, right };
}

/** Dry and wet gains of a linear blend (distortion, chorus, flanger...): dry*(1-m) + wet*m, m = percent / 100. Not equal-power. */
export function linearMix(percent) {
  const m = Math.max(0, Math.min(100, Number(percent) || 0)) / 100;
  return { dry: 1 - m, wet: m };
}

/** Dry and wet gains of an added effect (delay, reverb): the dry sound stays as it is, the effect is added at m. */
export function additiveMix(percent) {
  return { dry: 1, wet: Math.max(0, Math.min(100, Number(percent) || 0)) / 100 };
}

/** The soft shaper of the server distortion: x / (1 + |x|). */
export const softShape = (x) => x / (1 + Math.abs(x));
