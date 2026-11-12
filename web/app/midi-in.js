// MIDI in from the browser (Web MIDI): a keyboard on USB or a virtual MIDI cable (loopMIDI and the like) shows up as an input. Notes are read (note on, note off, and a note on with velocity 0), the sustain pedal
// (controller 64) and the pitch wheel; a channel can be chosen. Listening follows plugged and unplugged devices. @ample:midi-in
import { capabilities, whyNot } from '../lib/caps.mjs';
import { bendValue } from '../lib/virtual-keys.mjs';

/** True when this browser can do MIDI at all (Chrome and Edge do, over https or localhost). */
export const midiAvailable = () => capabilities().midi;

/**
 * Starts listening to every MIDI input. Returns { stop, names } or throws a message the user can read.
 * @param {{ onNoteOn: (pitch: number, velocity: number) => void, onNoteOff: (pitch: number) => void, onSustain?: (on: boolean) => void, onBend?: (value: number) => void, channel?: () => number, onChange?: (names: string[]) => void }} handlers
 * `channel()` is 0 for every channel, or 1 to 16 to listen to that channel only.
 */
export async function startMidiIn({ onNoteOn, onNoteOff, onSustain = () => {}, onBend = () => {}, channel = () => 0, onChange = () => {} }) {
  if (!midiAvailable()) throw new Error(whyNot('midi'));
  let access;
  try { access = await navigator.requestMIDIAccess(); } catch (error) { throw new Error(`No MIDI access: ${error.message || 'the browser did not allow it'}`); }
  const message = (event) => {
    const [status, pitch, velocity] = event.data, kind = status & 0xf0, wanted = channel();
    if (wanted > 0 && (status & 0x0f) + 1 !== wanted) return;   // another channel
    if (kind === 0x90 && velocity > 0) onNoteOn(pitch, velocity);
    else if (kind === 0x80 || (kind === 0x90 && velocity === 0)) onNoteOff(pitch);
    else if (kind === 0xb0 && pitch === 64) onSustain(velocity >= 64);
    else if (kind === 0xe0) onBend(bendValue(pitch, velocity));
  };
  const attach = () => {
    const inputs = [...access.inputs.values()];
    for (const input of inputs) input.onmidimessage = message;
    onChange(inputs.map((input) => input.name || input.id));
    return inputs;
  };
  const inputs = attach();
  access.onstatechange = attach;
  return {
    names: inputs.map((input) => input.name || input.id),
    stop() { access.onstatechange = null; for (const input of access.inputs.values()) input.onmidimessage = null; },
  };
}
