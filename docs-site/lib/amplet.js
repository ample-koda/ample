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

  /* The music of the docs (Sound mode): one theme per part of the site. All at 120 bpm, four chords of one bar each (8 s): a soft pad, an
     arpeggio in eighth notes and a bass on beats 1 and 3, the same as the music the page used to make in the browser, now an AMPLE song. */
  const THEMES = [
    { key: '', name: 'Welcome', wave: 'triangle', root: 57, chords: [[0, 4, 7], [5, 9, 12], [7, 11, 14], [0, 4, 7]], arp: [0, 1, 2, 1] },
    { key: 'philosophy', name: 'Always', wave: 'sine', root: 50, chords: [[0, 3, 7], [8, 12, 15], [5, 8, 12], [7, 10, 14]], arp: [0, 2, 1, 2] },
    { key: 'guide', name: 'First steps', wave: 'triangle', root: 60, chords: [[0, 4, 7], [7, 11, 14], [9, 12, 16], [5, 9, 12]], arp: [0, 1, 2, 1] },
    { key: 'studio', name: 'Blocks and cables', wave: 'square', root: 55, chords: [[0, 3, 7], [3, 7, 10], [5, 8, 12], [7, 10, 14]], arp: [0, 1, 2, 0] },
    { key: 'koda', name: 'Little note', wave: 'sine', root: 64, chords: [[0, 4, 7], [2, 5, 9], [4, 7, 11], [0, 4, 7]], arp: [2, 1, 0, 1] },
    { key: 'language', name: 'Syntax in D', wave: 'triangle', root: 62, chords: [[0, 4, 7], [0, 3, 7], [5, 9, 12], [7, 11, 14]], arp: [0, 2, 1, 2] },
    { key: 'blocks', name: 'Signal path', wave: 'sawtooth', root: 52, chords: [[0, 7, 12], [5, 12, 17], [3, 10, 15], [7, 14, 19]], arp: [0, 1, 2, 1] },
    { key: 'components', name: 'Built from parts', wave: 'triangle', root: 59, chords: [[0, 4, 7], [4, 7, 11], [2, 5, 9], [7, 11, 14]], arp: [0, 1, 2, 1] },
    { key: 'playground', name: 'Play', wave: 'square', root: 57, chords: [[0, 4, 7], [5, 9, 12], [7, 11, 14], [9, 12, 16]], arp: [0, 2, 1, 2] },
  ];
  /* The music comes in sets, one per tempo family of the docs sounds (a family is a tempo with its half and its double: 120 also plays with 60 and 240).
     Every set has three songs, so previous and next always have somewhere to go. */
  const THEME_SETS = [
    { bpm: 120, keys: ['', 'guide', 'blocks'] },
    { bpm: 90, keys: ['philosophy', 'language', 'components'] },
    { bpm: 100, keys: ['guide', 'studio', ''] },
    { bpm: 105, keys: ['koda', 'studio', 'philosophy'] },
    { bpm: 140, keys: ['playground', 'blocks', 'studio'] },
    { bpm: 150, keys: ['', 'koda', 'components'] },
  ];
  const sameFamily = (a, b) => [1, 2, 0.5].some((x) => Math.abs(a / b / x - 1) < 0.005);
  const themeSet = (bpm) => THEME_SETS.find((s) => sameFamily(bpm, s.bpm)) || null;
  const themeSpec = (th, bpm) => ({ title: 'Music: ' + th.name + ((bpm || 120) === 120 ? '' : ' (' + bpm + ' bpm)'), src: 'theme', p: { key: th.key, bpm: bpm || 120 } });
  const themeSpecs = () => THEME_SETS.flatMap((s) => s.keys.map((k) => themeSpec(THEMES.find((x) => x.key === k), s.bpm)));

  /* a source -> {tempo, blocks[], last: "name"} */
  function source(spec) {
    const p = spec.p || {}, src = spec.src || 'pad', semis = 0, out = { deps: [] };
    const play = (clipName, ev, sb, bpm, o) => { const c = clip(ev, sb, o || {}); out.tempo = bpm; out.blocks = [midiClip(clipName, c), block(o && o.type || 'synth', o && o.synth || 'voice', 'at 1 for ' + c.bars, [...(o && o.type === 'bass' ? [] : synthLines(o.sy)), 'from ' + clipName + '.notes -> notes'])]; out.last = o && o.synth || 'voice'; out.bars = c.bars; return out; };
    const SEQ = (notes, st, wave, env, gain, extra) => { const g = grid(st); return play('tune', seqEvents(notes, 1, null), g.sb, g.bpm, { type: 'synth', synth: 'voice', sy: { wave, env, gain }, ...(extra || {}) }); };
    switch (src) {
      case 'theme': { // the music of a part of the site (THEMES): a song of at least 60 s with intro, verses, choruses, a bridge and an outro
        const th = THEMES.find((x) => x.key === (p.key ?? '')) || THEMES[0], bpm = p.bpm || 120, C = th.chords;
        const turn = [C[2], C[3], C[0], C[1]], bridge = [C[3], C[1], C[3], C[2]];
        const section = (name, chords, bars, o) => ({ name, chords, bars, ...o });
        // the plan of the song; a verse and a chorus are 8 bars (the chords twice), the rest 4
        let plan = [section('intro', C, 4, { pad: 1 }), section('verse', C, 8, { pad: 1, arp: 'half', bass: 1 }), section('chorus', turn, 8, { pad: 1, arp: 'full', bass: 1, lead: 1, bell: 1 }),
          section('verse', C, 8, { pad: 1, arp: 'alt', bass: 1, bell: 1 }), section('bridge', bridge, 4, { pad: 1, bell: 1, lead: 2 }), section('chorus', turn, 8, { pad: 1, arp: 'full', bass: 1, lead: 1, bell: 1, up: 12 }),
          section('outro', C, 4, { pad: 1, bass: 2 })];
        const barSec = (4 * 60) / bpm; while (plan.reduce((s, x) => s + x.bars, 0) * barSec < 60) plan.splice(plan.length - 1, 0, section('chorus', turn, 8, { pad: 1, arp: 'full', bass: 1, lead: 1, bell: 1, up: 12 })); // a slow tempo needs no more, a fast one gets another chorus
        const clipOf = (ev) => clip(ev, 1, { forceNotes: true });
        const total = plan.reduce((s, x) => s + x.bars, 0), INSTR = ['pad', 'arp', 'bass', 'bell', 'lead'];
        let bar = 0, seed = [...(th.key || 'welcome')].reduce((h, ch) => (h * 31 + ch.charCodeAt(0)) >>> 0, 7);
        const rnd = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296);
        const leadShape = [0, 2, 1, 2, 0, 1, 2, 2].map(() => Math.floor(rnd() * 3)); // one melody shape per song, repeated with the chords of each bar
        // every section gets its own notes per instrument (relative to the start of the section), so an instrument only has a clip where it plays
        plan.forEach((s, si) => {
          s.start = bar + 1; s.loc = { pad: [], arp: [], bass: [], bell: [], lead: [] };
          for (let i = 0; i < s.bars; i++, bar++) {
            const c = s.chords[i % s.chords.length], at = i * 4, L = s.loc;
            if (s.pad) L.pad.push({ at, len: 4, pitches: c.map((x) => th.root - 12 + x), vel: null });
            if (s.arp) { const steps = s.arp === 'half' ? [0, 2, 4, 6] : [0, 1, 2, 3, 4, 5, 6, 7]; const order = s.arp === 'alt' ? [2, 1, 0, 1] : th.arp; for (const k of steps) L.arp.push({ at: at + k * 0.5, len: s.arp === 'half' ? 0.9 : 0.45, pitches: [th.root + 12 + (s.up || 0) + c[order[k % 4]]], vel: null }); }
            if (s.bass === 1) L.bass.push({ at, len: 1.5, pitches: [th.root - 24 + c[0]], vel: null }, { at: at + 2, len: 1.5, pitches: [th.root - 24 + c[0]], vel: null });
            if (s.bass === 2) L.bass.push({ at, len: 3.5, pitches: [th.root - 24 + c[0]], vel: null });
            if (s.bell && i % 2 === 1) L.bell.push({ at: at + 3, len: 1, pitches: [th.root + 24 + c[2]], vel: null });
            if (s.lead === 1) [0, 1, 1.5, 2.5].forEach((b, k) => L.lead.push({ at: at + b, len: k === 3 ? 1.5 : 0.5, pitches: [th.root + 12 + c[leadShape[(i * 4 + k) % 8]]], vel: null }));
            if (s.lead === 2) L.lead.push({ at: at, len: 3.5, pitches: [th.root + 12 + c[i % 3]], vel: null });
          }
        });
        // a section name that comes back is numbered: verse1, verse2, chorus1, chorus2 ...
        const seen = {}, times = {}; plan.forEach((s) => { times[s.name] = (times[s.name] || 0) + 1; });
        plan.forEach((s) => { seen[s.name] = (seen[s.name] || 0) + 1; s.label = s.name + (times[s.name] > 1 ? seen[s.name] : ''); s.end = s.start + s.bars - 1; });
        const INFO = {
          pad: ['PAD', 'soft chords, one chord a bar: the floor under everything', ['wave sine', 'attack 120 ms', 'release 900 ms', 'gain -16 dB']],
          arp: ['ARPEGGIO', 'the chord notes one by one in eighth notes (half as many in the first verse): the movement', ['wave ' + th.wave, 'attack 10 ms', 'release 200 ms', 'cutoff 4000 Hz', 'gain -18 dB']],
          bass: ['BASS', 'the root of every chord, low down: the weight', ['wave sine', 'attack 5 ms', 'release 150 ms', 'gain -12 dB']],
          bell: ['BELL', 'a short high note now and then: the sparkle', ['wave sine', 'attack 2 ms', 'decay 400 ms', 'sustain 0', 'release 300 ms', 'gain -22 dB']],
          lead: ['LEAD', 'the melody of the song, on top of the chords', ['wave triangle', 'attack 15 ms', 'release 250 ms', 'cutoff 5000 Hz', 'gain -17 dB']],
        };
        const inRange = (s) => (s.start === s.end ? 'bar ' + s.start : 'bars ' + s.start + '-' + s.end);
        out.tempo = bpm; out.bars = total;
        out.blocks = [
          '# ' + th.name + ': ' + plan.map((s) => s.label + ' ' + s.bars).join(', ') + ' (bars), ' + total + ' bars at ' + bpm + ' bpm\n'
          + '# The song is built per instrument, and every instrument per section: a clip of notes for each section where it plays (for example pad_' + plan[0].label + '),\n'
          + '# joined into one line of notes per instrument (pad_notes) that feeds the instrument. An instrument that is not in a section has no clip there.',
        ];
        for (const k of INSTR) {
          const parts = plan.filter((s) => s.loc[k].length); if (!parts.length) continue;
          const [nm, what, lines] = INFO[k];
          let intro = '# ' + nm + ': ' + what + '.\n# Plays in: ' + parts.map((s) => s.label + ' (' + inRange(s) + ')').join(', ') + '.\n';
          for (const s of parts) { const c = clipOf(s.loc[k]); out.blocks.push(intro + '# The ' + k + ' in the ' + s.label + ' (' + inRange(s) + '); it is read by ' + k + '_notes below.\n' + midiClip(k + '_' + s.label, { ...c, bars: s.bars })); intro = ''; }
          const expr = parts.map((s, j) => k + '_' + s.label + '.notes' + (s.start > 1 ? ' |> shift(' + (s.start - 1) + 'bars)' : '')).reduce((acc, e, j) => (j === 0 ? e : acc + ' |> merge(' + e + ')'));
          out.blocks.push('# The notes of the ' + k + ' over the whole song: its sections one after the other (merge puts them together, shift moves each one to its bar).\n' + block('midi-clip', k + '_notes', 'at 1 for ' + total, ['notes ' + expr]));
          out.blocks.push(block('synth', k, 'at 1 for ' + total, [...lines, 'from ' + k + '_notes.notes -> notes']));
        }
        const live = INSTR.filter((k) => plan.some((s) => s.loc[k].length));
        out.blocks.push('# The band: every instrument into one mixer.\n' + block('mixer', 'band', '', ['gains ' + live.map(() => '0').join(' '), ...live.map((k, j) => 'from ' + k + '.audio -> audio' + (j + 1))]));
        out.last = 'band'; return out;
      }
      case 'tone': return SEQ([69], 8, 'sine', 'attack 20 ms', -12, { forceNotes: false }) && (() => { out.tempo = 120; out.blocks[0] = midiClip('tune', { bars: 2, lines: ['melody "a4:8"'] }); out.blocks[1] = block('synth', 'voice', 'at 1 for 2', ['wave sine', 'sustain 100', 'gain -12 dB', 'from tune.notes -> notes']); out.bars = 2; return out; })();
      case 'seq': { const g = grid(p.step ?? 0.5), ev = seqEvents(p.notes || [60], p.len && p.len < 1 ? p.len : 1, p.vel); return play('tune', ev, g.sb, g.bpm, { type: 'synth', synth: 'voice', sy: { wave: WAVE[p.wave || 'triangle'], env: 'attack 8 ms\n  decay 300 ms\n  sustain 40\n  release 200 ms', gain: -8 } }); }
      case 'chords': { const g = grid((p.step ?? 1) / 4 * 4), st = p.step ?? 1; let sb = 1, bpm = Math.round(60 * sb / st); for (const s of [4, 2, 1]) { const b = Math.round(60 * s / st); if (b >= 60 && b <= 180) { sb = s; bpm = b; break; } } const ev = (p.chords || [[60, 64, 67]]).map((ch, i) => ({ at: i, len: 1, pitches: ch, vel: null })); return play('chords', ev, sb, bpm, { type: 'synth', synth: 'voice', sy: { wave: WAVE[p.wave || 'sawtooth'], env: 'attack 80 ms\n  release 300 ms', cutoff: 6000, gain: -12 } }); }
      case 'pad': { const c = { bars: 2, lines: ['chords Am F'] }; out.tempo = 120; out.bars = 2; out.blocks = [midiClip('chords', c), block('synth', 'pad', 'at 1 for 2', ['wave saw', 'attack 250 ms', 'release 600 ms', 'cutoff 6000 Hz', 'gain -12 dB', 'from chords.notes -> notes'])]; out.last = 'pad'; return out; }
      case 'pluck': return SEQ([69, 72, 76, 72, 69, 64, 67, 72], 0.5, 'triangle', 'attack 4 ms\n  decay 250 ms\n  sustain 0\n  release 150 ms', -8);
      case 'arp': return SEQ([57, 60, 64, 69, 64, 60, 57, 60, 53, 57, 60, 65, 60, 57, 53, 57], 0.25, 'square', 'attack 3 ms\n  decay 120 ms\n  sustain 0\n  release 60 ms', -16);
      case 'bass': { const g = grid(0.5); return play('line', seqEvents([45, 45, 48, 45, 41, 41, 43, 45], 1, null), g.sb, g.bpm, { type: 'bass', synth: 'low' }); }
      case 'noise': out.tempo = 120; out.bars = 2; out.blocks = [midiClip('tune', { bars: 2, lines: ['melody "c4:8"'] }), block('synth', 'hiss', 'at 1 for 2', ['wave noise', 'attack 5 ms', 'sustain 100', 'gain -18 dB', 'from tune.notes -> notes'])]; out.last = 'hiss'; return out;
      case 'rhythm': { const bpm = p.bpm || 120, bars = p.bars || 2, bpb = beatsPerBar(p), ev = rhythmEvents(p).sort((a, b) => a.at - b.at || a.pitches[0] - b.pitches[0]), c = clip(ev, 1, { forceNotes: true, bpb }); c.bars = bars; out.tempo = bpm; out.bars = bars; out.bpb = bpb; if (p.meter) out.meter = p.meter; out.blocks = [midiClip('rhythm', c), block('synth', 'blip', 'at 1 for ' + bars, ['wave sine', 'attack 1 ms', 'decay 70 ms', 'sustain 0', 'release 40 ms', 'gain -8 dB', 'from rhythm.notes -> notes'])]; out.last = 'blip'; return out; }
      case 'meter': { const beats = p.beats || 4, bpm0 = p.bpm || 100, sub = p.sub || 1, unit = bpm0 * sub, ev = []; const per = beats * sub, step = (4 * per) / (function g(a, b) { return b ? g(b, a % b) : a; })(4, per), total = Math.max(step, Math.round((4 * unit / 60) / step) * step); for (let i = 0; i < total; i++) ev.push({ at: i, len: 1, pitches: [i % (beats * sub) === 0 ? 96 : 91], vel: i % (beats * sub) === 0 ? 1.2 : i % sub === 0 ? 0.8 : 0.45 }); const c = clip(ev.map((e) => ({ ...e, vel: Math.min(e.vel, 1.27) })), 1, { forceNotes: true }); out.tempo = Math.min(300, unit); out.bars = c.bars; out.blocks = [midiClip('ticks', c), block('synth', 'click', 'at 1 for ' + c.bars, ['wave sine', 'attack 1 ms', 'decay 30 ms', 'sustain 0', 'release 20 ms', 'gain -10 dB', 'from ticks.notes -> notes'])]; out.last = 'click'; return out; }
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

  /* Every song of the docs is written to be played with: each block gets a short explanation and one or two tips ("try X instead of Y to Z"),
     as # comment lines above it. Comments change nothing in the sound, so a song that only got comments needs no new render. */
  const DOCS = {
    'midi-clip': ['A clip of notes: it makes no sound itself, it feeds the notes to an instrument.', 'Each note is bar:beat:length:pitch:velocity (beat and length in quarter notes, pitch 60 is middle C, velocity 1 to 127).', [['notes', 'tip: raise a pitch by 12 to play that note an octave higher, or by 7 for a fifth.'], ['melody', 'tip: in a melody, a note is name:length (c4:1 is middle C for one beat); r:1 is a rest.'], ['chords', 'tip: try Am F C G instead of the chords here; Am.F plays two chords in one bar.']]],
    synth: ['An instrument: it turns the notes it gets into sound with an oscillator.', 'wave is the shape of the tone, attack and release shape its start and end, cutoff takes away high frequencies.', [['wave', 'tip: try "wave saw" for a brighter, buzzier tone, or "wave sine" for a soft, round one.'], ['attack', 'tip: try "attack 400 ms" to let each note fade in slowly, like a string section.'], ['cutoff', 'tip: try "cutoff 1200 Hz" for a darker, warmer sound.'], ['gain', 'tip: gain is the level in dB; -6 dB is about half as loud.']]],
    bass: ['The built-in bass: low notes with a short, round sound.', '', [['gain', 'tip: try "gain -3 dB" to bring the bass forward in the mix.']]],
    drums: ['A drum machine: each row of the pattern is one sound, one character per sixteenth note.', 'x is a hit, X a loud hit, o a soft one, . is silence; kit builtin uses the sounds that come with AMPLE.', [['pattern', 'tip: try "kick:x...x...x...x..." for four on the floor, or move one x to shift the groove; 32 steps make a pattern of two bars.'], ['steps', 'tip: "steps 8" makes every step twice as long, so the same pattern plays at half the speed, whatever the tempo is.'], ['swing', 'tip: add "swing 20" to make the hats shuffle.']]],
    master: ['The master: what reaches it is what you hear. Every song has exactly one.', '', []],
    mixer: ['A mixer: several sounds in, one out. gains sets the level of each input in dB.', '', [['gains', 'tip: change one number in gains to -6 to make that input softer.']]],
    volume: ['A volume control for what goes through it.', '', [['gain', 'tip: try a lower gain to make this part quieter; enabled off switches it off.']]],
    reverb: ['A reverb: the sound of a room after the sound itself.', 'mix is how much room you hear, decay how long the room rings.', [['mix', 'tip: try "mix 80%" for a big hall, or "mix 15%" for just a touch of space.'], ['decay', 'tip: try "decay 90%" for a cathedral, "decay 20%" for a small room.']]],
    delay: ['A delay: echoes of the sound, a little later each time.', 'time is the gap between echoes, feedback how many come back.', [['time', 'tip: try "time 375 ms" (a dotted eighth at 120 bpm) for a rhythmic echo.'], ['feedback', 'tip: raise feedback for more echoes; at "feedback 80%" they keep going for a long time.']]],
    'delay-llama': ['The Delay Llama: a singing voice that sings the notes it gets.', 'mouth opens and closes the vowel, unison stacks several voices.', [['mouth', 'tip: try a curve like "mouth auto(1:0, 3:100)" to open the mouth slowly.'], ['unison', 'tip: try "unison 4" for a choir of llamas.']]],
    vocoder: ['A vocoder: one sound (the modulator) shapes the tone of another (the carrier).', '', [['bands', 'tip: fewer bands sound more robotic, more bands more like the voice.']]],
    eq: ['An equalizer: louder or softer in certain frequency ranges.', '', [['low', 'tip: try a lower low to take mud out of the sound.'], ['high', 'tip: raise high a little for more air and sparkle.']]],
    filter: ['A filter: it lets some frequencies through and takes others away.', 'cutoff is where the filter starts, resonance adds a peak there.', [['cutoff', 'tip: try a curve "cutoff auto(1:200, 3:8000)" for a filter sweep that opens up.'], ['resonance', 'tip: raise resonance for a sharper, squelchy sound.']]],
    compressor: ['A compressor: it makes loud parts softer, so the sound is more even and punchy.', '', [['threshold', 'tip: lower threshold to compress more; the sound gets denser.'], ['ratio', 'tip: try "ratio 8" for a strong, squashed sound.']]],
    distortion: ['A distortion: it drives the sound until it breaks up, for grit and growl.', '', [['drive', 'tip: try a higher drive for a dirtier sound, a lower one for warmth.']]],
    bitcrusher: ['A bitcrusher: fewer bits and a lower sample rate, for a lo-fi, digital sound.', '', [['bits', 'tip: try "bits 4" for the sound of an old video game.']]],
    tremolo: ['A tremolo: the level goes up and down, in a steady rhythm.', '', [['rate', 'tip: try a rate that matches the beat for a pulsing, rhythmic sound.'], ['depth', 'tip: depth 100 chops the sound fully on and off.']]],
    pan: ['A panner: places the sound left or right.', '', [['pan', 'tip: pan -32 is fully left, 32 fully right; try a curve like "pan auto(1:-32, 3:32)" to move it across.']]],
    pitch: ['A pitch shifter: higher or lower without changing the speed.', '', [['semitones', 'tip: try 12 for an octave up, -12 for an octave down.']]],
    width: ['A stereo width control: narrower towards mono, or wider than it was.', '', [['width', 'tip: try "width 150" for a wide sound, "width 0" for mono.']]],
    duck: ['A ducker: one sound gets softer whenever another one plays (sidechain).', '', [['depth', 'tip: raise depth for a stronger pumping effect.']]],
    chorus: ['A chorus: copies of the sound, slightly out of tune, for a wide shimmer.', '', [['mix', 'tip: try a lower mix for a subtle shimmer.']]],
    flanger: ['A flanger: a short, moving delay that sweeps like a jet.', '', [['period', 'tip: try a longer period for a slow, swooshing sweep.']]],
    phaser: ['A phaser: a moving notch in the sound, for a swirling effect.', '', [['period', 'tip: try a shorter period for a fast, bubbly sound.']]],
    reverse: ['Reverse: the sound plays backwards.', '', []],
  };
  function annotate(code) {
    const lines = String(code).split('\n'), out = [];
    for (let i = 0; i < lines.length; i++) {
      const ln = lines[i];
      if (/^composition\s/.test(ln)) {
        const tempo = (/\btempo\s+([\d.]+)/.exec(ln) || [])[1];
        out.push('# The composition line: the name of the song, the AMPLE version it is written for (ample 1), the tempo in beats per minute and the master seed.', '# tip: change the tempo' + (tempo ? ' (' + tempo + ')' : '') + ' to hear it faster or slower; the notes keep their place in the bars.', ln);
        continue;
      }
      const m = /^([a-z][\w-]*)\s+([A-Za-z_]\w*)\b.*\{\s*$/.exec(ln);
      if (m && DOCS[m[1]] && !(out.length && /^#/.test(out[out.length - 1]))) {
        let j = i + 1; const body = []; while (j < lines.length && !/^\}/.test(lines[j])) body.push(lines[j++]);
        const [what, how, tips] = DOCS[m[1]], params = new Set(body.map((b) => (/^\s*([a-z][\w-]*)/.exec(b) || [])[1]).filter(Boolean));
        out.push('# ' + m[1] + ' "' + m[2] + '": ' + what);
        if (how && (m[1] !== 'midi-clip' || params.has('notes'))) out.push('#   ' + how);
        for (const [p, tip] of tips.filter(([p]) => params.has(p)).slice(0, 2)) out.push('# ' + tip);
      }
      out.push(ln);
    }
    return out.join('\n');
  }
  function specToAmplet(spec, wet) {
    wet = wet !== false; const s = source(spec); const keepTempo = spec.src === 'meter' || spec.src === 'theme'; /* every sound lasts exactly 2, 4 or 8 s (the nearest), so layered previews can share one loop: the tempo is fitted, the notes keep their place in the bars. A tempo or meter click (src meter: the tempo is what the page teaches) keeps its tempo and lasts its whole bars; a beat or a rhythm is fitted like the rest, so it can be layered. */ if (keepTempo) { s.seconds = +(s.bars * 60 * (s.bpb || 4) / s.tempo).toFixed(6); } else { const bpb = s.bpb || 4, nat = s.bars * 60 * bpb / s.tempo, ok = [2, 4, 8].filter((T) => { const t = s.bars * 60 * bpb / T; return t >= 30 && t <= 300; }); s.seconds = ok.reduce((a, b) => (Math.abs(Math.log(b / nat)) < Math.abs(Math.log(a / nat)) ? b : a)); s.tempo = +(s.bars * 60 * bpb / s.seconds).toFixed(3); }
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
    return { code: annotate(code), deps: s.deps, bpm: s.tempo, bars: s.bars, seconds: s.seconds, id: hash(spec, wet) + (wet ? '' : '-dry') };
  }
  /** A rhythm preview: layers of hits (positions in quarter-note beats, repeated every p.cycle beats) over p.bars bars. Used by the AMPLE code and by the browser stand-in, so both play the same hits. */
  /** quarter-note beats in one bar of p.meter ("3/4" -> 3, "6/8" -> 3, "7/8" -> 3.5); 4 without a meter */
  function beatsPerBar(p) { const m = String(p.meter || '').split('/').map(Number), ok = m.length === 2 && m[0] > 0 && m[1] > 0; return ok ? (m[0] * 4) / m[1] : 4; }
  function rhythmEvents(p) { const bpb = beatsPerBar(p), total = (p.bars || 2) * bpb, cyc = p.cycle || bpb, ev = []; for (const L of p.layers || []) for (let o = 0; o < total - 1e-9; o += cyc) for (const a of L.at) { const b = +(o + a).toFixed(3); if (b < total - 1e-9) ev.push({ at: b, len: L.len || 0.25, pitches: [L.n || 76], vel: Math.min(1.2, L.v == null ? 1 : L.v) }); } return ev; }
  /** Layered previews -> one AMPLE song. layers: [{code, bpm, bars, seconds, title, gainDb}].
     Every block name gets a prefix l1_, l2_...; each layer's composition line and master block are replaced by a volume block per layer, one mixer and one master.
     The song lasts 8 s at the common tempo T; a shorter layer is repeated inside its clips (the clip and its instruments run the whole length), so the loop stays continuous. */
  function combineAmplets(layers) {
    const tempoOf = (L) => { const m = /\btempo\s+([\d.]+)/.exec(L.code || ''); return m ? +m[1] : +L.bpm || 120; };
    const tempos = layers.map(tempoOf), count = {};
    tempos.forEach((t) => (count[t] = (count[t] || 0) + 1));
    // the song runs at the fastest tempo: a slower layer (half, a quarter) is slowed down: its notes with stretch, its drums with fewer steps in a bar (steps 8 instead of 16: a step lasts as long as it did)
    // (the tempo of the song is the next line)
    const T = Math.max(...tempos) || 120;
    const TOTAL = (() => { /* the shortest length in which every layer's loop comes round again (the player only layers sounds that meet within 16 s), counted in beats so that a layer of 1.5 bars counts */ const g = (a, b) => (b ? g(b, a % b) : a); let t = 4; for (const L of layers) { const lt = tempoOf(L), secs = +L.seconds || (L.bars ? (L.bars * 240) / lt : 4), own = Math.max(1, Math.round((secs * T) / 60)); t = (t * own) / g(t, own); } const musicBars = Math.max(0, ...layers.filter((L) => L.music).map((L) => Math.round(((+L.seconds || 0) * T) / 240))); return Math.max(1, Math.min(Math.ceil(t / 4), Math.max(Math.round((16 * T) / 240), musicBars))); })();
    const parse = (code) => { // -> [{kind:'block', type, name, head, body[]} | {kind:'line', text}]
      const out = [], lines = String(code).split('\n'); let cur = null;
      for (const ln of lines) {
        if (cur) { if (/^\}\s*$/.test(ln)) { out.push(cur); cur = null; } else cur.body.push(ln); continue; }
        const m = /^([a-z][\w-]*)\s+([A-Za-z_][\w]*)\s*(.*?)\s*\{\s*$/.exec(ln);
        if (m && m[1] !== 'composition') cur = { kind: 'block', type: m[1], name: m[2], head: m[3], body: [] };
        else out.push({ kind: 'line', text: ln });
      }
      return out;
    };
    /* A song whose clips are built from other clips (notes pad_intro.notes |> shift(4bars) |> merge(...), like the music) is written out as plain notes first: the layers
       below are repeated and slowed down note by note, which needs the notes themselves. The clips it was built from, when nothing else uses them, are left out. */
    const flatten = (items) => {
      const blocks = items.filter((b) => b.kind === 'block' && b.type === 'midi-clip'), byName = new Map(blocks.map((b) => [b.name, b]));
      const lineOf = (b) => { const l = b.body.find((x) => /^\s*notes\s/.test(x)); return l ? l.replace(/^\s*notes\s+/, '').trim() : ''; };
      const composed = blocks.filter((b) => /^[A-Za-z_]\w*\.notes\b/.test(lineOf(b)));
      if (!composed.length) return items;
      const split = (s, sep) => { const out = []; let d = 0, last = 0; for (let k = 0; k < s.length; k++) { if (s[k] === '(') d++; else if (s[k] === ')') d--; else if (d === 0 && s.startsWith(sep, k)) { out.push(s.slice(last, k)); last = k + sep.length; k += sep.length - 1; } } out.push(s.slice(last)); return out.map((x) => x.trim()); };
      const events = (value) => value.trim().split(/\s+/).filter(Boolean).map((tok) => { const [main, ...rest] = tok.split(';'), p = main.split(':'); if (p.length < 5) throw new Error('note'); return { at: (+p[0] - 1) * 4 + +p[1], len: +p[2], tail: p.slice(3), extra: rest.length ? ';' + rest.join(';') : '' }; });
      const ev = (expr) => {
        const parts = split(expr, '|>'), head = /^([A-Za-z_]\w*)\.notes$/.exec(parts[0]), src = head && byName.get(head[1]);
        if (!src) throw new Error('source');
        let list = events(lineOf(src));
        for (const step of parts.slice(1)) {
          const m = /^([a-z]+)\((.*)\)$/s.exec(step), arg = m ? m[2].trim() : '';
          if (!m) throw new Error('step');
          if (m[1] === 'shift') { const a = /^([+-]?[\d.]+)\s*(bars?|beats?)$/.exec(arg); if (!a) throw new Error('step'); const by = +a[1] * (a[2][0] === 'b' && a[2][1] === 'a' ? 4 : 1); list = list.map((e) => ({ ...e, at: e.at + by })); }
          else if (m[1] === 'merge') list = list.concat(ev(arg));
          else throw new Error('step');
        }
        return list;
      };
      const used = new Set(), text = (list) => list.sort((a, b) => a.at - b.at).map((e) => { const bar = Math.floor(e.at / 4 + 1e-9) + 1, beat = +(e.at - (bar - 1) * 4).toFixed(6); return [bar, beat, +e.len.toFixed(6), ...e.tail].join(':') + e.extra; }).join(' ');
      for (const b of composed) {
        const expr = lineOf(b);
        try {
          const out = text(ev(expr)), head = /\bfor\s+(\d+)/.exec(b.head);
          for (const m of expr.matchAll(/([A-Za-z_]\w*)\.notes/g)) used.add(m[1]);
          b.body = ['  notes ' + out, ...(head ? ['  bars ' + head[1]] : [])];
        } catch { /* a step this does not know: the clip stays as it is */ }
      }
      const fromOthers = new Set(); for (const b of items) if (b.kind === 'block') for (const l of b.body) { const m = /^\s*from\s+([A-Za-z_]\w*)\./.exec(l); if (m) fromOthers.add(m[1]); }
      const gone = new Set([...used].filter((n) => !fromOthers.has(n) && !composed.some((c) => c.name === n)));
      const out = [];
      for (const it of items) {
        if (it.kind === 'block' && gone.has(it.name)) { while (out.length && out[out.length - 1].kind === 'line' && (/^\s*#/.test(out[out.length - 1].text) || out[out.length - 1].text.trim() === '')) out.pop(); continue; }
        out.push(it);
      }
      return out;
    };
    const blocksOut = [], volNames = [];
    layers.forEach((L, i) => {
      const P = 'l' + (i + 1) + '_', lt = tempos[i], f = T / lt, items = flatten(parse(L.code)), comments = [];
      const names = new Set(items.filter((b) => b.kind === 'block').map((b) => b.name));
      const ren = (s) => s.replace(/^(\s*from\s+)([A-Za-z_]\w*)(\.)/, (all, a, n, d) => a + (names.has(n) ? P + n : n) + d);
      const masterFrom = [], bodyBlocks = [], firstAt = blocksOut.length, redirect = new Map();
      let lead = []; // the comment lines above a block (its explanation and tips) stay with that block; those above the composition line are left out
      for (const it of items) {
        if (it.kind === 'line') { if (/^\s*#/.test(it.text)) lead.push(it.text.trim()); else if (/^composition\s/.test(it.text)) lead = []; continue; }
        it.lead = lead.map((c, k) => (k === 0 ? c.replace('"' + it.name + '"', '"' + P + it.name + '"') : c)); lead = [];
        if (it.type === 'master') { it.body.forEach((ln) => { const m = /^\s*from\s+([A-Za-z_]\w*)\.(\w+)/.exec(ln); if (m) masterFrom.push(m[1] + '.' + m[2]); }); continue; }
        bodyBlocks.push(it);
      }
      const slow = Math.round(f) > 1 ? Math.round(f) : 1, fast = f < 1 && Math.abs(1 / f - Math.round(1 / f)) < 1e-6 ? Math.round(1 / f) : 0, fac = slow > 1 ? slow : fast ? 1 / fast : 1, scale = 1;
      if (L.music) comments.unshift(
        '# ----------------------------------------------------------------------------',
        '# THE BACKGROUND MUSIC: "' + String(L.title || '').replace(/^Music: /, '') + '", the music of the AMPLE docs that was playing.',
        '# A song of its own: a soft pad, an arpeggio and a bass, four chords of one bar each at ' + lt + ' bpm, looped.',
        '# To switch the music off: remove the # in front of "enabled off" in the block ' + P + 'level below.',
        '# ----------------------------------------------------------------------------');
      if (fast) comments.push(
        '# This layer was at ' + lt + ' bpm and the song runs at ' + T + ' bpm (the drums of the song cannot change speed). So that it sounds as it did, in time with the others,',
        '# its notes are sped up ' + fast + ' times with stretch(' + 1 / fast + ')' + (fast > 1 ? ', its chords are played ' + fast + ' to a bar' : '') + '.');
      if (slow > 1) comments.push(
        '# This layer was at ' + lt + ' bpm and the song runs at ' + T + ' bpm. So that it sounds as it did, in time with the others,',
        '# its notes are slowed down ' + slow + ' times with stretch(' + slow + '), its chords are held ' + slow + ' times as long, and its drums get ' + 16 / slow + ' steps in a bar instead of 16 (a bar is shorter now, a step lasts as long as it did).');
      // the layer's own length in bars at its own tempo (it lasts 2, 4 or 8 s), and how many of its own bars fill the song before the stretch
      const secs = +L.seconds || (L.bars ? (L.bars * 240) / lt : 4), own = Math.max(1, Math.round((secs * lt) / 240)), span = Math.max(1, Math.round(TOTAL / fac)), reps = Math.max(1, Math.round(span / own));
      const isChords = (b) => b.type === 'midi-clip' && b.body.some((l) => /^\s*chords\s/.test(l));
      const isMelody = (b) => b.type === 'midi-clip' && b.body.some((l) => /^\s*melody\s/.test(l)); // a melody line cannot be read by a note pipeline, so a slowed melody gets longer notes instead of a stretch
      const clipNames = new Set(bodyBlocks.filter((b) => b.type === 'midi-clip' && !isChords(b) && !isMelody(b)).map((b) => b.name));
      for (const b of bodyBlocks) {
        let body = b.body.map(ren); const chordsSlow = slow > 1 && isChords(b), chordsFast = fast && isChords(b), melodySlow = fac !== 1 && isMelody(b), clipSpan = chordsSlow || chordsFast || melodySlow ? TOTAL : span;
        if (fac !== 1 && b.type !== 'midi-clip') body = body.map((ln) => ln.replace(/^(\s*from\s+)([A-Za-z_]\w*)\.notes\b/, (all, a, n) => (clipNames.has(n.slice(P.length)) ? a + n + '_slow.notes' : all)));
        if (b.type === 'drums') { // the built-in drums have a step of a bar divided by their steps: a slower layer gets fewer steps in a bar (16 / slow), then the same pattern lasts as long as it did
          body = body.map((ln) => ln.replace(/^(\s*bars\s+)[\d.]+\s*$/, (all, a) => a + TOTAL));
          if (slow > 1 && 16 % slow === 0 && !body.some((ln) => /^\s*steps\s/.test(ln))) { const at = body.findIndex((ln) => /^\s*pattern\s/.test(ln)); if (at >= 0) body.splice(at + 1, 0, '  steps ' + 16 / slow); }
        }
        if (b.type === 'midi-clip') {
          const oldBars = +((/^\s*bars\s+([\d.]+)/m.exec(b.body.join('\n')) || [])[1] || 2), bpb = 4;
          body = body.map((ln) => {
            let m;
            if ((m = /^(\s*)melody\s+"([^"]*)"\s*$/.exec(ln))) {
              const toks = m[2].trim().split(/\s+/).filter(Boolean).map((t) => { const k = t.lastIndexOf(':'); return [t.slice(0, k), +t.slice(k + 1) * scale * (melodySlow ? fac : 1)]; });
              const sum = toks.reduce((a, t) => a + t[1], 0), room = own * bpb * (melodySlow ? fac : 1) - sum; if (room > 1e-6) toks.push(['r', room]);
              const one = toks.map((t) => t[0] + ':' + num(t[1])).join(' ');
              return m[1] + 'melody "' + Array(reps).fill(one).join(' ') + '"';
            }
            if ((m = /^(\s*)notes\s+(.*)$/.exec(ln))) {
              const out = [];
              for (const tok of m[2].trim().split(/\s+/)) {
                const p = tok.split(':'); if (p.length < 4) continue;
                const abs = ((+p[0] - 1) * bpb + +p[1]) * scale, len = +p[2] * scale;
                for (let r = 0; r < reps; r++) { const a = abs + r * own * bpb, bar = Math.floor(a / bpb + 1e-9) + 1, beat = +(a - (bar - 1) * bpb).toFixed(3); out.push([bar, num(beat), num(Math.min(len, bpb - beat)), ...p.slice(3)].join(':')); }
              }
              return m[1] + 'notes ' + out.join(' ');
            }
            if ((m = /^(\s*)chords\s+(.*)$/.exec(ln))) { const per = Math.max(1, Math.round(oldBars / own)); let one = m[2].trim(); if (chordsSlow) one = one.split(/\s+/).map((c) => (slow === 2 && c.includes('.') ? c.split('.').join(' ') : Array(slow).fill(c).join(' '))).join(' '); else if (chordsFast && !one.includes('.')) { const cs = one.split(/\s+/), grouped = []; for (let k = 0; k < cs.length; k += fast) grouped.push(cs.slice(k, k + fast).join('.')); one = grouped.join(' '); } return m[1] + 'chords ' + Array(reps * per).fill(one).join(' '); }
            if ((m = /^(\s*)bars\s+[\d.]+\s*$/.exec(ln))) return m[1] + 'bars ' + clipSpan;
            return ln;
          });
        }
        const head = b.head.replace(/\bat\s+1\s+for\s+[\d.]+/, 'at 1 for ' + (b.type === 'midi-clip' ? (chordsSlow || chordsFast || melodySlow ? TOTAL : span) : TOTAL));
        blocksOut.push((b.lead && b.lead.length ? b.lead.join('\n') + '\n' : '') + b.type + ' ' + P + b.name + (head ? ' ' + head : '') + ' {\n' + body.join('\n') + '\n}');
        if (fac !== 1 && b.type === 'midi-clip' && !chordsSlow && !chordsFast && !melodySlow) blocksOut.push('midi-clip ' + P + b.name + '_slow at 1 for ' + TOTAL + ' {\n  notes ' + P + b.name + '.notes |> stretch(' + fac + ')\n}');
      }
      const vol = P + 'level', g = +(+(L.gainDb ?? 0)).toFixed(2);
      if (comments.length && blocksOut.length > firstAt) blocksOut[firstAt] = comments.join('\n') + '\n' + blocksOut[firstAt]; else if (comments.length) blocksOut.push(comments.join('\n'));
      blocksOut.push('volume ' + vol + ' {\n  gain ' + g + ' dB\n' + (L.music ? '  # enabled off\n' : '') + masterFrom.map((s) => { const [n, port] = s.split('.'); return '  from ' + (redirect.get(n) || (names.has(n) ? P + n : n)) + '.' + port + ' -> audio'; }).join('\n') + '\n}');
      volNames.push(vol);
    });
    const title = layers.map((L) => String(L.title || 'sound').replace(/[^\w ,.'()+\/-]/g, '')).join(' + ');
    const head = 'composition ' + q(title) + ' { ample 1  tempo ' + T + '  master-seed ' + SEED + ' }';
    const mix = block('mixer', 'layers', '', ['gains ' + volNames.map(() => '0').join(' '), ...volNames.map((v, k) => 'from ' + v + '.audio -> audio' + (k + 1))]);
    return annotate([head, '', '# ' + layers.length + ' layered sounds, ' + TOTAL + ' bars at ' + T + ' bpm (' + +((TOTAL * 240) / T).toFixed(2) + ' s, it loops)', '', blocksOut.join('\n\n'), '', mix, '', block('master', 'out', '', ['from layers.audio -> audio'])].join('\n') + '\n'); // the composition line, the volume of each layer, the mixer and the master get their explanation too
  }
  const api = { annotate, THEMES, THEME_SETS, themeSet, themeSpec, themeSpecs, sameFamily, specToAmplet, combineAmplets, hash, chordName, name, rhythmEvents, beatsPerBar };
  if (typeof module !== 'undefined' && module.exports) module.exports = api; else root.AmpletGen = api;
})(typeof window !== 'undefined' ? window : globalThis);
