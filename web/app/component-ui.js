// The mini layout of a component (docs/language/19-components.md, "Layout and live preview"): knobs, sliders, switches, choices, an xy pad, readouts and meters
// in rows and groups, drawn with the studio widgets. One renderer for the three places: the properties panel, the board card and the popup. @ample:component-ui
import { fieldFor } from './fields.js';
import { stripUnit } from '../lib/numfield-model.js';
import { curveEditor, fader, knob, led, meter, readout, selector, xyPad } from './studio/index.js';
import { el } from './ui.js';

/** The knob sizes by the word of the layout. */
const KNOB_SIZES = { s: 56, m: 72, l: 92 };

/** The colour names of a layout in the shades of the theme (a text such as "#ff8800" is used as it is). */
const TINTS = { accent: 'var(--acc)', green: 'var(--ok)', yellow: 'var(--warn)', orange: '#f59e4a', red: 'var(--bad)', pink: 'var(--render)', purple: 'var(--data)', blue: 'var(--audio)', teal: '#4fd1c5', gray: 'var(--dim)' };
export const tintColour = (tint) => (tint ? TINTS[tint] || (/^#[0-9a-fA-F]{6}$/.test(tint) ? tint : '') : '');

/** The knob sizes on the card: small, so that the card stays a card. */
const CARD_KNOB_SIZES = { s: 40, m: 48, l: 60 };

/** How tall the items of a card layout are, from the kinds of widget: the card is made that much taller (the board needs it to place and to fit the blocks). */
export function cardHeightOf(nodes) {
  const own = (n) => {
    if (n.kind === 'row') return Math.max(0, ...(n.children || []).map(own));
    if (n.kind === 'column') return (n.children || []).reduce((sum, c) => sum + own(c) + 4, 0);
    if (n.kind === 'group') return 22 + (n.children || []).reduce((sum, c) => sum + own(c) + 4, 0);
    return { knob: CARD_KNOB_SIZES[n.size || 'm'] + 18, slider: 34, toggle: 26, choice: 44, xy: 70, readout: 30, meter: 76, jam: 30 }[n.kind] || 44;
  };
  const items = nodes || [];
  return items.length ? items.reduce((sum, n) => sum + own(n) + 6, 8) : 0;
}

/** A range for a number property that has no MinValue/MaxValue: what the type usually means. */
const TYPE_RANGES = {
  Percent: [0, 100, 1], Probability: [0, 100, 1], Gain: [-60, 12, 0.5], Frequency: [20, 20000, 1], Velocity: [1, 127, 1], Octave: [-4, 4, 1], Int: [0, 100, 1], Float: [0, 100, 0.1], Number: [0, 100, 0.1],
};

/** Whether a layout needs this renderer: any item that is not the plain field of the panel (a layout of only fields keeps the standard rows). */
export function usesLayout(nodes) {
  return (nodes || []).some((n) => n.kind !== 'field');
}

/** The names of the properties a layout shows (the rest stay in the standard rows). */
export function propertiesIn(nodes) {
  const names = new Set();
  for (const n of nodes || []) {
    if (n.property && n.kind !== 'meter') names.add(n.property);
    if (n.second) names.add(n.second);
    propertiesIn(n.children).forEach((x) => names.add(x));
  }
  return names;
}

/** The range, step, unit and scale of a number property: the attributes when there are, else what the type usually means. */
export function rangeFor(meta) {
  const base = TYPE_RANGES[String(meta?.type || '').replace('?', '')] || [0, 100, 1];
  const min = Number.isFinite(meta?.min) ? meta.min : base[0];
  const max = Number.isFinite(meta?.max) ? meta.max : base[1];
  const step = Number.isFinite(meta?.step) ? meta.step : base[2];
  const decimals = step >= 1 ? 0 : Math.min(4, Math.ceil(-Math.log10(step)));
  return { min, max, step, decimals, scale: meta?.type === 'Frequency' && min > 0 ? 'log' : 'lin' };
}

const numberOf = (text, meta, fallback) => {
  const n = Number.parseFloat(stripUnit(text, meta?.unit));
  return Number.isFinite(n) ? n : fallback;
};

/** The text a number is written with in the song (a component writes them tight: -3dB, 35%). */
export const numberText = (value, meta) => `${Number(Number(value).toFixed(4))}${meta?.unit || ''}`;

/**
 * Draws a layout.
 * @param {{ parameters: object[] }} template the template of the component (its parameters carry the kind, range, unit and options)
 * @param {Record<string, string>} values the values of the block (text as written)
 * @param {object[]} nodes the items of the layout
 * @param {'panel' | 'card' | 'popup'} mode where it is drawn
 * @param {object} o
 * @param {boolean} [o.disabled]
 * @param {(name: string, text: string) => Promise<boolean> | void} o.save writes one property (one undo step)
 * @param {(items: { name: string, text: string }[]) => Promise<boolean> | void} [o.saveMany] writes several in one step (an xy pad)
 * @param {(changes: Record<string, string>) => void} [o.preview] told while a widget is dragged, before anything is written (the live preview)
 * @param {(name: string) => HTMLElement | null} [o.jam] the Jam button of a Notes property
 * @param {{ asked: boolean, available: boolean, reason: string, on: () => boolean, toggle: () => void }} [o.live] the Live switch (shown when the layout asks for a live preview)
 * @param {(name: string) => HTMLElement | null} [o.field] the standard row of a property (with its chip and automation), when the host has one
 * @returns {{ element: HTMLElement, used: Set<string>, meters: Map<string, HTMLCanvasElement> }}
 */
export function renderComponentUi(template, values, nodes, mode, o) {
  const meta = (name) => (template.parameters || []).find((p) => p.name === name);
  const meters = new Map();
  const labelOf = (n, name) => n.label || meta(name)?.label || name;
  const tinted = (n, element) => { const colour = tintColour(n.tint); if (colour) element.style.setProperty('--cui-tint', colour); return element; };
  const title = (name) => meta(name)?.documentation || '';

  const widget = (n) => {
    const name = n.property;
    const m = meta(name);
    if (n.kind === 'meter') {
      const canvas = meter({ label: labelOf(n, name), min: -60, max: 0, value: -60, unit: 'dB', decimals: 0, animate: false });
      canvas.dataset.output = name;
      meters.set(name, canvas);
      return el('div', { className: 'cui-item cui-meter', title: `The level of ${name} while the live preview plays` }, [el('span', { className: 'cui-label', textContent: labelOf(n, name) }), canvas]);
    }

    if (!m) return null;
    const value = values[name] ?? m.default ?? '';
    const wrap = (control, extra = '') => el('div', { className: `cui-item cui-${n.kind}${extra}`, title: title(name) }, [control]);
    switch (n.kind) {
      case 'knob': case 'slider': {
        const r = rangeFor(m);
        const options = { label: labelOf(n, name), min: r.min, max: r.max, step: r.step, decimals: r.decimals, scale: r.scale, unit: m.unit || '', value: numberOf(value, m, r.min), defaultValue: numberOf(m.default, m, r.min), disabled: o.disabled, accent: tintColour(n.tint) || undefined, onPreview: (v) => o.preview?.({ [name]: numberText(v, m) }), onCommit: (v) => o.save(name, numberText(v, m)) };
        if (n.kind === 'knob') return wrap(knob({ ...options, sizeName: n.size || 'm', size: (mode === 'card' ? CARD_KNOB_SIZES : KNOB_SIZES)[n.size || 'm'] }), ` size-${n.size || 'm'}`);
        return wrap(fader({ ...options, orientation: 'horizontal' }));
      }
      case 'toggle': {
        const control = led({ label: labelOf(n, name), value: String(value).trim() === 'true', disabled: o.disabled, accent: tintColour(n.tint) || undefined, onCommit: (on) => { o.preview?.({ [name]: String(on) }); return o.save(name, String(on)); } });
        return el('div', { className: 'cui-item cui-toggle', title: title(name) }, [control, el('span', { className: 'cui-label', textContent: labelOf(n, name) })]);
      }
      case 'choice': {
        const quoted = Boolean(m.quoted);
        const current = quoted ? String(value).replace(/^"|"$/g, '') : String(value).trim();
        const choices = (m.options || []).map((x) => ({ value: x, label: x }));
        const control = selector({ label: labelOf(n, name), choices, value: current, disabled: o.disabled, onCommit: (v) => { const text = quoted ? `"${v}"` : v; o.preview?.({ [name]: text }); return o.save(name, text); } });
        return el('div', { className: 'cui-item cui-choice', title: title(name) }, [el('span', { className: 'cui-label', textContent: labelOf(n, name) }), control]);
      }
      case 'xy': {
        const second = meta(n.second);
        if (!second) return null;
        const rx = rangeFor(m), ry = rangeFor(second);
        const control = xyPad({ label: `${labelOf(n, name)} / ${n.second}`, x: numberOf(value, m, rx.min), y: numberOf(values[n.second] ?? second.default, second, ry.min), xMin: rx.min, xMax: rx.max, yMin: ry.min, yMax: ry.max, onPreview: (v) => o.preview?.({ [name]: numberText(v.x, m), [n.second]: numberText(v.y, second) }), onCommit: (v) => { const items = [{ name, text: numberText(v.x, m) }, { name: n.second, text: numberText(v.y, second) }]; return o.saveMany ? o.saveMany(items) : items.reduce((p, i) => p.then(() => o.save(i.name, i.text)), Promise.resolve()); } });
        return el('div', { className: 'cui-item cui-xy', title: `${name} (horizontal) and ${n.second} (vertical)` }, [el('span', { className: 'cui-label', textContent: n.label || `${name} / ${n.second}` }), control]);
      }
      case 'readout': return el('div', { className: 'cui-item cui-readout', title: title(name) }, [el('span', { className: 'cui-label', textContent: labelOf(n, name) }), readout({ label: labelOf(n, name), value: numberOf(value, m, Number.NaN), format: () => String(value) })]);
      case 'jam': return o.jam?.(name) || null;
      case 'curve': {
        // a text property that holds points: x:y pairs from 0 to 1 ("0:0 0.5:1 1:0"), drawn and edited as a curve
        const W = 240, H = 120;
        const parsed = String(value).replace(/^"|"$/g, '').split(/\s+/).filter(Boolean).map((p) => p.split(':').map(Number)).filter((p) => p.length === 2 && p.every(Number.isFinite));
        const start = parsed.length >= 2 ? parsed : [[0, 0], [1, 1]];
        const text = (points) => '"' + points.map((p) => Number((p.x / W).toFixed(3)) + ':' + Number((p.y / H).toFixed(3))).join(' ') + '"';
        const control = curveEditor({ label: labelOf(n, name), width: W, height: H, snap: 1, minGap: 6, points: start.map(([x, y]) => ({ x: x * W, y: y * H })), disabled: o.disabled, onPreview: (points) => o.preview?.({ [name]: text(points) }), onCommit: (points) => o.save(name, text(points)) });
        return el('div', { className: 'cui-item cui-curve', title: title(name) }, [el('span', { className: 'cui-label', textContent: labelOf(n, name) }), control]);
      }
      case 'field': default: {
        // a curve has no editor yet and a field is the standard one: the standard row of the host when it has one, else the field of the panel
        const standard = o.field?.(name);
        if (standard) return standard;
        const field = fieldFor(name, m, String(value), { disabled: o.disabled, title: title(name), tpl: template.id, params: values, save: (text) => o.save(name, text) });
        return field ? el('div', { className: 'cui-item cui-field' }, [el('span', { className: 'cui-label', textContent: labelOf(n, name) }), field]) : null;
      }
    }
  };

  const draw = (items) => items.map((n) => {
    if (n.kind === 'row' || n.kind === 'column') return el('div', { className: `cui-${n.kind}` }, draw(n.children || []));
    if (n.kind === 'group') return tinted(n, el('fieldset', { className: 'cui-group' }, [el('legend', { textContent: n.label || '' }), el('div', { className: 'cui-column' }, draw(n.children || []))]));
    const made = widget(n);
    return made ? tinted(n, made) : null;
  }).filter(Boolean);

  const element = el('div', { className: `cui cui-${mode}`, role: 'group', 'aria-label': 'Controls' }, draw(nodes));
  if (o.live?.asked) {
    // the switch of the live preview: the sound changes while a knob moves (approximate; the build stays the truth)
    const on = o.live.on();
    const button = el('button', { type: 'button', className: `cui-live${on ? ' on' : ''}`, textContent: '● Live', title: o.live.reason, 'aria-pressed': String(on), disabled: !o.live.available && !on });
    button.onclick = (event) => { event.stopPropagation(); o.live.toggle(); };
    element.prepend(button);
  }
  return { element, used: propertiesIn(nodes), meters };
}
