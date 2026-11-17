import { modDelayAudio } from './clientside-mod-delay.mjs';
import { serverInt } from './server-int.mjs';

const number = serverInt;   // the server reads whole numbers: "5.5 ms" is 5

/** AMPLE chorus: 20 ms base, 1–15 ms triangle modulation, no feedback. */
export function chorusAudio(audio, params, signal) {
  if (params.enabled === 'off') return Promise.resolve(audio);
  return modDelayAudio(audio, {
    baseMs: 20,
    depthMs: Math.max(1, Math.min(15, number(params.depth, 5))),
    periodMs: Math.max(100, Math.min(10000, number(params.period, 1500))),
    feedback: 0,
    mix: Math.max(0, Math.min(100, number(params.mix, 50))),
    stereoQuarter: true,   // the right channel a quarter period ahead, as on the server (chorus version 2)
  }, signal);
}
