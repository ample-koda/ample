export function moveCurvePoint(points, index, x, y, width, height, snap, minGap) {
  if (index < 0 || index >= points.length) return points.map((point) => ({ ...point }));
  const next = points.map((point) => ({ ...point }));
  if (index > 0 && index < next.length - 1) {
    const low = Math.ceil((next[index - 1].x + minGap) / snap) * snap, high = Math.floor((next[index + 1].x - minGap) / snap) * snap;
    if (high >= low) next[index].x = Math.max(low, Math.min(high, Math.max(0, Math.min(width, Math.round(x / snap) * snap))));
  }
  next[index].y = Math.max(0, Math.min(height, Math.round(y / snap) * snap));
  return next;
}

export function insertCurvePoint(points, x, y, width, height, snap, minGap) {
  if (points.length < 2 || x <= points[0].x || x >= points.at(-1).x) return points.map((point) => ({ ...point }));
  const ordered = [...points].sort((left, right) => left.x - right.x);
  const right = ordered.findIndex((point) => point.x >= x), leftPoint = ordered[right - 1], rightPoint = ordered[right];
  const low = Math.ceil((leftPoint.x + minGap) / snap) * snap, high = Math.floor((rightPoint.x - minGap) / snap) * snap;
  if (high < low) return ordered;
  const nextX = Math.round(x / snap) * snap;
  if (nextX < low || nextX > high) return ordered;
  if (nextX <= leftPoint.x || nextX >= rightPoint.x || ordered.some((point) => point.x === nextX)) return ordered;
  ordered.splice(right, 0, { x: Math.max(0, Math.min(width, nextX)), y: Math.max(0, Math.min(height, Math.round(y / snap) * snap)) });
  return ordered;
}

export function removeCurvePoint(points, index) {
  if (points.length <= 2 || index <= 0 || index >= points.length - 1) return points.map((point) => ({ ...point }));
  return points.filter((_, pointIndex) => pointIndex !== index).map((point) => ({ ...point }));
}
