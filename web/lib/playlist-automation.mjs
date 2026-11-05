// The automation of a block as the Playlist shows it: which parameters of a block carry an automation text (auto(...), lfo(...), env(...), fadein(...), fadeout(...)),
// which ones can still get one, and the path of a curve for a lane. Pure, tested without a DOM; the texts are read and written by automation-model.mjs. @ample:playlist-automation
import { AUTOMATABLE, automationRange, isAutomation, parseAuto } from './automation-model.mjs';

/** The parameters of a node (its template id `tpl`, its `params`) that hold an automation: [{ name, text, kind, points, range }]. */
export function automatedParams(node) {
  const out = [];
  for (const [name, text] of Object.entries(node.params || {})) {
    if (!isAutomation(text)) continue;
    const range = automationRange(node.tpl, name, node.params) || AUTOMATABLE[node.tpl]?.[name] || null;
    const kind = /^\s*(\w+)\(/.exec(String(text))?.[1] || 'auto';
    out.push({ name, text: String(text), kind, points: parseAuto(text), range });
  }
  return out;
}

/** The parameters of a node that can be automated and are not yet: [{ name, range }]. */
export function addableParams(node) {
  const have = new Set(automatedParams(node).map((p) => p.name));
  return Object.entries(AUTOMATABLE[node.tpl] || {}).filter(([name]) => !have.has(name)).map(([name, range]) => ({ name, range: automationRange(node.tpl, name, node.params) || range }));
}

/** 0 to 1 for a value in a range (logarithmic when the range says so). */
export function normalise(value, range) {
  const v = Math.max(range.min, Math.min(range.max, value));
  return range.log ? Math.log(v / range.min) / Math.log(range.max / range.min) : (v - range.min) / (range.max - range.min);
}

/** The SVG path of a curve: it starts at the left edge at the first value, goes to each point (bar b is at (b - 1) * bw) and holds the last value to `width`. Null without points. */
export function curvePath(points, range, bw, width, height, pad = 3) {
  if (!points?.length || !range) return null;
  const y = (v) => pad + (1 - normalise(v, range)) * (height - 2 * pad);
  const pieces = [`M0,${y(points[0].value).toFixed(1)}`];
  for (const p of points) pieces.push(`L${((p.bar - 1) * bw).toFixed(1)},${y(p.value).toFixed(1)}`);
  pieces.push(`L${width},${y(points.at(-1).value).toFixed(1)}`);
  return pieces.join(' ');
}

/** The y of a plain value (a parameter that is not automated) as a flat line, or null when the text is not a number. */
export function flatY(text, range, height, pad = 3) {
  const value = Number.parseFloat(String(text ?? '').replace(/[a-z%\s]+$/i, ''));
  if (!Number.isFinite(value) || !range) return null;
  return pad + (1 - normalise(value, range)) * (height - 2 * pad);
}
