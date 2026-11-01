/* Sound previews for the docs: a small wave player + waveform visualizer.
   Over http(s) the player plays the sound that AMPLE rendered (audio/<id>.mp3); without it (file://, or a missing file) the sound is rendered in the browser (OfflineAudioContext) as a rough stand-in.
   <div class="pv" data-pv='{"title":"..","src":"pad","fx":"reverb","p":{"mix":0.5},"wav":null}'></div> */
(function () {
  const SR = 44100, DUR = 4; const MAX_LAYERS = 4;
  const midi = (n) => 440 * Math.pow(2, (n - 69) / 12);

  /* ---------- shared audio context (the Vibe/Sound code in app.js can reuse it) ---------- */
  const A = (window.AmpleAudio = {
    live: new Map(), /* key of a sound -> the preview that plays it (a page change throws the element away, the sound goes on: a new element for the same sound adopts it) */
    mix() { if (!this.master) return; const n = Math.max(1, this.set.size); this.master.gain.setTargetAtTime(0.85 / Math.pow(n, 0.75), this.ctx.currentTime, 0.05); /* every extra layer lowers the level of all of them (1: 0.85, 2: 0.51, 3: 0.37) */ },
    ctx: null, master: null, analyser: null, set: new Map(), t0: null, // set: playing previews (stop -> makeLoop); t0: audio-clock time of buffer position 0 of the shared timeline
    get() {
      if (!this.ctx) {
        this.ctx = new (window.AudioContext || window.webkitAudioContext)();
        this.master = this.ctx.createGain(); this.master.gain.value = 0.85; this.lim = this.ctx.createDynamicsCompressor(); this.lim.threshold.value = -3; this.lim.knee.value = 0; this.lim.ratio.value = 20; this.lim.attack.value = 0.003; this.lim.release.value = 0.15; /* safety limiter: layered previews must not distort */
        this.analyser = this.ctx.createAnalyser(); this.analyser.fftSize = 256;
        this.master.connect(this.lim); this.lim.connect(this.analyser); this.analyser.connect(this.ctx.destination);
      }
      if (this.ctx.state === 'suspended') this.ctx.resume();
      return this;
    },
    level() { if (!this.analyser) return 0; const d = new Uint8Array(this.analyser.frequencyBinCount); this.analyser.getByteFrequencyData(d); return d.reduce((a, b) => a + b, 0) / d.length / 255 * 2.2; },
  });

  /* ---------- sources: short, musical, deterministic ---------- */
  function noiseBuf(c, secs, seed) {
    const b = c.createBuffer(2, Math.ceil(c.sampleRate * secs), c.sampleRate); let s = seed || 1;
    for (let ch = 0; ch < 2; ch++) { const d = b.getChannelData(ch); for (let i = 0; i < d.length; i++) { s = (s * 1664525 + 1013904223) >>> 0; d[i] = (s / 4294967296) * 2 - 1; } }
    return b;
  }
  function env(g, t, a, d, v, dur) { g.gain.setValueAtTime(0, t); g.gain.linearRampToValueAtTime(v, t + a); g.gain.exponentialRampToValueAtTime(0.0001, t + Math.max(a + 0.01, dur)); }
  function osc(c, out, type, f, t, dur, v, a, detune) {
    const o = c.createOscillator(), g = c.createGain(); o.type = type; o.frequency.value = f; if (detune) o.detune.value = detune;
    env(g, t, a || 0.01, 0, v, dur); o.connect(g); g.connect(out); o.start(t); o.stop(t + dur + 0.05);
  }
  const SOURCES = {
    tone(c, out, semis) { osc(c, out, 'sine', midi(69 + semis), 0, DUR, 0.3, 0.02); },
    pad(c, out, semis) { // A minor, then F: slow saw pad
      [[57, 60, 64], [53, 57, 60]].forEach((ch, i) => ch.forEach((n) => { osc(c, out, 'sawtooth', midi(n + semis), i * 2, 2.1, 0.07, 0.25, -6); osc(c, out, 'sawtooth', midi(n + semis), i * 2, 2.1, 0.07, 0.25, 6); }));
    },
    pluck(c, out, semis) { [69, 72, 76, 72, 69, 64, 67, 72].forEach((n, i) => osc(c, out, 'triangle', midi(n + semis), i * 0.5, 0.7, 0.35, 0.004)); },
    arp(c, out, semis) { [57, 60, 64, 69, 64, 60, 57, 60, 53, 57, 60, 65, 60, 57, 53, 57].forEach((n, i) => osc(c, out, 'square', midi(n + semis), i * 0.25, 0.22, 0.12, 0.004)); },
    bass(c, out, semis) { [45, 45, 48, 45, 41, 41, 43, 45].forEach((n, i) => osc(c, out, 'sawtooth', midi(n + semis), i * 0.5, 0.45, 0.25, 0.01)); },
    drums(c, out) {
      const nb = noiseBuf(c, 0.3, 7);
      for (let b = 0; b < 8; b++) {
        const t = b * 0.5;
        if (b % 2 === 0) { const o = c.createOscillator(), g = c.createGain(); o.frequency.setValueAtTime(150, t); o.frequency.exponentialRampToValueAtTime(40, t + 0.15); env(g, t, 0.003, 0, 0.9, 0.3); o.connect(g); g.connect(out); o.start(t); o.stop(t + 0.35); }
        else { const s = c.createBufferSource(), f = c.createBiquadFilter(), g = c.createGain(); s.buffer = nb; f.type = 'bandpass'; f.frequency.value = 1800; env(g, t, 0.002, 0, 0.5, 0.18); s.connect(f); f.connect(g); g.connect(out); s.start(t); s.stop(t + 0.25); }
        for (let h = 0; h < 2; h++) { const th = t + h * 0.25, s = c.createBufferSource(), f = c.createBiquadFilter(), g = c.createGain(); s.buffer = nb; f.type = 'highpass'; f.frequency.value = 7000; env(g, th, 0.001, 0, 0.18, 0.05); s.connect(f); f.connect(g); g.connect(out); s.start(th); s.stop(th + 0.08); }
      }
    },
    seq(c, out, semis, p) { const st = p.step ?? 0.5, v = p.vel || []; (p.notes || [60]).forEach((n, i) => { if (n != null) osc(c, out, p.wave || 'triangle', midi(n + semis), i * st, st * (p.len ?? 1.5), (v[i] ?? 1) * 0.32, 0.01); }); },
    chords(c, out, semis, p) { const st = p.step ?? 1, w = p.wave || 'sawtooth'; (p.chords || [[60, 64, 67]]).forEach((ch, i) => ch.forEach((n) => { osc(c, out, w, midi(n + semis), i * st, st * 1.05, w === 'sawtooth' ? 0.07 : 0.13, 0.08, -5); if (w === 'sawtooth') osc(c, out, w, midi(n + semis), i * st, st * 1.05, 0.07, 0.08, 5); })); },
    rhythm(c, out, semis, p) { const bt = 60 / (p.bpm || 120); /* beats are quarter notes, also in 6/8 and 7/8 */ for (const e of AmpletGen.rhythmEvents(p)) { const t = e.at * bt; if (t < DUR - 0.01) osc(c, out, 'sine', midi(e.pitches[0]), t, 0.09, 0.4 * e.vel, 0.002); } },
    meter(c, out, semis, p) { const st = 60 / (p.bpm || 100), nb = p.beats || 4, sub = p.sub || 1; for (let i = 0, t = 0; t < DUR - 0.01; i++, t += st) { osc(c, out, 'sine', i % nb === 0 ? 1500 : 1000, t, 0.07, 0.4, 0.002); for (let s = 1; s < sub; s++) osc(c, out, 'sine', 700, t + s * st / sub, 0.04, 0.12, 0.002); } },
    grid(c, out, semis, p) {
      const st = 60 / (p.bpm || 120) / (p.res === 16 ? 4 : 2), len = Math.max((p.kick || '').length, (p.snare || '').length, (p.hat || '').length, 1), nb = noiseBuf(c, 0.3, 7);
      const noise = (t, type, fr, v, d) => { const s = c.createBufferSource(), f = c.createBiquadFilter(), g = c.createGain(); s.buffer = nb; f.type = type; f.frequency.value = fr; env(g, t, 0.002, 0, v, d); s.connect(f); f.connect(g); g.connect(out); s.start(t); s.stop(t + d + 0.05); };
      for (let t0 = 0; t0 < DUR - 0.01; t0 += len * st) for (let k = 0; k < len; k++) {
        const t = t0 + k * st; if (t >= DUR) break; const hit = (s) => { const ch = (s || '')[k]; return ch === 'x' ? 1 : ch === 'X' ? 1.5 : ch === 'o' ? 0.5 : 0; };
        if (hit(p.kick)) { const o = c.createOscillator(), g = c.createGain(); o.frequency.setValueAtTime(150, t); o.frequency.exponentialRampToValueAtTime(40, t + 0.15); env(g, t, 0.003, 0, 0.8 * hit(p.kick), 0.3); o.connect(g); g.connect(out); o.start(t); o.stop(t + 0.35); }
        if (hit(p.snare)) noise(t, 'bandpass', 1800, 0.5 * hit(p.snare), 0.18);
        if (hit(p.hat)) noise(t, 'highpass', 7000, 0.18 * hit(p.hat), 0.05);
      }
    },
    noise(c, out) { const s = c.createBufferSource(), g = c.createGain(); s.buffer = noiseBuf(c, DUR, 3); g.gain.value = 0.25; s.connect(g); g.connect(out); s.start(0); s.stop(DUR); },
    voice(c, out) { // a scripted "talking" saw: syllables with vowel formants
      VOWELS.forEach((v, i) => { const t = i * 0.5; const o = c.createOscillator(), g = c.createGain(); o.type = 'sawtooth'; o.frequency.value = midi(48 + [0, 0, 3, 5, 3, 0, 2, 0][i]); env(g, t, 0.03, 0, 0.5, 0.42);
        v.forEach((fr) => { const f = c.createBiquadFilter(); f.type = 'bandpass'; f.frequency.value = fr; f.Q.value = 6; o.connect(f); f.connect(g); });
        g.connect(out); o.start(t); o.stop(t + 0.5); });
    },
  };
  const VOWELS = [[700, 1200], [300, 2300], [450, 800], [500, 1800], [700, 1200], [300, 700], [500, 1800], [450, 800]];

  /* ---------- effects: rough WebAudio stand-ins, named after the blocks ---------- */
  const lfo = (c, rate, depth, target, shape) => { const o = c.createOscillator(), g = c.createGain(); o.type = shape || 'sine'; o.frequency.value = rate; g.gain.value = depth; o.connect(g); g.connect(target); o.start(0); return o; };
  const ir = (c, secs, decay) => { const n = Math.ceil(c.sampleRate * secs), b = c.createBuffer(2, n, c.sampleRate); let s = 11; for (let ch = 0; ch < 2; ch++) { const d = b.getChannelData(ch); for (let i = 0; i < n; i++) { s = (s * 1664525 + 1013904223) >>> 0; d[i] = ((s / 4294967296) * 2 - 1) * Math.pow(1 - i / n, 1 + decay * 4); } } return b; };
  const mixer = (c, inp, wet, amt) => { const o = c.createGain(), d = c.createGain(), w = c.createGain(); d.gain.value = 1 - amt * 0.6; w.gain.value = amt; inp.connect(d); d.connect(o); wet.connect(w); w.connect(o); return o; };
  const curve = (fn) => { const a = new Float32Array(2048); for (let i = 0; i < 2048; i++) a[i] = fn(i / 1024 - 1); return a; };
  const FX = {
    volume(c, i, p) { const g = c.createGain(); g.gain.value = Math.pow(10, (p.db ?? -6) / 20); i.connect(g); if (p.fade) { g.gain.setValueAtTime(g.gain.value, 0); g.gain.linearRampToValueAtTime(0.001, DUR); } return g; },
    pan(c, i, p) { const n = c.createStereoPanner(); n.pan.value = p.pan ?? -0.8; if (p.sweep) lfo(c, 0.5, 0.9, n.pan); i.connect(n); return n; },
    filter(c, i, p) { const f = c.createBiquadFilter(); f.type = p.type || 'lowpass'; f.Q.value = p.q ?? 4; f.frequency.setValueAtTime(p.from ?? 300, 0); f.frequency.exponentialRampToValueAtTime(p.to ?? 6000, DUR); i.connect(f); return f; },
    eq(c, i, p) { const f = c.createBiquadFilter(); f.type = 'peaking'; f.frequency.value = p.hz ?? 2500; f.Q.value = 1.2; f.gain.value = p.db ?? 12; i.connect(f); return f; },
    reverb(c, i, p) { const cv = c.createConvolver(); cv.buffer = ir(c, 0.4 + (p.decay ?? 0.6) * 2.6, p.decay ?? 0.6); i.connect(cv); return mixer(c, i, cv, p.mix ?? 0.5); },
    delay(c, i, p) { const d = c.createDelay(2), fb = c.createGain(); d.delayTime.value = p.time ?? 0.375; fb.gain.value = p.feedback ?? 0.45; i.connect(d); d.connect(fb); fb.connect(d); return mixer(c, i, d, p.mix ?? 0.5); },
    chorus(c, i, p) { const d = c.createDelay(0.1); d.delayTime.value = 0.025; lfo(c, p.rate ?? 0.8, 0.004, d.delayTime); i.connect(d); return mixer(c, i, d, 0.55); },
    flanger(c, i, p) { const d = c.createDelay(0.05), fb = c.createGain(); d.delayTime.value = 0.004; fb.gain.value = 0.6; lfo(c, p.rate ?? 0.3, 0.003, d.delayTime); i.connect(d); d.connect(fb); fb.connect(d); return mixer(c, i, d, 0.6); },
    phaser(c, i, p) { let n = i; const stages = []; for (let k = 0; k < 4; k++) { const f = c.createBiquadFilter(); f.type = 'allpass'; f.frequency.value = 800 + k * 400; lfo(c, p.rate ?? 0.4, 500, f.frequency); n.connect(f); n = f; stages.push(f); } return mixer(c, i, n, 0.6); },
    tremolo(c, i, p) { const g = c.createGain(); g.gain.value = 0.5; lfo(c, p.rate ?? 6, p.depth ?? 0.5, g.gain); i.connect(g); return g; },
    distortion(c, i, p) { const s = c.createWaveShaper(), k = 1 + (p.drive ?? 0.7) * 40; s.curve = curve((x) => Math.tanh(x * k) * 0.6); s.oversample = '4x'; i.connect(s); return s; },
    bitcrusher(c, i, p) { const s = c.createWaveShaper(), lv = Math.pow(2, p.bits ?? 3); s.curve = curve((x) => Math.round(x * lv) / lv); i.connect(s); return s; },
    compressor(c, i, p) { const k = c.createDynamicsCompressor(); k.threshold.value = p.threshold ?? -40; k.ratio.value = p.ratio ?? 12; k.attack.value = 0.003; k.release.value = 0.15; const m = c.createGain(); m.gain.value = 2.2; i.connect(k); k.connect(m); return m; },
    gate(c, i, p) { const g = c.createGain(); g.gain.value = 0; for (let t = 0; t < DUR; t += 0.25) { g.gain.setValueAtTime(1, t); g.gain.setValueAtTime(0, t + 0.12); } i.connect(g); return g; },
    duck(c, i, p) { const g = c.createGain(); for (let t = 0; t < DUR; t += 0.5) { g.gain.setValueAtTime(0.08, t); g.gain.linearRampToValueAtTime(1, t + 0.4); } i.connect(g); return g; },
    width(c, i, p) { const d = c.createDelay(0.05), m = c.createChannelMerger(2); d.delayTime.value = 0.018; const s = c.createChannelSplitter(2); i.connect(s); s.connect(m, 0, 0); s.connect(d, 1); d.connect(m, 0, 1); return m; },
    vocoder(c, i) { return i; }, // handled in render()
    dry(c, i) { return i; },
  };
  const FX_NOTE = { vocoder: 'Scripted voice (vowel formants) on a saw chord: a rough stand-in for the vocoder block.', reverse: 'Reversed render.', pitch: 'Pitch shifted by resampling.', stretch: 'Slowed down by resampling.' };

  /* ---------- render once to a buffer (cached) ---------- */
  const cache = new Map();
  async function render(spec, wet) {
    const key = JSON.stringify([spec.src, wet ? spec.fx : 'dry', spec.p]);
    if (cache.has(key)) return cache.get(key);
    const real = await loadReal(spec, wet); if (real) { let pk = 0; for (let ch = 0; ch < real.numberOfChannels; ch++) for (const v of real.getChannelData(ch)) pk = Math.max(pk, Math.abs(v)); if (pk > 0) for (let ch = 0; ch < real.numberOfChannels; ch++) { const d = real.getChannelData(ch), k = 0.8 / pk; for (let i = 0; i < d.length; i++) d[i] *= k; } cache.set(key, real); return real; }
    const c = new OfflineAudioContext(2, SR * DUR, SR), pre = c.createGain(), p = spec.p || {};
    let src = spec.src || 'pad', fx = wet ? (spec.fx || 'dry') : 'dry';
    if (fx === 'vocoder' && wet) { // carrier = chord, bands follow a scripted vowel pattern
      const car = c.createGain(); SOURCES.pad(c, car, 0);
      const bands = [200, 350, 550, 800, 1200, 1800, 2600, 3800]; const sum = c.createGain(); sum.gain.value = 4;
      bands.forEach((fr) => { const f = c.createBiquadFilter(), g = c.createGain(); f.type = 'bandpass'; f.frequency.value = fr; f.Q.value = 5; car.connect(f); f.connect(g); g.connect(sum);
        g.gain.value = 0; VOWELS.forEach((v, k) => { const hit = v.some((vf) => Math.abs(Math.log2(vf / fr)) < 0.45); g.gain.setTargetAtTime(hit ? 1 : 0.04, k * 0.5, 0.03); }); });
      sum.connect(c.destination);
    } else {
      (SOURCES[src] || SOURCES.pad)(c, pre, wet && fx === 'pitch' ? (p.semitones ?? 7) : 0, p);
      const out = (FX[fx] || FX.dry)(c, pre, p); out.connect(c.destination);
    }
    let buf = await c.startRendering();
    if (wet && (spec.fx === 'reverse' || spec.fx === 'stretch')) { // buffer-level effects
      if (spec.fx === 'reverse') for (let ch = 0; ch < 2; ch++) buf.getChannelData(ch).reverse();
    }
    // normalise so dry and wet are comparable
    let pk = 0; for (let ch = 0; ch < 2; ch++) for (const v of buf.getChannelData(ch)) pk = Math.max(pk, Math.abs(v));
    if (pk > 0) for (let ch = 0; ch < 2; ch++) { const d = buf.getChannelData(ch); const k = 0.8 / pk; for (let i = 0; i < d.length; i++) d[i] *= k; }
    cache.set(key, buf); return buf;
  }

  /* ---------- the player widget ---------- */
  function draw(cv, buf, pos, on, lvl) {
    lvl = lvl || 0; const vibe = document.body.classList.contains("alive");
    const g = cv.getContext('2d'), w = cv.width = cv.clientWidth * devicePixelRatio, h = cv.height = cv.clientHeight * devicePixelRatio;
    const st = getComputedStyle(document.documentElement), acc = st.getPropertyValue('--acc').trim() || '#5fb3ff', dim = st.getPropertyValue('--line').trim() || '#444';
    g.clearRect(0, 0, w, h);
    if (!buf) { g.fillStyle = dim; g.fillRect(0, h / 2 - 1, w, 2); return; }
    const d = buf.getChannelData(0), step = Math.max(1, Math.floor(d.length / (w / 3)));
    for (let x = 0, k = 0; x < w; x += 3, k++) {
      let mx = 0; for (let i = k * step; i < (k + 1) * step && i < d.length; i += 8) mx = Math.max(mx, Math.abs(d[i]));
      const played = x / w <= pos, near = on && vibe ? Math.max(0, 1 - Math.abs(x / w - pos) * 14) : 0; g.fillStyle = played ? acc : dim; if (vibe && on && played) { g.shadowColor = acc; g.shadowBlur = near * 14 * devicePixelRatio; } else g.shadowBlur = 0; const bh = Math.min(h, Math.max(2, mx * h * 0.9 * (1 + near * lvl * 1.4))); g.fillRect(x, (h - bh) / 2, 2 * devicePixelRatio, bh);
    }
    if (on) { g.shadowBlur = vibe ? 18 * devicePixelRatio : 0; g.fillStyle = acc; g.fillRect(pos * w, 0, (2 + (vibe ? lvl * 5 : 0)) * devicePixelRatio, h); if (vibe) for (let k = 0; k < 3; k++) { g.globalAlpha = 0.5 * lvl; g.beginPath(); g.arc(pos * w, h / 2, (4 + k * 7 + lvl * 10) * devicePixelRatio, 0, 6.283); g.strokeStyle = acc; g.lineWidth = devicePixelRatio; g.stroke(); } g.globalAlpha = 1; g.shadowBlur = 0; }
  }
  const loadCM = () => window.AmpleCM ? Promise.resolve(window.AmpleCM) : new Promise((res, rej) => { const s = document.createElement('script'); s.src = 'vendor/codemirror-ample.js'; s.onload = () => (window.AmpleCM ? res(window.AmpleCM) : rej()); s.onerror = rej; document.head.appendChild(s); });
  const escH = (s) => s.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));
  /* the rendered sound (AMPLE -> mp3, site/audio/<id>.mp3): only over http(s), because file:// may not fetch; otherwise the browser stand-in is used */
  async function loadReal(spec, wet) { if (!/^https?:/.test(location.protocol) || !window.AmpletGen) return null; try { const r = await fetch('audio/' + AmpletGen.hash(spec, wet) + (wet ? '' : '-dry') + '.mp3'); if (!r.ok) return null; const raw = await A.get().ctx.decodeAudioData(await r.arrayBuffer()), ctx = A.get().ctx, T = [2, 4, 8].reduce((a, c) => (Math.abs(c - raw.duration) < Math.abs(a - raw.duration) ? c : a)), N = Math.round(T * raw.sampleRate), b = ctx.createBuffer(raw.numberOfChannels, N, raw.sampleRate), tile = false; /* a rendered sound is exactly 2, 4 or 8 s; the mp3 decoder adds a hair of padding, so cut it to exactly that length (2 and 4 divide 8: layered previews stay phase-locked) */ for (let ch = 0; ch < raw.numberOfChannels; ch++) { const src = raw.getChannelData(ch), dst = b.getChannelData(ch); for (let i = 0; i < N; i++) { const j = tile ? i % src.length : i; if (j < src.length) dst[i] = src[j]; } } b._real = true; return b; } catch { return null; } }
  /* a plain read-only AMPLE code block (the example song of a block page, taken from the spec) */
  function codeHydrate(el) {
    const code = decodeURIComponent(el.dataset.code); el.innerHTML = '<div class="pv-srcbar"><span>' + (el.dataset.note || '') + '</span><button type="button" class="pv-copy">Copy</button></div><div class="pv-cm"></div>'; const host = el.querySelector('.pv-cm');
    el.querySelector('.pv-copy').onclick = (e) => { navigator.clipboard?.writeText(code); e.target.textContent = 'Copied'; setTimeout(() => (e.target.textContent = 'Copy'), 1200); };
    loadCM().then(({ createAmpleEditor }) => { host.style.height = Math.min(420, code.split(String.fromCharCode(10)).length * 18 + 16) + 'px'; const m = {}; createAmpleEditor(host, m, code); m.readOnly = true; }).catch(() => { host.innerHTML = '<pre><code>' + escH(code) + '</code></pre>'; });
  }
  function hydrate(el) {
    if (el.dataset.ready) return; el.dataset.ready = 1;
    const spec = JSON.parse(el.dataset.pv), hasFx = spec.fx && spec.fx !== 'dry';
    el.innerHTML = `<div class="pv-top"><button class="pv-play" aria-label="Play preview" type="button">▶</button><div class="pv-meta"><b>${spec.title || 'Preview'}</b><small>${spec.note || FX_NOTE[spec.fx] || 'Rough browser preview, not the real block.'}</small></div>${hasFx ? '<div class="pv-ab" role="group" aria-label="Dry or with effect"><button type="button" data-m="dry">Dry</button><button type="button" data-m="wet" class="on">With effect</button></div>' : ''}</div><canvas class="pv-wave" aria-hidden="true"></canvas>`;
    const play = el.querySelector('.pv-play'), cv = el.querySelector('canvas'); let mode = 'wet', node = null, t0 = 0, raf = 0, buf = null, qt = 0; const key = window.AmpletGen ? AmpletGen.hash(spec, true) : JSON.stringify([spec.src, spec.fx, spec.p]);
    const top = el.querySelector('.pv-top'), src = document.createElement('div'); src.className = 'pv-src'; src.hidden = true; el.appendChild(src);
    const cbtn = document.createElement('button'); cbtn.type = 'button'; cbtn.className = 'pv-code'; cbtn.setAttribute('aria-expanded', 'false'); cbtn.title = 'Show this sound as AMPLE code'; cbtn.innerHTML = '&lt;/&gt; AMPLE'; top.appendChild(cbtn);
    const refreshCode = () => { if (src.hidden || !window.AmpletGen) return; const r = AmpletGen.specToAmplet(spec, mode === 'wet'), dep = r.deps.length ? 'Needs: ' + r.deps.map((d) => 'drum kit in ' + d.to + ' (pack ' + d.pack + ')').join(', ') : 'No extra files needed';
      src.innerHTML = '<div class="pv-srcbar"><span>' + dep + '</span><button type="button" class="pv-copy">Copy</button></div><div class="pv-cm"></div>'; const host = src.querySelector('.pv-cm');
      src.querySelector('.pv-copy').onclick = (e) => { navigator.clipboard?.writeText(r.code); e.target.textContent = 'Copied'; setTimeout(() => (e.target.textContent = 'Copy'), 1200); };
      loadCM().then(({ createAmpleEditor }) => { host.style.height = Math.min(380, r.code.split('\n').length * 18 + 16) + 'px'; const m = {}; createAmpleEditor(host, m, r.code); m.readOnly = true; }).catch(() => { host.innerHTML = '<pre><code>' + escH(r.code) + '</code></pre>'; }); };
    cbtn.onclick = () => { src.hidden = !src.hidden; cbtn.setAttribute('aria-expanded', String(!src.hidden)); refreshCode(); };
    const stop = () => { if (node) { try { node.onended = null; node.stop(); } catch {} node = null; } cancelAnimationFrame(raf); play.textContent = '▶'; el.classList.remove('playing'); if (A.live.get(key)?.stop === stop) A.live.delete(key); A.set.delete(stop); if (!A.set.size) { A.t0 = null; A.sticky = false; } clearTimeout(qt); A.mix(); el.classList.remove('queued'); window.level = 0; document.documentElement.style.setProperty('--lvl', 0); document.dispatchEvent(new CustomEvent('ample-preview', { detail: { playing: false } })); draw(cv, buf, 0, false); };
    const load = async () => { el.classList.add('loading'); buf = await render(spec, mode === 'wet'); el.classList.remove('loading'); if (buf._real) { const sm = el.querySelector('.pv-meta small'); if (sm) sm.textContent = 'Rendered by AMPLE from the code below.'; } draw(cv, buf, 0, false); };
    const reg = () => A.live.set(key, { el, stop, cancel: () => cancelAnimationFrame(raf), state: () => ({ node, t0, buf, mode }) });
    const start = async (from, join) => {
      const a = A.get(), group = join && A.set.size > 0; while (A.set.size >= MAX_LAYERS) A.set.keys().next().value(); /* at most MAX_LAYERS sounds at once: the oldest one stops, so the page never gets noisy */ if (!group) [...A.set.keys()].forEach((s) => s()); if (!buf) await load();
      node = a.ctx.createBufferSource(); node.buffer = buf; node.connect(a.master); let off = (from || 0) * buf.duration, when = 0;
      if (group && A.t0 != null) { // join the running timeline on the next beat (at most 0.5 s wait), at the same position in the loop
        const now = a.ctx.currentTime; when = A.t0 + Math.ceil((now - A.t0 + 0.03) / 0.5) * 0.5; off = (when - A.t0) % buf.duration; t0 = A.t0; node.loop = true;
        el.classList.add('queued'); qt = setTimeout(() => el.classList.remove('queued'), Math.max(0, (when - now) * 1000)); A.set.forEach((lp) => lp());
      } else { t0 = a.ctx.currentTime - off; A.t0 = t0; node.onended = stop; if (A.sticky) node.loop = true; }
      node.start(when, off); A.set.set(stop, null); A.mix(); A.set.set(stop, () => { if (node) { node.loop = true; node.onended = null; } }); play.textContent = '❚❚'; el.classList.add('playing'); reg(); document.dispatchEvent(new CustomEvent('ample-preview', { detail: { playing: true } }));
      tick();
    };
    const tick = () => { const a = A.get(), p = (((a.ctx.currentTime - t0) / buf.duration) % 1 + 1) % 1, lv = A.level(); window.level = Math.min(0.5, lv * 0.6); document.documentElement.style.setProperty("--lvl", Math.min(1, lv).toFixed(3)); draw(cv, buf, Math.min(1, p), true, Math.min(1, lv)); raf = requestAnimationFrame(tick); };
    /* A plain click layers this sound over the others (they stay in time with each other) and keeps it playing, also over page changes; clicking a playing sound stops that one. No modifier key is needed. */
    play.style.cssText += ';touch-action:manipulation';
    play.title = 'Play'; play.onclick = () => { if (node) return stop(); A.sticky = true; start(0, true); };
    cv.onclick = () => play.onclick(); /* the waveform is a second play/stop button */
    el.querySelectorAll('.pv-ab button').forEach((b) => (b.onclick = async () => { const was = !!node, pos = node ? (A.ctx.currentTime - t0) / buf.duration : 0; mode = b.dataset.m; refreshCode(); el.querySelectorAll('.pv-ab button').forEach((x) => x.classList.toggle('on', x === b)); if (node) stop(); buf = null; await load(); if (was) start(pos, A.set.size > 0); }));
    /* back on a page where this sound is still playing (started earlier, it keeps going over page changes): take it over, show the progress and a stop button again */
    const ex = A.live.get(key); if (ex && ex.stop !== stop && !ex.el.isConnected) { const s = ex.state(); if (s.node) { ex.cancel(); A.set.delete(ex.stop); node = s.node; buf = s.buf; t0 = s.t0; mode = s.mode; node.onended = null; A.set.set(stop, () => { if (node) { node.loop = true; node.onended = null; } }); play.textContent = '❚❚'; el.classList.add('playing'); el.querySelectorAll('.pv-ab button').forEach((x) => x.classList.toggle('on', x.dataset.m === mode)); reg(); tick(); } }
    const io = new IntersectionObserver((es) => { if (es[0].isIntersecting) { io.disconnect(); load(); } }); io.observe(el); // render when it scrolls into view (silent, no audio yet)
    draw(cv, null, 0, false);
  }
  /* clickable keyboard: <div class="kb" data-kb='{"from":60,"to":72,"hi":[60,64,67],"play":"chord"}'> */
  const NN = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'], isB = (n) => [1, 3, 6, 8, 10].includes(n % 12);
  function kTone(n, t0, dur) { const a = A.get(), c = a.ctx, o = c.createOscillator(), g = c.createGain(), now = c.currentTime + t0; o.type = 'triangle'; o.frequency.value = midi(n); g.gain.setValueAtTime(0, now); g.gain.linearRampToValueAtTime(0.28, now + 0.01); g.gain.exponentialRampToValueAtTime(0.0001, now + dur); o.connect(g); g.connect(a.master); o.start(now); o.stop(now + dur + 0.05); }
  function kbHydrate(el) {
    if (el.dataset.ready) return; el.dataset.ready = 1; const s = JSON.parse(el.dataset.kb), from = s.from ?? 60, to = s.to ?? 72, hi = new Set(s.hi || []), whites = []; for (let n = from; n <= to; n++) if (!isB(n)) whites.push(n);
    const W = 100 / whites.length, nm = (n) => NN[n % 12] + (Math.floor(n / 12) - 1); let h = '<div class="kb-keys" role="group" aria-label="Piano keys">';
    whites.forEach((n, i) => (h += '<button type="button" class="wk' + (hi.has(n) ? ' hi' : '') + '" data-n="' + n + '" aria-label="' + nm(n) + '" style="left:' + i * W + '%;width:' + W + '%"><span>' + (s.label === false ? '' : NN[n % 12] + (n % 12 === 0 ? Math.floor(n / 12) - 1 : '')) + '</span></button>'));
    for (let n = from; n <= to; n++) if (isB(n)) { const i = whites.filter((w) => w < n).length; h += '<button type="button" class="bk' + (hi.has(n) ? ' hi' : '') + '" data-n="' + n + '" aria-label="' + nm(n) + '" style="left:' + (i * W - W * 0.3) + '%;width:' + W * 0.6 + '%"></button>'; }
    h += '</div><div class="kb-bar">' + (s.play ? '<button type="button" class="kb-play">▶ Play ' + (s.play === 'chord' ? 'together' : 'in order') + '</button>' : '') + '<span>' + (s.caption || 'Click the keys to hear them.') + '</span></div>'; el.innerHTML = h;
    const flash = (n) => { const k = el.querySelector('[data-n="' + n + '"]'); if (k) { k.classList.add('on'); setTimeout(() => k.classList.remove('on'), 250); } };
    el.querySelectorAll('[data-n]').forEach((k) => (k.onclick = () => { kTone(+k.dataset.n, 0, 1.2); flash(+k.dataset.n); }));
    const pb = el.querySelector('.kb-play'); if (pb) pb.onclick = () => { const ns = [...hi].sort((a, b) => a - b); ns.forEach((n, i) => { const t = s.play === 'chord' ? 0 : i * 0.32; kTone(n, t, s.play === 'chord' ? 1.8 : 0.7); setTimeout(() => flash(n), t * 1000); }); };
  }
  window.AmplePreviews = { render, hydrate: (root) => { (root || document).querySelectorAll('.pv[data-pv]').forEach(hydrate); (root || document).querySelectorAll('.kb[data-kb]').forEach(kbHydrate); (root || document).querySelectorAll('.pv-src[data-code]').forEach(codeHydrate); }, has: (fx) => !!FX[fx] || fx === 'reverse' || fx === 'pitch' };
  window.addEventListener('hashchange', () => { if (!A.sticky) [...A.set.keys()].forEach((s) => s()); });
})();
