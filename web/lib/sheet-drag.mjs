// Bottom-sheet drag maths (phone layout). Pure, so it can be tested without a browser.

/** Largest sheet height: most of the viewport, but the header and the nav bar stay visible. */
export function sheetMax(viewportHeight, reserved = 120) {
  return Math.max(160, Math.round(viewportHeight - reserved));
}

/**
 * The sheet height while the handle is dragged. Dragging up grows the sheet, down shrinks it.
 * `close` is true when the pointer ends well below the smallest useful height (a swipe down closes the sheet).
 */
export function dragSheet({ startY, y, startHeight, max, min = 160, closeBelow = 110 }) {
  const raw = startHeight + (startY - y);
  return { height: Math.max(min, Math.min(max, Math.round(raw))), close: raw < closeBelow };
}

/** A press that barely moved is a tap on the handle (it closes the sheet). */
export function isTap(startY, endY, slop = 6) {
  return Math.abs(endY - startY) <= slop;
}
