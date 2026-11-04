// The lanes under the AMPLE Jam roll (a switch in the menu, on by default in the track view): where the clip sits in the arrangement (drag a part to move it, its right edge to
// resize it), the sound of the block that plays the notes (as built) and the automation of the blocks after the clip (a click opens the curve editor). @ample:pianoroll-lite
import { el, showMenu } from './ui.js';
import { api } from './api.js';
import { S, edit, flat } from './store.js';
import { peaksOf } from './player.js';
import { sizePianoRollCanvas } from './pianoroll-draw.js';
import { AUTOMATABLE, automationRange, parseAuto } from '../lib/automation-model.mjs';
import { openAutomationEditor } from './automation-editor.js';

const PARTS_H = 34, SOUND_H = 44, ROW_H = 40;

/**
 * ctx: { panel, node, ppq, bar, locked, keyW(), width(), xOf(tick), atOf(x), requestDraw(), announce(text) }.
 * Returns { element, visible(), setVisible(on), redraw(), layout() }. Nothing here changes the notes; the parts and the automation are written through the same edits as the rest of the studio.
 */
export function createJamLanes(ctx) {
  const { panel, node, ppq, bar, locked } = ctx;
  const parts = el('canvas', { className: 'mls-lane', 'aria-label': 'The parts of this clip in the arrangement. Drag a part to move it, drag its right edge to resize it.' });
  const sound = el('canvas', { className: 'mls-lane', 'aria-label': 'The sound of the block that plays these notes, as built' });
  const automation = el('canvas', { className: 'mls-lane', 'aria-label': 'Automation of the blocks after this clip. Click a lane to edit it.' });
  const element = el('div', { className: 'mls-lanes', hidden: true }, [parts, sound, automation]);
  let on = false, lanes = [], partDrag = null, soundPeaks = null, soundFor = '';
  const css = () => { const style = getComputedStyle(panel), get = (name, fallback) => style.getPropertyValue(name).trim() || fallback; return { dim: get('--dim', '#8a94a3'), acc: get('--acc', '#4aa3ff'), line: get('--line', '#333b46'), panel: get('--panel2', '#222831'), text: get('--text', '#e6e9ee') }; };

  const blocksAfter = () => {
    const links = flat().links, nodes = flat().nodes, seen = new Set([node.id]), order = [];
    for (let queue = [node.id]; queue.length;) {
      const from = queue.shift();
      for (const l of links.filter((x) => x.from === from && !seen.has(x.to))) { seen.add(l.to); const n = nodes.find((x) => x.id === l.to); if (n) { order.push(n); queue.push(n.id); } }
    }
    return order;
  };
  const setup = () => {
    lanes = [];
    // the clip's own pitch bend comes first (a pitch wheel over the clip), then the automation of the blocks after it
    const self = liveNode();
    if (self.tpl === 'midi-clip') lanes.push({ node: self, name: 'bend', range: automationRange('midi-clip', 'bend', self.params), bend: true });
    for (const n of blocksAfter()) for (const [name, range] of Object.entries(AUTOMATABLE[n.tpl] || {})) lanes.push({ node: n, name, range });
  };

  const liveNode = () => flat().nodes.find((n) => n.id === node.id) ?? node;
  const partsOf = () => { const live = liveNode(); return live.parts?.length ? live.parts : [{ at: 1, length: Number(live.params.bars) > 0 ? Number(live.params.bars) : 1, name: '' }]; };
  const shown = () => (partDrag ? partDrag.parts : partsOf());
  const barPx = (bars) => ((bars * bar) / ppq) * (ctx.xOf(ppq) - ctx.xOf(0));
  const localX = (clientX) => { const r = parts.getBoundingClientRect(); return ((clientX - r.left) / r.width) * parts.clientWidth; };
  const barAt = (clientX) => Math.floor(ctx.atOf(localX(clientX)) / bar) + 1;
  const partAt = (clientX, list) => {
    const x = localX(clientX);
    for (let i = 0; i < list.length; i++) { const x0 = ctx.xOf((list[i].at - 1) * bar), x1 = x0 + barPx(list[i].length); if (x >= x0 && x <= x1) return { index: i, edge: x >= x1 - 8 }; }
    return null;
  };
  parts.style.cursor = locked ? 'default' : 'grab';
  parts.onpointerdown = (e) => {
    if (locked || !liveNode().parts?.length) return;
    const list = partsOf().map((p) => ({ ...p })), hit = partAt(e.clientX, list);
    if (!hit) return;
    parts.setPointerCapture(e.pointerId);
    partDrag = { ...hit, parts: list, origin: list.map((p) => ({ ...p })), startBar: barAt(e.clientX) };
  };
  parts.onpointermove = (e) => {
    if (!partDrag) { if (!locked) { const h = partAt(e.clientX, partsOf()); parts.style.cursor = h ? (h.edge ? 'ew-resize' : 'grab') : 'default'; } return; }
    const delta = barAt(e.clientX) - partDrag.startBar, o = partDrag.origin[partDrag.index], q = partDrag.parts[partDrag.index];
    if (partDrag.edge) q.length = Math.max(1, o.length + delta); else q.at = Math.max(1, o.at + delta);
    ctx.requestDraw();
  };
  parts.onpointerup = async () => {
    if (!partDrag) return;
    const done = partDrag; partDrag = null;
    if (done.parts.some((p, i) => p.at !== done.origin[i].at || p.length !== done.origin[i].length)) {
      await edit([{ type: 'setParts', block: node.id, parts: done.parts.map((p) => ({ at: p.at, length: p.length, name: p.name || '' })) }]);
      ctx.announce(`Part now at bar ${done.parts[done.index].at} for ${done.parts[done.index].length} bars`);
    }
    ctx.requestDraw();
  };
  parts.onpointercancel = () => { partDrag = null; ctx.requestDraw(); };

  const soundBlock = () => blocksAfter().find((n) => S.build?.takes?.[n.id]);
  async function loadSound() {
    const target = soundBlock(), take = target && S.build.takes[target.id];
    if (!take) { soundPeaks = null; soundFor = ''; return; }
    if (soundFor === take) return;
    soundFor = take;
    try {
      const [p, record] = await Promise.all([peaksOf(take, 4000), api.get(`/api/takes/${encodeURIComponent(take)}`)]);
      if (soundFor === take) { soundPeaks = { ...p, firstBar: Number(record.plan?.settings?.firstBar) || 1, title: target.title }; sound.title = `Sound of ${target.title}`; ctx.requestDraw(); }
    } catch { soundPeaks = null; }
  }

  automation.style.cursor = 'pointer';
  // right-click on the pitch bend lane: how far the curve reaches (the range of the pitch wheel)
  automation.oncontextmenu = (e) => {
    const r = automation.getBoundingClientRect(), y = ((e.clientY - r.top) / r.height) * automation.clientHeight, lane = lanes[Math.floor((y - 3) / ROW_H)];
    if (!lane?.bend || locked) return;
    e.preventDefault();
    const current = Number.parseInt(String(liveNode().params.bend_range ?? '').replace(/"/g, ''), 10) || 2;
    showMenu(e.clientX, e.clientY, [{ head: 'Pitch bend range' }, ...[1, 2, 5, 7, 12, 24].map((st) => ({ label: `${st === current ? '● ' : ''}±${st} semitone${st === 1 ? '' : 's'}${st === 2 ? ' (the usual pitch wheel)' : st === 12 ? ' (an octave)' : ''}`, fn: async () => { await edit([{ type: 'setParameter', block: node.id, name: 'bend_range', value: String(st) }]); setup(); ctx.requestDraw(); } }))]);
  };
  automation.onclick = async (e) => {
    if (locked) return;
    const r = automation.getBoundingClientRect(), y = ((e.clientY - r.top) / r.height) * automation.clientHeight, lane = lanes[Math.floor((y - 3) / ROW_H)];
    if (!lane) return;
    const bars = Math.max(0, ...flat().nodes.flatMap((n) => (n.parts || []).map((p) => p.at + p.length)));
    const result = await openAutomationEditor({ type: lane.node.tpl, name: lane.name, title: `${lane.node.title} ${lane.name}`, text: lane.node.params[lane.name] ?? '', bars, tempo: S.doc?.project?.tempo, block: lane.node.id });
    if (result === undefined) return;
    await edit([result === '' ? { type: 'removeParameter', block: lane.node.id, name: lane.name } : { type: 'setParameter', block: lane.node.id, name: lane.name, value: result }]);
    setup(); layout(); ctx.requestDraw();
  };

  const ready = (cv) => { const g = cv.getContext('2d'), k = window.devicePixelRatio || 1; g.setTransform(k, 0, 0, k, 0, 0); g.clearRect(0, 0, cv.clientWidth, cv.clientHeight); return g; };
  function layout() {
    if (!on) return;
    const w = Math.max(200, (element.clientWidth || 0) > 0 ? element.clientWidth - ctx.keyW() : ctx.width()), ratio = window.devicePixelRatio || 1;
    sizePianoRollCanvas(parts, w, PARTS_H, ratio); sizePianoRollCanvas(sound, w, SOUND_H, ratio); sizePianoRollCanvas(automation, w, Math.max(30, lanes.length * ROW_H + 6), ratio);
    for (const cv of [parts, sound, automation]) cv.style.marginLeft = `${ctx.keyW()}px`;
  }
  function redraw() {
    if (!on) return;
    const c = css(), width = (cv) => cv.clientWidth;
    let g = ready(parts);
    g.fillStyle = c.panel; g.fillRect(0, 0, width(parts), PARTS_H); g.fillStyle = c.dim; g.font = '10px sans-serif'; g.fillText('arrangement', 4, 12);
    for (const p of shown()) {
      const x = ctx.xOf((p.at - 1) * bar), w = barPx(p.length);
      g.fillStyle = c.acc; g.globalAlpha = 0.75; g.fillRect(x, 14, Math.max(2, w - 2), 16); g.globalAlpha = 1;
      g.fillStyle = '#fff'; g.fillText(p.name || node.title, x + 5, 26); g.fillRect(x + Math.max(2, w - 2) - 4, 17, 2, 10);
    }
    g = ready(sound); g.fillStyle = c.panel; g.fillRect(0, 0, width(sound), SOUND_H); g.fillStyle = c.dim; g.font = '10px sans-serif';
    if (!soundPeaks) { g.fillText('Build the song to see the sound of the block after this clip next to its notes.', 8, 24); loadSound(); }
    else {
      g.fillText(soundPeaks.title, 4, 12);
      const bpm = Number(S.doc?.project?.tempo) || 120, n = soundPeaks.peaks.length / 2, mid = SOUND_H / 2 + 4, x0 = ctx.xOf((soundPeaks.firstBar - 1) * bar), pxPerSec = (bpm / 60) * (ctx.xOf(ppq) - ctx.xOf(0));
      g.fillStyle = c.acc;
      for (let x = Math.max(0, Math.floor(x0)); x < width(sound); x++) {
        const i = Math.floor(((x - x0) / pxPerSec / soundPeaks.seconds) * n);
        if (i < 0) continue;
        if (i >= n) break;
        g.fillRect(x, mid - soundPeaks.peaks[i * 2 + 1] * (mid - 14), 1, Math.max(1, (soundPeaks.peaks[i * 2 + 1] - soundPeaks.peaks[i * 2]) * (mid - 14)));
      }
    }
    g = ready(automation);
    if (!lanes.length) { g.fillStyle = c.dim; g.font = '11px sans-serif'; g.fillText('No block after this clip has a parameter that can be automated (volume, pan, filter, compressor, delay, reverb...).', 8, 20); return; }
    lanes.forEach((lane, i) => {
      const y0 = 3 + i * ROW_H, h = 34, range = lane.range;
      g.fillStyle = c.panel; g.fillRect(0, y0, width(automation), h);
      const bendRange = Number.parseInt(String(lane.node.params.bend_range ?? '').replace(/"/g, ''), 10) || 2;
      g.fillStyle = c.text; g.font = '10px sans-serif'; g.fillText(lane.bend ? 'pitch bend' : lane.node.title, 4, y0 + 13); g.fillStyle = c.dim; g.fillText(lane.bend ? `±${bendRange} st · right-click: range` : lane.name, 4, y0 + 26);
      if (lane.bend) { g.fillStyle = c.line; g.fillRect(0, y0 + h / 2, width(automation), 1); }
      const value = lane.node.params[lane.name] ?? '', points = parseAuto(value);
      const norm = (v) => (range.log ? Math.log(Math.max(range.min, v) / range.min) / Math.log(range.max / range.min) : (v - range.min) / (range.max - range.min));
      const yOfValue = (v) => y0 + h - 4 - Math.max(0, Math.min(1, norm(v))) * (h - 8);
      g.strokeStyle = c.acc; g.lineWidth = 2; g.beginPath();
      if (points) { points.forEach((p, k) => { const x = ctx.xOf((p.bar - 1) * bar), y = yOfValue(p.value); if (k === 0) g.moveTo(0, y); g.lineTo(x, y); }); g.lineTo(width(automation), yOfValue(points[points.length - 1].value)); }
      else {
        const flatValue = Number.parseFloat(String(value).replace(/[a-z%\s]+$/i, '')), y = yOfValue(Number.isFinite(flatValue) ? flatValue : range.min < 0 ? 0 : range.min);
        g.moveTo(0, y); g.lineTo(width(automation), y);
        if (/^\s*(lfo|env)\(/.test(value)) { g.fillStyle = c.dim; g.fillText(value, 60, y0 + 12); }
      }
      g.stroke(); g.lineWidth = 1; g.fillStyle = c.line; g.fillRect(0, y0 + h, width(automation), 1);
    });
  }
  return {
    element,
    visible: () => on,
    setVisible(value) { on = value; element.hidden = !value; if (value) { setup(); loadSound(); layout(); } ctx.requestDraw(); },
    refresh() { if (on) { setup(); layout(); loadSound(); } },
    layout, redraw,
  };
}
