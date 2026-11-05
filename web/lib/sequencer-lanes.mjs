// The lanes of a sequencer block, read from its text for the Playlist: which channels sit on which lane (\`lanes lane1=kick lane2=hat+open ...\`), which channels each
// pattern plays, and where the playlist of the block puts its clips (\`clip pulse track=1 at=1 for=32\`) and sections. Pure, tested without a DOM. @ample:sequencer-lanes

/** The text of the block that starts at line `start` (0-based): up to its matching closing brace. */
export function blockLines(text, start) {
  const lines = String(text || '').split('\n');
  let depth = 0, seen = false;
  const out = [];
  for (let i = start; i < lines.length; i++) {
    out.push(lines[i]);
    for (const ch of lines[i]) { if (ch === '{') { depth++; seen = true; } else if (ch === '}') depth--; }
    if (seen && depth <= 0) break;
  }
  return out;
}

/** { lanes: [{ id, channels }], patterns: { name: { bars, channels } }, clips: [{ pattern, track, at, length }], sections: [{ name, at, length }] } from the lines of a block. */
export function parseSequencer(lines) {
  const lanes = [], patterns = {}, clips = [], sections = [];
  let current = null, inPlaylist = false, playlistRow = -1;
  for (const [row, raw] of lines.entries()) {
    const line = raw.trim();
    if (!line || line.startsWith('#')) continue;
    const words = line.split(/\s+/);
    if (words[0] === 'lanes') {
      for (const w of words.slice(1)) { const [id, list] = w.split('='); if (id && list) lanes.push({ id, channels: list.split('+').filter(Boolean) }); }
      continue;
    }
    if (words[0] === 'pattern' && words[1]) {
      inPlaylist = false;
      const bars = Number((words.find((w) => w.startsWith('bars=')) || 'bars=1').slice(5)) || 1;
      current = patterns[words[1]] = { bars, channels: [] };
      continue;
    }
    if (words[0] === 'playlist') { inPlaylist = true; current = null; playlistRow = row; continue; }
    if (inPlaylist) {
      const field = (key) => Number((words.find((w) => w.startsWith(`${key}=`)) || '').slice(key.length + 1));
      if (words[0] === 'clip' && words[1]) clips.push({ pattern: words[1], track: field('track'), at: field('at'), length: field('for'), row });
      else if (words[0] === 'section' && words[1]) sections.push({ name: words[1], at: field('at'), length: field('for') });
      continue;
    }
    if (current && words.length > 1 && !['sequence', 'sequencer', 'channel'].includes(words[0])) current.channels.push(words[0]);
  }
  return { lanes, patterns, clips, sections, playlistRow };
}

/**
 * The clips of each lane in the bars of the song: a playlist clip lies on a lane when its pattern plays a channel of that lane.
 * `blockAt` is the first bar of the sequencer block in the song; the playlist counts from 1 inside the block.
 */
export function laneClips(parsed, blockAt = 1) {
  return parsed.lanes.map((lane) => ({
    id: lane.id,
    channels: lane.channels,
    clips: parsed.clips
      .filter((c) => parsed.patterns[c.pattern]?.channels.some((ch) => lane.channels.includes(ch)))
      .map((c) => ({ pattern: c.pattern, at: blockAt + c.at - 1, length: c.length, row: c.row, track: c.track })),
  })).filter((lane) => lane.clips.length);
}

/** The line of a playlist clip with a new place and length (the rest of the line, its pattern and its track, stays as written). */
export function rewriteClipLine(line, { at, length }) {
  let out = line;
  if (Number.isInteger(at)) out = out.replace(/\bat=\d+/, `at=${at}`);
  if (Number.isInteger(length)) out = out.replace(/\bfor=\d+/, `for=${length}`);
  return out;
}

/** A new playlist clip line, indented like the playlist lines around it. */
export const clipLine = (pattern, track, at, length, indent = '      ') => `${indent}clip ${pattern} track=${track} at=${at} for=${length}`;

/**
 * The lines of a block after an edit of its playlist: `{ move: { row, at, length } }` rewrites a clip line, `{ remove: row }` takes it out, `{ add: { pattern, track, at, length } }`
 * puts a clip after the last clip (or after the playlist line). `parsed` is parseSequencer of the same lines; the clip's `at` here is inside the block (counted from 1).
 */
export function editPlaylist(lines, parsed, edit) {
  const out = [...lines];
  if (edit.move) { out[edit.move.row] = rewriteClipLine(out[edit.move.row], edit.move); return out; }
  if (edit.pattern) { out[edit.pattern.row] = out[edit.pattern.row].replace(/(\bclip\s+)\S+/, (_, head) => head + edit.pattern.name); return out; }
  if (edit.remove !== undefined) { out.splice(edit.remove, 1); return out; }
  if (edit.add) {
    if (parsed.playlistRow < 0) return null;
    const after = parsed.clips.length ? Math.max(...parsed.clips.map((c) => c.row)) : parsed.playlistRow;
    const indent = /^\s*/.exec(lines[after])?.[0] || '      ';
    out.splice(after + 1, 0, clipLine(edit.add.pattern, edit.add.track, edit.add.at, edit.add.length, parsed.clips.length ? indent : indent + '  '));
    return out;
  }
  return null;
}
