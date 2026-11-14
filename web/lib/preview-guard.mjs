// Whether the song or the chosen range changed while a preview was being prepared. A preview that waits for an answer of the server (the status of the
// builds) can be overtaken by an edit or a new range; what it then plays must not be the sound of the song as it was. Pure, so node --test checks it.

/** What a preview captures when it starts: the open song, its text and the chosen range (a copy, so a later change cannot reach it). */
export const captureOf = (path, text, range) => ({ path, text, range: range ? { ...range } : null });

/** True when the open song, its text or the chosen range is not what was captured. */
export function changedSince(captured, path, text, range) {
  return captured.path !== path || captured.text !== text || JSON.stringify(range || null) !== JSON.stringify(captured.range || null);
}

export const CHANGED_MESSAGE = 'The song or preview range changed while audio was preparing. Build the preview again.';
