// The picture of Koda: the eighth note of the logo with a face and a ribbon tail, and what each emotion looks like (ported from plan/pet/demo). One Koda object per picture; all of them share one
// animation loop that only runs while one is shown and the page is visible, and stands still for reduced motion. @ample:koda
import { TAILS, createTail, stepTail } from '../lib/koda-tail.mjs';

// eyes: which eyes; mouth: the path of the mouth in the head (before its tilt), '' for the talking mouth that opens and closes
export const EMO = {
  neutral:   { eyes: 'open',     mouth: 'M86 185h12',                 name: 'Rest',        use: 'Waiting until you ask something.' },
  happy:     { eyes: 'happy',    mouth: 'M82 182q10 11 20 0',         name: 'Happy',       use: 'You said yes: the draft is kept.' },
  excited:   { eyes: 'wide',     mouth: 'M80 181q12 17 24 0z',        name: 'Excited',     use: 'A draft is ready and Koda likes it.' },
  love:      { eyes: 'love',     mouth: 'M84 183q8 8 16 0',           name: 'In love',     use: 'A lovely result, or a compliment.' },
  surprised: { eyes: 'wide',     mouth: 'M92 179a5 6.5 0 1 0 .01 0z', name: 'Surprised',   use: 'Something unexpected, like a warning.' },
  confused:  { eyes: 'confused', mouth: 'M82 186q5-6 10 0t10 0',      name: 'Confused',    use: 'The question is not clear yet.' },
  sad:       { eyes: 'sad',      mouth: 'M83 190q9-9 18 0',           name: 'Sad',         use: 'It did not work: a build failed.' },
  sleepy:    { eyes: 'sleepy',   mouth: 'M89 186a3 3 0 1 0 6 0a3 3 0 1 0 -6 0', name: 'Sleepy', use: 'Waiting in the queue.' },
  angry:     { eyes: 'angry',    mouth: 'M84 187q8-4 16 0',           name: 'Grumpy',      use: 'Something keeps going wrong.' },
  shy:       { eyes: 'shy',      mouth: 'M86 185q6 5 12 0',           name: 'Shy',         use: 'Koda made a mistake and says sorry.' },
  think:     { eyes: 'open',     mouth: 'M87 185h10',                 name: 'Thinking',    use: 'Koda is working on an answer: little notes walk along the beam.' },
  scan:      { eyes: 'open',     mouth: 'M87 185h10',                 name: 'Looking it up', use: 'Koda is reading the song with a magnifying glass.' },
  build:     { eyes: 'open',     mouth: 'M86 185q6 4 12 0',           name: 'Building',    use: 'A real build is running: a hard hat, a hammer, sparks and bricks that stack up.' },
  sketch:    { eyes: 'open',     mouth: 'M87 185h10',                 name: 'Sketching',   use: 'A preview build is running: a hard hat, a blueprint, a pencil and a spirit level.' },
  asking:    { eyes: 'wide',     mouth: 'M92 181a3.2 4 0 1 0 .01 0z', name: 'Asking',      use: '' },
  talk:      { eyes: 'open',     mouth: '',                           name: 'Talking',     use: '' },
  jump:      { eyes: 'happy',    mouth: 'M80 181q12 17 24 0z',        name: 'Jumping',     use: '' },
};

const note = (x, y, c, d) => `<g class="nt" style="--dx:${d}px;--rot:${d > 0 ? 25 : -20}deg"><ellipse cx="${x}" cy="${y}" rx="7" ry="5" fill="${c}" transform="rotate(-20 ${x} ${y})"/><path d="M${x + 6} ${y - 2}V${y - 20}q6 2 8 8" fill="none" stroke="${c}" stroke-width="3" stroke-linecap="round"/></g>`;
const heart = (x, y) => `<path class="heart" d="M${x} ${y + 6}c-9-6-12-12-7-16 3-2 6-1 7 3 1-4 4-5 7-3 5 4 2 10-7 16z"/>`;

export function kodaMarkup(label = 'Koda, a teal eighth note with a face') {
  return `<svg class="pet" viewBox="0 0 260 240" data-emo="neutral" data-eyes="open" role="img" aria-label="${label}">
  <ellipse class="shadow" cx="130" cy="222" rx="56" ry="7"/>
  <path class="wave" d="M60 222H200"/>
  <path class="tail glow" d=""/>
  <g class="all">
    <g class="lens"><g class="lensbody"><circle cx="0" cy="0" r="10" fill="#08201f55" stroke="#9ff7ff" stroke-width="3.2"/><path d="M7.5 7.5l9 9" stroke="#9ff7ff" stroke-width="4" stroke-linecap="round"/><path d="M-5 -3a6 6 0 0 1 5-4" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" opacity=".7"/></g></g>
    <g class="note2"><g class="h2"><ellipse class="teal glow" cx="186" cy="184" rx="21" ry="15" transform="rotate(-20 186 184)"/></g><path class="tealline glow" d="M205 176V72" style="stroke-width:8"/></g>
    <path class="tealline glow beam" pathLength="100" d="M124 52L205 70" style="stroke-width:13;stroke-linecap:butt"/>
    <g class="note1">
      <path class="tealline glow" d="M124 164V52" style="stroke-width:9"/>
      <g class="h1">
        <ellipse class="teal glow" cx="92" cy="172" rx="36" ry="26" transform="rotate(-20 92 172)"/>
        <ellipse class="blush" cx="66" cy="182" rx="8" ry="5"/><ellipse class="blush" cx="117" cy="168" rx="8" ry="5"/>
        <g class="eg e-open"><circle class="eye" cx="80" cy="168" r="8"/><circle class="pupil" cx="80" cy="168" r="3.6"/><circle class="eye" cx="103" cy="161" r="8"/><circle class="pupil" cx="103" cy="161" r="3.6"/></g>
        <g class="eg e-wide"><circle class="eye" cx="80" cy="168" r="10.5"/><circle class="pupil" cx="80" cy="168" r="3.2"/><circle class="eye" cx="103" cy="161" r="10.5"/><circle class="pupil" cx="103" cy="161" r="3.2"/></g>
        <g class="eg e-happy"><path class="inkline" d="M72 172q8-12 16 0M95 165q8-12 16 0"/></g>
        <g class="eg e-sleepy"><path class="inkline" d="M72 167q8 9 16 0M95 160q8 9 16 0"/></g>
        <g class="eg e-angry"><ellipse class="eye" cx="80" cy="169" rx="8" ry="6.4"/><circle class="pupil" cx="81" cy="170" r="3.6"/><ellipse class="eye" cx="103" cy="162" rx="8" ry="6.4"/><circle class="pupil" cx="102" cy="163" r="3.6"/><path class="inkline" d="M67 154L91 163M114 148L94 157"/></g>
        <g class="eg e-sad"><circle class="eye" cx="80" cy="168" r="8"/><circle class="pupil" cx="79" cy="166" r="3.6"/><circle class="eye" cx="103" cy="161" r="8"/><circle class="pupil" cx="102" cy="159" r="3.6"/><path class="inkline" d="M68 161L90 154M94 148L115 155"/></g>
        <g class="eg e-love"><path class="inkline" style="fill:#ff4fd8;stroke:#ff4fd8;stroke-width:1.5" d="M80 176c-11-7-14-13-8-18 3-2 7-1 8 3 1-4 5-5 8-3 6 5 3 11-8 18zM103 169c-11-7-14-13-8-18 3-2 7-1 8 3 1-4 5-5 8-3 6 5 3 11-8 18z"/></g>
        <g class="eg e-shy"><circle class="eye" cx="80" cy="168" r="8"/><circle class="pupil" cx="77" cy="172" r="3.6"/><circle class="eye" cx="103" cy="161" r="8"/><circle class="pupil" cx="100" cy="165" r="3.6"/></g>
        <g class="eg e-confused"><circle class="eye" cx="80" cy="168" r="6.5"/><circle class="pupil" cx="82" cy="167" r="3"/><circle class="eye" cx="103" cy="160" r="10"/><circle class="pupil" cx="101" cy="162" r="3.4"/><path class="inkline" d="M70 157h16M93 145q9-6 19 0"/></g>
        <g transform="rotate(-20 92 184)"><path class="inkline mouthpath" d="M86 185h12"/><ellipse class="ink mouth m-talk" cx="92" cy="185" rx="8" ry="1.8"/><g class="mouthtape" aria-hidden="true"><g class="tapeflap"><rect x="70" y="177.5" width="44" height="15" rx="2.2" fill="#d8d2b4" stroke="#7a7459" stroke-width="1.6"/><path d="M78 181v7M86 181v7M94 181v7M102 181v7" stroke="#a79f78" stroke-width="1.2" stroke-linecap="round"/></g></g></g>
        <path class="inkline sharp" d="M69 156L90 161M114 150L95 154"/>
        <g class="band" transform="translate(88 153) rotate(-14)"><g class="bi"><path class="tails" d="M30 6q16-3 25 5M30 9q13 6 19 17" fill="none" stroke="#e5233a" stroke-width="4.5" stroke-linecap="round"/><path d="M-31 5q31-10 62 0v8q-31-10-62 0z" fill="#e5233a" stroke="#a3121f" stroke-width="1.4"/><circle cx="0" cy="3" r="3.4" fill="#fff" fill-opacity=".9"/></g></g>
      </g>
    </g>
    <g class="xform" transform="translate(92 166)"><circle class="xr1" r="12"/><circle class="xr2" r="12"/><g class="xs"><path d="M0 -30V-50" transform="rotate(0)"/><path d="M0 -30V-50" transform="rotate(45)"/><path d="M0 -30V-50" transform="rotate(90)"/><path d="M0 -30V-50" transform="rotate(135)"/><path d="M0 -30V-50" transform="rotate(180)"/><path d="M0 -30V-50" transform="rotate(225)"/><path d="M0 -30V-50" transform="rotate(270)"/><path d="M0 -30V-50" transform="rotate(315)"/></g></g>
    <g class="cons">
      <g class="hat" transform="translate(88 150) rotate(-14)"><path d="M-23 3a23 21 0 0 1 46 0z" fill="#ffc933" stroke="#e29a00" stroke-width="2"/><path d="M-30 3h60" stroke="#e29a00" stroke-width="5" stroke-linecap="round"/><path d="M-3 -17v19" stroke="#fff" stroke-opacity=".55" stroke-width="3" stroke-linecap="round"/></g>
      <g class="real">
        <g class="v v0">
          <g class="hammer"><path d="M150 178L150 138" stroke="#c98a4b" stroke-width="5" stroke-linecap="round"/><rect x="138" y="128" width="24" height="13" rx="2.5" fill="#aeb6d8" stroke="#6f79a3" stroke-width="1.6"/></g>
          <g class="spk"><path d="M168 150l3 6 6 3-6 3-3 6-3-6-6-3 6-3z" fill="#ffd166"/><path d="M176 134l2 4 4 2-4 2-2 4-2-4-4-2 4-2z" fill="#fff6c4"/></g>
          <g class="bricks"><rect class="brick" x="176" y="206" width="22" height="10" rx="1.5" fill="#e0644a"/><rect class="brick" x="200" y="206" width="22" height="10" rx="1.5" fill="#f0883a"/><rect class="brick" x="188" y="195" width="22" height="10" rx="1.5" fill="#ffc933"/></g>
          <g class="cone"><path d="M40 216l8-26h8l8 26z" fill="#ff7a2f"/><path d="M43 206h18" stroke="#fff" stroke-width="3"/><rect x="36" y="215" width="32" height="4" rx="1.5" fill="#d9531a"/></g>
        </g>
        <g class="v v1">
          <rect x="140" y="204" width="84" height="11" rx="2" fill="#c98a4b" stroke="#8a5a28" stroke-width="1.5"/>
          <g class="saw"><path d="M150 196l44-4" stroke="#aeb6d8" stroke-width="7" stroke-linecap="round"/><path d="M152 201l4 3 4-3 4 3 4-3 4 3 4-3 4 3 4-3 4 3 4-3" fill="none" stroke="#6f79a3" stroke-width="1.6"/><rect x="190" y="184" width="14" height="12" rx="4" fill="#e0644a"/></g>
          <g class="dust"><circle cx="166" cy="206" r="2.2" fill="#f4d9a8"/><circle cx="178" cy="207" r="1.8" fill="#f4d9a8"/><circle cx="190" cy="206" r="2.4" fill="#f4d9a8"/></g>
        </g>
        <g class="v v2">
          <path d="M222 216V112" stroke="#ffb84d" stroke-width="6" stroke-linecap="round"/><path d="M190 116h44" stroke="#ffb84d" stroke-width="5" stroke-linecap="round"/><path d="M222 112l-12 4M222 112l12 4" stroke="#ffb84d" stroke-width="3"/>
          <g class="rope"><path d="M200 117V158" stroke="#dfe6ff" stroke-width="1.8"/><path d="M196 158h8l-2 4h-4z" fill="#9aa4c7"/><rect x="190" y="162" width="20" height="10" rx="1.5" fill="#e0644a"/></g>
        </g>
      </g>
      <g class="prev">
        <g class="v v0">
          <g class="plan"><rect x="132" y="134" width="50" height="34" rx="3" fill="#1e6bd6" stroke="#9fd0ff" stroke-width="1.6"/><path d="M132 145h50M132 156h50M145 134v34M158 134v34M171 134v34" stroke="#9fd0ff" stroke-opacity=".5" stroke-width="1"/><path class="draw" d="M138 162q10-16 20-6t18-14" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round"/></g>
          <g class="pencil"><path d="M0 0l16-16" stroke="#ffd166" stroke-width="5" stroke-linecap="round"/><path d="M-3 3l3-3 3 3z" fill="#555"/></g>
          <g class="level"><rect x="54" y="206" width="52" height="10" rx="3" fill="#9be15d" stroke="#5b9f2c" stroke-width="1.6"/><circle class="lvb" cx="80" cy="211" r="3" fill="#fff" fill-opacity=".85"/></g>
        </g>
        <g class="v v1">
          <rect x="132" y="176" width="26" height="22" rx="5" fill="#ffc933" stroke="#e29a00" stroke-width="1.8"/><path d="M138 187h14" stroke="#333" stroke-width="2.5" stroke-linecap="round"/>
          <g class="tape"><rect x="158" y="184" width="62" height="5" fill="#ffe27a" stroke="#e29a00" stroke-width="1"/><path d="M162 184v5M170 184v3M178 184v5M186 184v3M194 184v5M202 184v3M210 184v5" stroke="#333" stroke-width="1"/><rect x="219" y="180" width="3" height="13" fill="#9aa4c7"/></g>
        </g>
        <g class="v v2">
          <rect x="128" y="146" width="62" height="56" rx="3" fill="#ffffff14" stroke="#9fd0ff" stroke-opacity=".6" stroke-width="1.4"/>
          <circle class="arc" cx="150" cy="190" r="22" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" transform="rotate(-90 150 190)"/>
          <g class="legs"><path d="M150 190L164 140" stroke="#dfe6ff" stroke-width="3.5" stroke-linecap="round"/><path d="M150 190L146 150" stroke="#dfe6ff" stroke-width="3.5" stroke-linecap="round"/><circle cx="164" cy="140" r="4" fill="#ffd166"/></g>
        </g>
      </g>
    </g>
    <g class="walkers">
      <g><animateMotion dur="1.5s" repeatCount="indefinite" path="M124 40L205 58"/><g class="hop"><ellipse rx="5.5" ry="4" fill="#fff" transform="rotate(-20)"/><path d="M4.6 -1V-12" stroke="#fff" stroke-width="2" stroke-linecap="round"/></g></g>
      <g><animateMotion dur="1.5s" begin="-.75s" repeatCount="indefinite" path="M124 40L205 58"/><g class="hop" style="animation-delay:-.17s"><ellipse rx="5.5" ry="4" fill="#ff4fd8" transform="rotate(-20)"/><path d="M4.6 -1V-12" stroke="#ff4fd8" stroke-width="2" stroke-linecap="round"/></g></g>
    </g>
  </g>
  <g class="xg x-hearts">${heart(150, 120)}${heart(40, 100)}${heart(170, 80)}</g>
  <g class="xg x-zzz"><text class="zz" x="140" y="110">z</text><text class="zz" x="140" y="110">z</text><text class="zz" x="140" y="110">z</text></g>
  <g class="xg x-q"><text class="mark" x="150" y="96">?</text></g>
  <g class="xg x-bang"><text class="mark" x="152" y="98">!</text></g>
  <g class="xg x-tear"><path class="tear" d="M106 176q-5 8 0 11 5-3 0-11z"/></g>
  <g class="xg x-anger"><path class="anger" d="M44 128l12 6M44 136l12-6M52 124v-10M60 128l-4 6"/></g>
  <g class="xg x-sparks"><path class="spark" d="M36 74l4 10 10 4-10 4-4 10-4-10-10-4 10-4z"/><path class="spark" d="M178 92l3 7 7 3-7 3-3 7-3-7-7-3 7-3z"/></g>
  <g class="xg x-notes">${note(60, 90, '#ff4fd8', -34)}${note(150, 70, '#2de2ff', 10)}${note(206, 96, '#ffd166', 36)}</g>
</svg>`;
}

const instances = new Set();
let frame = 0;
const still = () => typeof matchMedia === 'function' && matchMedia('(prefers-reduced-motion: reduce)').matches;
function loop(t) {
  frame = 0;
  let any = false;
  for (const k of instances) if (!k.paused && k.svg.isConnected) { k.step(t); any = true; }
  if (any && !document.hidden) frame = requestAnimationFrame(loop);
}
const wake = () => { if (!frame && !document.hidden) frame = requestAnimationFrame(loop); };
if (typeof document !== 'undefined') document.addEventListener('visibilitychange', wake);

/** A picture of Koda in a host element. */
export class Koda {
  constructor(host, { emotion = 'neutral', label } = {}) {
    host.innerHTML = kodaMarkup(label);
    this.host = host;
    this.svg = host.querySelector('svg');
    this.all = this.svg.querySelector('.all');
    this.tailEl = this.svg.querySelector('.tail');
    this.mouth = this.svg.querySelector('.mouthpath');
    this.tail = createTail(124, 52);
    this.prev = null;
    this.paused = false;
    this.power = 0.5;
    this.#decorate();
    this.set(emotion);
    instances.add(this);
    wake();
  }
  set(name) {
    const e = EMO[name] ?? EMO.neutral;
    this.emotion = EMO[name] ? name : 'neutral';
    this.cfg = this.#tail(TAILS[this.emotion] ?? TAILS.neutral);
    this.svg.dataset.emo = this.emotion;
    this.svg.dataset.eyes = e.eyes;
    this.svg.dataset.mouth = e.mouth ? 'path' : 'talk';
    this.mouth.setAttribute('d', e.mouth || 'M86 185h12');
    this.mouth.style.display = e.mouth ? '' : 'none';
    wake();
  }
  /** A gleam in the eyes, an aura and a few stars: they only show when the model and the thinking are strong (see setPower). */
  #decorate() {
    const NS = 'http://www.w3.org/2000/svg';
    const make = (name, attrs) => { const n = document.createElementNS(NS, name); for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, String(v)); return n; };
    for (const pupil of this.svg.querySelectorAll('.e-open .pupil, .e-wide .pupil')) {
      pupil.after(make('circle', { class: 'gleam', r: 1.7, cx: Number(pupil.getAttribute('cx')) + 2.4, cy: Number(pupil.getAttribute('cy')) - 2.6 }));
    }
    this.all.prepend(make('circle', { class: 'pwraura', cx: 130, cy: 138, r: 104 }));
    const stars = make('g', { class: 'stars', 'aria-hidden': 'true' });
    [[34, 70, 1], [226, 96, 0.8], [62, 24, 0.6]].forEach(([x, y, s], i) => stars.append(make('path', { class: 'pstar', d: 'M0 -7L2 -2L7 0L2 2L0 7L-2 2L-7 0L-2 -2Z', transform: `translate(${x} ${y}) scale(${s})`, style: `animation-delay:${i * 0.7}s` })));
    this.all.append(stars);
  }
  /**
   * How capable the model is known to be, together with how hard it thinks (power: { score 0..1, tier 0..4 }): the same Koda, only more or less radiant. A small, quick setting is soft and relaxed
   * (a blush, a calm tail), a large model that thinks hard is focused: a gleam in the eyes, a glow, a slow aura and a steadier, more precise tail; the very top gets a few stars.
   */
  setPower(power) {
    this.power = power?.score ?? 0.5;
    this.svg.dataset.power = String(power?.tier ?? 2);
    this.svg.style.setProperty('--pw', String(this.power));
    this.cfg = this.#tail(TAILS[this.emotion] ?? TAILS.neutral);
  }
  /** Unsafe mode (the user lets Koda change things directly): a red headband, sharper eyes and a tighter, more purposeful movement for as long as it lasts. */
  setRisk(unsafe, bare = false) {
    const was = !!this.unsafe;
    this.unsafe = !!unsafe;
    if (unsafe && bare) this.svg.dataset.mode = 'bare'; else delete this.svg.dataset.mode;   // bare metal: the headband and the glow go from red towards purple and pink
    if (this.unsafe) this.svg.dataset.risk = 'unsafe'; else delete this.svg.dataset.risk;
    this.cfg = this.#tail(TAILS[this.emotion] ?? TAILS.neutral);
    if (was !== this.unsafe) this.#transform(this.unsafe);
  }
  /** The change between draft and live is a small transformation (rings, sparks, the headband that ties on); it has its own pictures, so it goes with every other animation. */
  #transform(on) {
    const s = this.svg;
    clearTimeout(this.xt);
    delete s.dataset.xf;
    void s.getBoundingClientRect();
    s.dataset.xf = on ? 'on' : 'off';
    this.xt = setTimeout(() => { delete s.dataset.xf; }, 1100);
  }
  #tail(cfg) {
    // a stronger setting moves with more precision (stiffer, a little less sway); the middle is unchanged
    const calm = { ...cfg, stiff: cfg.stiff * (0.85 + (0.3 * this.power)), amp: cfg.amp * (1.1 - (0.2 * this.power)) };
    return this.unsafe ? { ...calm, stiff: calm.stiff * 1.6, amp: calm.amp * 0.6 } : calm;
  }
  /** Only the mouth of another emotion (the eyes stay as they are): a face that sings along without blinking between open and closed eyes. */
  setMouth(name) {
    const e = EMO[name] ?? EMO.neutral;
    this.mouth.setAttribute('d', e.mouth || 'M86 185h12');
    this.mouth.style.display = e.mouth ? '' : 'none';
  }
  /** Which variation of the constructing picture shows (a real build or a preview has three); they change as the build makes progress. */
  setVariant(n) { this.svg.dataset.cv = String(((Number(n) || 0) % 3 + 3) % 3); }
  pause(flag) { this.paused = !!flag; if (!flag) wake(); }
  step(t) {
    // where the tail is fixed, with the turn and the movement of the body (the animation of the picture) taken into account
    const css = getComputedStyle(this.all).transform;
    let a = 1, b = 0, c = 0, d = 1, e = 0, f = 0;
    if (css && css !== 'none') [a, b, c, d, e, f] = css.slice(css.indexOf('(') + 1, -1).split(',').map(Number);
    const ox = 130, oy = 214, px = 124 - ox, py = 52 - oy;
    const rect = this.svg.getBoundingClientRect();
    const k = 260 / Math.max(1, rect.width);
    let dx = 0, dy = 0;
    if (this.prev) { dx = (rect.left - this.prev.left) * k; dy = (rect.top - this.prev.top) * k; if (Math.abs(dx) + Math.abs(dy) > 200) dx = dy = 0; }
    this.prev = rect;
    this.tailEl.setAttribute('d', stepTail(this.tail, { bx: ox + a * px + c * py + e, by: oy + b * px + d * py + f, rot: Math.atan2(b, a), t, dx, dy, cfg: this.cfg, still: still() }));
  }
  destroy() { instances.delete(this); this.host.replaceChildren(); }
}
