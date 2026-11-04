const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

export function compareAudioBuffers(input, output, segments = 128) {
  const frames = Math.min(input.length, output.length);
  if (!frames || !(segments > 0)) return { input: [], output: [], gainDb: [] };
  const count = Math.min(segments, frames), inputEnvelope = [], outputEnvelope = [], gainDb = [];
  for (let segment = 0; segment < count; segment++) {
    const from = Math.floor(segment * frames / count), to = Math.max(from + 1, Math.floor((segment + 1) * frames / count));
    let inputPower = 0, outputPower = 0;
    for (let frame = from; frame < to; frame++) { inputPower += input[frame] * input[frame]; outputPower += output[frame] * output[frame]; }
    const inputRms = Math.sqrt(inputPower / (to - from)), outputRms = Math.sqrt(outputPower / (to - from));
    inputEnvelope.push(clamp(inputRms, 0, 1)); outputEnvelope.push(clamp(outputRms, 0, 1));
    gainDb.push(20 * Math.log10(Math.max(1e-5, outputRms) / Math.max(1e-5, inputRms)));
  }
  return { input: inputEnvelope, output: outputEnvelope, gainDb };
}

export function envelopePath(values, baseline, amplitude, width) {
  if (!values.length) return '';
  return values.map((value, index) => `${index ? 'L' : 'M'}${(index / Math.max(1, values.length - 1) * width).toFixed(2)} ${(baseline + (index % 2 ? 1 : -1) * value * amplitude).toFixed(2)}`).join(' ');
}
