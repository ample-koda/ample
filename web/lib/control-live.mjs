// Controls in the live chain (plan local-preview 2.8): a `curve` block (and `map` blocks after it) steers a parameter of a block in the chain while it plays. The numbers are those of the
// server (ControlBlocks.cs and ControlCurve.cs): a control holds per mille (a general control 0 to 1000, a typed one its unit times 1000), a connected general control is scaled to the
// range of the parameter, a typed one is the value itself. Pure (no audio), tested in node. `follow` (a level measured from a sound) and `fire` (notes) are not made live here.
import { AUTOMATABLE, automationValueAt, parseAuto } from './automation-model.mjs';

const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const unquote = (text) => String(text ?? '').trim().replace(/^"|"$/g, '');

/** A number as per mille like the server's Fixed(): "0.5" is 500, "1" is 1000; words and units are ignored; the fallback when there is no number. */
export function fixed(text, fallback) {
  const m = /^\s*(-)?(\d+)(?:\.(\d+))?/.exec(unquote(text));
  if (!m) return fallback;
  const value = Number(m[2]) * 1000 + Number(((m[3] || '') + '000').slice(0, 3));
  return m[1] ? -value : value;
}

/** The unit of a curve or map block ('Hz', 'dB', ... or null for a general control). */
export const unitOf = (params) => { const u = unquote(params?.unit); return !u || u === 'none' ? null : u; };

/** The value of a curve block at a bar position (1-based, fractional), in per mille: null when the text is an lfo or env (the browser follows auto(...) and plain numbers only). */
export function curveLevel(params, bar) {
  const text = unquote(params?.value);
  if (params?.enabled === 'off') return 0;
  const points = parseAuto(text);
  if (points) { const value = automationValueAt(points, bar); return unitOf(params) ? value * 1000 : value; }
  if (/^\s*(lfo|env|fadein|fadeout)\(/.test(text)) return null;
  return fixed(text, 0);
}

/** The same as the server's map block (without the smoothing, which is a time filter): input in per mille, result in per mille. */
export function mapLevel(params, input) {
  if (params?.enabled === 'off') return input;
  let outLow = fixed(params?.out_low, 0), outHigh = fixed(params?.out_high, 1000);
  const inLow = fixed(params?.in_low, 0), inHigh = fixed(params?.in_high, 1000), span = inHigh - inLow === 0 ? 1 : inHigh - inLow;
  if (params?.invert === 'on') [outLow, outHigh] = [outHigh, outLow];
  let mapped = outLow + Math.trunc(((input - inLow) * (outHigh - outLow)) / span);
  if (params?.limit !== 'off') mapped = clamp(mapped, Math.min(outLow, outHigh), Math.max(outLow, outHigh));
  return mapped;
}

/**
 * The controls that steer the parameters of blocks: [{ target, param, chain: [block ids from the curve to the last map] }] for every control link whose source is a curve (through maps).
 * nodes: [{ id, tpl, params }], links: flat().links, targets: a function (id, param) => whether the block is in the live chain and the parameter moves live.
 */
export function controlTargets(nodes, links, targets) {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const found = [];
  for (const link of links) {
    if (link.type !== 'control' || link.disabled) continue;
    const target = byId.get(link.to);
    if (!target || !AUTOMATABLE[target.tpl]?.[link.inName] || !targets(link.to, link.inName)) continue;
    const chain = [];
    let at = byId.get(link.from), guard = 0, out = link.outName || 'level';
    while (at && guard++ < 8) {
      chain.unshift(at.id);
      if (at.tpl === 'curve' || at.tpl === 'follow') break;
      if (at.tpl !== 'map') { at = null; break; }
      const up = links.find((l) => l.to === at.id && l.type === 'control' && l.inName === 'in' && !l.disabled);
      at = up && byId.get(up.from); out = up?.outName || 'level';
    }
    if (at?.tpl === 'curve' || at?.tpl === 'follow') found.push({ target: link.to, param: link.inName, chain, out });
  }
  return found;
}

/** The level (per mille) at the end of a control chain at a bar; null when a block in it cannot be followed live. */
export function chainLevel(chain, nodesById, bar, follow = null, out = 'level', seconds = 0) {
  let level = null;
  for (const id of chain) {
    const node = nodesById.get(id);
    if (!node) return null;
    level = node.tpl === 'follow' ? (follow ? follow(id, out, seconds) : null) : node.tpl === 'curve' ? curveLevel(node.params, bar) : level === null ? null : mapLevel(node.params, level);
    if (level === null) return null;
  }
  return level;
}

/** Whether the end of a control chain is typed (a unit): the last block that has a unit decides, as on the server where map sets the unit of its output. */
export function chainTyped(chain, nodesById) {
  let typed = false;
  for (const id of chain) { const node = nodesById.get(id); if (node) typed = Boolean(unitOf(node.params)); }
  return typed;
}

/** The text of a parameter that a control sets: a general control is scaled to the range of the parameter (whole numbers, as on the server), a typed one is the value in its unit. */
export function controlledValue(tpl, param, level, typed) {
  const range = AUTOMATABLE[tpl]?.[param];
  if (!range) return null;
  const min = Math.round(range.min), max = Math.round(range.max);
  const value = typed ? Math.round(level / 1000) : min + Math.trunc(((max - min) * clamp(level, 0, 1000) + 500) / 1000);
  return range.unit ? `${value} ${range.unit}` : String(value);
}
