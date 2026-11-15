// What the performer is told about the load of the device before and during a set (issue 281): measured against estimated headroom, a plain level and a line
// for the screen. Pure: the caller supplies the samples that the audio context measured. Nothing here reads the audio engine or the DOM.

const clamp01 = (value) => Math.min(1, Math.max(0, Number(value) || 0));

/** The words for a load from 0 (idle) to 1 (the audio thread has no time left). */
export const levelOf = (peak) => (peak < 0.6 ? 'comfortable' : peak < 0.85 ? 'tight' : 'at risk');

/**
 * A crude estimate for a device that cannot measure: each clip that plays costs about the same share of the audio thread. It is only ever shown as an estimate.
 * @param {number} clips how many clips play at once
 * @param {number} [perClip] the assumed share of capacity of one clip
 */
export const estimateLoad = (clips, perClip = 0.03) => clamp01(Math.max(0, Number(clips) || 0) * perClip);

/**
 * The summary of measured samples ({ average, peak } per update, each 0..1): the highest peak and the mean of the averages.
 * @param {Array<{ average: number, peak: number }>} samples
 */
export function summarizeLoad(samples) {
  const list = (samples || []).filter((s) => s && Number.isFinite(Number(s.peak)));
  if (!list.length) return { measured: false, peak: 0, average: 0, level: levelOf(0) };
  const peak = Math.max(...list.map((s) => clamp01(s.peak)));
  const average = list.reduce((sum, s) => sum + clamp01(s.average), 0) / list.length;
  return { measured: true, peak, average, level: levelOf(peak) };
}

/**
 * The report of a rehearsal check as lines of text, with the measured result when the device could measure and the estimate (labelled) when it could not.
 * @param {{ supported: boolean, summary?: object, estimate: number, clips: number }} result
 */
export function describeCheck({ supported, summary, estimate, clips, scene = null, bypassable = [] }) {
  const percent = (value) => `${Math.round(clamp01(value) * 100)} %`;
  const about = scene ? ` (the busiest scene, ${scene})` : '';
  const protection = bypassable.length
    ? `If the load stays high during the set, the filters of ${bypassable.join(', ')} are taken off; the header says so while it lasts, and they come back when the load is calm.`
    : 'No lane has a filter that closes the sound, so there is nothing a fallback has to take away.';
  if (supported && summary?.measured) {
    return [
      `Measured on this device with ${clips} clip${clips === 1 ? '' : 's'} playing together${about}: peak load ${percent(summary.peak)}, average ${percent(summary.average)} (${summary.level}).`,
      `The estimate was ${percent(estimate)}; the measured value is the one to trust.`,
      summary.level === 'comfortable' ? `There is room. ${protection}` : `Little room: play fewer clips at once or take the filters off before the set. ${protection}`,
    ];
  }

  return [
    `This browser cannot measure the load of the audio thread, so only an estimate is shown: about ${percent(estimate)} for ${clips} clip${clips === 1 ? '' : 's'} playing together${about} (an estimate, not a measurement).`,
    `Rehearse the set once on this device and listen for dropouts: without a measurement nothing is taken away automatically. ${protection}`,
  ];
}
