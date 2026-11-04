// Where a bubble of Koda goes: above the top of the note, or under Koda when there is no room above, kept inside its container, with the tail pointing at Koda. Pure: rectangles in, numbers out. @ample:koda

/**
 * pet: { left, top, width, height } of Koda in the container; container: { width, height }; bubble: { width, height }.
 * Returns { left, top, below, tail } where tail is the x of the point of the tail inside the bubble.
 */
export function placeBubble({ pet, container, bubble, gap = 14, edge = 6 }) {
  const ax = pet.left + pet.width * 0.47;
  const above = pet.top + pet.height * 0.1;
  const under = pet.top + pet.height * 0.93;
  let top = above - bubble.height - gap;
  let below = false;
  if (top < edge) { top = under + gap; below = true; }
  top = Math.max(edge, Math.min(top, container.height - bubble.height - edge));
  const left = Math.max(edge, Math.min(ax - bubble.width * 0.7, container.width - bubble.width - edge));
  const tail = Math.max(26, Math.min(ax - left, bubble.width - 26));
  return { left, top, below, tail };
}

/** A bubble that stays where it was while Koda is dragged: its tail turns to Koda (the top edge when Koda is above it) and a line joins the tail to Koda. */
export function tetherFor({ pet, container, bubble, at, edge = 6 }) {
  const ax = pet.left + pet.width * 0.47;
  const petTop = pet.top + pet.height * 0.1;
  const petBottom = pet.top + pet.height * 0.93;
  const petAbove = petTop < at.top;
  const tail = Math.max(26, Math.min(ax - at.left, bubble.width - 26));
  return { below: petAbove, tail, from: { x: at.left + tail, y: at.top + (petAbove ? -9 : bubble.height + 9) }, to: { x: ax, y: petAbove ? petTop + 10 : petBottom - 6 } };
}

/** The spot of Koda kept inside the container, with room above for a jump and the tail. */
export function clampSpot({ left, top }, size, container, roomAbove = 46) {
  return { left: Math.max(0, Math.min(left, container.width - size.width)), top: Math.max(roomAbove, Math.min(top, container.height - size.height * 0.8)) };
}
