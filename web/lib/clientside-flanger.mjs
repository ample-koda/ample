import { modDelayAudio } from './clientside-mod-delay.mjs';

const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/ms|%/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

/** AMPLE flanger: 3 ms base, 1–3 ms triangle modulation and bounded feedback. */
export function flangerAudio(audio, params, signal) {
  if (params.enabled === 'off') return Promise.resolve(audio);
  return modDelayAudio(audio, {
    baseMs: 3,
    depthMs: Math.max(1, Math.min(3, number(params.depth, 2))),
    periodMs: Math.max(100, Math.min(10000, number(params.period, 2000))),
    feedback: Math.max(0, Math.min(90, number(params.feedback, 50))),
    mix: Math.max(0, Math.min(100, number(params.mix, 50))),
  }, signal);
}
