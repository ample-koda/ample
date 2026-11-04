import { el } from '../ui.js';

export function xyPad(options) {
  const x = el('input', { type: 'range', min: String(options.xMin), max: String(options.xMax), value: String(options.x), 'aria-label': `${options.label} horizontal` });
  const y = el('input', { type: 'range', min: String(options.yMin), max: String(options.yMax), value: String(options.y), 'aria-label': `${options.label} vertical` });
  const update = (preview) => { const value = { x: Number(x.value), y: Number(y.value) }; options.onPreview(value); if (!preview) options.onCommit(value); };
  x.oninput = y.oninput = () => update(true); x.onchange = y.onchange = () => update(false);
  return el('div', { className: 'studio-xy' }, [x, y]);
}
