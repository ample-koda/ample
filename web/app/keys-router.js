// Where the notes of the keyboard dock (and of a MIDI input) go: a window that can be played (the Jam, the Delay Llama window) registers itself here, and the dock plays the window that was used last, so
// the dock hooks into the window that is open instead of guessing from the selection. Without such a window the dock plays the selected block. @ample:virtual-keyboard

const receivers = [];
const listeners = new Set();
let activeId = null;

/**
 * @param {{ id: string, title: string, kind?: string, alive?: () => boolean, noteOn: (pitch: number, velocity: number) => void, noteOff: (pitch: number) => void,
 *   knobs?: () => object[], record?: () => void, recording?: () => boolean }} receiver
 * @returns {() => void} the way to take it out again
 */
export function registerReceiver(receiver) {
  const at = receivers.findIndex((item) => item.id === receiver.id);
  if (at >= 0) receivers.splice(at, 1);
  receivers.push(receiver);
  activeId = receiver.id;
  tell();
  return () => { const i = receivers.indexOf(receiver); if (i >= 0) receivers.splice(i, 1); if (activeId === receiver.id) activeId = null; tell(); };
}

/** The window that was used last (the one that was opened last until another is clicked); windows that are gone are dropped. */
export function activeReceiver() {
  for (let i = receivers.length - 1; i >= 0; i--) if (receivers[i].alive && !receivers[i].alive()) receivers.splice(i, 1);
  return receivers.find((item) => item.id === activeId) ?? receivers.at(-1) ?? null;
}

export function activateReceiver(id) { if (activeId !== id && receivers.some((item) => item.id === id)) { activeId = id; tell(); } }
export function onReceivers(listener) { listeners.add(listener); return () => listeners.delete(listener); }
function tell() { for (const listener of listeners) listener(); }
