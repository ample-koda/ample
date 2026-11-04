// One MIDI in for the whole app: the buttons of the Jam, the Delay Llama window and the keyboard dock all switch the same input, and its notes go to one place (keys-play.js). It is remembered, so it
// comes back on when a window that plays is opened the next time. @ample:midi-in
import { midiAvailable, startMidiIn } from './midi-in.js';

const STORE = 'ample.midi.on', CHANNEL = 'ample.midi.channel';
const stateListeners = new Set(), noteListeners = new Set();
let run = null, names = [], starting = null, channelNow = null;

const saved = () => { try { return localStorage.getItem(STORE) === '1'; } catch { return false; } };
const keep = (on) => { try { localStorage.setItem(STORE, on ? '1' : '0'); } catch { /* storage can be blocked */ } };
const tell = () => { for (const listener of stateListeners) listener(Boolean(run), names); };

/** The channel that is listened to: 0 for all, 1 to 16 for one (remembered). */
export function midiChannel() { if (channelNow === null) { try { channelNow = Number(localStorage.getItem(CHANNEL)) || 0; } catch { channelNow = 0; } } return channelNow; }
export function setMidiChannel(channel) { channelNow = Math.max(0, Math.min(16, Math.round(channel))); try { localStorage.setItem(CHANNEL, String(channelNow)); } catch { /* storage can be blocked */ } tell(); }
export const midiCan = midiAvailable;
export const midiActive = () => Boolean(run);
export const midiNames = () => names;
/** Hears the switch (on or off) and the devices that are connected. Returns the way to stop. */
export function onMidiState(listener) { stateListeners.add(listener); return () => stateListeners.delete(listener); }
/** Hears the input: (type 'on' | 'off', pitch, velocity), ('sustain', 0, 127 or 0) for the pedal and ('bend', 0, -1 to 1) for the wheel. Returns the way to stop. */
export function onMidiNote(listener) { noteListeners.add(listener); return () => noteListeners.delete(listener); }

/** Switches the MIDI input on or off. Throws a message the user can read when it cannot be switched on. */
export async function setMidi(on, remember = true) {
  if (!on) { run?.stop(); run = null; names = []; if (remember) keep(false); tell(); return; }
  if (run) return;
  if (!starting) {
    starting = startMidiIn({
      onNoteOn: (pitch, velocity) => { for (const listener of noteListeners) listener('on', pitch, velocity); },
      onNoteOff: (pitch) => { for (const listener of noteListeners) listener('off', pitch, 0); },
      onSustain: (on) => { for (const listener of noteListeners) listener('sustain', 0, on ? 127 : 0); },
      onBend: (value) => { for (const listener of noteListeners) listener('bend', 0, value); },
      channel: midiChannel,
      onChange: (list) => { names = list; tell(); },
    }).then((started) => { run = started; names = started.names; if (remember) keep(true); tell(); }).finally(() => { starting = null; });
  }
  await starting;
}

/** Puts the input back on when it was on the last time (quietly: a browser that does not allow it is left alone). */
export function restoreMidi() { if (!run && saved() && midiAvailable()) setMidi(true, false).catch(() => {}); }
export const toggleMidi = () => setMidi(!run);
