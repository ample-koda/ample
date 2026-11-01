/* Sound preview spec -> AMPLE song (.amplet), plus a stable id for its rendered audio.
   Works in the browser (window.AmpletGen) and in Node (require). One place decides how every sound in the docs is written in AMPLE,
   so the code that is shown, the code that is rendered to mp3 and the browser stand-in all come from the same spec.
   Language: v1 stream names (audio, notes), checked with `ample check`. Optional dependencies (a drum kit) are returned in `deps`. */

const SEED = 20261017; // the same master seed in every demo, so a copy/paste sounds exactly like the preview (the spec examples use it too)
(function (root) {
  const NN = ['c', 'c#', 'd', 'd#', 'e', 'f', 'f#', 'g', 'g#', 'a', 'a#', 'b'], ROOTS = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];
  const name = (n) => NN[((n % 12) + 12) % 12] + (Math.floor(n / 12) - 1);
  const num = (x) => String(+(+x).toFixed(3));
  const WAVE = { sine: 'sine', triangle: 'triangle', sawtooth: 'saw', saw: 'saw', square: 'square' };
  const CHORDS = { '': [0, 4, 7], m: [0, 3, 7], dim: [0, 3, 6], aug: [0, 4, 8], sus4: [0, 5, 7], 7: [0, 4, 7, 10], maj7: [0, 4, 7, 11], m7: [0, 3, 7, 10] };
  const chordName = (ns) => { const s = [...ns].sort((a, b) => a - b), r = s[0]; if (s[s.length - 1] - r > 12) return null; const iv = s.map((n) => n - r).join(','); for (const [k, v] of Object.entries(CHORDS)) if (v.join(',') === iv) return ROOTS[r % 12] + k; return null; };
  const hash = (spec, wet) => { const t = JSON.stringify([spec.src || 'pad', wet ? spec.fx || 'dry' : spec.fx === 'width' ? 'width-dry' : 'dry', spec.p || {}]); let h = 0x811c9dc5; for (let i = 0; i < t.length; i++) { h ^= t.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; } return h.toString(16).padStart(8, '0'); };
  const q = (s) => '"' + String(s).replace(/["\\]/g, '') + '"';

  /* time grid: pick beats-per-step so that the tempo is a sensible number */
  function grid(st) { for (const sb of [1, 0.5, 0.25, 2, 4]) { const bpm = Math.round(60 * sb / st); if (bpm >= 60 && bpm <= 180) return { sb, bpm }; } return { sb: 1, bpm: Math.max(30, Math.min(300, Math.round(60 / st))) }; }

  /* events: [{at (steps from 0), len (steps), pitches[], vel}] -> clip body (melody, chords or notes lines) */
  function clip(events, sb, opt) {
    const total = Math.max(...events.map((e) => e.at + e.len)), bars = Math.max(1, Math.ceil(total * sb / (opt.bpb || 4) - 1e-9)), BPB = opt.bpb || 4;
    const simple = events.every((e) => e.pitches.length === 1 && e.vel == null) && sb >= 0.25 && !opt.forceNotes;
    if (simple) { // melody "c4:1 d4:1 r:1": names and lengths in beats, rests with r
      let t = 0, out = []; for (const e of events) { if (e.at > t) out.push('r:' + num((e.at - t) * sb)); out.push(name(e.pitches[0]) + ':' + num(e.len * sb)); t = e.at + e.len; }
      return { bars, lines: ['melody ' + q(out.join(' '))] };
    }
    if (events.every((e) => e.pitches.length > 1 && chordName(e.pitches)) && sb === 4 && events.every((e, i) => e.at === i)) return { bars, lines: ['chords ' + events.map((e) => chordName(e.pitches)).join(' ')] };
    const notes = []; // beats are quarter notes, 0-based inside a 4/4 bar; a note must fit in its bar
    for (const e of events) for (const n of e.pitches) { const b0 = e.at * sb, bar = Math.floor(b0 / BPB + 1e-9) + 1, beat = +(b0 - (bar - 1) * BPB).toFixed(3), len = Math.min(e.len * sb, BPB - beat); notes.push(bar + ':' + num(beat) + ':' + num(len) + ':' + n + ':' + Math.round((e.vel == null ? 1 : e.vel) * 100)); }
    const lines = ['notes ' + notes.join(' ')];
    return { bars, lines };
  }
  const seqEvents = (notes, len, vel) => notes.map((n, i) => (n == null ? null : { at: i, len: len || 1, pitches: [n], vel: vel ? vel[i] : null })).filter(Boolean);

  const synthLines = (o) => ['wave ' + o.wave, ...(o.env ? [o.env] : []), ...(o.cutoff ? ['cutoff ' + o.cutoff + ' Hz'] : []), 'gain ' + o.gain + ' dB'];
  const block = (type, nm, head, lines) => type + ' ' + nm + (head ? ' ' + head : '') + ' {\n' + lines.map((l) => '  ' + l).join('\n') + '\n}';
  const midiClip = (nm, c) => block('midi-clip', nm, 'at 1 for ' + c.bars, [...c.lines, 'bars ' + c.bars]);
  const OCT = (n) => n;

  /* a source -> {tempo, blocks[], last: "name"} */
  function source(spec) {
    const p = spec.p || {}, src = spec.src || 'pad', semis = 0, out = { deps: [] };
    const play = (clipName, ev, sb, bpm, o) => { const c = clip(ev, sb, o || {}); out.tempo = bpm; out.blocks = [midiClip(clipName, c), block(o && o.type || 'synth', o && o.synth || 'voice', 'at 1 for ' + c.bars, [...(o && o.type === 'bass' ? [] : synthLines(o.sy)), 'from ' + clipName + '.notes -> notes'])]; out.last = o && o.synth || 'voice'; out.bars = c.bars; return out; };
    const SEQ = (notes, st, wave, env, gain, extra) => { const g = grid(st); return play('tune', seqEvents(notes, 1, null), g.sb, g.bpm, { type: 'synth', synth: 'voice', sy: { wave, env, gain }, ...(extra || {}) }); };
    switch (src) {
      case 'tone': return SEQ([69], 8, 'sine', 'attack 20 ms', -12, { forceNotes: false }) && (() => { out.tempo = 120; out.blocks[0] = midiClip('tune', { bars: 2, lines: ['melody "a4:8"'] }); out.blocks[1] = block('synth', 'voice', 'at 1 for 2', ['wave sine', 'sustain 100', 'gain -12 dB', 'from tune.notes -> notes']); out.bars = 2; return out; })();
      case 'seq': { const g = grid(p.step ?? 0.5), ev = seqEvents(p.notes || [60], p.len && p.len < 1 ? p.len : 1, p.vel); return play('tune', ev, g.sb, g.bpm, { type: 'synth', synth: 'voice', sy: { wave: WAVE[p.wave || 'triangle'], env: 'attack 8 ms\n  decay 300 ms\n  sustain 40\n  release 200 ms', gain: -8 } }); }
      case 'chords': { const g = grid((p.step ?? 1) / 4 * 4), st = p.step ?? 1; let sb = 1, bpm = Math.round(60 * sb / st); for (const s of [4, 2, 1]) { const b = Math.round(60 * s / st); if (b >= 60 && b <= 180) { sb = s; bpm = b; break; } } const ev = (p.chords || [[60, 64, 67]]).map((ch, i) => ({ at: i, len: 1, pitches: ch, vel: null })); return play('chords', ev, sb, bpm, { type: 'synth', synth: 'voice', sy: { wave: WAVE[p.wave || 'sawtooth'], env: 'attack 80 ms\n  release 300 ms', cutoff: 6000, gain: -12 } }); }
      case 'pad': { const c = { bars: 2, lines: ['chords Am F'] }; out.tempo = 120; out.bars = 2; out.blocks = [midiClip('chords', c), block('synth', 'pad', 'at 1 for 2', ['wave saw', 'attack 250 ms', 'release 600 ms', 'cutoff 6000 Hz', 'gain -12 dB', 'from chords.notes -> notes'])]; out.last = 'pad'; return out; }
      case 'pluck': return SEQ([69, 72, 76, 72, 69, 64, 67, 72], 0.5, 'triangle', 'attack 4 ms\n  decay 250 ms\n  sustain 0\n  release 150 ms', -8);
      case 'arp': return SEQ([57, 60, 64, 69, 64, 60, 57, 60, 53, 57, 60, 65, 60, 57, 53, 57], 0.25, 'square', 'attack 3 ms\n  decay 120 ms\n  sustain 0\n  release 60 ms', -16);
      case 'bass': { const g = grid(0.5); return play('line', seqEvents([45, 45, 48, 45, 41, 41, 43, 45], 1, null), g.sb, g.bpm, { type: 'bass', synth: 'low' }); }
      case 'noise': out.tempo = 120; out.bars = 2; out.blocks = [midiClip('tune', { bars: 2, lines: ['melody "c4:8"'] }), block('synth', 'hiss', 'at 1 for 2', ['wave noise', 'attack 5 ms', 'sustain 100', 'gain -18 dB', 'from tune.notes -> notes'])]; out.last = 'hiss'; return out;
      case 'rhythm': { const bpm = p.bpm || 120, bars = p.bars || 2, bpb = beatsPerBar(p), ev = rhythmEvents(p).sort((a, b) => a.at - b.at || a.pitches[0] - b.pitches[0]), c = clip(ev, 1, { forceNotes: true, bpb }); c.bars = bars; out.tempo = bpm; out.bars = bars; out.bpb = bpb; if (p.meter) out.meter = p.meter; out.blocks = [midiClip('rhythm', c), block('synth', 'blip', 'at 1 for ' + bars, ['wave sine', 'attack 1 ms', 'decay 70 ms', 'sustain 0', 'release 40 ms', 'gain -8 dB', 'from rhythm.notes -> notes'])]; out.last = 'blip'; return out; }
      case 'meter': { const beats = p.beats || 4, bpm0 = p.bpm || 100, sub = p.sub || 1, unit = bpm0 * sub, ev = []; const total = Math.ceil(4 * unit / 60); for (let i = 0; i < total; i++) ev.push({ at: i, len: 1, pitches: [i % (beats * sub) === 0 ? 96 : 91], vel: i % (beats * sub) === 0 ? 1.2 : i % sub === 0 ? 0.8 : 0.45 }); const c = clip(ev.map((e) => ({ ...e, vel: Math.min(e.vel, 1.27) })), 1, { forceNotes: true }); out.tempo = Math.min(300, unit); out.bars = c.bars; out.blocks = [midiClip('ticks', c), block('synth', 'click', 'at 1 for ' + c.bars, ['wave sine', 'attack 1 ms', 'decay 30 ms', 'sustain 0', 'release 20 ms', 'gain -10 dB', 'from ticks.notes -> notes'])]; out.last = 'click'; return out; }
      case 'grid': case 'drums': { const g = src === 'drums' ? { kick: 'x.......x.......', snare: '....x.......x...', hat: 'x.x.x.x.x.x.x.x.', bpm: 120 } : { ...p }; const up = (s) => (p.res === 16 || src === 'drums') ? s : s.split('').map((c) => c + '.').join(''); const bpm = g.bpm || 120, lens = ['kick', 'snare', 'hat'].map((k) => (g[k] ? up(g[k]).length : 0)), L = Math.max(...lens, 16), bars = Math.max(1, Math.round(4 * bpm / 240)); const pat = ['kick', 'snare', 'hat'].filter((k) => g[k]).map((k) => k + ':' + up(g[k]).padEnd(L, '.')); out.tempo = bpm; out.bars = bars; out.deps = []; out.blocks = [block('drums', 'beat', 'at 1 for ' + bars, ['kit builtin', 'pattern ' + pat.join(' ')])]; out.last = 'beat'; return out; }
      case 'voice': out.tempo = 120; out.bars = 2; out.blocks = [midiClip('tune', { bars: 2, lines: ['melody "c3:1 c3:1 d#3:1 f3:1 d#3:1 c3:1 d3:1 c3:1"'] }), block('delay-llama', 'llama', 'at 1 for 2', ['vowel auto(1:0, 2:50, 3:100)', 'mouth 70', 'gain -6 dB', 'from tune.notes -> notes'])]; out.last = 'llama'; return out;
      default: return source({ ...spec, src: 'pad' });
    }
  }

  const pct = (x) => Math.round(x * 100) + '%';
  const FXMAP = {
    volume: (p) => ['gain ' + (p.fade ? 'auto(1:0, 3:-60)' : (p.db ?? -6) + ' dB')],
    pan: (p) => p.sweep ? null : ['pan ' + Math.round((p.pan ?? -0.8) * 32)],
    filter: (p) => ['type lowpass', 'cutoff auto(1:' + (p.from ?? 300) + ', 3:' + (p.to ?? 6000) + ')'],
    eq: (p) => { const hz = p.hz ?? 2500, db = p.db ?? 12; return hz <= 500 ? ['low ' + db + ' dB', 'low_hz ' + hz + ' Hz'] : hz < 2500 ? ['mid ' + db + ' dB'] : ['high ' + db + ' dB', 'high_hz ' + hz + ' Hz']; },
    reverb: (p) => ['size 50%', 'decay ' + pct(p.decay ?? 0.6), 'mix ' + pct(p.mix ?? 0.5)],
    delay: (p) => ['time ' + Math.round((p.time ?? 0.375) * 1000) + ' ms', 'feedback ' + pct(p.feedback ?? 0.45), 'mix ' + pct(p.mix ?? 0.5)],
    chorus: (p) => ['depth 5 ms', 'period ' + Math.round(1000 / (p.rate ?? 0.8) * 1.5) + ' ms', 'mix 55'],
    flanger: (p) => ['depth 2 ms', 'period ' + Math.round(1000 / (p.rate ?? 0.3)) + ' ms', 'feedback 60', 'mix 60'],
    phaser: (p) => ['stages 4', 'period ' + Math.round(1000 / (p.rate ?? 0.4)) + ' ms', 'mix 60'],
    tremolo: (p) => ['mode tremolo', 'rate ' + (p.rate ?? 6) + ' Hz', 'depth ' + Math.round((p.depth ?? 0.5) * 100) + ' %'],
    distortion: (p) => ['drive ' + Math.round((p.drive ?? 0.7) * 30) + ' dB', 'mode soft', 'level -6 dB'],
    bitcrusher: (p) => ['bits ' + (p.bits ?? 3), 'mix 100'],
    compressor: (p) => ['threshold ' + (p.threshold ?? -40) + ' dB', 'ratio ' + (p.ratio ?? 12), 'makeup 6 dB'],
    duck: () => ['depth 20 dB', 'release 400 ms', 'pattern x...x...x...x...'],
    width: () => ['width 200'],
    pitch: (p) => ['semitones ' + (p.semitones ?? 7)],
    reverse: () => [],
  };
  const FXBLOCK = { gate: 'tremolo' };

  /** The song for one preview. wet=false gives the dry version (no effect). Returns {code, deps, bpm, bars, id}. */
  function specToAmplet(spec, wet) {
    wet = wet !== false; const s = source(spec); /* every sound lasts exactly 2, 4 or 8 s (the nearest), so layered previews can share one loop: the tempo is fitted, the notes keep their place in the bars */ { const bpb = s.bpb || 4, nat = s.bars * 60 * bpb / s.tempo, ok = [2, 4, 8].filter((T) => { const t = s.bars * 60 * bpb / T; return t >= 30 && t <= 300; }); s.seconds = ok.reduce((a, b) => (Math.abs(Math.log(b / nat)) < Math.abs(Math.log(a / nat)) ? b : a)); s.tempo = +(s.bars * 60 * bpb / s.seconds).toFixed(3); }
    const fx = wet ? spec.fx || 'dry' : 'dry', p = spec.p || {}, blocks = [...s.blocks], notes = [];
    let last = s.last;
    if (spec.fx === 'width') { /* a mono sound has nothing to widen: use two slightly detuned pads, one a little to the left and one to the right (dry and with effect both start from this stereo pad) */ const sy = (nm, d) => block('synth', nm, 'at 1 for 2', ['wave saw', 'detune ' + d, 'attack 250 ms', 'release 600 ms', 'cutoff 6000 Hz', 'gain -15 dB', 'from chords.notes -> notes']); blocks.length = 0; blocks.push(midiClip('chords', { bars: 2, lines: ['chords Am F'] }), sy('pad_a', 0), sy('pad_b', 25), block('pan', 'left_side', '', ['pan -10', 'from pad_a.audio -> audio']), block('pan', 'right_side', '', ['pan 10', 'from pad_b.audio -> audio']), block('mixer', 'blend', '', ['gains 0 0', 'from left_side.audio -> audio1', 'from right_side.audio -> audio2'])); last = 'blend'; notes.push('# two slightly detuned pads, one a little left and one a little right: a stereo sound that the width block can widen'); }
    const add = (type, nm, lines, from, extra) => { blocks.push(block(type, nm, extra || '', [...lines, ...from.map(([f, i]) => 'from ' + f + '.audio -> ' + i)])); last = nm; };
    if (fx !== 'dry') {
      if (fx === 'vocoder') { // a voice speaks through the chord: modulator = a singing llama, carrier = the pad
        const car = source({ src: 'pad' }); blocks.length = 0; blocks.push(...car.blocks, midiClip('speech', { bars: 2, lines: ['melody "c3:1 c3:1 d#3:1 f3:1 d#3:1 c3:1 d3:1 c3:1"'] }), block('delay-llama', 'llama', 'at 1 for 2', ['vowel auto(1:0, 2:50, 3:100)', 'gain -6 dB', 'from speech.notes -> notes']));
        add('vocoder', 'vocoder', ['bands 20', 'mix 100 %'], [['llama', 'modulator'], ['pad', 'carrier']]); notes.push('# the voice (llama) speaks through the chord (pad)');
      } else if (fx === 'pan' && p.sweep) { add('tremolo', 'sweep', ['mode autopan', 'rate 0.5 Hz', 'depth 90 %'], [[last, 'audio']]); }
      else if (fx === 'gate') { add('tremolo', 'chop', ['mode tremolo', 'rate 4 Hz', 'depth 100 %', 'shape square'], [[last, 'audio']]); notes.push('# a square tremolo stands in for a gated pad'); }
      else if (FXMAP[fx]) { const lines = FXMAP[fx](p); if (lines) add(fx, fx, lines, [[last, 'audio']]); }
    }
    const title = (spec.title || 'preview').replace(/[^\w ,.'()+\/-]/g, '');
    const head = 'composition ' + q(title) + ' { ample 1  tempo ' + s.tempo + (s.meter ? '  meter ' + s.meter : '') + '  master-seed ' + SEED + ' }';
    const code = [head, ...(notes.length ? ['', ...notes] : []), '', blocks.join('\n\n'), '', block('master', 'out', '', ['from ' + last + '.audio -> audio'])].join('\n') + '\n';
    return { code, deps: s.deps, bpm: s.tempo, bars: s.bars, seconds: s.seconds, id: hash(spec, wet) + (wet ? '' : '-dry') };
  }
  /** A rhythm preview: layers of hits (positions in quarter-note beats, repeated every p.cycle beats) over p.bars bars. Used by the AMPLE code and by the browser stand-in, so both play the same hits. */
  /** quarter-note beats in one bar of p.meter ("3/4" -> 3, "6/8" -> 3, "7/8" -> 3.5); 4 without a meter */
  function beatsPerBar(p) { const m = String(p.meter || '').split('/').map(Number), ok = m.length === 2 && m[0] > 0 && m[1] > 0; return ok ? (m[0] * 4) / m[1] : 4; }
  function rhythmEvents(p) { const bpb = beatsPerBar(p), total = (p.bars || 2) * bpb, cyc = p.cycle || bpb, ev = []; for (const L of p.layers || []) for (let o = 0; o < total - 1e-9; o += cyc) for (const a of L.at) { const b = +(o + a).toFixed(3); if (b < total - 1e-9) ev.push({ at: b, len: L.len || 0.25, pitches: [L.n || 76], vel: Math.min(1.2, L.v == null ? 1 : L.v) }); } return ev; }
  const api = { specToAmplet, hash, chordName, name, rhythmEvents, beatsPerBar };
  if (typeof module !== 'undefined' && module.exports) module.exports = api; else root.AmpletGen = api;
})(typeof window !== 'undefined' ? window : globalThis);
