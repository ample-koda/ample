export function projectViewFromZoom(value, currentView, pointerX, trackLeft, resistance) {
  if (value < 0) {
    return { view: 'details', size: 0 };
  }
  return { view: 'tiles', size: Math.max(0, Math.min(4, value)) };
}

export function projectRowsWindow(total, scrollTop, viewportHeight, rowHeight, overscan, threshold) {
  if (total <= threshold) return { start: 0, end: total };
  const start = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const end = Math.min(total, start + Math.ceil(viewportHeight / rowHeight) + overscan * 2);
  return { start, end };
}
