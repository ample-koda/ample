// AMP2089 (the browser twin of the parser): a block that has no length of its own (no "at ... for ..." and no bars) covers 8 bars; when the MIDI file that feeds it is longer, the rest is cut off. Only the case of
// a midi block with bars is seen here, and a midi-clip when the caller can work out its notes (clipBars(block) -> bars or null); the diagnostic is shown when the language server cannot be reached. @ample:block-length

const DEFAULT_BARS = 8;

/** The bars of the MIDI file that feeds a block (through its notes or sequence links), or null when that is not one known MIDI file. */
function upstreamBars(block, byName, seen = new Set(), clipBars = null) {
  if (seen.has(block.name)) return null;
  seen.add(block.name);
  if (block.type === 'midi') {
    const bars = Number(block.parameters?.find((p) => p.name === 'bars')?.value);
    return Number.isFinite(bars) && bars >= 1 ? Math.floor(bars) : null;
  }
  if (block.type === 'midi-clip') return clipBars ? clipBars(block) : null;
  const fed = (block.froms || []).filter((f) => !f.disabled && ['notes', 'sequence'].includes(f.output)).map((f) => (byName.get(f.block) ? upstreamBars(byName.get(f.block), byName, new Set(seen), clipBars) : null));
  return fed.length === 1 ? fed[0] : null;
}

/** The diagnostics (LSP shape) for the blocks of a song: blocks is the list of the document of the editor, lines the lines of the song text. */
export function lengthDiagnostics(blocks = [], lines = [], { clipBars = null } = {}) {
  const byName = new Map(blocks.map((b) => [b.name, b]));
  const out = [];
  for (const b of blocks) {
    if (b.type === 'midi-clip' || b.type === 'midi') continue;
    if ((b.parts?.length ?? 0) > 0 || b.parameters?.some((p) => p.name === 'bars')) continue;
    if (!(b.froms || []).some((f) => !f.disabled && ['notes', 'sequence'].includes(f.output))) continue;
    const bars = upstreamBars(b, byName, new Set(), clipBars);
    if (bars === null || bars <= DEFAULT_BARS) continue;
    const header = lines[b.line] ?? '';
    const old = b.class && b.version ? `${b.class}@${b.version}` : '';
    const word = old && header.includes(old) ? old : b.name;   // a v1 header has no class word: mark the name
    const at = Math.max(0, header.indexOf(word));
    out.push({ range: { start: { line: b.line, character: at }, end: { line: b.line, character: at + word.length } }, severity: 2, code: 'AMP2089', source: 'ample (browser)', message: `This block has no length (at ... for ...), so it covers ${DEFAULT_BARS} bars, but the music that feeds it is ${bars} bars long: the rest is cut off.`, data: { hint: 'Write at 1 for auto so that the block follows the music that feeds it.' } });
  }
  return out;
}
