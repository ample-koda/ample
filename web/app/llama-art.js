// The Delay Llama: a cartoon llama that sings along with the voice. Its own character (not Koda): a woolly cream llama with long ears, buck teeth, headphones and a saddle blanket in the neon of AMPLE, on a stage light.
// Two poses: 'sit' (the holy llama, cross-legged with its hooves folded in prayer and prayer beads, the default) and 'stand' (on four legs with a saddle blanket).
// It reads a small state and nothing else: { pitch (MIDI note, or null when silent), vowel (0 a .. 1 u), mouth (0 closed .. 1 open), level (0..1), vibrato (0..1) }.
// The neck stretches with the pitch, the mouth takes the shape of the vowel and opens with mouth and level, the ears wobble with the vibrato, the eyes close a little on a
// note... see below. Purely a picture: the synth works the same without it. @ample:delay-llama

const NS = 'http://www.w3.org/2000/svg';
const node = (tag, attrs = {}, parent = null) => { const n = document.createElementNS(NS, tag); for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, String(v)); parent?.append(n); return n; };
const lerp = (a, b, t) => a + (b - a) * t;
// An attribute is only written when its text changed: a write of the same text still costs a style recalculation of the whole picture.
const put = (el, name, value) => { const text = String(value); const key = '_' + name; if (el[key] !== text) { el[key] = text; el.setAttribute(name, text); } };
// the mouth per vowel point [a, e, i, o, u]: width and height when fully open
const MOUTH_W = [17, 16, 19, 11, 8], MOUTH_H = [15, 10, 5, 14, 10];
const along = (table, vowel) => { const p = Math.max(0, Math.min(1, vowel)) * 4, i = Math.min(3, Math.floor(p)); return lerp(table[i], table[i + 1], p - i); };

/** The soft neon light round a llama of this scheme, as a CSS background for the box it stands in (empty for a scheme without a glow). */
export function auraOf(scheme) {
  const C = SCHEMES[scheme] || SCHEMES.trans;
  return C.glow ? 'radial-gradient(closest-side, ' + C.glow + '66 0%, ' + (C.glow2 || C.glow) + '33 55%, transparent 78%)' : '';
}

/** A singing llama. Returns { element, set(state), destroy() }. */
/** The colour schemes of the llama: the wool, the ink line, the face and the things it wears. */
export const SCHEMES = {
  // the first one: a cream llama with a brown line, orange beads, AMPLE neon headphones
  cream: { woolLight: '#fffaf0', wool: '#ead8bd', face: '#fff4e2', snout: '#f6e2c6', tan: '#dcc6a6', line: '#6b4f3a', hoof: '#4a3426', hoof2: '#5a4232', bead: '#e8823a', bead2: '#c9662a', cheek: '#f7a1a8', tummy: '#fff6e8', phones: '#1d2430', flower: '#f08a2c', pupil: '#2b2420', neonA: '#2de2ff', neonB: '#ff4fd8' },
  // family of Koda: the dark ink line of Koda, white wool, cyan as the accent
  koda: { woolLight: '#ffffff', wool: '#e6eef5', face: '#ffffff', snout: '#eef4f8', tan: '#d5e2ec', line: '#0b0f14', hoof: '#1b2530', hoof2: '#26323f', bead: '#2de2ff', bead2: '#9ff7ff', cheek: '#ff9ec7', tummy: '#f6fbff', phones: '#0b0f14', flower: '#ffd166', pupil: '#0b0f14', neonA: '#9ff7ff', neonB: '#2de2ff' },
  // saffron: a warm nod to the Delay Lama, golden wool, maroon and saffron
  saffron: { woolLight: '#fff1cf', wool: '#f2c879', face: '#fff3d6', snout: '#f7dca4', tan: '#e5b562', line: '#6e2a1c', hoof: '#5a2318', hoof2: '#6e2a1c', bead: '#8b2635', bead2: '#b33a3a', cheek: '#f39a7a', tummy: '#fff6de', phones: '#3a1410', flower: '#ff7a2f', pupil: '#2a120c', neonA: '#ffb347', neonB: '#ff4f6d' },
  // neon night: a synthwave llama, lavender wool with a glowing line
  neon: { glow: '#ff4fd8', woolLight: '#b9a6ff', wool: '#7a63d9', face: '#cbbcff', snout: '#a995f2', tan: '#6a55c4', line: '#14102b', hoof: '#14102b', hoof2: '#221a44', bead: '#2de2ff', bead2: '#ff4fd8', cheek: '#ff6fd8', tummy: '#d8ccff', phones: '#0b0f14', flower: '#2de2ff', pupil: '#14102b', neonA: '#2de2ff', neonB: '#ff4fd8' },
  // mocha: a brown llama, the colour of a real one, with a cream face
  // lime: the neon green of AMPLE (its lit keys) on a cream llama, with a green glow round it
  lime: { woolLight: '#fbfff2', wool: '#e3eccf', face: '#fdfff6', snout: '#eef5dc', tan: '#d3dfba', line: '#24331a', hoof: '#1f2a16', hoof2: '#2b3a1f', bead: '#9cf04f', bead2: '#2de2ff', cheek: '#f7a1a8', tummy: '#fbfff4', phones: '#0b0f14', flower: '#9cf04f', pupil: '#16200f', neonA: '#9cf04f', neonB: '#2de2ff', glow: '#9cf04f' },
  // trans: the colours of the transgender flag in neon: a light blue llama with a pink face and white snout and tummy, a deep navy line, and a pink glow inside a blue one
  trans: { woolLight: '#c4f0ff', wool: '#5bcefa', face: '#f5a9b8', snout: '#ffffff', tan: '#8fdcf9', line: '#1a2a4a', hoof: '#1a2a4a', hoof2: '#243a63', bead: '#f5a9b8', bead2: '#5bcefa', cheek: '#ff7fa8', tummy: '#ffffff', phones: '#0b0f14', flower: '#f5a9b8', pupil: '#1a2a4a', neonA: '#f5a9b8', neonB: '#5bcefa', glow: '#ff8fb5', glow2: '#5bcefa' },
  mocha: { woolLight: '#c99f78', wool: '#9c6f4b', face: '#f3dfc6', snout: '#e9cfae', tan: '#8a5f3e', line: '#3b2416', hoof: '#2a1a10', hoof2: '#3b2416', bead: '#2de2ff', bead2: '#ff4fd8', cheek: '#f29a8a', tummy: '#e7c9a4', phones: '#1d2430', flower: '#ffd166', pupil: '#1c120b', neonA: '#2de2ff', neonB: '#ff4fd8' },
};

const HEAD_DROP = 14;

export function createLlama({ size = 220, title = 'Delay Llama', pose = 'sit', scheme = 'trans' } = {}) {
  const C = SCHEMES[scheme] || SCHEMES.trans;
  const sitting = pose !== 'stand', neckBase = sitting ? 170 : 186;
  const svg = node('svg', { viewBox: '0 -56 200 316', width: size, height: Math.round(size * 1.58), class: 'delay-llama-art', role: 'img', 'aria-label': `${title}, singing` });
  const defs = node('defs', {}, svg);
  const wool = node('radialGradient', { id: `llama-wool-${Math.random().toString(36).slice(2, 7)}`, cx: '45%', cy: '35%', r: '70%' }, defs);
  node('stop', { offset: '0%', 'stop-color': C.woolLight }, wool); node('stop', { offset: '100%', 'stop-color': C.wool }, wool);
  const woolFill = `url(#${wool.id})`, line = C.line;
  const roundNeck = node('linearGradient', { id: `llama-neck-${wool.id.slice(-5)}`, x1: 0, x2: 1, y1: 0, y2: 0 }, defs);
  node('stop', { offset: '0%', 'stop-color': C.wool }, roundNeck); node('stop', { offset: '45%', 'stop-color': C.woolLight }, roundNeck); node('stop', { offset: '100%', 'stop-color': C.wool }, roundNeck);
  const neon = node('linearGradient', { id: `llama-neon-${wool.id.slice(-5)}`, x1: 0, x2: 1, y1: 0, y2: 1 }, defs);
  node('stop', { offset: '0%', 'stop-color': C.neonA }, neon); node('stop', { offset: '100%', 'stop-color': C.neonB }, neon);
  const glow = node('radialGradient', { id: `llama-glow-${wool.id.slice(-5)}` }, defs);
  node('stop', { offset: '0%', 'stop-color': C.neonA, 'stop-opacity': 0.55 }, glow); node('stop', { offset: '60%', 'stop-color': C.neonB, 'stop-opacity': 0.18 }, glow); node('stop', { offset: '100%', 'stop-color': C.neonB, 'stop-opacity': 0 }, glow);
  const neonFill = `url(#${neon.id})`;
  // a scheme with a glow gets a soft neon light round the llama: see auraOf, a still picture behind it (a filter on a picture that moves is repainted with every frame)

  // the body: a llama standing seen from the side and a little from the front: a fluffy woolly body on four slim legs with hooves, a fluffy tail,
  // the long neck rising from the front of the body (the head looks at you)
  const body = node('g', {}, svg);
  const stage = node('ellipse', { cx: 124, cy: 254, rx: 86, ry: 12, fill: `url(#${glow.id})` }, body);   // a stage light in the colours of AMPLE under its hooves
  if (!sitting) {
  const legs = [[86, 0], [104, 1], [150, 0], [168, 1]];
  for (const [x, back] of legs) {
    node('path', { d: `M${x - 6} 206 L${x - 5} 246 Q${x} 250 ${x + 5} 246 L${x + 6} 206 Z`, fill: back ? C.tan : C.wool, stroke: line, 'stroke-width': 2, 'stroke-linejoin': 'round' }, body);
    node('path', { d: `M${x - 6} 246 Q${x} 254 ${x + 6} 246 L${x + 6} 250 Q${x} 256 ${x - 6} 250 Z`, fill: C.hoof, stroke: line, 'stroke-width': 1.5 }, body);   // a hoof
  }
  node('ellipse', { cx: 128, cy: 192, rx: 62, ry: 30, fill: woolFill, stroke: line, 'stroke-width': 2 }, body);
  // the fleece: soft curls along the back and the belly
  for (const [x, y, r] of [[80, 176, 13], [98, 168, 14], [118, 165, 15], [138, 165, 15], [158, 168, 14], [176, 178, 12], [96, 214, 11], [118, 218, 11], [140, 218, 11], [162, 212, 10]]) node('circle', { cx: x, cy: y, r, fill: woolFill, stroke: line, 'stroke-width': 2 }, body);
  node('ellipse', { cx: 128, cy: 194, rx: 56, ry: 24, fill: woolFill }, body);   // fills the inside of the curls
  for (const [x, y, r] of [[190, 182, 9], [196, 174, 8], [194, 190, 7]]) node('circle', { cx: x, cy: y, r, fill: woolFill, stroke: line, 'stroke-width': 2 }, body);   // the tail
  // a little saddle blanket in the colours of AMPLE, so it is the AMPLE llama
  node('path', { d: 'M112 168 Q132 160 152 168 L156 196 Q132 204 108 196 Z', fill: neonFill, stroke: line, 'stroke-width': 2, 'stroke-linejoin': 'round' }, body);
  node('path', { d: 'M110 186 Q132 194 154 186', fill: 'none', stroke: '#fff', 'stroke-width': 2.5, opacity: 0.8 }, body);
  for (const x of [114, 126, 138, 150]) node('path', { d: `M${x} 198 l3 7 l3 -7`, fill: 'none', stroke: 'var(--studio, #ff4fd8)', 'stroke-width': 2, 'stroke-linecap': 'round' }, body);   // the tassels
  } else {
    // the holy llama: sitting cross-legged, upright, the front hooves resting on the knees in a mudra, at peace while it sings
    const tan = C.tan;
    node('path', { d: 'M150 232 C176 236 186 222 196 214 C200 226 188 246 160 246 Z', fill: woolFill, stroke: line, 'stroke-width': 2 }, body);   // the tail, curled round
    node('ellipse', { cx: 100, cy: 236, rx: 74, ry: 17, fill: woolFill, stroke: line, 'stroke-width': 2 }, body);   // the crossed hind legs
    node('path', { d: 'M38 236 C60 222 90 226 104 242', fill: 'none', stroke: line, 'stroke-width': 2, 'stroke-linecap': 'round' }, body);
    node('path', { d: 'M162 236 C140 222 110 226 96 242', fill: 'none', stroke: line, 'stroke-width': 2, 'stroke-linecap': 'round' }, body);
    for (const [x, flip] of [[30, 1], [170, -1]]) node('path', { d: `M${x} 230 q${-10 * flip} 4 ${-8 * flip} 12 q${8 * flip} 4 ${14 * flip} -2 Z`, fill: C.hoof, stroke: line, 'stroke-width': 1.5 }, body);   // the feet sticking out
    node('ellipse', { cx: 100, cy: 196, rx: 40, ry: 42, fill: woolFill, stroke: line, 'stroke-width': 2 }, body);   // the upright body
    for (const [x, y, r] of [[66, 176, 10], [134, 176, 10], [64, 200, 9], [136, 200, 9]]) node('circle', { cx: x, cy: y, r, fill: woolFill, stroke: line, 'stroke-width': 2 }, body);   // fleece at the sides
    node('ellipse', { cx: 100, cy: 200, rx: 34, ry: 36, fill: woolFill }, body);
    node('ellipse', { cx: 100, cy: 206, rx: 20, ry: 24, fill: C.tummy, opacity: 0.85 }, body);   // the light tummy
  }
  // the arms of the holy llama: folded in prayer, the front hooves together in front of the chest (drawn later, over the beads)
  const prayer = () => {
    if (!sitting) return null;
    const tan = C.tan, arms = node('g', {}, svg);
    for (const [x0, elbow] of [[70, 62], [130, 138]]) {
      const d = `M${x0} 178 C${elbow} 204 ${elbow + (x0 < 100 ? 20 : -20)} 218 ${x0 < 100 ? 97 : 103} 206`;
      node('path', { d, fill: 'none', stroke: line, 'stroke-width': 15, 'stroke-linecap': 'round' }, arms);
      node('path', { d, fill: 'none', stroke: tan, 'stroke-width': 11, 'stroke-linecap': 'round' }, arms);
    }
    // the hooves pressed together, pointing up
    node('path', { d: 'M100 208 C92 206 92 190 96 182 C98 179 100 180 100 183 Z', fill: C.hoof, stroke: line, 'stroke-width': 1.5, 'stroke-linejoin': 'round' }, arms);
    node('path', { d: 'M100 208 C108 206 108 190 104 182 C102 179 100 180 100 183 Z', fill: C.hoof2, stroke: line, 'stroke-width': 1.5, 'stroke-linejoin': 'round' }, arms);
    return arms;
  };

  // the neck and the head move together: the head rises with the pitch
  const neck = node('path', { fill: `url(#${roundNeck.id})`, stroke: line, 'stroke-width': 2, 'stroke-linejoin': 'round' }, svg);
  const head = node('g', {}, svg);
  const earLeft = node('ellipse', { cx: 0, cy: 0, rx: 7, ry: 20, fill: C.wool, stroke: line, 'stroke-width': 2 }, head);
  const earRight = node('ellipse', { cx: 0, cy: 0, rx: 7, ry: 20, fill: C.wool, stroke: line, 'stroke-width': 2 }, head);
  for (const [x, y, r] of [[86, 30, 11], [100, 25, 13], [114, 30, 11], [93, 38, 10], [107, 38, 10]]) node('circle', { cx: x, cy: y, r, fill: woolFill, stroke: line, 'stroke-width': 2 }, head);   // the tuft
  node('ellipse', { cx: 100, cy: 62, rx: 30, ry: 30, fill: C.face, stroke: line, 'stroke-width': 2 }, head);   // the face
  const fringe = node('g', {}, head);
  node('ellipse', { cx: 100, cy: 84, rx: 22, ry: 17, fill: C.snout, stroke: line, 'stroke-width': 2 }, head);   // the snout
  node('ellipse', { cx: 76, cy: 73, rx: 7.5, ry: 5, fill: C.cheek, opacity: 0.75 }, head);                         // rosy cheeks
  node('ellipse', { cx: 124, cy: 73, rx: 7.5, ry: 5, fill: C.cheek, opacity: 0.75 }, head);
  const eyes = [86, 114].map((x) => {
    const open = node('g', {}, head);
    node('ellipse', { cx: x, cy: 57, rx: 7.5, ry: 8, fill: '#fff', stroke: line, 'stroke-width': 1.8 }, open);
    const pupil = node('circle', { cx: x, cy: 59, r: 4.4, fill: C.pupil }, open);
    node('circle', { cx: x + 1.6, cy: 57.4, r: 1.5, fill: '#fff' }, open);
    // a soft, heavy upper lid: always a little down (calm), all the way for a slow blink
    const lid = node('path', { fill: C.face, stroke: line, 'stroke-width': 1.8, 'stroke-linejoin': 'round' }, open);
    // the closed eye of bliss: a gentle downward curve with a lash
    const closed = node('path', { d: `M${x - 7} 57 Q${x} 63 ${x + 7} 57`, fill: 'none', stroke: line, 'stroke-width': 2.2, 'stroke-linecap': 'round' }, head);
    return { pupil, lid, open, closed, x };
  });
  // a little content smile for when it is quiet
  const smile = node('path', { d: 'M92 90 Q100 96 108 90', fill: 'none', stroke: line, 'stroke-width': 2, 'stroke-linecap': 'round' }, head);
  node('path', { d: 'M94 78 q3 2 6 0 q3 2 6 0', fill: 'none', stroke: line, 'stroke-width': 1.8, 'stroke-linecap': 'round' }, head);   // the nose
  if (sitting) {
    for (const [x, d] of [[78, 5], [88, 7], [98, 6], [108, 8], [118, 5]]) node('path', { d: `M${x} 34 q4 ${d} 1 ${d + 6} q6 -4 7 -${d + 4} Z`, fill: woolFill, stroke: line, 'stroke-width': 1.6, 'stroke-linejoin': 'round' }, fringe);
    for (const [x, y] of [[80, 30], [121, 34]]) {
      for (let k = 0; k < 5; k++) { const a = (k / 5) * 2 * Math.PI; node('circle', { cx: (x + Math.cos(a) * 3.6).toFixed(1), cy: (y + Math.sin(a) * 3.6).toFixed(1), r: 2.8, fill: C.flower, stroke: line, 'stroke-width': 0.8 }, head); }
      node('circle', { cx: x, cy: y, r: 2, fill: '#ffd34d' }, head);
    }
  }
  // headphones: it is a musician
  node('path', { d: 'M70 58 C68 14 132 14 130 58', fill: 'none', stroke: C.phones, 'stroke-width': 6, 'stroke-linecap': 'round' }, head);
  node('path', { d: 'M70 58 C68 14 132 14 130 58', fill: 'none', stroke: neonFill, 'stroke-width': 2.5, 'stroke-linecap': 'round' }, head);
  for (const x of [64, 126]) node('rect', { x, y: 48, width: 11, height: 20, rx: 5, fill: neonFill, stroke: C.phones, 'stroke-width': 2 }, head);
  const mouth = node('ellipse', { cx: 100, cy: 91, rx: 10, ry: 2, fill: '#5a2b2b', stroke: line, 'stroke-width': 2 }, head);
  const tongue = node('ellipse', { cx: 100, cy: 95, rx: 6, ry: 2, fill: '#e77f86' }, head);
  const teeth = node('g', {}, head);
  node('rect', { x: 95.5, y: 86, width: 4, height: 6, rx: 1, fill: '#fff', stroke: line, 'stroke-width': 1 }, teeth);
  node('rect', { x: 100.5, y: 86, width: 4, height: 6, rx: 1, fill: '#fff', stroke: line, 'stroke-width': 1 }, teeth);

  // a woolly ruff where the neck meets the body (drawn over the neck), the head in front of it
  for (const [x, y, r] of [[82, neckBase - 4, 12], [100, neckBase, 13], [118, neckBase - 4, 12]]) node('circle', { cx: x, cy: y, r, fill: woolFill, stroke: line, 'stroke-width': 2 }, svg);
  if (sitting) {
    // the prayer beads: warm orange beads round the neck and a pendant in the neon of AMPLE
    for (let k = 0; k <= 14; k++) {
      const a = Math.PI * (k / 14);
      node('circle', { cx: (100 - Math.cos(a) * 26).toFixed(1), cy: (neckBase - 2 + Math.sin(a) * 22).toFixed(1), r: 3.4, fill: k % 2 ? C.bead : C.bead2, stroke: line, 'stroke-width': 1 }, svg);
    }
    node('path', { d: `M100 ${neckBase + 22} l7 10 l-7 10 l-7 -10 Z`, fill: neonFill, stroke: line, 'stroke-width': 1.5 }, svg);
    node('path', { d: `M100 ${neckBase + 42} l-3 8 h6 Z`, fill: '#ff4fd8', stroke: line, 'stroke-width': 1 }, svg);
  }
  const arms = prayer();
  svg.append(head);

  // little notes that float up while it sings
  const notes = node('g', { 'font-size': 18, 'font-family': 'system-ui, sans-serif', fill: neonFill }, svg);
  const floating = [];

  // the praying hands follow the head on a soft spring: a little behind, a little past and back to rest (never a straight line)
  const hands = { y: 0, vy: 0, r: 0, vr: 0, last: 0 };
  let state = { pitch: null, vowel: 0.5, mouth: 0.6, level: 0, vibrato: 0.3 }, frame = 0, start = performance.now(), lastNote = 0, blinkAt = 4200, lastDraw = 0, forceDraw = false;
  const draw = (now) => {
    // 20 pictures a second on a phone and 30 elsewhere, a third of that while it is quiet: it sways slowly, it does not need more
    const wait = (state.pitch !== null && state.level > 0.02 ? 1 : 2.2) * (innerWidth <= 700 ? 50 : 33);
    if (!forceDraw && lastDraw && now - lastDraw < wait - 2) { frame = requestAnimationFrame(draw); return; }
    lastDraw = now; forceDraw = false;
    const t = (now - start) / 1000, singing = state.pitch !== null && state.level > 0.02;
    put(stage, 'opacity', (singing ? 0.55 + state.level * 0.45 : 0.4).toFixed(2));
    // the pitch from about C3 to C6 stretches the neck by up to 46 px
    const stretch = singing ? Math.max(0, Math.min(1, (state.pitch - 48) / 36)) : 0.15;
    const lift = -stretch * 46 + Math.sin(t * 2 * Math.PI * 0.22) * 1.6;
    const tilt = Math.sin(t * 2 * Math.PI * 0.12) * (singing ? 4 : 5);   // a slow, dreamy sway of the head
    // the head sits HEAD_DROP lower than its own place, over the top of the neck, so that the neck goes up into it
    put(head, 'transform', `translate(0 ${(lift + HEAD_DROP).toFixed(2)}) rotate(${tilt.toFixed(2)} 100 84)`);
    const breath = 1 + Math.sin(t * 2 * Math.PI * 0.2) * 0.012;   // it breathes, slowly
    put(body, 'transform', `translate(100 ${sitting ? 236 : 250}) scale(${breath.toFixed(4)}) translate(-100 -${sitting ? 236 : 250})`);
    const top = 96 + lift;   // the neck starts behind the snout
    if (arms) {
      const dt = Math.min(0.05, hands.last ? (now - hands.last) / 1000 : 0.016);
      hands.last = now;
      // the target: up a little with the pitch (a sixth of what the head does), a little tilt with the sway, a small lift on a loud note
      const wantY = lift * 0.16 - (singing ? state.level * 2.5 : 0), wantR = tilt * 0.45;
      // a damped spring (stiffness 38, damping 7): it lags, overshoots a touch and settles
      hands.vy += ((wantY - hands.y) * 38 - hands.vy * 7) * dt; hands.y += hands.vy * dt;
      hands.vr += ((wantR - hands.r) * 38 - hands.vr * 7) * dt; hands.r += hands.vr * dt;
      put(arms, 'transform', 'translate(0 ' + hands.y.toFixed(2) + ') rotate(' + hands.r.toFixed(2) + ' 100 196)');
    }
    // a little wider than before and gently curved: narrow under the head, wider where it grows out of the shoulders
    put(neck, "d", `M83 ${top} C80 ${top + 30} 78 ${neckBase - 24} 75 ${neckBase} L125 ${neckBase} C122 ${neckBase - 24} 120 ${top + 30} 117 ${top} Z`);
    // the ears wobble with the vibrato
    const wobble = (singing ? 3 + state.vibrato * 7 : 2.5) * Math.sin(t * 2 * Math.PI * (singing ? 5.5 * 0.6 : 0.3));   // the ears float, they never flap
    put(earLeft, 'transform', `translate(74 32) rotate(${-28 + wobble})`);
    put(earRight, 'transform', `translate(126 32) rotate(${28 - wobble})`);
    // the mouth: the vowel gives the shape, mouth and level how far it opens
    const open = singing ? Math.max(0.08, Math.min(1, state.mouth * (0.4 + state.level * 0.9))) : 0.05;
    const w = along(MOUTH_W, state.vowel) * (0.7 + open * 0.4), h = Math.max(1.5, along(MOUTH_H, state.vowel) * open);
    put(mouth, 'rx', w.toFixed(1)); put(mouth, 'ry', h.toFixed(1)); put(mouth, 'cy', (90 + h * 0.4).toFixed(1));
    put(tongue, 'rx', (w * 0.55).toFixed(1)); put(tongue, 'ry', (h * 0.35).toFixed(1)); put(tongue, 'cy', (90 + h * 0.9).toFixed(1));
    put(tongue, 'opacity', h > 4 ? 1 : 0);
    // quiet: a small content smile instead of the mouth
    for (const part of [mouth, tongue, teeth]) put(part, 'visibility', singing ? 'visible' : 'hidden');
    put(smile, 'visibility', singing ? 'hidden' : 'visible');
    // the eyes: half shut on a loud, high note (it is giving it all), a blink now and then, the pupils look up a little with the pitch
    // the eyes: it sings with its eyes closed in bliss; quiet, they are soft and half-lidded with a slow blink now and then
    const phase = now % 6000, blink = phase > blinkAt && phase < blinkAt + 420, blissful = singing && state.level > 0.15;
    const heavy = blink ? 1 : 0.42 + Math.sin(t * 2 * Math.PI * 0.15) * 0.05;
    for (const e of eyes) {
      put(e.open, 'visibility', blissful ? 'hidden' : 'visible');
      put(e.closed, 'visibility', blissful ? 'visible' : 'hidden');
      const lidLow = 48 + heavy * 18;
      put(e.lid, 'd', `M${e.x - 9} 47 L${e.x + 9} 47 L${e.x + 9} ${(lidLow - 4).toFixed(1)} Q${e.x} ${(lidLow + 2).toFixed(1)} ${e.x - 9} ${(lidLow - 4).toFixed(1)} Z`);
    }
    // the notes
    if (singing && now - lastNote > 900 - state.level * 300) {
      lastNote = now;
      const text = node('text', { x: 128 + Math.random() * 30, y: 86 + lift }, notes); text.textContent = Math.random() < 0.5 ? '♪' : '♫';
      floating.push({ text, born: now, drift: (Math.random() - 0.3) * 30 });
    }
    for (let i = floating.length - 1; i >= 0; i--) {
      const f = floating[i], age = (now - f.born) / 2600;   // the notes drift up slowly
      if (age >= 1) { f.text.remove(); floating.splice(i, 1); continue; }
      put(f.text, 'transform', `translate(${(f.drift * age).toFixed(1)} ${(-60 * age).toFixed(1)})`);
      put(f.text, 'opacity', (1 - age).toFixed(2));
    }
    frame = requestAnimationFrame(draw);
  };
  frame = requestAnimationFrame(draw);
  return {
    element: svg,
    set(next) { state = { ...state, ...next }; },
    /** Draws one moment at once (for a still picture or a test), with time t in seconds. */
    still(next, t = 0.3) { state = { ...state, ...next }; cancelAnimationFrame(frame); start = performance.now() - t * 1000; forceDraw = true; draw(performance.now()); cancelAnimationFrame(frame); },
    destroy() { cancelAnimationFrame(frame); svg.remove(); },
  };
}
