const panels = new Map();

export function registerStudioPanel(type, render) {
  if (!type || typeof render !== 'function') throw new TypeError('A studio panel needs a type and render function.');
  panels.set(type, render);
}

export function hasStudioPanel(type) { return panels.has(type); }

export function renderStudioPanel(type, context) {
  return panels.get(type)?.(context) || null;
}
