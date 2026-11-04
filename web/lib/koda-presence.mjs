// The presence of Koda: visible, hiding, hidden, peeking, appearing, and muted on top of it. A small state machine without DOM, so the rules can be tested. @ample:koda

/** The states of the presence: what is seen and what Koda may do. */
export const PRESENCE = ['visible', 'hiding', 'hidden', 'peeking', 'appearing'];

export const initial = (saved = {}) => ({
  presence: saved.hidden ? 'hidden' : 'visible',
  muted: !!saved.muted,
  pending: false,   // an answer or a draft is ready and has not been shown
  spot: saved.spot ?? null,
});

/** Hide: only from visible; the animation runs in 'hiding' and ends in 'hidden' (or 'peeking' when there is something pending). */
export function hide(s) { return s.presence === 'visible' || s.presence === 'appearing' ? { ...s, presence: 'hiding' } : s; }
export function hidden(s) { return s.presence === 'hiding' ? { ...s, presence: s.pending ? 'peeking' : 'hidden' } : s; }
/** Show: from hidden or peeking; the animation runs in 'appearing' and ends in 'visible'. */
export function show(s) { return s.presence === 'hidden' || s.presence === 'peeking' || s.presence === 'hiding' ? { ...s, presence: 'appearing' } : s; }
export function appeared(s) { return s.presence === 'appearing' ? { ...s, presence: 'visible' } : s; }
export const mute = (s) => ({ ...s, muted: true });
export const unmute = (s) => ({ ...s, muted: false });

/** Something is ready (an answer, a draft): a muted or hidden Koda does not speak, it shows a dot, or peeks out of the edge. */
export function ready(s) {
  const next = { ...s, pending: true };
  if (s.presence === 'hidden') next.presence = 'peeking';
  return next;
}
/** Koda may open a bubble now: visible, not muted. */
export const mayOpenBubble = (s) => s.presence === 'visible' && !s.muted;
/** What the page keeps between visits. */
export const toSaved = (s) => ({ hidden: s.presence === 'hidden' || s.presence === 'hiding' || s.presence === 'peeking', muted: s.muted, spot: s.spot });
/** Whether the picture of Koda needs the animation loop (the tail): not while Koda is out of sight. */
export const animating = (s) => s.presence === 'visible' || s.presence === 'hiding' || s.presence === 'appearing' || s.presence === 'peeking';
