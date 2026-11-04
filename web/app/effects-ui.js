// Compact properties cards for the built-in audio effects.
// @ample:effect-properties

import { el, toast } from './ui.js';
import { fieldFor } from './fields.js';
import { effectModel } from '../lib/effects-model.js';
import { toggle } from './player.js';
import { openStudioPanel } from './studio/modal.js';

const svg = (viewBox, children) => {
  const node = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  node.setAttribute('viewBox', viewBox);
  node.setAttribute('aria-hidden', 'true');
  children.forEach(([tag, attrs]) => {
    const child = document.createElementNS('http://www.w3.org/2000/svg', tag);
    Object.entries(attrs).forEach(([key, value]) => child.setAttribute(key, value));
    node.append(child);
  });
  return node;
};

function visual(model) {
  const v = model.visual;
  if (v?.kind === 'curve') {
    const points = v.points.map(([x, y]) => `${x + 5},${y + 8}`).join(' ');
    return svg('0 0 110 48', [
      ['path', { d: 'M5 24H105', class: 'fx-grid' }], ['polyline', { points, class: 'fx-line' }],
      ...v.points.map(([x, y]) => ['circle', { cx: x + 5, cy: y + 8, r: 2.5, class: 'fx-dot' }]),
    ]);
  }
  if (v?.kind === 'compression') {
    const x = 8 + (Math.max(-60, Math.min(0, v.threshold)) + 60) / 60 * 50;
    const slope = Math.max(.18, 1 / Math.max(1, v.ratio));
    return svg('0 0 110 54', [
      ['path', { d: 'M8 46H102M8 46V5', class: 'fx-grid' }],
      ['path', { d: `M8 46L${x} 8L${x + 42} ${8 + 38 * slope}`, class: 'fx-line' }],
      ['path', { d: `M${x} 8V47`, class: 'fx-mark' }],
    ]);
  }
  if (v?.kind === 'delay') {
    return el('div', { className: 'fx-delayviz', title: `${v.time} ms · ${v.feedback}% feedback` },
      [0, 1, 2, 3].map((i) => el('i', { style: `height:${Math.max(18, 100 - i * v.feedback)}%` })));
  }
  if (v?.kind === 'space') {
    return el('div', { className: 'fx-spaceviz', title: `Room ${v.size}% · decay ${v.decay}%` }, [
      el('i', { style: `width:${22 + v.size * .45}%;height:${22 + v.size * .45}%` }),
      el('b', { textContent: `${v.decay}%` }),
    ]);
  }
  return el('div', { className: `fx-glyph fx-${model.type}`, 'aria-hidden': 'true' });
}

/** Builds a card from template metadata; every edit is sent through the normal parameter command. */
export function renderEffectCard(type, definitions, values, options) {
  const model = effectModel(type, definitions, values);
  if (!model) return null;
  const card = el('section', { className: `fx-card fx-${type}` });
  const head = el('div', { className: 'fx-head' });
  const bypass = el('button', { className: `fx-bypass${model.enabled === 'on' ? ' on' : ''}`, textContent: model.enabled === 'on' ? 'ON' : 'OFF', role: 'switch', 'aria-checked': String(model.enabled === 'on'), 'aria-label': 'Effect enabled', title: 'Effect enabled', disabled: options.disabled });
  bypass.onclick = () => options.setParameter('enabled', model.enabled === 'on' ? 'off' : 'on');
  const play = el('button', { className: 'fx-play', textContent: '▶', 'aria-label': 'Build only this block, then play its take', title: 'Build only this block, then play its take' });
  play.onclick = async () => {
    try {
      const report = options.readOnly ? null : await options.build();
      const block = (report?.blocks || []).find((b) => (b.block ?? b.Block) === options.block);
      const take = block?.takeId ?? block?.TakeId ?? (options.readOnly ? options.take : null);
      if (take) await toggle(take); else toast('No audio take was produced for this block.');
    } catch (error) { toast(error.message); }
  };
  const advanced = el('button', { type: 'button', className: 'fx-advanced', textContent: '↗', 'aria-label': 'Open advanced controls', title: 'Open advanced controls' });
  advanced.onclick = () => openStudioPanel({ type, title: type, block: options.block, definitions, getParams: options.getParams, readOnly: options.disabled, preview: () => {}, commit: options.setParameter });
  head.append(el('strong', { textContent: type.toUpperCase() }), el('span', { className: 'sp' }), advanced, bypass, play);
  card.append(head, visual(model));
  const grid = el('div', { className: 'fx-controls' });
  model.controls.forEach((meta) => {
    const name = meta.name;
    const input = fieldFor(name, meta, meta.value, {
      disabled: options.disabled, title: meta.documentation || '',
      save: (value) => options.setParameter(name, value),
    });
    if (input) grid.append(el('label', { className: 'fx-control' }, [el('span', { textContent: meta.label || name }), meta.unit ? el('span', { className: 'fx-value' }, [input, el('small', { textContent: meta.unit })]) : input]));
  });
  card.append(grid);
  return card;
}
