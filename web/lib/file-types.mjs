// One source of truth for what the studio can open from the Project panel (main.js openProjectFile) and what the tabs may therefore show.
export const AUDIO_EXTENSIONS = ['.wav', '.flac', '.mp3', '.ogg', '.m4a', '.aif', '.aiff'];
export const MIDI_EXTENSIONS = ['.mid', '.midi'];
export const SEQUENCE_EXTENSIONS = ['.seq', '.ampleseq'];
export const LYRICS_EXTENSIONS = ['.lyr', '.lrc', '.amplelrc'];
export const TEXT_EXTENSIONS = ['.lyr', '.lrc', '.amplelrc', '.srt', '.txt', '.chords', '.md', '.csv', '.json', '.amplec'];

export function extensionOf(path) {
  const name = String(path || '');
  const i = name.lastIndexOf('.');
  return i < 0 ? '' : name.slice(i).toLowerCase();
}

export function isOpenable(path) {
  const e = extensionOf(path);
  return [AUDIO_EXTENSIONS, MIDI_EXTENSIONS, SEQUENCE_EXTENSIONS, TEXT_EXTENSIONS].some((list) => list.includes(e));
}
