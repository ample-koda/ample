// What Koda asks of a model. The questions that need one ("tell me more", "do this") go through this object; the server side (the pet lane of the assistant: its own login and session, one thing at a time)
// replaces it. Until then Koda says plainly that the model is not linked. Tests and the pet lane use setBackend. @ample:koda

export class KodaUnavailable extends Error {
  constructor(message = 'Koda has no model to ask yet.') { super(message); this.name = 'KodaUnavailable'; }
}
export class KodaBusy extends Error {
  constructor(message = 'Koda is still busy with the last question.') { super(message); this.name = 'KodaBusy'; }
}

/**
 * A backend has: explain(subject, question, { signal }) and run(subject, instruction, { signal }). Both resolve with
 *   { kind: 'answer', title, text, more } or { kind: 'draft', summary: [{ type: 'add' | 'change' | 'remove' | 'link', text }], note, apply?: async () => boolean }
 * and reject with KodaUnavailable, KodaBusy or any Error (shown as a sad bubble).
 */
const unlinked = {
  async explain() { throw new KodaUnavailable(); },
  async run() { throw new KodaUnavailable(); },
};

let current = unlinked;
export const backend = () => current;
export const setBackend = (next) => { current = next ?? unlinked; };
