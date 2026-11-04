/**
 * The report of a meter block ("peak -4.0 dBFS | rms -22.2 dBFS | loudness -26.3 LUFS | crest 18.1 dB | clipped 0 | 32.0 s") as numbers.
 * Returns null for any other text.
 */
export function parseMeterReport(text) {
  if (typeof text !== 'string') return null;
  const m = /peak (-?[\d.]+|silence)[^|]*\|\s*rms (-?[\d.]+|silence)[^|]*\|\s*loudness (-?[\d.]+|silence)[^|]*\|\s*crest ([\d.]+|n\/a)[^|]*\|\s*clipped (\d+)/.exec(text);
  if (!m) return null;
  const num = (v) => (v === 'silence' || v === 'n/a' ? null : Number(v));
  const corr = /correlation (-?[\d.]+)/.exec(text);
  return { peak: num(m[1]), rms: num(m[2]), loudness: num(m[3]), crest: num(m[4]), clipped: Number(m[5]), stereo: /\| stereo /.test(text), correlation: corr ? Number(corr[1]) : null };
}

/** The short form shown in the node: "peak -4.0 · -26.3 LUFS · crest 18.1" and whether it is a warning (clipped, or a peak at 0 dBFS). */
export function meterLine(report) {
  const f = (v, unit) => (v === null ? '—' : `${v.toFixed(1)}${unit}`);
  return { text: `peak ${f(report.peak, '')} · ${f(report.loudness, ' LUFS')} · crest ${f(report.crest, '')}${report.correlation === null ? '' : ` · corr ${report.correlation.toFixed(2)}`}`, warn: report.clipped > 0 || (report.peak !== null && report.peak > -0.1) || (report.correlation !== null && report.correlation < -0.2) };
}
