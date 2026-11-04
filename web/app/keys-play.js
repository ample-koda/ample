// Where played notes go, for the keyboard dock and for the MIDI input alike: the window that was used last (keys-router.js: the Jam, the Delay Llama window) or, without one, the selected block (a MIDI clip,
// an instrument that a clip feeds, a Delay Llama). One player for the whole app; the MIDI input of midi-hub.js is wired to it here, so a note from a MIDI cable is played even when no dock is open. It also
// knows the hold latch and the sustain pedal, the pitch wheel, the transposing and the scale lock. @ample:virtual-keyboard
import { S, flat } from './store.js';
import { createNoteAudition } from './note-audition.js';
import { llamaLive, llamaTweaks, onLlamaTweak } from './llama-live.js';
import { llamaSettings } from '../lib/clientside-delay-llama.mjs';
import { liveAudioContext } from './live-play.js';
import { playbackOutput } from './playback-volume.js';
import { activeReceiver } from './keys-router.js';
import { onMidiNote } from './midi-hub.js';
import { snapToScale } from '../lib/virtual-keys.mjs';
import { ownSound } from './keys-sound.js';

const INSTRUMENTS = new Set(['gm', 'synth', 'piano', 'guitar', 'sampler', 'sequencer', 'drums', 'delay-llama']);

/** What the selected block means for the keyboard: { kind: 'clip', clip, choose } | { kind: 'llama', node } | null. */
export function keysTarget() {
  const graph = flat(), node = graph.nodes.find((candidate) => candidate.id === S.sel);
  if (!node) return null;
  if (node.tpl === 'midi-clip') return { kind: 'clip', clip: node.id, title: node.title || node.id };
  if (node.tpl === 'delay-llama') return { kind: 'llama', node, title: node.title || node.id };
  if (!INSTRUMENTS.has(node.tpl)) return null;
  const feed = graph.links.find((link) => !link.disabled && link.to === node.id && link.inName === 'notes');
  const clip = feed && graph.nodes.find((candidate) => candidate.id === feed.from && candidate.tpl === 'midi-clip');
  return clip ? { kind: 'clip', clip: clip.id, choose: node.id, title: node.title || node.id } : null;
}

const ppqOf = (clip) => Number.parseInt(clip?.params?.ppq ?? '', 10) || 1_000_000;
const tempo = () => Number(S.doc?.project?.tempo) || 120;

function createPlayer() {
  const auditions = new Map();   // clip id -> audition
  const voices = new Map();      // played key -> { pitch, stop, released }
  // hold: a latch of the screen; pedal: the sustain pedal of a MIDI keyboard; scale: a Set of pitch classes (or null) that played notes are moved into when lock is on; bendRange: semitones of a full wheel
  // plays: 'auto' (the window used last, else the selected block, else the dock's own sound) or 'own' (always the dock's own sound)
  const settings = { hold: false, pedal: false, transpose: 0, scale: null, lock: false, bendRange: 2, plays: 'auto' };
  const down = new Set();        // the keys that are held down right now
  const taps = new Set();        // listeners of what is played ('on' and 'off' with the pitch that sounds, 'bend' with semitones), for an ad-hoc take
  const tell = (type, pitch, value) => { for (const tap of taps) tap(type, pitch, value); };
  let llama = null, llamaNode = null, llamaStop = null, llamaOut = null;
  const auditionFor = (target) => {
    if (!auditions.has(target.clip)) {
      const clip = flat().nodes.find((node) => node.id === target.clip);
      auditions.set(target.clip, createNoteAudition({ clipId: target.clip, ppq: ppqOf(clip), bpm: tempo }));
    }
    const audition = auditions.get(target.clip);
    if (target.choose) audition.preview.choose(target.choose);
    return audition;
  };
  async function llamaFor(target) {
    const ctx = liveAudioContext();
    if (!llama) {
      llama = await llamaLive(ctx);
      llamaOut = ctx.createGain(); llamaOut.connect(playbackOutput(ctx));
      llama.connect(llamaOut);
    }
    llamaNode = target.node;
    llamaStop?.();
    llamaStop = onLlamaTweak((block, patch) => { if (block === llamaNode?.id) llama.set(llamaSettings({ ...llamaNode.params, ...llamaTweaks(block), ...patch })); });
    llama.set(llamaSettings({ ...target.node.params, ...llamaTweaks(target.node.id) }));
    return llama;
  }
  /** Starts a note on where the keyboard plays (the window used last, else the selected block); resolves to the way to end it, or null when there is nowhere to play. */
  async function sound(pitch, velocity) {
    if (settings.plays !== 'own') {
      const receiver = activeReceiver();
      if (receiver) { receiver.noteOn(pitch, velocity); return () => receiver.noteOff(pitch); }
      const target = keysTarget();
      if (target?.kind === 'llama') { const voice = await llamaFor(target); voice.noteOn(pitch, velocity); return () => voice.noteOff(pitch); }
      if (target) { const audition = auditionFor(target), id = audition.trigger({ pitch, velocity, length: ppqOf(null) }, { heldMs: 4000, startedAt: performance.now() }); return () => audition.release(id); }
    }
    // nothing else to play (or the own sound was asked for): the dock plays an instrument itself
    return ownSound.play(pitch, velocity);
  }
  const release = (key) => {
    const voice = voices.get(key);
    if (!voice) return;
    voices.delete(key);
    voice.released = true;
    tell('off', voice.pitch, 0);
    voice.stop?.();   // a note that is still starting is ended by the one that starts it
  };
  const sustained = () => settings.hold || settings.pedal;
  const letGo = () => { for (const key of [...voices.keys()]) if (!down.has(key)) release(key); };
  return {
    settings,
    sound,
    /** Listens to what is played; returns the way to stop. */
    tap(listener) { taps.add(listener); return () => taps.delete(listener); },
    async on(played, velocity) {
      let pitch = Math.max(0, Math.min(127, played + settings.transpose));
      if (settings.lock && settings.scale) pitch = snapToScale(pitch, settings.scale);
      down.add(played);
      if (voices.has(played)) release(played);
      const voice = { pitch, stop: null, released: false };
      voices.set(played, voice);
      tell('on', pitch, velocity);
      const stop = await sound(pitch, velocity);
      if (!stop) { if (voices.get(played) === voice) { voices.delete(played); voice.released = true; tell('off', pitch, 0); } return; }
      voice.stop = stop;
      if (voice.released) stop();   // the key was let go before the sound had started
    },
    off(played) { down.delete(played); if (!sustained()) release(played); },
    /** Hold: the notes keep sounding after the key is let go, until Hold is switched off. */
    setHold(on) { settings.hold = on; if (!on && !settings.pedal) letGo(); },
    /** The sustain pedal: like Hold, but it follows the pedal. */
    setPedal(on) { settings.pedal = on; if (!on && !settings.hold) letGo(); },
    /** The pitch wheel, -1 to 1: it bends what is sounding in the window that is played (or a Delay Llama), as many semitones as the range says. */
    bend(value) {
      const semitones = value * settings.bendRange;
      tell('bend', 0, semitones);
      const receiver = activeReceiver();
      if (receiver) receiver.bend?.(semitones); else llama?.bend(semitones);
    },
    /** True when a note goes to the dock's own sound (nothing is open or selected to play, or it was asked for). */
    usesOwnSound() { return settings.plays === 'own' || (!activeReceiver() && !keysTarget()); },
    /** Renders the notes of the keys on the screen ahead for the clip that is played, so that they sound with the real instrument and not the rough sketch voice. */
    warm(first, last) {
      const target = settings.plays === 'own' || activeReceiver() ? null : keysTarget();
      if (target?.kind === 'clip') auditionFor(target).warm([], last, first);
    },
    setScale(set, lock) { settings.scale = set; settings.lock = Boolean(lock && set); },
    allOff() { down.clear(); for (const key of [...voices.keys()]) release(key); },
  };
}

export const player = createPlayer();
// a note, the pedal and the wheel of the MIDI input are played like those of the keyboard
onMidiNote((type, pitch, value) => {
  if (type === 'on') player.on(pitch, value);
  else if (type === 'off') player.off(pitch);
  else if (type === 'sustain') player.setPedal(value >= 64);
  else if (type === 'bend') player.bend(value);   // (the value of the wheel travels in the third place)
});
