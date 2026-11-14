// Where a floating window may be: all of it inside the viewport when it fits, otherwise its top left corner at the corner of the viewport, so its title
// bar and its controls stay within reach. Pure, so node --test checks it.

/**
 * @param {{left: number, top: number, width: number, height: number}} want where the window is wanted
 * @param {{width: number, height: number}} view the size of the viewport
 * @returns {{left: number, top: number}}
 */
export function keepInside({ left, top, width, height }, view) {
  const maxLeft = Math.max(0, view.width - Math.min(width, view.width));
  const maxTop = Math.max(0, view.height - Math.min(height, view.height));
  const x = Number.isFinite(left) ? left : 0, y = Number.isFinite(top) ? top : 0;
  return { left: Math.round(Math.min(Math.max(0, x), maxLeft)), top: Math.round(Math.min(Math.max(0, y), maxTop)) };
}
