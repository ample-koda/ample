// Quick or held arrow keys on a control whose value is written to the song and drawn again only when the server answers: each key must step from the value the
// key before asked for, not from the value of the last drawing, or three key repeats before the first answer all write "one step on" (Studio bug hunt
// 2026-11-24, bug 10: a clip held on ArrowRight from bar 5 ended at bar 6 instead of 8). A value asked for is remembered for a short while per control; after
// that pause (or on another control) the drawn value counts again, so an undo or an edit elsewhere is never stepped over for long.

/**
 * @param {{ now?: () => number, keepMs?: number }} [options]
 * @returns {{ from: (key: string, drawn: any) => any, asked: (key: string, value: any) => void, forget: () => void }}
 */
export function createPendingSteps({ now = () => Date.now(), keepMs = 700 } = {}) {
  let pending = null;   // { key, value, at }
  return {
    /** The value to step from: the one asked for last on this control within the last moments, else the drawn one. */
    from(key, drawn) {
      return pending && pending.key === key && now() - pending.at < keepMs ? pending.value : drawn;
    },
    /** Remembers the value asked for on a control. */
    asked(key, value) { pending = { key, value, at: now() }; },
    /** Forgets it (an undo, another song). */
    forget() { pending = null; },
  };
}
