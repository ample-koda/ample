// A place inside a clip as a place in the song, in seconds: the clip is placed at a bar (1 is the start of the song), and the place is measured in beats from the
// left edge of the clip. Pure, so node --test checks it.
export function songSeconds({ placedAtBar = 1, localBeats = 0, beatsPerBar = 4, bpm = 120 }) {
  const beats = (Math.max(1, placedAtBar) - 1) * beatsPerBar + Math.max(0, localBeats);
  return (beats * 60) / Math.max(1, bpm);
}
