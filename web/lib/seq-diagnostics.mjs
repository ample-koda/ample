// The problems of a .seq text, found while it is typed and not only when the block is built: the same rules as the sequencer has (notes of a bar in beats, samples in the
// project), each with the line and the columns it is about. Pure, tested without a DOM. @ample:seq-diagnostics

import { check, parseSeq } from './seq-model.mjs';

const NOTE_TYPES = new Set(['notes', 'bass', 'piano', 'gm']);
const decimal = /^\d+(\.\d+)?$/;

/** What is wrong with one note `bar:beat:length:pitch:velocity` (the beat and the length in beats, four to a bar), or '' when it is fine. */
export function noteProblem(token) {
  const f = token.split(':');
  if (f.length !== 5 && f.length !== 8 && f.length !== 9) return 'a note is bar:beat:length:pitch:velocity (five numbers)';
  const [bar, beat, length, pitch, velocity] = f;
  if (!/^\d+$/.test(bar) || Number(bar) < 1) return 'the bar is a whole number from 1';
  if (!decimal.test(beat) || !decimal.test(length)) return 'the beat and the length are numbers of beats, such as 0, 1.5 or 0.75';
  if (Number(beat) >= 4) return `the start is ${beat}: a bar has 4 beats, so the start is below 4 (beats, not sixteenth steps)`;
  if (Number(length) <= 0) return 'the length must be more than 0';
  if (Number(beat) + Number(length) > 4) return `the note ends at beat ${Number(beat) + Number(length)}, past the end of the bar (4)`;
  if (!/^\d+$/.test(pitch) || Number(pitch) > 127) return 'the pitch is a MIDI note from 0 to 127';
  if (!/^\d+$/.test(velocity) || Number(velocity) < 1 || Number(velocity) > 127) return 'the velocity is from 1 to 127';
  return '';
}

/**
 * @param {string} text the .seq text
 * @param {Set<string> | string[] | null} files the paths of the project (null: do not check the samples)
 * @returns {{ code: string, severity: 1 | 2, message: string, range: { start: { line: number, character: number }, end: { line: number, character: number } } }[]}
 */
export function seqDiagnostics(text, files = null) {
  const lines = String(text ?? '').replace(/\r\n/g, '\n').split('\n');
  const out = [];
  const add = (line, from, to, message, severity = 1) => out.push({ code: 'AMP5380', severity, message, range: { start: { line, character: from }, end: { line, character: Math.max(to, from + 1) } } });
  const known = files ? new Set(files) : null;
  const parsed = parseSeq(text);
  if (parsed.error) {
    const at = /^line (\d+):/.exec(parsed.error);
    const line = at ? Math.max(0, Number(at[1]) - 1) : 0;
    add(line, 0, (lines[line] || '').length, parsed.error.replace(/^line \d+:\s*/, ''));
  } else for (const problem of check(parsed.seq)) add(0, 0, (lines[0] || '').length, problem);

  const types = {};
  let inPattern = false;
  lines.forEach((raw, i) => {
    const line = raw.replace(/\r$/, ''), trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) return;
    const indent = line.length - line.trimStart().length;
    const words = trimmed.split(/\s+/);
    if (indent === 0) {
      inPattern = words[0] === 'pattern';
      if (words[0] === 'channel' && words[1] && words[2]) {
        types[words[1]] = words[2];
        // the files a channel plays must be in the project (a pack: reference is not a file of the project)
        for (const match of line.matchAll(/\b(sample|set|file)=("[^"]*"|\S+)/g)) {
          const value = match[2].replace(/^"|"$/g, '');
          if (!known || value.startsWith('pack:') || !value) continue;
          const exists = known.has(value) || [...known].some((path) => path.startsWith(`${value.replace(/\/$/, '')}/`));
          if (!exists) add(i, match.index + match[1].length + 1, match.index + match[0].length, `The ${match[1] === 'set' ? 'sample set' : 'sample'} '${value}' is not in the project.`);
        }
      }
      return;
    }
    if (!inPattern) return;
    const channel = words[0];
    if (!NOTE_TYPES.has(types[channel])) return;
    // the notes of a row: every token after the channel name
    let from = line.indexOf(channel) + channel.length;
    for (const token of line.slice(from).split(/(\s+)/)) {
      if (token.trim()) {
        const problem = noteProblem(token);
        if (problem) add(i, from, from + token.length, `The note '${token}' of '${channel}': ${problem}.`);
      }

      from += token.length;
    }
  });
  return out;
}
