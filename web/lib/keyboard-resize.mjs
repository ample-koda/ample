// Resizing a panel with the keyboard: the new size for a key press on a separator, kept between the smallest and the largest size. Pure, so node --test
// checks it. A key that does not resize gives null.

/**
 * @param {number} size the size now, in pixels
 * @param {string} key the key (ArrowLeft, ArrowUp, Home, End, ...)
 * @param {{ min: number, max: number, step?: number, grow: string, shrink: string, shift?: boolean }} o grow and shrink are the arrow keys that make the panel bigger and smaller
 * @returns {number | null}
 */
export function resizedByKey(size, key, { min, max, step = 24, grow, shrink, shift = false }) {
  const amount = shift ? step * 4 : step;
  let next;
  if (key === grow) next = size + amount;
  else if (key === shrink) next = size - amount;
  else if (key === 'Home') next = min;
  else if (key === 'End') next = max;
  else return null;
  return Math.max(min, Math.min(max, Math.round(next)));
}
