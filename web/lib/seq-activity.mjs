// Which channels of a sequence are sounding at a place of the song (GUI flow 11: the light next to a channel). `position` is a bar number as a decimal (1 is the start of bar 1).
import { barsOfRow, rowsOf } from './seq-model.mjs';
import { notesOf } from './seq-notes.mjs';

export function activeChannels(seq, position, beatsPerBar = 4) {
  const on = new Set();
  if (!seq || !(position >= 1)) return on;
  const clip = seq.clips.find((c) => position >= c.at && position < c.at + c.bars);
  const pattern = clip && seq.patterns.find((p) => p.name === clip.pattern);
  if (!pattern) return on;
  const relative = (position - clip.at) % pattern.bars, barIndex = Math.floor(relative), inBar = relative - barIndex, beat = inBar * beatsPerBar, rows = rowsOf(seq, pattern);
  for (const channel of seq.channels) {
    const text = rows[channel.name] ?? '';
    if (!text.trim() || text.trim() === '-') continue;
    if (channel.type === 'drum') {
      const steps = barsOfRow(text, pattern.bars)[barIndex] ?? '';
      const step = steps[Math.min(steps.length - 1, Math.floor(inBar * steps.length))] ?? '.';
      if (step !== '.' && step !== '-') on.add(channel.name);
    } else if (channel.type === 'loop' || channel.type === 'guitar') on.add(channel.name);   // a loop or a strum plays through the pattern
    else if (notesOf(text).some((n) => n.bar - 1 === barIndex && beat >= n.beat && beat < n.beat + Math.max(0.25, n.length))) on.add(channel.name);
  }
  return on;
}
