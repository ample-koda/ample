// The latency of the live chain in one figure (local-preview chunk 5.1): what the audio context adds (processing and output device) plus the look-ahead of the blocks in the chain (a limiter).
// Pure: the chain hands in the numbers (seconds), the badge shows the text and the tooltip names the contributors.

const ms = (seconds) => Math.max(0, Number(seconds) || 0) * 1000;
const format = (value) => (value >= 10 ? String(Math.round(value)) : value >= 0.05 ? value.toFixed(1) : '0');

/** { base, output } in seconds from the context, blocks as [{ id, title, seconds }]. Returns { totalMs, blocksMs, parts, label, tooltip }. */
export function latencyReport({ base = 0, output = 0, blocks = [] } = {}) {
  const parts = [];
  if (base > 0) parts.push({ name: 'audio processing', ms: ms(base) });
  parts.push({ name: 'output device', ms: ms(output), unknown: !(output > 0) });
  for (const block of blocks) if (block.seconds > 0) parts.push({ name: block.title || block.id, ms: ms(block.seconds), block: block.id });
  const totalMs = parts.reduce((sum, part) => sum + part.ms, 0), blocksMs = blocks.reduce((sum, block) => sum + ms(block.seconds), 0);
  const tooltip = `Delay between a knob and what you hear: ${parts.map((part) => `${part.name} ${part.unknown ? 'not reported by this browser' : `${format(part.ms)} ms`}`).join(', ')}. The A/B against the build is delayed by the look-ahead of the blocks (${format(blocksMs)} ms) so both line up.`;
  return { totalMs, blocksMs, parts, label: `Latency ${format(totalMs)} ms`, tooltip };
}
