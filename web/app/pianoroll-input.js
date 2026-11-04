/** Convert a pointer event into the canvas's CSS-pixel coordinate space. */
export function pianoRollPoint(canvas, event) {
  const rect = canvas.getBoundingClientRect();
  return { x: ((event.clientX - rect.left) / rect.width) * canvas.clientWidth, y: ((event.clientY - rect.top) / rect.height) * canvas.clientHeight };
}

/** Hit-test the roll view rows, with the resize handle kept at the note's right edge. */
export function pianoRollHit(notes, x, y, pxBeat, rowHeight, xOf, yOf, ppq, resizeEdge) {
  let best = null;
  for (const note of notes) {
    const left = xOf(note.at), width = Math.max(2, (note.length / ppq) * pxBeat), top = yOf(note.pitch);
    const right = left + width, handle = Math.max(10, resizeEdge);
    if (x >= left && x <= right + 4 && y >= top && y <= top + rowHeight)
      best = { note, edge: x >= Math.max(left + Math.min(width * 0.5, 4), right - handle) };
  }
  return best;
}
