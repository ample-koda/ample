// The meter of the song that is open, for the code that draws and edits bars (piano roll, grids, transport): how many quarter-note beats a bar has.
// 4 for 4/4 (the default and the value without a meter), 3 for 3/4 and 6/8, 5 for 5/4, 3.5 for 7/8. The song says it on its composition line (meter 3/4); the
// server sends it as project.beatsPerBar and the Lite reads it from the text. Code that used to write 4 * ppq writes barBeats() * ppq. The twin of NotePipeline.Bar / RenderRequest.BeatsPerBar.
let beats = 4;

/** Gets the beats of a bar in quarter notes (4 unless the open song has another meter). */
export const barBeats = () => beats;

/** Sets the beats of a bar; anything that is not a positive number up to 32 (16/2, the largest meter the server takes) means 4/4. */
export function setBarBeats(value) {
  const n = Number(value);
  beats = Number.isFinite(n) && n > 0 && n <= 32 ? n : 4;
}

/** The beats of a meter as the server takes it (Parser AMP1120): 1 to 16 over 2, 4, 8 or 16; any other meter is 4/4 there (bug 11: 9/2 was drawn as 4/4, 3/5 as 2.4). */
export function meterBeats(top, bottom) {
  const n = Number(top), d = Number(bottom);
  return Number.isInteger(n) && n >= 1 && n <= 16 && [2, 4, 8, 16].includes(d) ? (4 * n) / d : 4;
}

/** Reads the meter of a song text (the composition line) and sets it: meter 3/4 gives 3, 6/8 gives 3, none gives 4. Returns the beats. */
export function setBarBeatsFromText(text) {
  const m = /^\s*(?:composition|project)\s+"(?:[^"\\]|\\.)*"\s*\{[^}\n]*?\bmeter\s+(\d+)\/(\d+)\b/m.exec(String(text ?? ''));
  setBarBeats(m ? meterBeats(m[1], m[2]) : 4);
  return beats;
}

/** The sixteenth-note steps of a bar: 16 in 4/4, 12 in 3/4 and 6/8, 20 in 5/4. */
export const stepsPerBar = () => Math.max(1, Math.round(4 * beats));

/** The length of a bar in milliseconds at a tempo, for the meter of the open song (or the beats given): 2000 for 4/4 at 120, 1500 for 3/4. */
export const barMilliseconds = (tempo, beatsPerBar = beats) => Math.round((beatsPerBar * 60000) / Math.max(1, Number(tempo) || 120));
