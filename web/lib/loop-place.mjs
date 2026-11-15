// Where a looped stretch of audio is placed in a block, with the join of every repeat crossfaded (issue 79). It is the same arithmetic as AudioCut.Place of the server (src/Ample.Audio/Engine/AudioCut.cs):
// the first pass plays plain, from the second pass on the start of a pass is blended with the tail of the previous one; loop_start and loop_end cut an intro that plays once and an outro that plays once at the end;
// both join the loop with the same crossfade. The samples that are blended are the seam. The server keeps whole numbers of 16 bits and rounds every blend; here a sample is a float, so the two differ by half a step at most
// (web/lib/test/loop-place.test.mjs checks that against what the server made).

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

export const FADE_CURVES = ['equal-power', 'linear', 'cosine', 'log'];

/** The curve named in a parameter (empty or unknown: equal-power, the default). */
export const curveOf = (text) => {
  const name = String(text ?? '').trim().replace(/^"|"$/g, '').toLowerCase();
  return FADE_CURVES.includes(name) ? name : 'equal-power';
};

/** The weight of the incoming side at fraction t (0..1) of the overlap and of the outgoing side. Equal-power keeps the level of unrelated material, linear that of related material. */
export function fadeWeights(curve, t) {
  switch (curve) {
    case 'linear': return { in: t, out: 1 - t };
    case 'cosine': return { in: (1 - Math.cos(Math.PI * t)) / 2, out: (1 + Math.cos(Math.PI * t)) / 2 };
    case 'log': return { in: Math.log10(1 + 9 * t), out: Math.log10(1 + 9 * (1 - t)) };
    default: return { in: Math.sin(t * Math.PI / 2), out: Math.cos(t * Math.PI / 2) };
  }
}

/** A length as a count of samples: seconds (3, 3s), beats (2 beats) or bars (1 bar); empty or not a length gives null. */
export function samplesOf(text, rate, samplesPerBar, beatsPerBar = 4) {
  const value = String(text ?? '').trim().replace(/^"|"$/g, '').trim();
  const number = /^[\d.]*/.exec(value)[0];
  const unit = value.slice(number.length).trim().toLowerCase();
  const n = Number.parseFloat(number);
  if (!Number.isFinite(n) || n < 0) return null;
  const samples = unit === 'bar' || unit === 'bars' ? n * samplesPerBar : unit === 'beat' || unit === 'beats' ? n * samplesPerBar / beatsPerBar : unit === '' || unit === 's' ? n * rate : -1;
  return samples < 0 ? null : Math.round(samples);
}

const blend = (outgoing, incoming, w) => clamp(outgoing * w.out + incoming * w.in, -1, 1);

/**
 * Samples at the places offset, offset + 1 and so on (relative to the start of the stretch): silence beyond its end, or repeating it when `loop` is set. `seam`, when given (a Uint8Array as long as `length`),
 * gets a 1 at every sample that was blended. loopFrom and loopTo (in the stretch) cut the intro and the outro.
 */
export function placeLoop(stretch, { offset = 0, length, loop = false, crossfade = 0, curve = 'equal-power', loopFrom = 0, loopTo = -1, seam = null }) {
  const result = new Float32Array(length);
  if (stretch.length === 0) return result;
  if (!loop) {
    for (let i = 0; i < length; i++) {
      const at = offset + i;
      if (at >= 0 && at < stretch.length) result[i] = stretch[at];
    }

    return result;
  }

  const from = clamp(Math.trunc(loopFrom), 0, stretch.length - 1);
  const to = loopTo < 0 ? stretch.length : clamp(Math.trunc(loopTo), from + 1, stretch.length);
  const intro = stretch.subarray(0, from), body = stretch.subarray(from, to), outro = stretch.subarray(to);
  const fade = clamp(Math.trunc(crossfade), 0, Math.floor(body.length / 2));
  const period = body.length - fade;
  const weights = Array.from({ length: fade }, (_, j) => fadeWeights(curve, (j + 0.5) / fade));
  const join = Math.min(fade, intro.length);
  const start = intro.length - join;   // where the first pass of the loop begins: it overlaps the end of the intro
  for (let i = 0; i < length; i++) {
    const at = offset + i;
    if (at < 0) continue;
    if (at < start) { result[i] = intro[at]; continue; }
    const x = at - start;
    if (at < intro.length) {
      // the intro hands over to the first pass of the loop
      result[i] = join > 0 ? blend(intro[at], body[x], weights[Math.floor(x * fade / join)]) : intro[at];
      if (seam && join > 0) seam[i] = 1;
      continue;
    }

    const y = at - start;
    if (fade > 0 && y >= period && y % period < fade) {
      const p = y % period;
      result[i] = blend(body[period + p], body[p], weights[p]);
      if (seam) seam[i] = 1;
    } else result[i] = body[y % period];
  }

  if (outro.length > 0) {
    const outroFade = Math.min(fade, outro.length);
    const from0 = length - outro.length;
    for (let i = Math.max(0, from0); i < length; i++) {
      const j = i - from0;
      result[i] = j < outroFade ? blend(result[i], outro[j], weights[Math.floor(j * fade / Math.max(1, outroFade))]) : outro[j];
      if (seam && j < outroFade) seam[i] = 1;
    }
  }

  return result;
}

/** A whole sound as its two ports: `body` is the sound with the seam silent and `region` only the seam, silent elsewhere; body plus region is the sound, sample for sample. */
export function splitSeam(channels, seam) {
  const mask = (channel, joins) => channel.map((value, i) => ((seam[i] === 1) === joins ? value : 0));
  return { body: channels.map((channel) => mask(channel, false)), region: channels.map((channel) => mask(channel, true)) };
}

/**
 * For the song preview: a region that goes into an effect is the sound that effect works on (the build does the same), so its link is read as the audio input `in` when the effect has none of its own.
 * The links of a region merge stay as they are.
 */
export function regionLinksAsAudio(nodes, links) {
  const tpl = new Map(nodes.map((node) => [node.id, node.tpl]));
  return links.map((link) => {
    if (link.type !== 'region' || link.disabled || tpl.get(link.to) === 'region-merge') return link;
    const hasAudio = links.some((other) => other.to === link.to && other.type === 'audio' && other.inName === 'in' && !other.disabled);
    return hasAudio ? link : { ...link, type: 'audio', inName: 'in' };
  });
}

/**
 * An effect that hangs on the `render` port of another effect (`from eq.render -> render`) works on the sound that effect makes: the build runs the two as one chain from the same source. For the preview
 * and the live chain that link is the audio input of the second effect, taken from the `audio` output of the first, when the second has no audio input of its own.
 */
export function renderLinksAsAudio(nodes, links) {
  const known = new Set(nodes.map((node) => node.id));
  const hasInput = (id) => links.some((other) => other.to === id && other.type === 'audio' && (other.inName === 'in' || other.inName === 'audio') && !other.disabled);
  return links.map((link) => (link.type === 'render' && !link.disabled && known.has(link.from) && known.has(link.to) && !hasInput(link.to)
    ? { ...link, type: 'audio', inName: 'in', outName: 'audio' } : link));
}

/** The sounds added together, sample for sample, and cut at full scale like the 16 bit sum of the build (the region merge); a mono sound feeds both sides of a stereo one. */
export function sumSaturated(parts) {
  const sampleRate = parts[0].sampleRate;
  const length = Math.max(...parts.map((part) => part.channels[0].length));
  const channels = Array.from({ length: Math.max(...parts.map((part) => part.channels.length)) }, () => new Float32Array(length));
  channels.forEach((out, c) => {
    for (const part of parts) {
      const channel = part.channels[Math.min(c, part.channels.length - 1)];
      for (let i = 0; i < channel.length; i++) out[i] += channel[i];
    }

    for (let i = 0; i < length; i++) out[i] = clamp(out[i], -1, 1);
  });
  return { sampleRate, channels };
}
