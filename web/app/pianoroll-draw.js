/** Size a canvas in CSS pixels while retaining crisp vector drawing at the active display scale. */
export function sizePianoRollCanvas(canvas, cssWidth, cssHeight, ratio) {
  canvas.style.width = `${cssWidth}px`;
  canvas.style.height = `${cssHeight}px`;
  canvas.width = Math.max(1, Math.round(cssWidth * ratio));
  canvas.height = Math.max(1, Math.round(cssHeight * ratio));
  canvas.getContext('2d').setTransform(ratio, 0, 0, ratio, 0, 0);
}

/** Draw one note glyph from the roll's view model. */
export function drawPianoRollNote(context, note, geometry, style) {
  const { x, y, width, height } = geometry;
  context.globalAlpha = style.alpha;
  if (style.highContrast) {
    context.fillStyle = style.fill;
    context.fillRect(x, y, width, height);
  } else {
    const gradient = context.createLinearGradient(0, y, 0, y + height);
    gradient.addColorStop(0, style.fill);
    gradient.addColorStop(1, style.shade);
    context.fillStyle = gradient;
    if (style.selected) { context.shadowColor = style.fill; context.shadowBlur = 8; }
    context.fillRect(x, y, width, height);
    context.shadowBlur = 0;
    context.fillStyle = 'rgba(255,255,255,.35)';
    context.fillRect(x, y, width, 1);
  }
  if (style.drum) {
    // a hit of the drum kit (General MIDI channel 10): a cyan-to-magenta edge and a diamond at the start
    const edge = context.createLinearGradient(x, 0, x + Math.max(width, 1), 0);
    edge.addColorStop(0, '#2de2ff'); edge.addColorStop(1, '#ff4fd8');
    context.strokeStyle = edge; context.lineWidth = 1.5;
    context.strokeRect(x + 0.75, y + 0.75, Math.max(1, width - 1.5), Math.max(1, height - 1.5));
    if (height >= 9 && width >= 12) {
      const cx = x + 6, cy = y + height / 2, r = Math.min(4, height / 3);
      context.fillStyle = '#ffffff'; context.beginPath(); context.moveTo(cx, cy - r); context.lineTo(cx + r, cy); context.lineTo(cx, cy + r); context.lineTo(cx - r, cy); context.closePath(); context.fill();
    }
  }
  context.globalAlpha = 1;
}
