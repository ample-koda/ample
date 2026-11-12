// The trim block in the browser (port of ExtraTrim.cs): find the stretches of sound above `threshold`, separated by a silence of at least `gap`, cut to the first, the last or all of them with `padding`,
// and fade the cut. Float samples stand for the server's 16-bit ones (the level compare uses 32768 times the sample). Returns { audio, report }.
const num = (text, fallback) => { const m = /^\s*(-?\d+(?:\.\d+)?)/.exec(String(text ?? '').replace(/^"|"$/g, '')); return m ? Number(m[1]) : fallback; };
const ms = (text, fallback) => { const t = String(text ?? '').replace(/^"|"$/g, '').trim(); const v = num(t, null); if (v === null) return fallback; return /\ss?$|[0-9]s$/i.test(t) && !/ms$/i.test(t) ? Math.round(v * 1000) : Math.round(v); };

function fadeLength(text, length, rate) {
  const t = String(text ?? '').replace(/^"|"$/g, '').trim();
  if (t.endsWith('%')) return Math.trunc(length * Math.min(100, Math.max(0, num(t, 0))) / 100);
  return Math.min(length, Math.trunc(ms(t, 0) * rate / 1000));
}

export function trimAudio(audio, params) {
  const rate = audio.sampleRate, left = audio.channels[0], right = audio.channels[1] || left, stereo = audio.channels.length > 1;
  if (params.enabled === 'off') return { audio, report: '' };
  const thresholdDb = num(params.threshold, -45), amplitude = Math.min(32767, Math.max(1, Math.trunc(32768 * 10 ** (thresholdDb / 20))));
  const gapMs = Math.max(1, ms(params.gap, 400)), paddingMs = Math.max(0, ms(params.padding, 50)), mode = String(params.sections || 'warn').replace(/^"|"$/g, '');
  const perMs = Math.max(1, Math.trunc(rate / 1000)), count = Math.ceil(left.length / perMs), active = new Uint8Array(count);
  for (let i = 0; i < left.length; i++) if (!active[Math.trunc(i / perMs)] && (Math.abs(Math.trunc(left[i] * 32768)) > amplitude || Math.abs(Math.trunc(right[i] * 32768)) > amplitude)) active[Math.trunc(i / perMs)] = 1;
  const sections = []; let start = -1, lastActive = -1;
  for (let k = 0; k < count; k++) {
    if (active[k]) { if (start < 0) start = k; lastActive = k; } else if (start >= 0 && k - lastActive >= gapMs) { sections.push([start, lastActive]); start = -1; }
  }
  if (start >= 0) sections.push([start, lastActive]);
  const seconds = (v) => (v / 1000).toFixed(2);
  if (!sections.length) return { audio, report: `trim: no sound above ${Number(thresholdDb.toFixed(1))} dB in this recording, nothing was cut.` };
  const [first, last] = mode === 'first' ? sections[0] : mode === 'last' ? sections[sections.length - 1] : [sections[0][0], sections[sections.length - 1][1]];
  const from = Math.max(0, first * perMs - paddingMs * perMs), to = Math.min(left.length, (last + 1) * perMs + paddingMs * perMs);
  const cut = audio.channels.map((c) => c.slice(from, to)), length = cut[0].length;
  const inLength = Math.min(length, fadeLength(params.fadein, length, rate)), outLength = Math.min(length, fadeLength(params.fadeout, length, rate));
  for (const channel of (stereo ? cut : [cut[0]])) {
    for (let i = 0; i < inLength; i++) channel[i] *= i / inLength;
    for (let i = 0; i < outLength; i++) channel[length - 1 - i] *= i / outLength;
  }
  let report = `trim: sound from ${seconds(first)} s to ${seconds(last + 1)} s of ${seconds(count)} s` + (sections.length === 1 ? ' (1 section).' : ` (${sections.length} sections: ${sections.map((s) => `${seconds(s[0])}-${seconds(s[1] + 1)}`).join(', ')}).`);
  if (sections.length > 1 && mode === 'warn') report += ' WARNING: this recording holds several stretches of sound; the cut runs from the start of the first to the end of the last.';
  return { audio: { sampleRate: rate, channels: cut }, report };
}

/** The silence block: `length` in ms (or seconds with a unit) or `auto` (the bars of the block). */
export function silenceAudio(params, autoSamples, rate) {
  const text = String(params.length ?? 'auto').replace(/^"|"$/g, '').trim();
  const samples = text && text.toLowerCase() !== 'auto' ? Math.trunc(ms(text, 0) * rate / 1000) : autoSamples;
  return { sampleRate: rate, channels: [new Float32Array(Math.max(0, samples))] };
}
